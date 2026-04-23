using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using ReportsAutomationApp.Models;

namespace ReportsAutomationApp.Services
{
    public class BatchSqlGeneratorService : IExtractionStep
    {
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClient;

        public BatchSqlGeneratorService(IWebHostEnvironment env, IConfiguration config, IHttpClientFactory httpClientFactory)
        {
            _env = env;
            _config = config;
            _httpClient = httpClientFactory;
        }

        public int StepId => 3;

        // Class to deserialize the AI's JSON array response
        private class BatchSqlResult
        {
            public string ScenarioId { get; set; }
            public string Final_SQL { get; set; }
        }

        public async Task<StepResult> ExecuteAsync(string reportPath, string semanticModelPath)
        {
            try
            {
                string reportName = new DirectoryInfo(reportPath).Name;
                string safeReportName = ExportFileNameHelper.ToSafeToken(reportName);
                string modelName = new DirectoryInfo(semanticModelPath).Name;
                string safeModelName = ExportFileNameHelper.ToSafeToken(modelName);
                string exportsFolder = Path.Combine(_env.WebRootPath, "Exports");
                string schemaFolder = Path.Combine(_env.WebRootPath, "SchemaMaps");
                string configFolder = Path.Combine(_env.WebRootPath, "FabricRepo", "data", "SF-Source-Config");

                if (!Directory.Exists(exportsFolder))
                    return new StepResult { IsSuccess = false, Message = "Exports folder not found. Please run Step 1 and 2 first." };

                var daxFile = new DirectoryInfo(exportsFolder)
                    .GetFiles($"DAX_{safeReportName}_*.csv")
                    .OrderByDescending(f => f.CreationTime)
                    .FirstOrDefault();

                var mapFile = new DirectoryInfo(exportsFolder)
                    .GetFiles($"SemanticMap_{safeModelName}_*.txt")
                    .OrderByDescending(f => f.CreationTime)
                    .FirstOrDefault();

                string schemaPath = Path.Combine(schemaFolder, "QA_EDW_VIEWS_Schema.txt");
                string jsonConfigPath = Path.Combine(configFolder, "EstimateLineItemConfig.json");

                if (daxFile == null || mapFile == null)
                    return new StepResult { IsSuccess = false, Message = "Missing DAX CSV or Semantic Map TXT. Ensure Steps 1 & 2 are completed." };

                if (!File.Exists(schemaPath))
                    return new StepResult { IsSuccess = false, Message = "Missing Snowflake Schema. Please click 'Sync Git & Schema'." };

                string semanticContext = await File.ReadAllTextAsync(mapFile.FullName);
                string snowflakeSchemaContext = await File.ReadAllTextAsync(schemaPath);
                string lakehouseToSnowflakeMapping = File.Exists(jsonConfigPath) ? await BuildMappingContextFromJsonAsync(jsonConfigPath) : "";

                var daxRows = ParseCsv(daxFile.FullName);

                if (daxRows.Count <= 1)
                    return new StepResult { IsSuccess = false, Message = "DAX CSV is empty or only contains headers." };

                string endpoint = _config["AzureOpenAI:Endpoint"]?.TrimEnd('/');
                string deployment = _config["AzureOpenAI:DeploymentName"];
                string apiVersion = _config["AzureOpenAI:ApiVersion"];
                string apiKey = _config["AzureOpenAI:ApiKey"];

                if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(apiKey))
                    return new StepResult { IsSuccess = false, Message = "Azure OpenAI credentials missing in appsettings.json." };

                string requestUrl = $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={apiVersion}";
                using var httpClient = _httpClient.CreateClient();
                httpClient.DefaultRequestHeaders.Clear();
                httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

                StringBuilder outputCsv = new StringBuilder();
                outputCsv.AppendLine(string.Join(",", daxRows[0].Select(h => $"\"{h.Replace("\"", "\"\"")}\"")) + ",\"Final_Snowflake_SQL\"");

