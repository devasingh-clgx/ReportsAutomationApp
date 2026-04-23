using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using ReportsAutomationApp.Models;

namespace ReportsAutomationApp.Services
{
    public class SemanticMapperService : IExtractionStep
    {
        private readonly IWebHostEnvironment _env;

        public SemanticMapperService(IWebHostEnvironment env)
        {
            _env = env;
        }

        public int StepId => 2;

        public async Task<StepResult> ExecuteAsync(string reportPath, string semanticModelPath)
        {
            try
            {
                string modelName = new DirectoryInfo(semanticModelPath).Name;
                string safeModelName = ExportFileNameHelper.ToSafeToken(modelName);
                string definitionPath = Path.Combine(semanticModelPath, "definition");

                if (!Directory.Exists(definitionPath))
                {
                    return new StepResult { IsSuccess = false, Message = $"Semantic Model definition folder not found: {definitionPath}" };
                }

                string contextText = await GenerateModelContextAsync(definitionPath, modelName);

                string exportsFolder = Path.Combine(_env.WebRootPath, "Exports");
                if (!Directory.Exists(exportsFolder)) Directory.CreateDirectory(exportsFolder);

                string fileName = $"SemanticMap_{safeModelName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                string filePath = Path.Combine(exportsFolder, fileName);

                await File.WriteAllTextAsync(filePath, contextText);

                return new StepResult
                {
                    IsSuccess = true,
                    Message = $"Successfully mapped Semantic Model: {modelName}",
                    DownloadFilePath = $"/Exports/{fileName}",
                    ExtractedDataPreview = contextText.Substring(0, Math.Min(contextText.Length, 600)) + "\n\n... [Data Truncated for Preview]"
                };
            }
            catch (Exception ex)
            {
                return new StepResult { IsSuccess = false, Message = $"Error mapping Semantic Model: {ex.Message}" };
            }
        }

        // ==========================================
        // SNOWFLAKE OPTIMIZED CORE LOGIC 
        // ==========================================
        private async Task<string> GenerateModelContextAsync(string semanticModelDefinitionPath, string modelName)
        {
            StringBuilder context = new StringBuilder();
            context.AppendLine($"### SEMANTIC MODEL: {modelName}\n");
            context.AppendLine("STRICT PHYSICAL MAPPING (CRITICAL: USE THESE NAMES IN SNOWFLAKE SQL):");

            var tableMappings = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);
            var relationships = new List<RelationshipInfo>();

            string[] tmdlFiles = Directory.GetFiles(semanticModelDefinitionPath, "*.tmdl", SearchOption.AllDirectories);

