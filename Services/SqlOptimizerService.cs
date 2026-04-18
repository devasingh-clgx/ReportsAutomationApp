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
    public class SqlOptimizerService : IExtractionStep
    {
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClient;

        public SqlOptimizerService(IWebHostEnvironment env, IConfiguration config, IHttpClientFactory httpClientFactory)
        {
            _env = env;
            _config = config;
            _httpClient = httpClientFactory;
        }

        public int StepId => 4;

        public async Task<StepResult> ExecuteAsync(string reportPath, string semanticModelPath)
        {
            try
            {
                string reportName = new DirectoryInfo(reportPath).Name;
                string exportsFolder = Path.Combine(_env.WebRootPath, "Exports");

                if (!Directory.Exists(exportsFolder))
                    return new StepResult { IsSuccess = false, Message = "Exports folder not found." };

                // Find the output from Step 3
                var step3File = new DirectoryInfo(exportsFolder)
                    .GetFiles($"Final_SnowflakeSQL_{reportName}_*.csv")
                    .OrderByDescending(f => f.CreationTime)
                    .FirstOrDefault();

                if (step3File == null)
                    return new StepResult { IsSuccess = false, Message = "Missing Step 3 SQL CSV. Ensure Step 3 is completed." };

                var rows = ParseCsv(step3File.FullName);
                if (rows.Count <= 1)
                    return new StepResult { IsSuccess = false, Message = "Step 3 CSV is empty." };

                string endpoint = _config["AzureOpenAI:Endpoint"]?.TrimEnd('/');
                string deployment = _config["AzureOpenAI:DeploymentName"];
                string apiVersion = _config["AzureOpenAI:ApiVersion"];
                string apiKey = _config["AzureOpenAI:ApiKey"];

                string requestUrl = $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={apiVersion}";
                using var httpClient = _httpClient.CreateClient();
                httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

                StringBuilder outputCsv = new StringBuilder();

                // Keep original headers, rename the last column to Optimized_SQL
                var headers = new List<string>(rows[0]);
                headers[headers.Count - 1] = "Optimized_Snowflake_SQL";
                outputCsv.AppendLine(string.Join(",", headers.Select(h => $"\"{h.Replace("\"", "\"\"")}\"")));

                int processedCount = 0;

                for (int i = 1; i < rows.Count; i++)
                {
                    var row = rows[i];
                    string rawSql = row.Last(); // The SQL generated in Step 3

                    if (string.IsNullOrWhiteSpace(rawSql) || rawSql.StartsWith("-- ERROR"))
                    {
                        outputCsv.AppendLine(string.Join(",", row.Select(c => $"\"{c?.Replace("\"", "\"\"") ?? ""}\"")));
                        continue;
                    }

                    string visualName = row[3];
                    string visualType = row[4];

                    string systemPrompt = GetOptimizerPrompt();
                    string userPrompt = $@"
                    VISUAL TYPE: {visualType}
                    VISUAL NAME: {visualName}

                    RAW SQL TO OPTIMIZE:
                    {rawSql}";

                    string optimizedSql = await CallAzureOpenAiAsync(httpClient, requestUrl, systemPrompt, userPrompt);

                    row[row.Count - 1] = optimizedSql; // Replace raw SQL with optimized SQL
                    outputCsv.AppendLine(string.Join(",", row.Select(c => $"\"{c?.Replace("\"", "\"\"") ?? ""}\"")));
                    processedCount++;
                }

                string outFileName = $"Optimized_SnowflakeSQL_{reportName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                string outFilePath = Path.Combine(exportsFolder, outFileName);
                await File.WriteAllTextAsync(outFilePath, outputCsv.ToString());

                return new StepResult
                {
                    IsSuccess = true,
                    Message = $"Successfully optimized SQL for {processedCount} visuals.",
                    DownloadFilePath = $"/Exports/{outFileName}",
                    ExtractedDataPreview = outputCsv.ToString().Substring(0, Math.Min(outputCsv.Length, 1000)) + "...\n[Data Truncated]"
                };
            }
            catch (Exception ex)
            {
                return new StepResult { IsSuccess = false, Message = $"Error in SQL Optimization: {ex.Message}" };
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
                temperature = 0.0,
                max_tokens = 3000
            };

            int maxRetries = 3;
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
                    int waitTime = baseDelayMs * attempt;
                    if (response.Headers.TryGetValues("Retry-After", out var retryHeaders) && int.TryParse(retryHeaders.FirstOrDefault(), out int retrySeconds))
                        waitTime = retrySeconds * 1000;

                    await Task.Delay(waitTime);
                    continue;
                }
            }
            return "-- ERROR: Optimizer API Call Failed.";
        }

        private string GetOptimizerPrompt()
        {
            return @"You are a Senior Snowflake Database Administrator.
Your job is to take raw, machine-generated Snowflake SQL and refactor it to human-grade, highly optimized standards.
Output ONLY raw Snowflake SQL. No explanations.

### REFACTORING RULES:
1. EXTRACT GLOBAL FILTERS: If you see a CASE WHEN condition repeated across multiple aggregates (e.g., CASE WHEN IS_CURRENT = 'Yes'), remove it from the CASE statements and apply it once as a global WHERE clause.
2. PREVENT TRUNCATION (CAST TO FLOAT): Whenever an AVG() function or a division (/) operator is used on a numeric column (like AGE), you MUST wrap the column in CAST(column AS FLOAT). 
   - Example: AVG(CAST(mei.DEPRECIATION_AGE AS FLOAT))
3. REMOVE UNNECESSARY NESTING: Simplify redundant COALESCE or math logic if it is excessively nested.
4. MAINTAIN INTENT: Do not change the table names, schema names, or column names. Only optimize the logical structure and data types.
5. PRUNE COLUMNS: If the query has excessive GROUP BY columns that look like irrelevant tooltips, you may prune them to keep the query focused on the primary metric.";
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
                else if (c == ',' && !inQuotes) { currentRow.Add(currentCell.ToString()); currentCell.Clear(); }
                else if ((c == '\r' || c == '\n') && !inQuotes)
                {
                    if (c == '\r' && i + 1 < fileContent.Length && fileContent[i + 1] == '\n') i++;
                    currentRow.Add(currentCell.ToString()); currentCell.Clear();
                    if (currentRow.Any(cell => !string.IsNullOrWhiteSpace(cell))) parsedData.Add(new List<string>(currentRow));
                    currentRow.Clear();
                }
                else { currentCell.Append(c); }
            }
            if (currentCell.Length > 0 || currentRow.Count > 0) { currentRow.Add(currentCell.ToString()); parsedData.Add(currentRow); }
            return parsedData;
        }
    }
}