                // Group the rows by VisualId (Column Index 3)
                var rowsByVisual = daxRows.Skip(1).Where(r => r.Count >= 11).GroupBy(r => r[3]).ToList();
                int processedVisuals = 0;
                int processedScenarios = 0;

                foreach (var visualGroup in rowsByVisual)
                {
                    string visualId = visualGroup.Key;
                    var baseRow = visualGroup.First();

                    // Shared metadata across all scenarios for this visual
                    string visualType = baseRow[5];
                    string columnsUsed = baseRow[8];
                    string measuresUsed = baseRow[9];
                    string daxFormulas = baseRow[10];

                    StringBuilder scenariosBuilder = new StringBuilder();
                    foreach (var row in visualGroup)
                    {
                        scenariosBuilder.AppendLine($"- ScenarioId: {row[0]}");
                        scenariosBuilder.AppendLine($"  VisualName/ScenarioName: {row[4]}");
                        scenariosBuilder.AppendLine($"  VisualFilters: {row[6]}");
                        scenariosBuilder.AppendLine($"  PageFilters: {row[7]}");
                        scenariosBuilder.AppendLine();
                    }

                    string systemPrompt = GetMasterSystemPrompt();

                    string userPrompt = $@"
                        ### 1. SEMANTIC MAPPING CONTEXT (DAX Name -> Physical Snowflake View):
                        {semanticContext}

                        ### 2. FALLBACK LAKEHOUSE MAPPING:
                        {lakehouseToSnowflakeMapping}

                        ### 3. SNOWFLAKE COLUMN SCHEMA:
                        {snowflakeSchemaContext}

                        ### 4. SHARED VISUAL METADATA:
                        - Visual ID: {visualId}
                        - Visual Type: {visualType}
                        - Grouping Columns: {columnsUsed}

                        ### 5. DAX MEASURES TO TRANSLATE:
                        {daxFormulas}

                        ### 6. SCENARIOS TO GENERATE:
                        You must generate a unique SQL query for each ScenarioId below. Integrate the specific PageFilters (like DATEADD/BETWEEN) and VisualFilters into the WHERE clause for that specific scenario.
                        
                        {scenariosBuilder.ToString()}
                    ";

                    string jsonResponse = await CallAzureOpenAiAsync(httpClient, requestUrl, systemPrompt, userPrompt);

                    // Clean markdown blocks if the AI accidentally wrapped the JSON
                    string cleanedJson = jsonResponse.Replace("```json", "").Replace("```", "").Trim();

                    List<BatchSqlResult> aiResults = new List<BatchSqlResult>();
                    try
                    {
                        aiResults = JsonSerializer.Deserialize<List<BatchSqlResult>>(cleanedJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch (Exception)
                    {
                        // Fallback if JSON parsing totally fails
                    }

                    // Map the results back to the original rows
                    foreach (var row in visualGroup)
                    {
                        string targetScenarioId = row[0];
                        var matchedResult = aiResults?.FirstOrDefault(r => r.ScenarioId == targetScenarioId);

                        string finalSql = matchedResult != null ? matchedResult.Final_SQL : "-- ERROR: AI failed to return valid JSON for this scenario.";

                        var newRow = new List<string>(row) { finalSql };
                        outputCsv.AppendLine(string.Join(",", newRow.Select(c => $"\"{c?.Replace("\"", "\"\"") ?? ""}\"")));
                        processedScenarios++;
                    }

                    processedVisuals++;
                }

                string outFileName = $"Final_SnowflakeSQL_BATCH_{safeReportName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                string outFilePath = Path.Combine(exportsFolder, outFileName);
                await File.WriteAllTextAsync(outFilePath, outputCsv.ToString());

