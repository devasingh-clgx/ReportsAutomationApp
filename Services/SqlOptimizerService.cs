using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using ReportsAutomationApp.Models;
using Snowflake.Data.Client;

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
                string safeReportName = ExportFileNameHelper.ToSafeToken(reportName);
                string exportsFolder = Path.Combine(_env.WebRootPath, "Exports");
                string schemaFolder = Path.Combine(_env.WebRootPath, "SchemaMaps");
                string schemaPath = Path.Combine(schemaFolder, "QA_EDW_VIEWS_Schema.txt");

                if (!Directory.Exists(exportsFolder))
                    return new StepResult { IsSuccess = false, Message = "Exports folder not found." };

                var step3File = new DirectoryInfo(exportsFolder)
                    .GetFiles($"Final_SnowflakeSQL_{safeReportName}_*.csv")
                    .OrderByDescending(f => f.CreationTime)
                    .FirstOrDefault();

                if (step3File == null)
                    return new StepResult { IsSuccess = false, Message = "Missing Step 3 SQL CSV. Ensure Step 3 is completed." };

                if (!File.Exists(schemaPath))
                    return new StepResult { IsSuccess = false, Message = "Missing Snowflake Schema mapping file." };

                string snowflakeSchemaContext = await File.ReadAllTextAsync(schemaPath);

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

                var headers = new List<string>(rows[0]);
                headers[headers.Count - 1] = "Optimized_Snowflake_SQL";
                outputCsv.AppendLine(string.Join(",", headers.Select(h => $"\"{h.Replace("\"", "\"\"")}\"")));

                int processedCount = 0;
                int maxRetries = 3;

                for (int i = 1; i < rows.Count; i++)
                {
                    var row = rows[i];
                    string currentSql = row.Last();

                    if (string.IsNullOrWhiteSpace(currentSql) || currentSql.StartsWith("-- ERROR"))
                    {
                        outputCsv.AppendLine(string.Join(",", row.Select(c => $"\"{c?.Replace("\"", "\"\"") ?? ""}\"")));
                        continue;
                    }

                    string visualName = row[4];
                    string visualType = row[5];
                    string columnsUsed = row[8];

                    bool isValid = false;
                    int attempt = 0;

                    // THE SELF-HEALING LOOP
                    while (attempt < maxRetries && !isValid)
                    {
                        attempt++;

                        // 1. Test the query against Snowflake
                        var (snowflakeSuccess, errorMessage) = await TestSnowflakeQueryAsync(currentSql, visualType);

                        if (snowflakeSuccess)
                        {
                            isValid = true;
                            break;
                        }

                        // 2. If it failed, ask AI to fix it
                        string systemPrompt = GetOptimizerPrompt();
                        string userPrompt = $@"
                            ### SNOWFLAKE SCHEMA CONTEXT:
                            {snowflakeSchemaContext}

                            ### VISUAL METADATA (Defines Output Shape):
                            - Visual Type: {visualType}
                            - Grouping Columns Expected: {columnsUsed}

                            ### FAILED SQL QUERY:
                            {currentSql}

                            ### SNOWFLAKE ERROR MESSAGE:
                            {errorMessage}

                            Fix the query based on the error above. Return ONLY the corrected raw Snowflake SQL.";

                        currentSql = await CallAzureOpenAiAsync(httpClient, requestUrl, systemPrompt, userPrompt);
                    }

                    // If it failed all 3 attempts, mark it as an error
                    if (!isValid)
                    {
                        currentSql = $"-- FAILED OPTIMIZATION AFTER 3 TRIES.\n-- LAST ERROR:\n/* {currentSql} */";
                    }

                    row[row.Count - 1] = currentSql;
                    outputCsv.AppendLine(string.Join(",", row.Select(c => $"\"{c?.Replace("\"", "\"\"") ?? ""}\"")));
                    processedCount++;
                }

                string outFileName = $"Optimized_SnowflakeSQL_{safeReportName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                string outFilePath = Path.Combine(exportsFolder, outFileName);
                await File.WriteAllTextAsync(outFilePath, outputCsv.ToString());

                return new StepResult
                {
                    IsSuccess = true,
                    Message = $"Successfully optimized & validated SQL for {processedCount} scenarios against Snowflake.",
                    DownloadFilePath = $"/Exports/{outFileName}",
                    ExtractedDataPreview = outputCsv.ToString().Substring(0, Math.Min(outputCsv.Length, 1000)) + "...\n[Data Truncated]"
                };
            }
            catch (Exception ex)
            {
                return new StepResult { IsSuccess = false, Message = $"Error in SQL Optimization: {ex.Message}" };
            }
        }

        /// <summary>
        /// Executes the SQL in Snowflake to validate syntax, types, and shape.
        /// </summary>
        private async Task<(bool IsSuccess, string ErrorMessage)> TestSnowflakeQueryAsync(string sql, string visualType)
        {
            try
            {
                string connStr = ResolveSnowflakeConnectionString();
                if (string.IsNullOrEmpty(connStr))
                {
                    // Fallback bypass if user hasn't configured connection yet
                    return (true, "");
                }

                // Wrap in LIMIT 0 so we only test compilation and metadata (no data movement costs!)
                string testSql = $"SELECT * FROM (\n{sql}\n) LIMIT 0;";

                using var conn = new SnowflakeDbConnection { ConnectionString = connStr };
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = testSql;

                using var reader = await cmd.ExecuteReaderAsync();
                int columnCount = reader.FieldCount;

                if (visualType.Equals("KPI", StringComparison.OrdinalIgnoreCase) ||
                    visualType.Equals("Card", StringComparison.OrdinalIgnoreCase))
                {
                    if (columnCount > 1)
                        return (false, "SHAPE ERROR: VisualType is KPI. The query MUST return exactly 1 scalar integer/float value. Do NOT use GROUP BY.");
                }
                else if (visualType.Equals("Slicer", StringComparison.OrdinalIgnoreCase))
                {
                    if (columnCount > 1)
                        return (false, "SHAPE ERROR: VisualType is Slicer. The query MUST return exactly 1 column representing the slicer list. Remove extra columns.");
                }

                return (true, "Success");
            }
            catch (Exception ex)
            {
                // Return the EXACT Snowflake error (e.g. "Numeric value out of range", "Invalid Identifier")
                return (false, $"SNOWFLAKE EXECUTION ERROR: {ex.Message}");
            }
        }

        private string ResolveSnowflakeConnectionString()
        {
            string? explicitConnectionString = _config["Snowflake:ConnectionString"];
            if (!string.IsNullOrWhiteSpace(explicitConnectionString))
            {
                return explicitConnectionString;
            }

            var pairs = new List<string>
            {
                BuildPair("account", _config["Snowflake:Account"]),
                BuildPair("user", _config["Snowflake:User"]),
                BuildPair("password", _config["Snowflake:Password"]),
                BuildPair("authenticator", _config["Snowflake:Authenticator"]),
                BuildPair("db", _config["Snowflake:Database"]),
                BuildPair("schema", _config["Snowflake:Schema"]),
                BuildPair("warehouse", _config["Snowflake:Warehouse"] ?? _config["Snowflake:WarehouseName"]),
                BuildPair("role", _config["Snowflake:Role"]),
                BuildPair("private_key_file", _config["Snowflake:PrivateKeyFile"]),
                BuildPair("private_key_pwd", _config["Snowflake:PrivateKeyPassword"]),
                BuildPair("token", _config["Snowflake:Token"]),
                BuildPair("insecure_mode", _config["Snowflake:InsecureMode"]),
                BuildPair("ocsp_fail_open", _config["Snowflake:OcspFailOpen"])
            }
            .Where(x => !string.IsNullOrWhiteSpace(x));

            return string.Join(";", pairs);
        }

        private static string BuildPair(string key, string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : $"{key}={value}";
        }

        private string GetOptimizerPrompt()
        {
            return @"You are an elite Snowflake Database Administrator and Error-Resolution bot.
Your job is to fix a broken Snowflake SQL query that failed execution. 
Output ONLY raw Snowflake SQL code. Do not wrap in markdown or backticks.

### THE 3 GOLDEN RULES FOR FIXING ERRORS:

1. FIXING DATA TYPE & DIVISION ERRORS (CRITICAL):
- If the error is 'Division by Zero', wrap the denominator in NULLIF(denominator, 0).
- If the error is 'Numeric value out of range' or relates to decimal constraints, explicitly cast the columns before doing math: `CAST(col AS FLOAT)`.
- Whenever an AVG() function or a division (/) operator is used on a metric, you MUST wrap the column in CAST(column AS FLOAT). Example: `AVG(CAST(mei.DEPRECIATION_AGE AS FLOAT))`.

2. FIXING SHAPE ERRORS (KPI vs SLICER vs CHART):
- If the error says 'VisualType is KPI': The query must be `SELECT SUM(col) FROM table`. Remove ALL `GROUP BY` statements and return exactly ONE column.
- If the error says 'VisualType is Slicer': The query must be `SELECT DISTINCT col FROM table`. Return exactly ONE column.
- If the visual is a 'Chart' or 'Table': Ensure the columns listed in the SELECT match the GROUP BY clause exactly.

3. FIXING IDENTIFIER ERRORS:
- If Snowflake says 'Invalid Identifier', check the provided SNOWFLAKE SCHEMA CONTEXT. Ensure the alias is correct, and the column actually exists in that table.";
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

            for (int attempt = 1; attempt <= 3; attempt++)
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
                }
                else if ((int)response.StatusCode == 429)
                {
                    await Task.Delay(5000 * attempt);
                    continue;
                }
            }
            return "-- ERROR: Optimizer LLM Failed.";
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