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
    public class SqlGeneratorService : IExtractionStep
    {
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClient;

        public SqlGeneratorService(IWebHostEnvironment env, IConfiguration config, IHttpClientFactory httpClientFactory)
        {
            _env = env;
            _config = config;
            _httpClient = httpClientFactory;
        }

        public int StepId => 3;

        public async Task<StepResult> ExecuteAsync(string reportPath, string semanticModelPath)
        {
            try
            {
                string reportName = new DirectoryInfo(reportPath).Name;
                string modelName = new DirectoryInfo(semanticModelPath).Name;
                string exportsFolder = Path.Combine(_env.WebRootPath, "Exports");
                string schemaFolder = Path.Combine(_env.WebRootPath, "SchemaMaps");
                string configFolder = Path.Combine(_env.WebRootPath, "FabricRepo", "data", "SF-Source-Config");

                if (!Directory.Exists(exportsFolder))
                    return new StepResult { IsSuccess = false, Message = "Exports folder not found. Please run Step 1 and 2 first." };

                var daxFile = new DirectoryInfo(exportsFolder)
                    .GetFiles($"DAX_{reportName}_*.csv")
                    .OrderByDescending(f => f.CreationTime)
                    .FirstOrDefault();

                var mapFile = new DirectoryInfo(exportsFolder)
                    .GetFiles($"SemanticMap_{modelName}_*.txt")
                    .OrderByDescending(f => f.CreationTime)
                    .FirstOrDefault();

                string schemaPath = Path.Combine(schemaFolder, "QA_EDW_VIEWS_Schema.txt");
                string jsonConfigPath = Path.Combine(configFolder, "EstimateLineItemConfig.json");

                if (daxFile == null || mapFile == null)
                    return new StepResult { IsSuccess = false, Message = "Missing DAX CSV or Semantic Map TXT. Ensure Steps 1 & 2 are completed." };

                if (!File.Exists(schemaPath))
                    return new StepResult { IsSuccess = false, Message = "Missing Snowflake Schema. Please click 'Sync Git & Schema' on the layout page." };

                if (!File.Exists(jsonConfigPath))
                    return new StepResult { IsSuccess = false, Message = $"Missing JSON Mapping file at {jsonConfigPath}." };

                // Read all three layers of context
                string semanticContext = await File.ReadAllTextAsync(mapFile.FullName);
                string snowflakeSchemaContext = await File.ReadAllTextAsync(schemaPath);
                string lakehouseToSnowflakeMapping = await BuildMappingContextFromJsonAsync(jsonConfigPath);

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

                int processedCount = 0;

                for (int i = 1; i < daxRows.Count; i++)
                {
                    var row = daxRows[i];
                    if (row.Count < 10) continue;

                    string visualName = row[3];
                    string visualType = row[4];
                    string visualFilters = row[5];
                    string pageFilters = row[6];
                    string columnsUsed = row[7];
                    string measuresUsed = row[8];
                    string daxFormulas = row[9];

                    string systemPrompt = GetMasterSystemPrompt();

                    // The Full Context is Back!
                    string userPrompt = $@"
                        ### 1. SEMANTIC MAPPING CONTEXT (DAX Name -> Logical Lakehouse Name):
                        {semanticContext}

                        ### 2. LAKEHOUSE TO SNOWFLAKE TABLE MAPPING (Lakehouse Name -> Physical Snowflake View):
                        {lakehouseToSnowflakeMapping}

                        ### 3. SNOWFLAKE COLUMN SCHEMA (To verify existence):
                        {snowflakeSchemaContext}

                        ### 4. VISUAL METADATA:
                        - Visual Name: {visualName}
                        - Visual Type: {visualType}
                        - Grouping Columns: {columnsUsed}
                        - Visual Filters: {visualFilters}
                        - Page Filters: {pageFilters}

                        ### 5. DAX MEASURES TO TRANSLATE:
                        {daxFormulas}

                        Write the final, highly optimized Snowflake SQL query for this visual.";

                    string generatedSql = await CallAzureOpenAiAsync(httpClient, requestUrl, systemPrompt, userPrompt);

                    var newRow = new List<string>(row) { generatedSql };
                    outputCsv.AppendLine(string.Join(",", newRow.Select(c => $"\"{c?.Replace("\"", "\"\"") ?? ""}\"")));
                    processedCount++;
                }

                string outFileName = $"Final_SnowflakeSQL_{reportName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                string outFilePath = Path.Combine(exportsFolder, outFileName);
                await File.WriteAllTextAsync(outFilePath, outputCsv.ToString());

                return new StepResult
                {
                    IsSuccess = true,
                    Message = $"Successfully generated direct Snowflake SQL for {processedCount} visuals.",
                    DownloadFilePath = $"/Exports/{outFileName}",
                    ExtractedDataPreview = outputCsv.ToString().Substring(0, Math.Min(outputCsv.Length, 1000)) + "...\n[Data Truncated]"
                };
            }
            catch (Exception ex)
            {
                return new StepResult { IsSuccess = false, Message = $"Error in SQL Generation: {ex.Message}" };
            }
        }

        private async Task<string> BuildMappingContextFromJsonAsync(string jsonPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Use this mapping to convert the Lakehouse table name into the exact physical Snowflake view name.");
            sb.AppendLine("Format: [Lakehouse Name] -> [Snowflake Target Schema].[Snowflake Target Table]");

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
            catch (Exception) { }

            return sb.ToString();
        }

        private async Task<string> CallAzureOpenAiAsync(HttpClient httpClient, string url, string systemPrompt, string userPrompt)
        {
            var payload = new
            {
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.0,
                max_tokens = 3000
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
                                return textContent.GetString().Replace("```sql", "").Replace("```", "").Trim();
                            }
                        }
                    }
                    return "-- ERROR: Failed to parse LLM response.";
                }
                else if ((int)response.StatusCode == 429)
                {
                    if (attempt == maxRetries) return "-- ERROR: Azure OpenAI Rate Limit Exceeded after max retries.";

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
                    string error = await response.Content.ReadAsStringAsync();
                    return $"-- ERROR: API Call Failed. {response.StatusCode}\n-- {error}";
                }
            }

            return "-- ERROR: Unknown API failure.";
        }

        private string GetMasterSystemPrompt()
        {
            return @"You are an elite DAX-to-Snowflake-SQL compiler.
Your job is to translate Power BI visual logic into highly optimized, readable Snowflake SQL.
Output ONLY raw Snowflake SQL code. No talk, no notes, no markdown formatting.

### THE 3-STEP MAPPING PROCESS (CRITICAL):
You MUST follow this mapping chain. Do not skip to Lakehouse mapping!
1. Read the DAX Table name (e.g., 'Merged Estimate Item').
2. Look at the SEMANTIC MAPPING CONTEXT to find the Lakehouse name (e.g., 'merged_estimate_item').
3. Look at the LAKEHOUSE TO SNOWFLAKE TABLE MAPPING to find the physical table (e.g., 'merged_estimate_item' -> QA_EDW.VIEWS.VW_MERGED_ESTIMATE_ITEM_DIMFACT).
4. Verify the columns against the SNOWFLAKE COLUMN SCHEMA.
FATAL ERROR: Returning `CWS.MERGED_ESTIMATE_ITEM` in the FROM clause. You MUST use the Snowflake physical name!

### STRICT SNOWFLAKE SYNTAX RULES (FATAL ERRORS IF IGNORED):
1. NO SQL SERVER BRACKETS: NEVER use [ ]. 
2. NO QUOTES ON IDENTIFIERS: DO NOT use double quotes around table or column names. Write the raw, uppercase identifiers (e.g., QA_EDW.VIEWS.VW_DIM_ITEM_CATEGORY).
3. READABLE ALIASES: You MUST use short, readable table aliases when joining or selecting (e.g., FROM QA_EDW.VIEWS.VW_MERGED_ESTIMATE_ITEM_DIMFACT AS mei). Do not repeat the full table name in the SELECT or WHERE clauses.
4. SCHEMA ENFORCEMENT: Ensure the schema from the mapping (e.g., QA_EDW.VIEWS) is used in the FROM clause.

### VISUAL AWARENESS & GROUPING RULES:
1. Visual Type 'KPI', 'Card', 'Shape', 'Textbox': Output a query that returns exactly ONE row and ONE scalar value. Do NOT use a global GROUP BY.
2. Visual Type 'Chart', 'Table', 'Matrix': Include the ""Grouping Columns"" in both the SELECT and GROUP BY clauses. Apply Visual/Page filters in the global WHERE clause.

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