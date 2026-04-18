using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
        private const int LakehouseSqlColumnIndex = 10;
        private const int MinimumColumnCount = 11;
        private const int DefaultCortexBatchSize = 5;
        private const int MaxCortexBatchSize = 20;
        private static readonly IReadOnlyDictionary<string, string> DefaultModelAliases =
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Claude Opus 4.6"] = "claude-4-opus",
                ["Claude Sonnet 4.6"] = "claude-4-sonnet",
                ["OpenAI GPT 5.4"] = "openai-gpt-4.1"
            });

        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
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
                string cortexModel = ResolveCortexModel();
                int batchSize = GetCortexBatchSize();
                var finalSqlByRowIndex = new Dictionary<int, string>();
                var requestsBySql = new Dictionary<string, CortexRequest>(StringComparer.Ordinal);

                for (int i = 1; i < csvRows.Count; i++)
                {
                    var row = csvRows[i];
                    if (row.Count < MinimumColumnCount) continue;

                    string lakehouseSql = row[LakehouseSqlColumnIndex];

                    if (RequiresNoSnowflakeSql(lakehouseSql))
                    {
                        finalSqlByRowIndex[i] = "No SQL Needed";
                        continue;
                    }

                    if (requestsBySql.TryGetValue(lakehouseSql, out var existingRequest))
                    {
                        existingRequest.RowIndexes.Add(i);
                        continue;
                    }

                    requestsBySql.Add(lakehouseSql, new CortexRequest(lakehouseSql, BuildPrompt(lakehouseSql), i));
                }

                if (requestsBySql.Count > 0)
                {
                    using var conn = new SnowflakeDbConnection();
                    conn.ConnectionString = connString;
                    await conn.OpenAsync();

                    foreach (var batch in requestsBySql.Values.Chunk(batchSize))
                    {
                        var batchList = batch.ToList();
                        var batchResults = await ExecuteCortexBatchAsync(conn, batchList, cortexModel);

                        for (int index = 0; index < batchList.Count; index++)
                        {
                            var request = batchList[index];
                            string finalSnowflakeSql = batchResults[index];

                            foreach (int rowIndex in request.RowIndexes)
                            {
                                finalSqlByRowIndex[rowIndex] = finalSnowflakeSql;
                                processedCount++;
                            }
                        }
                    }
                }

                for (int i = 1; i < csvRows.Count; i++)
                {
                    var row = csvRows[i];
                    if (row.Count < MinimumColumnCount) continue;

                    var newRow = new List<string>(row)
                    {
                        finalSqlByRowIndex.TryGetValue(i, out var finalSnowflakeSql) ? finalSnowflakeSql : string.Empty
                    };

                    outputCsv.AppendLine(string.Join(",", newRow.Select(c => $"\"{c?.Replace("\"", "\"\"") ?? ""}\"")));
                }

                // 4. Save the Final Step 4 Output
                string outFileName = $"Final_SnowflakeSQL_{reportName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                string outFilePath = Path.Combine(exportsFolder, outFileName);
                await File.WriteAllTextAsync(outFilePath, outputCsv.ToString());

                return new StepResult
                {
                    IsSuccess = true,
                    Message = $"Successfully optimized {processedCount} queries using Snowflake Cortex with {requestsBySql.Count} unique prompt(s).",
                    DownloadFilePath = $"/Exports/{outFileName}",
                    ExtractedDataPreview = outputCsv.ToString().Substring(0, Math.Min(outputCsv.Length, 1000)) + "...\n[Data Truncated]"
                };
            }
            catch (Exception ex)
            {
                return new StepResult { IsSuccess = false, Message = $"Error in Cortex Optimization (Step 4): {ex.Message}" };
            }
        }

        private async Task<List<string>> ExecuteCortexBatchAsync(SnowflakeDbConnection conn, IReadOnlyList<CortexRequest> batch, string modelName)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = BuildCortexBatchQuery(batch, modelName);

            var results = Enumerable.Repeat(string.Empty, batch.Count).ToArray();

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                int sequence = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                if (sequence < 0 || sequence >= results.Length)
                {
                    continue;
                }

                results[sequence] = reader.IsDBNull(1)
                    ? string.Empty
                    : CleanCortexResponse(reader.GetString(1));
            }

            return results.ToList();
        }

        private static string BuildCortexBatchQuery(IReadOnlyList<CortexRequest> batch, string modelName)
        {
            var values = string.Join(",", batch.Select((request, index) =>
                $"({index}, '{EscapeSqlLiteral(request.Prompt)}')"));

            return $@"
SELECT batch.seq,
       SNOWFLAKE.CORTEX.COMPLETE('{EscapeSqlLiteral(modelName)}', batch.prompt) AS LLM_RESPONSE
FROM (
    SELECT COLUMN1 AS seq, COLUMN2 AS prompt
    FROM VALUES {values}
) AS batch
ORDER BY batch.seq";
        }

        private int GetCortexBatchSize()
        {
            int configuredBatchSize = _config.GetValue<int?>("Snowflake:CortexBatchSize") ?? DefaultCortexBatchSize;
            return Math.Clamp(configuredBatchSize, 1, MaxCortexBatchSize);
        }

        private string ResolveCortexModel()
        {
            string? configuredModel = _config["Snowflake:CortexModel"];
            if (string.IsNullOrWhiteSpace(configuredModel))
            {
                throw new InvalidOperationException("Snowflake:CortexModel is missing in appsettings.json.");
            }

            string selectedModel = configuredModel.Trim();

            string? configuredAliasModel = _config[$"Snowflake:CortexModels:{selectedModel}"];
            if (!string.IsNullOrWhiteSpace(configuredAliasModel))
            {
                return configuredAliasModel.Trim();
            }

            if (DefaultModelAliases.TryGetValue(selectedModel, out string? mappedModel))
            {
                return mappedModel;
            }

            if (selectedModel.Contains('-', StringComparison.Ordinal))
            {
                return selectedModel;
            }

            throw new InvalidOperationException($"Snowflake:CortexModel '{selectedModel}' is not mapped to a valid Snowflake Cortex model. Configure Snowflake:CortexModels:{selectedModel} in appsettings.json.");
        }

        private static bool RequiresNoSnowflakeSql(string lakehouseSql)
        {
            return string.IsNullOrWhiteSpace(lakehouseSql)
                || lakehouseSql.Contains("No DAX Formulas", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildPrompt(string lakehouseSql, string schemaMetadata = "")
        {
            return $@"You are an Expert Snowflake Database Administrator and SQL Optimizer.
Your task is to translate the provided generic/Lakehouse SQL into perfectly formatted, production-ready Snowflake SQL.

### STRICT COMPILER RULES:
1. TARGET SCHEMA: You MUST map all tables to the ""QA_EDW"".""VIEWS"" schema.
2. IDENTIFIERS: You MUST enclose all database, schema, table, and column names in double quotes ("""") if they contain spaces, special characters, or are case-sensitive. NEVER use SQL Server brackets [ ].
   - Example Bad: [QA_EDW].[VIEWS].[My Table]
   - Example Good: ""QA_EDW"".""VIEWS"".""My Table""
3. DIALECT PERFECTION: Ensure all functions are native to Snowflake. 
   - Replace ISNULL with NVL or COALESCE.
   - Replace standard string concatenations with CONCAT() or ||.
   - Ensure explicit CAST() or :: operations where data types might clash.
4. TABLE/COLUMN ALIGNMENT: If the Lakehouse SQL references a column or table that looks slightly off, use your best judgment to align it to standard dimensional modeling naming conventions.
5. NO CHATTER: Output ONLY the raw SQL query. Do not include markdown (```sql), explanations, or trailing comments.

### LAKEHOUSE SQL TO CONVERT:
{lakehouseSql}
";
        }

        private static string CleanCortexResponse(string response)
        {
            return response.Replace("```sql", "", StringComparison.OrdinalIgnoreCase)
                .Replace("```", string.Empty, StringComparison.Ordinal)
                .Trim();
        }

        private static string EscapeSqlLiteral(string value)
        {
            return value.Replace("'", "''");
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

        private static string BuildPair(string key, string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : $"{key}={value}";
        }

        private sealed class CortexRequest
        {
            public CortexRequest(string lakehouseSql, string prompt, int rowIndex)
            {
                LakehouseSql = lakehouseSql;
                Prompt = prompt;
                RowIndexes = new List<int> { rowIndex };
            }

            public string LakehouseSql { get; }
            public string Prompt { get; }
            public List<int> RowIndexes { get; }
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