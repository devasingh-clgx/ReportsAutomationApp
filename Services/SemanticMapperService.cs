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
                string definitionPath = Path.Combine(semanticModelPath, "definition");

                if (!Directory.Exists(definitionPath))
                {
                    return new StepResult { IsSuccess = false, Message = $"Semantic Model definition folder not found: {definitionPath}" };
                }

                string contextText = await GenerateModelContextAsync(definitionPath, modelName);

                string exportsFolder = Path.Combine(_env.WebRootPath, "Exports");
                if (!Directory.Exists(exportsFolder)) Directory.CreateDirectory(exportsFolder);

                string fileName = $"SemanticMap_{modelName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
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
        // CORE LOGIC (Integrated from your original code)
        // ==========================================
        private async Task<string> GenerateModelContextAsync(string semanticModelDefinitionPath, string modelName)
        {
            StringBuilder context = new StringBuilder();
            context.AppendLine($"### SEMANTIC MODEL: {modelName}\n");
            context.AppendLine("STRICT PHYSICAL MAPPING (CRITICAL: USE THESE NAMES):");

            var tableMappings = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);
            var relationships = new List<RelationshipInfo>();

            // Traverse all TMDL files in the definition folder (tables, relationships, etc.)
            string[] tmdlFiles = Directory.GetFiles(semanticModelDefinitionPath, "*.tmdl", SearchOption.AllDirectories);

            foreach (string file in tmdlFiles)
            {
                string fileContent = await File.ReadAllTextAsync(file);
                string fileName = Path.GetFileNameWithoutExtension(file);

                // --- EXTRACT TABLE INFO ---
                if (file.Contains(@"\tables\"))
                {
                    TableInfo info = new TableInfo { DaxName = fileName };

                    // Priority 1 & 2: Extract Schema
                    var schemaMatch = Regex.Match(fileContent, @"Schema\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                    if (schemaMatch.Success)
                    {
                        info.PhysicalSchema = schemaMatch.Groups[1].Value;
                    }
                    else if (Regex.IsMatch(fileContent, @"\bCWS\b", RegexOptions.IgnoreCase))
                    {
                        info.PhysicalSchema = "CWS";
                    }
                    else
                    {
                        var prefixMatch = Regex.Match(fileContent, @"(\w+)\.(Item|Entity|Table)", RegexOptions.IgnoreCase);
                        if (prefixMatch.Success) info.PhysicalSchema = prefixMatch.Groups[1].Value;
                    }

                    // Extract Physical Table Name (Item or Entity)
                    var itemMatch = Regex.Match(fileContent, @"Item\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                    var entityMatch = Regex.Match(fileContent, @"Entity\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);

                    if (itemMatch.Success) info.PhysicalTableName = itemMatch.Groups[1].Value;
                    else if (entityMatch.Success) info.PhysicalTableName = entityMatch.Groups[1].Value;

                    // Fallbacks
                    if (string.IsNullOrEmpty(info.PhysicalSchema)) info.PhysicalSchema = "dbo";
                    if (string.IsNullOrEmpty(info.PhysicalTableName)) info.PhysicalTableName = fileName;

                    // Extract Columns mapping (DAX Name -> Source Column)
                    var columnMatches = Regex.Matches(fileContent, @"column\s+'?([^'\r\n]+)'?[\s\S]*?sourceColumn:\s*([^\r\n]+)");
                    foreach (Match col in columnMatches)
                    {
                        info.Columns.Add($"'{col.Groups[1].Value.Trim()}' -> {col.Groups[2].Value.Trim()}");
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
                context.AppendLine($"- DAX Table '{t.DaxName}' -> SQL Table: [{t.PhysicalSchema}].[{t.PhysicalTableName}]");

                if (t.Columns.Count > 0)
                {
                    context.AppendLine($"  Columns (DAX -> SQL): {string.Join(", ", t.Columns)}");
                }
            }

            // Build Context String for Relationships
            context.AppendLine("\nRELATIONSHIPS (JOIN KEYS):");
            foreach (var rel in relationships)
            {
                if (tableMappings.TryGetValue(rel.FromTable, out var fromInfo) && tableMappings.TryGetValue(rel.ToTable, out var toInfo))
                {
                    context.AppendLine($"- Join: [{fromInfo.PhysicalSchema}].[{fromInfo.PhysicalTableName}].[{rel.FromColumn}] = [{toInfo.PhysicalSchema}].[{toInfo.PhysicalTableName}].[{rel.ToColumn}]");
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