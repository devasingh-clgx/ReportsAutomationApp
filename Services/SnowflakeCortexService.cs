using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using ReportsAutomationApp.Models;
using Snowflake.Data.Client;

namespace ReportsAutomationApp.Services
{
    public class SnowflakeCortexService : IExtractionStep
    {
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private static readonly SemaphoreSlim _authLock = new SemaphoreSlim(1, 1);
        public SnowflakeCortexService(IWebHostEnvironment env, IConfiguration config)
        {
            _env = env;
            _config = config;
        }

        public int StepId => 4;

        public async Task<StepResult> ExecuteAsync(string reportPath, string semanticModelPath)
        {
            try
            {
                string reportName = new DirectoryInfo(reportPath).Name;
                string exportsFolder = Path.Combine(_env.WebRootPath, "Exports");

                // 1. Find the generated Lakehouse SQL CSV from Step 3
                var step3File = new DirectoryInfo(exportsFolder)
                    .GetFiles($"GeneratedSQL_{reportName}_*.csv")
                    .OrderByDescending(f => f.CreationTime)
                    .FirstOrDefault();

                if (step3File == null)
                    return new StepResult { IsSuccess = false, Message = "Missing Step 3 output. Run Step 3 first." };

                var csvRows = ParseCsv(step3File.FullName);
                if (csvRows.Count <= 1)
                    return new StepResult { IsSuccess = false, Message = "Step 3 CSV is empty." };

                // 2. Setup Output CSV
                StringBuilder outputCsv = new StringBuilder();
                // Add the new final column header
                outputCsv.AppendLine(string.Join(",", csvRows[0].Select(h => $"\"{h.Replace("\"", "\"\"")}\"")) + ",\"Final_Snowflake_SQL\"");

                string connString = ResolveSnowflakeConnectionString();
                if (string.IsNullOrEmpty(connString))
                    return new StepResult { IsSuccess = false, Message = "Snowflake Connection String missing in appsettings.json." };

                int processedCount = 0;

                // 3. Connect to Snowflake and process row-by-row
                using (var conn = new SnowflakeDbConnection())
                {
                    conn.ConnectionString = connString;
                    await OpenSnowflakeConnectionAsync(conn, connString);

                    for (int i = 1; i < csvRows.Count; i++)
                    {
                        var row = csvRows[i];
                        if (row.Count < 11) continue;

                        // The Lakehouse SQL is the last column from Step 3
                        string lakehouseSql = row[10];

                        // Skip if no SQL was generated
                        if (string.IsNullOrWhiteSpace(lakehouseSql) || lakehouseSql.Contains("No DAX Formulas"))
                        {
                            var skippedRow = new List<string>(row) { "No SQL Needed" };
                            outputCsv.AppendLine(string.Join(",", skippedRow.Select(c => $"\"{c?.Replace("\"", "\"\"") ?? ""}\"")));
                            continue;
                        }

                        // Create the Cortex Prompt exactly as you designed
                        string prompt = $@"
You are an expert Snowflake Data Engineer.
Your task is to take the provided generic Lakehouse SQL and convert it into highly optimized Snowflake SQL.

RULES:
1. Replace all generic schemas (like [CWS] or [dbo]) with the target schema: QA_EDW.VIEWS.
2. Ensure table and column names do not use SQL Server brackets [ ]. Use Snowflake double quotes """" ONLY if the name contains spaces or special characters, otherwise leave them unquoted.
3. Ensure all functions (like ISNULL, COALESCE, DATE math) use native Snowflake syntax.
4. Output ONLY the raw Snowflake SQL query. Do not include markdown formatting or explanations.

### LAKEHOUSE SQL TO CONVERT:
{lakehouseSql}
";
                        // Escape single quotes to prevent SQL injection in the Cortex command
                        string safePrompt = prompt.Replace("'", "''");

                        // We use mistral-large2 (or your preferred model) via Cortex
                        string cortexQuery = $"SELECT SNOWFLAKE.CORTEX.COMPLETE('mistral-large2', '{safePrompt}') AS LLM_RESPONSE";

                        string finalSnowflakeSql = "";

                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = cortexQuery;
                            using (var reader = await cmd.ExecuteReaderAsync())
                            {
                                if (await reader.ReadAsync())
                                {
                                    finalSnowflakeSql = reader.GetString(0);
                                    // Clean up markdown just in case
                                    finalSnowflakeSql = finalSnowflakeSql.Replace("```sql", "").Replace("```", "").Trim();
                                }
                            }
                        }

                        var newRow = new List<string>(row) { finalSnowflakeSql };
                        outputCsv.AppendLine(string.Join(",", newRow.Select(c => $"\"{c?.Replace("\"", "\"\"") ?? ""}\"")));
                        processedCount++;
                    }
                }

                // 4. Save the Final Step 4 Output
                string outFileName = $"Final_SnowflakeSQL_{reportName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                string outFilePath = Path.Combine(exportsFolder, outFileName);
                await File.WriteAllTextAsync(outFilePath, outputCsv.ToString());

                return new StepResult
                {
                    IsSuccess = true,
                    Message = $"Successfully optimized {processedCount} queries using Snowflake Cortex.",
                    DownloadFilePath = $"/Exports/{outFileName}",
                    ExtractedDataPreview = outputCsv.ToString().Substring(0, Math.Min(outputCsv.Length, 1000)) + "...\n[Data Truncated]"
                };
            }
            catch (Exception ex)
            {
                return new StepResult { IsSuccess = false, Message = $"Error in Cortex Optimization (Step 4): {ex.Message}" };
            }
        }

        private async Task OpenSnowflakeConnectionAsync(SnowflakeDbConnection conn, string connectionString)
        {
            // External browser auth is interactive and can break when many runs happen at once.
            // Serialize only that mode; keep password/key/OAuth modes fully concurrent.
            if (UsesInteractiveAuthenticator(connectionString))
            {
                await _authLock.WaitAsync();
                try
                {
                    await conn.OpenAsync();
                }
                finally
                {
                    _authLock.Release();
                }

                return;
            }

            await conn.OpenAsync();
        }

        private static bool UsesInteractiveAuthenticator(string connectionString)
        {
            return connectionString.Contains("authenticator=externalbrowser", StringComparison.OrdinalIgnoreCase);
        }

        private string ResolveSnowflakeConnectionString()
        {
            string explicitConnectionString = _config["Snowflake:ConnectionString"];
            if (!string.IsNullOrWhiteSpace(explicitConnectionString))
            {
                return explicitConnectionString;
            }

            // Fallback: build from individual keys when ConnectionString is not provided.
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

        private static string BuildPair(string key, string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : $"{key}={value}";
        }

        // Re-use the exact same robust CSV parser from Step 3
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
                    if (inQuotes && i + 1 < fileContent.Length && fileContent[i + 1] == '\"')
                    {
                        currentCell.Append('\"'); i++;
                    }
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