            foreach (string file in tmdlFiles)
            {
                string fileContent = await File.ReadAllTextAsync(file);
                string fileName = Path.GetFileNameWithoutExtension(file);

                // --- EXTRACT TABLE INFO ---
                if (file.Contains(@"\tables\"))
                {
                    TableInfo info = new TableInfo { DaxName = fileName };

                    // 1. SNOWFLAKE SPECIFIC EXTRACTION (Highest Priority)
                    var sfSchemaMatch = Regex.Match(fileContent, @"\[Name\s*=\s*""([^""]+)""\s*,\s*Kind\s*=\s*""Schema""\]", RegexOptions.IgnoreCase);
                    var sfViewMatch = Regex.Match(fileContent, @"\[Name\s*=\s*""([^""]+)""\s*,\s*Kind\s*=\s*""(?:View|Table)""\]", RegexOptions.IgnoreCase);

                    if (sfSchemaMatch.Success) info.PhysicalSchema = sfSchemaMatch.Groups[1].Value;
                    if (sfViewMatch.Success) info.PhysicalTableName = sfViewMatch.Groups[1].Value;

                    // 2. STANDARD SQL/ODATA FALLBACKS (If not Snowflake)
                    if (string.IsNullOrEmpty(info.PhysicalSchema))
                    {
                        var schemaMatch = Regex.Match(fileContent, @"Schema\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                        if (schemaMatch.Success) info.PhysicalSchema = schemaMatch.Groups[1].Value;
                        else if (Regex.IsMatch(fileContent, @"\bCWS\b", RegexOptions.IgnoreCase)) info.PhysicalSchema = "CWS";
                    }

                    if (string.IsNullOrEmpty(info.PhysicalTableName))
                    {
                        var itemMatch = Regex.Match(fileContent, @"Item\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                        var entityMatch = Regex.Match(fileContent, @"Entity\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);

                        if (itemMatch.Success) info.PhysicalTableName = itemMatch.Groups[1].Value;
                        else if (entityMatch.Success) info.PhysicalTableName = entityMatch.Groups[1].Value;
                    }

                    // 3. ABSOLUTE FALLBACK (Snowflake Default)
                    if (string.IsNullOrEmpty(info.PhysicalSchema)) info.PhysicalSchema = "VIEWS"; // Fixed from "dbo"
                    if (string.IsNullOrEmpty(info.PhysicalTableName)) info.PhysicalTableName = fileName;

                    // 4. EXTRACT COLUMNS (DAX -> SQL)
                    var columnMatches = Regex.Matches(fileContent, @"column\s+'?([^'\r\n]+)'?[\s\S]*?sourceColumn:\s*([^\r\n]+)");
                    foreach (Match col in columnMatches)
                    {
                        info.Columns.Add($"'{col.Groups[1].Value.Trim()}' -> {col.Groups[2].Value.Trim()}");
                    }

                    // 5. EXTRACT MEASURES (Tells the AI which table a measure belongs to)
                    var measureMatches = Regex.Matches(fileContent, @"^\s*measure\s+'?([^'=]+)'?\s*=", RegexOptions.Multiline);
                    foreach (Match m in measureMatches)
                    {
                        info.Measures.Add(m.Groups[1].Value.Trim());
                    }

                    tableMappings[fileName] = info;
                }

                // --- EXTRACT RELATIONSHIPS ---
                var relMatches = Regex.Matches(fileContent, @"relationship\s+[^\r\n]+[\s\S]*?fromColumn:\s*'([^']+)'\.\[?([^\]\r\n]+)\]?[\s\S]*?toColumn:\s*'([^']+)'\.\[?([^\]\r\n]+)\]?");
                foreach (Match rel in relMatches)
                {
                    relationships.Add(new RelationshipInfo
                    {
                        FromTable = rel.Groups[1].Value.Trim(),
                        FromColumn = rel.Groups[2].Value.Trim(),
                        ToTable = rel.Groups[3].Value.Trim(),
                        ToColumn = rel.Groups[4].Value.Trim()
                    });
                }
            }

            // Build Context String for Tables
            foreach (var kvp in tableMappings)
            {
                var t = kvp.Value;

                // Formats Snowflake physical path properly (e.g. QA_EDW.VIEWS.VW_MERGED_...)
                string physicalPath = t.PhysicalSchema.Equals("VIEWS", StringComparison.OrdinalIgnoreCase)
                    ? $"QA_EDW.VIEWS.{t.PhysicalTableName}"
                    : $"[{t.PhysicalSchema}].[{t.PhysicalTableName}]";

                context.AppendLine($"- DAX Table '{t.DaxName}' -> SQL Table: {physicalPath}");

                if (t.Columns.Count > 0)
                {
                    context.AppendLine($"  Columns (DAX -> SQL): {string.Join(", ", t.Columns)}");
                }

                if (t.Measures.Count > 0)
                {
                    context.AppendLine($"  Homed Measures: [{string.Join("], [", t.Measures)}]");
                }
                context.AppendLine();
            }

            // Build Context String for Relationships
            context.AppendLine("RELATIONSHIPS (JOIN KEYS):");
            foreach (var rel in relationships)
            {
                if (tableMappings.TryGetValue(rel.FromTable, out var fromInfo) && tableMappings.TryGetValue(rel.ToTable, out var toInfo))
                {
                    string fromPath = fromInfo.PhysicalSchema.Equals("VIEWS", StringComparison.OrdinalIgnoreCase) ? $"QA_EDW.VIEWS.{fromInfo.PhysicalTableName}" : $"[{fromInfo.PhysicalSchema}].[{fromInfo.PhysicalTableName}]";
                    string toPath = toInfo.PhysicalSchema.Equals("VIEWS", StringComparison.OrdinalIgnoreCase) ? $"QA_EDW.VIEWS.{toInfo.PhysicalTableName}" : $"[{toInfo.PhysicalSchema}].[{toInfo.PhysicalTableName}]";

                    context.AppendLine($"- Join: {fromPath}.[{rel.FromColumn}] = {toPath}.[{rel.ToColumn}]");
                }
            }

            return context.ToString();
        }

        // --- Helper Classes ---
        private class TableInfo
        {
            public string DaxName { get; set; } = "";
            public string PhysicalTableName { get; set; } = "";
            public string PhysicalSchema { get; set; } = "";
            public List<string> Columns { get; set; } = new List<string>();
            public List<string> Measures { get; set; } = new List<string>(); // Added Measures List
        }

        private class RelationshipInfo
        {
            public string FromTable { get; set; } = "";
            public string FromColumn { get; set; } = "";
            public string ToTable { get; set; } = "";
            public string ToColumn { get; set; } = "";
        }
    }
}