                return new StepResult
                {
                    IsSuccess = true,
                    Message = $"Successfully batch-processed {processedVisuals} visuals covering {processedScenarios} scenarios.",
                    DownloadFilePath = $"/Exports/{outFileName}",
                    ExtractedDataPreview = outputCsv.ToString().Substring(0, Math.Min(outputCsv.Length, 1000)) + "...\n[Data Truncated]"
                };
            }
            catch (Exception ex)
            {
                return new StepResult { IsSuccess = false, Message = $"Error in Batch SQL Generation: {ex.Message}" };
            }
        }

        private async Task<string> BuildMappingContextFromJsonAsync(string jsonPath)
        {
            var sb = new StringBuilder();
            try
            {
                string jsonString = await File.ReadAllTextAsync(jsonPath);
                using (JsonDocument doc = JsonDocument.Parse(jsonString))
                {
                    if (doc.RootElement.TryGetProperty("Tables", out JsonElement tablesArray))
                    {
                        foreach (var table in tablesArray.EnumerateArray())
                        {
                            string lakehouseName = table.GetProperty("DName").GetString() ?? "";
                            string snowflakeName = table.GetProperty("Name").GetString() ?? "";
                            string schema = table.GetProperty("Schema").GetString() ?? "VIEWS";
                            sb.AppendLine($"- {lakehouseName} -> QA_EDW.{schema}.{snowflakeName}");
                        }
                    }
                }
            }
            catch { }
            return sb.ToString();
        }

        private async Task<string> CallAzureOpenAiAsync(HttpClient httpClient, string url, string systemPrompt, string userPrompt)
        {
            // By adding "response_format" we strictly coerce compatible Azure OpenAI models into outputting valid JSON
            var payload = new
            {
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.0,
                max_tokens = 4000,
                response_format = new { type = "json_object" }
            };

            int maxRetries = 5;
            int baseDelayMs = 5000;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(url, content);

                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    using (JsonDocument doc = JsonDocument.Parse(responseJson))
                    {
                        if (doc.RootElement.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
                        {
                            var firstChoice = choices[0];
                            if (firstChoice.TryGetProperty("message", out JsonElement message) && message.TryGetProperty("content", out JsonElement textContent))
                            {
                                return textContent.GetString();
                            }
                        }
                    }
                    return "[]";
                }
                else if ((int)response.StatusCode == 429)
                {
                    if (attempt == maxRetries) return "[]";
                    int waitTime = baseDelayMs * attempt;
                    if (response.Headers.TryGetValues("Retry-After", out var retryHeaders) && int.TryParse(retryHeaders.FirstOrDefault(), out int retrySeconds))
                    {
                        waitTime = retrySeconds * 1000;
                    }
                    await Task.Delay(waitTime);
                    continue;
                }
                else
                {
                    return "[]";
                }
            }
            return "[]";
        }

        private string GetMasterSystemPrompt()
        {
            return @"You are an elite DAX-to-Snowflake-SQL compiler.
Your job is to translate Power BI visual logic into highly optimized, readable Snowflake SQL for MULTIPLE scenarios.

CRITICAL INSTRUCTION: You MUST respond ONLY with a raw JSON object containing an array called ""scenarios"". Do not use markdown wrappers. Do not include any conversational text.

JSON SCHEMA REQUIREMENT:
{
  ""scenarios"": [
    {
      ""ScenarioId"": ""<ID>"",
      ""Final_SQL"": ""<RAW_SQL_STRING>""
    }
  ]
}

### THE 2-STEP MAPPING PROCESS (CRITICAL):
1. Look at the SEMANTIC MAPPING CONTEXT to find the exact Snowflake View name for the tables in the DAX formulas.
2. Verify the columns against the SNOWFLAKE COLUMN SCHEMA.
FATAL ERROR: Returning `CWS.MERGED_ESTIMATE_ITEM` in the FROM clause. You MUST use the Snowflake physical name (e.g., QA_EDW.VIEWS.VW_MERGED_ESTIMATE_ITEM_DIMFACT)!

### STRICT SNOWFLAKE SYNTAX RULES (FATAL ERRORS IF IGNORED):
1. NO SQL SERVER BRACKETS: NEVER use [ ]. 
2. NO QUOTES ON IDENTIFIERS: DO NOT use double quotes around table or column names. Write the raw, uppercase identifiers.
3. READABLE ALIASES: You MUST use short, readable table aliases when joining or selecting. Do not repeat the full table name in the SELECT or WHERE clauses.
4. SCHEMA ENFORCEMENT: Ensure the schema from the mapping (e.g., QA_EDW.VIEWS) is used in the FROM clause.

### VISUAL AWARENESS & GROUPING RULES:
1. Visual Type 'KPI', 'Card', 'Shape', 'Textbox': Output a query that returns exactly ONE row and ONE scalar value. Do NOT use a global GROUP BY.
2. Visual Type 'Chart', 'Table', 'Matrix': Include the ""Grouping Columns"" in both the SELECT and GROUP BY clauses. Apply Visual/Page filters in the global WHERE clause.

### SCENARIO RULES:
- You will receive a list of Scenarios. The base SQL logic (JOINs, SELECT columns, GROUP BY) will likely be identical across all scenarios for this visual.
- The ONLY difference will be the `WHERE` clause. You must adapt the `WHERE` clause for each scenario based on its specific `PageFilters` and `VisualFilters`.

### DAX-TO-SNOWFLAKE TRANSLATION RULES:
Rule 1: Handling Nulls
- Whenever performing arithmetic on columns that might contain nulls, wrap them in COALESCE(X, 0).

Rule 2: Safe Division
- MUST use NULLIF. Example: SUM(mei.SALES) / NULLIF(SUM(mei.COSTS), 0)

Rule 3: Translating CALCULATE
- CALCULATE almost always translates to conditional aggregation using CASE WHEN inside an aggregate function. Example: SUM(CASE WHEN Region = 'North' THEN Sales ELSE 0 END).

Rule 4: COUNT and DISTINCT
- Treat DAX COUNT() identically to DISTINCTCOUNT(). Both should be translated to COUNT(DISTINCT alias.column) in Snowflake.

Rule 5: Case-Insensitive ORDER BY (CRITICAL FOR CHARTS/TABLES)
- DAX sorting is case-insensitive. Snowflake is case-sensitive.
- If the visual has grouping columns, you MUST add an ORDER BY clause. 
- When applying an ORDER BY on a string column to mimic DAX, wrap the column in UPPER(). 
- Example: ORDER BY UPPER(mei.CATEGORY_NAME)

Rule 6: Time Intelligence
- TOTALYTD translates to: SUM(Sales) OVER (PARTITION BY YEAR(Date) ORDER BY Date)
- SAMEPERIODLASTYEAR translates to: DATEADD(year, -1, Date)

Rule 7: VARIABLES (VAR)
- If a VAR is a scalar number, calculate it inline. Only use CTEs (WITH clause) for modular, heavily reused table subqueries.";
        }
        private List<List<string>> ParseCsv(string filePath)
        {
            var parsedData = new List<List<string>>();
            string fileContent = File.ReadAllText(filePath);
            var currentRow = new List<string>();
            StringBuilder currentCell = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < fileContent.Length; i++)
            {
                char c = fileContent[i];
                if (c == '\"')
                {
                    if (inQuotes && i + 1 < fileContent.Length && fileContent[i + 1] == '\"') { currentCell.Append('\"'); i++; }
                    else { inQuotes = !inQuotes; }
                }
                else if (c == ',' && !inQuotes)
                {
                    currentRow.Add(currentCell.ToString()); currentCell.Clear();
                }
                else if ((c == '\r' || c == '\n') && !inQuotes)
                {
                    if (c == '\r' && i + 1 < fileContent.Length && fileContent[i + 1] == '\n') i++;
                    currentRow.Add(currentCell.ToString()); currentCell.Clear();
                    if (currentRow.Any(cell => !string.IsNullOrWhiteSpace(cell))) parsedData.Add(new List<string>(currentRow));
                    currentRow.Clear();
                }
                else { currentCell.Append(c); }
            }

            if (currentCell.Length > 0 || currentRow.Count > 0)
            {
                currentRow.Add(currentCell.ToString()); parsedData.Add(currentRow);
            }

            return parsedData;
        }
    }
}