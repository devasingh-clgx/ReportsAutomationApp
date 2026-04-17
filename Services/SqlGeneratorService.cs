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

                if (!Directory.Exists(exportsFolder))
                    return new StepResult { IsSuccess = false, Message = "Exports folder not found. Please run Step 1 and 2 first." };

                // 1. Find the latest Step 1 (DAX) and Step 2 (Semantic Map) files
                var daxFile = new DirectoryInfo(exportsFolder)
                    .GetFiles($"DAX_{reportName}_*.csv")
                    .OrderByDescending(f => f.CreationTime)
                    .FirstOrDefault();

                var mapFile = new DirectoryInfo(exportsFolder)
                    .GetFiles($"SemanticMap_{modelName}_*.txt")
                    .OrderByDescending(f => f.CreationTime)
                    .FirstOrDefault();

                if (daxFile == null || mapFile == null)
                    return new StepResult { IsSuccess = false, Message = "Missing DAX CSV or Semantic Map TXT. Ensure Steps 1 & 2 are completed." };

                string semanticContext = await File.ReadAllTextAsync(mapFile.FullName);
                var daxRows = ParseCsv(daxFile.FullName);

                if (daxRows.Count <= 1)
                    return new StepResult { IsSuccess = false, Message = "DAX CSV is empty or only contains headers." };

                // 2. Setup Azure OpenAI Client
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

                // 3. Prepare the Output CSV
                StringBuilder outputCsv = new StringBuilder();
                // Extract headers from the first row and append our new SQL column
                outputCsv.AppendLine(string.Join(",", daxRows[0].Select(h => $"\"{h.Replace("\"", "\"\"")}\"")) + ",\"Lakehouse_SQL\"");

                int processedCount = 0;

                // 4. Iterate through each visual and call the LLM
                for (int i = 1; i < daxRows.Count; i++)
                {
                    var row = daxRows[i];
                    if (row.Count < 10) continue; // Skip malformed rows

                    string visualName = row[3];
                    string visualType = row[4];
                    string visualFilters = row[5];
                    string pageFilters = row[6];
                    string columnsUsed = row[7];
                    string measuresUsed = row[8];
                    string daxFormulas = row[9];

                    // Construct the LLM Prompts
                    string systemPrompt = GetMasterSystemPrompt();
                    string userPrompt = $@"
                                        ### SEMANTIC MAPPING CONTEXT:
                                        {semanticContext}

                                        ### VISUAL METADATA:
                                        - Visual Name: {visualName}
                                        - Visual Type: {visualType}
                                        - Grouping Columns (Dimensions): {columnsUsed}
                                        - Applied Filters (Visual Level): {visualFilters}
                                        - Applied Filters (Page Level): {pageFilters}

                                        ### DAX MEASURES TO TRANSLATE:
                                        {daxFormulas}

                                        Write the Snowflake Lakehouse SQL query for this visual.";

                    // Call Azure OpenAI using the created HttpClient
                    string generatedSql = await CallAzureOpenAiAsync(httpClient, requestUrl, systemPrompt, userPrompt);

                    // Append the original row + the new SQL to the output CSV
                    var newRow = new List<string>(row) { generatedSql };
                    outputCsv.AppendLine(string.Join(",", newRow.Select(c => $"\"{c?.Replace("\"", "\"\"") ?? ""}\"")));
                    processedCount++;
                }

                // 5. Save the generated SQL file
                string outFileName = $"GeneratedSQL_{reportName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                string outFilePath = Path.Combine(exportsFolder, outFileName);
                await File.WriteAllTextAsync(outFilePath, outputCsv.ToString());

                return new StepResult
                {
                    IsSuccess = true,
                    Message = $"Successfully generated SQL for {processedCount} visuals.",
                    DownloadFilePath = $"/Exports/{outFileName}",
                    ExtractedDataPreview = outputCsv.ToString().Substring(0, Math.Min(outputCsv.Length, 1000)) + "...\n[Data Truncated]"
                };
            }
            catch (Exception ex)
            {
                return new StepResult { IsSuccess = false, Message = $"Error in SQL Generation (Step 3): {ex.Message}" };
            }
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
                temperature = 0.0, // Strictly logic, zero creativity
                max_tokens = 3000
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync();
                return $"-- ERROR: API Call Failed. {response.StatusCode}\n-- {error}";
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            using (JsonDocument doc = JsonDocument.Parse(responseJson))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
                {
                    var firstChoice = choices[0];
                    if (firstChoice.TryGetProperty("message", out JsonElement message) && message.TryGetProperty("content", out JsonElement textContent))
                    {
                        string sql = textContent.GetString();
                        // Clean up markdown block if the model ignores the "no markdown" rule
                        sql = sql.Replace("```sql", "").Replace("```", "").Trim();
                        return sql;
                    }
                }
            }

            return "-- ERROR: Failed to parse LLM response.";
        }

        private string GetMasterSystemPrompt()
        {
            return @"You are an elite DAX-to-Snowflake-SQL compiler.
Your job is to translate Power BI visual logic into clean, readable, and highly optimized Snowflake Lakehouse SQL.
Output ONLY raw T-SQL/Snowflake SQL code. No talk, no notes, no markdown formatting.

### STRICT SCHEMA RULES (FATAL ERRORS IF IGNORED):
1. NEVER use the DAX Table Name in your SQL query. DAX table names often contain spaces (e.g., 'MERGED ESTIMATE ITEM') and are NOT the real database tables.
2. You MUST lookup the exact physical SQL Table Name from the provided Semantic Mapping context. 
   - Example Context: ""- DAX Table 'MERGED ESTIMATE ITEM' -> SQL Table: [CWS].[MERGED_ESTIMATE_ITEM_DIMFACT]""
   - Correct SQL: [CWS].[MERGED_ESTIMATE_ITEM_DIMFACT].[COLUMN_NAME]
   - Fatal Error SQL: [CWS].[MERGED ESTIMATE ITEM].[COLUMN_NAME]
3. NEVER default to 'dbo' if the mapping provided specifies 'CWS' or any other schema.

### VISUAL AWARENESS & GROUPING RULES:
1. If Visual Type is 'KPI', 'Card', 'Shape', or 'Textbox': Output a query that returns exactly ONE row and ONE scalar value. Do NOT use a global GROUP BY unless using a subquery.
2. If Visual Type is 'Chart', 'Table', or 'Matrix': Include the provided ""Grouping Columns"" in both the SELECT and GROUP BY clauses.
3. EXCEPTION TO GROUP BY: If the ""Grouping Columns"" list contains an implicit aggregation (e.g., `CountNonNull(...)` or `Sum(...)`), put the aggregation in the SELECT clause, but DO NOT put the underlying column in the GROUP BY clause.

### DAX-TO-SQL TRANSLATION & OPTIMIZATION RULES:
You must translate DAX logic into clean, performant SQL. Avoid bloated, repetitive queries.

Rule 1: Clean Global Filtering (Avoid CASE WHEN Explosions)
- If a DAX measure uses `CALCULATE(..., Table[Column] = 'Value')` and this filter applies to the entire complex math block, DO NOT repeat `CASE WHEN Table[Column] = 'Value'` 50 times inside the SELECT.
- Instead, apply that filter to a global `WHERE` clause, OR create a clean base CTE (e.g., `WITH FilteredBase AS (SELECT * FROM Table WHERE Column = 'Value')`) and perform the math on the CTE.

Rule 2: Handling Blanks, Nulls, and Zeroes safely but cleanly
- DAX ignores nulls in addition. SQL does not (`NULL + 5 = NULL`).
- When adding columns, use COALESCE cleanly: `SUM(COALESCE(Col1, 0) + COALESCE(Col2, 0))`. Do NOT over-nest COALESCE functions unneccessarily.

Rule 3: Safe Division (Divide by Zero)
- Division (/) throws a fatal error if denominator is 0. 
- MUST use NULLIF: `SUM(Sales) / NULLIF(SUM(Costs), 0)`

Rule 4: Time Intelligence
- TOTALYTD translates to: `SUM(Sales) OVER (PARTITION BY YEAR(Date) ORDER BY Date)`
- SAMEPERIODLASTYEAR translates to: `DATEADD(year, -1, Date)`

Rule 5: VARIABLES (VAR)
- If a DAX `VAR` is a scalar mathematical result, calculate it inline. 
- If a DAX `VAR` generates a filtered sub-table that is heavily reused, turn it into a CTE.";
        }
        // --- Robust Custom CSV Parser to handle newlines inside DAX strings ---
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
                    // Handle escaped quotes ("")
                    if (inQuotes && i + 1 < fileContent.Length && fileContent[i + 1] == '\"')
                    {
                        currentCell.Append('\"');
                        i++; // Skip the second quote
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    currentRow.Add(currentCell.ToString());
                    currentCell.Clear();
                }
                else if ((c == '\r' || c == '\n') && !inQuotes)
                {
                    if (c == '\r' && i + 1 < fileContent.Length && fileContent[i + 1] == '\n') i++; // Handle \r\n

                    currentRow.Add(currentCell.ToString());
                    currentCell.Clear();

                    if (currentRow.Any(cell => !string.IsNullOrWhiteSpace(cell)))
                    {
                        parsedData.Add(new List<string>(currentRow));
                    }
                    currentRow.Clear();
                }
                else
                {
                    currentCell.Append(c);
                }
            }

            // Add the final row if file doesn't end with newline
            if (currentCell.Length > 0 || currentRow.Count > 0)
            {
                currentRow.Add(currentCell.ToString());
                parsedData.Add(currentRow);
            }

            return parsedData;
        }
    }
}