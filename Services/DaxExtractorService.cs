using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using ReportsAutomationApp.Models;

namespace ReportsAutomationApp.Services
{
    public class DaxExtractorService : IExtractionStep
    {
        private readonly IWebHostEnvironment _env;

        public DaxExtractorService(IWebHostEnvironment env)
        {
            _env = env;
        }

        public int StepId => 1;

        public async Task<StepResult> ExecuteAsync(string reportPath, string semanticModelPath)
        {
            try
            {
                string tablesFolderPath = Path.Combine(semanticModelPath, "definition", "tables");
                string pagesPath = Path.Combine(reportPath, "definition", "pages");
                string reportName = new DirectoryInfo(reportPath).Name;
                string modelName = new DirectoryInfo(semanticModelPath).Name;

                if (!Directory.Exists(tablesFolderPath))
                    return new StepResult { IsSuccess = false, Message = $"Semantic Model tables folder not found: {tablesFolderPath}" };

                if (!Directory.Exists(pagesPath))
                    return new StepResult { IsSuccess = false, Message = $"Report pages folder not found: {pagesPath}" };

                var daxDictionary = ExtractAllDax(tablesFolderPath);

                StringBuilder csvContent = new StringBuilder();
                // UPDATE 1: Split ItemsUsed into ColumnsUsed and MeasuresUsed
                csvContent.AppendLine("ScenarioId,ModelName,PageName,VisualName,VisualType,VisualFilters,PageFilters,ColumnsUsed,MeasuresUsed,Dax");

                int visualsProcessed = 0;
                int scenarioIdCounter = 1;

                foreach (var pageFolder in Directory.GetDirectories(pagesPath))
                {
                    // ... (keep the pageName and pageJson parsing exactly the same) ...

                    // (Assuming you kept the page logic here...)
                    string pageName = new DirectoryInfo(pageFolder).Name;
                    string pageJsonPath = Path.Combine(pageFolder, "page.json");
                    string pageFiltersFormatted = "None";

                    if (File.Exists(pageJsonPath))
                    {
                        try
                        {
                            string pageJsonContent = await File.ReadAllTextAsync(pageJsonPath);
                            using (JsonDocument pageDoc = JsonDocument.Parse(pageJsonContent))
                            {
                                if (pageDoc.RootElement.TryGetProperty("displayName", out JsonElement displayNameElement))
                                    pageName = displayNameElement.GetString() ?? pageName;

                                List<string> pageFilters = new List<string>();
                                FindFilters(pageDoc.RootElement, pageFilters);
                                if (pageFilters.Count > 0) pageFiltersFormatted = string.Join(" | ", pageFilters);
                            }
                        }
                        catch { }
                    }

                    string visualsPath = Path.Combine(pageFolder, "visuals");
                    if (!Directory.Exists(visualsPath)) continue;

                    foreach (var visualFolder in Directory.GetDirectories(visualsPath))
                    {
                        string visualId = new DirectoryInfo(visualFolder).Name;
                        string visualJsonPath = Path.Combine(visualFolder, "visual.json");

                        if (File.Exists(visualJsonPath))
                        {
                            string jsonContent = await File.ReadAllTextAsync(visualJsonPath);
                            using (JsonDocument doc = JsonDocument.Parse(jsonContent))
                            {
                                string visualCategory = ExtractVisualCategory(doc);
                                string visualName = ExtractVisualTitle(doc, visualId, visualCategory);

                                List<string> visualFilters = new List<string>();
                                FindFilters(doc.RootElement, visualFilters);
                                string visualFiltersFormatted = visualFilters.Count > 0 ? string.Join(" | ", visualFilters) : "None";

                                List<string> rawQueryRefs = new List<string>();
                                FindQueryRefs(doc.RootElement, rawQueryRefs);

                                if (rawQueryRefs.Count > 0)
                                {
                                    List<string> columnsUsed = new List<string>();
                                    List<string> measuresUsed = new List<string>();
                                    StringBuilder combinedDax = new StringBuilder();

                                    // NEW: Setup the recursive tracker queues
                                    HashSet<string> resolvedMeasures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    Queue<string> measureQueue = new Queue<string>();

                                    // 1. Initial pass from visual elements
                                    foreach (var qRef in rawQueryRefs)
                                    {
                                        string cleanName = qRef;
                                        int dotIndex = qRef.IndexOf('.');
                                        if (dotIndex >= 0) cleanName = qRef.Substring(dotIndex + 1);

                                        if (daxDictionary.ContainsKey(cleanName))
                                        {
                                            if (!resolvedMeasures.Contains(cleanName))
                                            {
                                                resolvedMeasures.Add(cleanName);
                                                measureQueue.Enqueue(cleanName);
                                                measuresUsed.Add(cleanName);
                                            }
                                        }
                                        else
                                        {
                                            // If it's not a DAX measure, it must be a physical column!
                                            if (!columnsUsed.Contains(qRef)) columnsUsed.Add(qRef);
                                        }
                                    }

                                    // 2. GAP 1 FIX: Recursive DAX Lineage Tracer!
                                    while (measureQueue.Count > 0)
                                    {
                                        string currentMeasure = measureQueue.Dequeue();
                                        string currentDax = daxDictionary[currentMeasure];

                                        // Append the current measure to our final output
                                        combinedDax.AppendLine($"--- {currentMeasure} ---");
                                        combinedDax.AppendLine(currentDax);
                                        combinedDax.AppendLine();

                                        // Scan the DAX math for nested references like: [Estimate Item Key Count]
                                        var matches = Regex.Matches(currentDax, @"\[([^\]]+)\]");
                                        foreach (Match match in matches)
                                        {
                                            string nestedItem = match.Groups[1].Value.Trim();

                                            // If this nested item is a known measure we haven't processed yet...
                                            if (daxDictionary.ContainsKey(nestedItem) && !resolvedMeasures.Contains(nestedItem))
                                            {
                                                resolvedMeasures.Add(nestedItem);
                                                measureQueue.Enqueue(nestedItem);
                                                measuresUsed.Add(nestedItem); // Add to the CSV column list so the LLM knows it's there
                                            }
                                        }
                                    }

                                    string columnsStr = columnsUsed.Count > 0 ? string.Join("\n", columnsUsed) : "None";
                                    string measuresStr = measuresUsed.Count > 0 ? string.Join("\n", measuresUsed) : "None";
                                    string finalDax = combinedDax.ToString().Trim();

                                    if (string.IsNullOrEmpty(finalDax))
                                        finalDax = "No DAX Formulas";

                                    csvContent.AppendLine(FormatCsvRow(
                                        scenarioIdCounter.ToString(),
                                        modelName,
                                        pageName,
                                        visualName,
                                        visualCategory,
                                        visualFiltersFormatted,
                                        pageFiltersFormatted,
                                        columnsStr,
                                        measuresStr,
                                        finalDax
                                    ));

                                    visualsProcessed++;
                                    scenarioIdCounter++;
                                }
                            }
                        }
                    }
                }

                string exportsFolder = Path.Combine(_env.WebRootPath, "Exports");
                if (!Directory.Exists(exportsFolder)) Directory.CreateDirectory(exportsFolder);

                string fileName = $"DAX_{reportName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                string filePath = Path.Combine(exportsFolder, fileName);

                await File.WriteAllTextAsync(filePath, csvContent.ToString());

                return new StepResult
                {
                    IsSuccess = true,
                    Message = $"Successfully extracted {visualsProcessed} visuals with filter data.",
                    DownloadFilePath = $"/Exports/{fileName}",
                    ExtractedDataPreview = csvContent.ToString().Substring(0, Math.Min(csvContent.Length, 500)) + "...\n[Data Truncated for Preview]"
                };
            }
            catch (Exception ex)
            {
                return new StepResult { IsSuccess = false, Message = $"Error extracting DAX: {ex.Message}" };
            }
        }

        // =========================================================
        // POWER BI AST FILTER PARSER (The Magic Sauce)
        // =========================================================

        private void FindFilters(JsonElement element, List<string> filterExpressions)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    if (prop.Name == "filters" && prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement filterItem in prop.Value.EnumerateArray())
                        {
                            if (filterItem.ValueKind == JsonValueKind.Object && filterItem.TryGetProperty("filter", out JsonElement filterDef))
                            {
                                ExtractFilterExpressions(filterDef, filterExpressions);
                            }
                            else
                            {
                                ExtractFilterExpressions(filterItem, filterExpressions);
                            }
                        }
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        string strVal = prop.Value.GetString();
                        if (strVal != null && strVal.Contains("\"filters\""))
                        {
                            try
                            {
                                using (var innerDoc = JsonDocument.Parse(strVal))
                                {
                                    FindFilters(innerDoc.RootElement, filterExpressions);
                                }
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        FindFilters(prop.Value, filterExpressions);
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    FindFilters(item, filterExpressions);
                }
            }
        }

        private void ExtractFilterExpressions(JsonElement filterDef, List<string> filterExpressions)
        {
            if (filterDef.ValueKind == JsonValueKind.Object)
            {
                if (filterDef.TryGetProperty("Where", out JsonElement whereArray) && whereArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var conditionItem in whereArray.EnumerateArray())
                    {
                        if (conditionItem.TryGetProperty("Condition", out JsonElement condElement))
                        {
                            string expr = ParseConditionAst(condElement);
                            if (!string.IsNullOrEmpty(expr) && !filterExpressions.Contains(expr))
                                filterExpressions.Add(expr);
                        }
                    }
                }
                else
                {
                    string expr = ParseConditionAst(filterDef);
                    if (!string.IsNullOrEmpty(expr) && !filterExpressions.Contains(expr))
                        filterExpressions.Add(expr);
                }
            }
        }

        // Translates Power BI's AST into SQL-like readable strings
        private string ParseConditionAst(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return "";
            List<string> parts = new List<string>();

            if (node.TryGetProperty("Not", out JsonElement notNode))
            {
                string inner = ParseConditionAst(notNode);
                if (!string.IsNullOrEmpty(inner)) return $"NOT ({inner})";
            }
            else if (node.TryGetProperty("In", out JsonElement inNode))
            {
                string prop = GetPropertyPath(inNode);
                string vals = GetLiteralValues(inNode);
                return $"{prop} IN [{vals}]";
            }
            else if (node.TryGetProperty("Comparison", out JsonElement compNode))
            {
                string prop = GetPropertyPath(compNode);
                string vals = GetLiteralValues(compNode);
                int kind = 0;

                if (compNode.TryGetProperty("ComparisonKind", out JsonElement kindNode) && kindNode.ValueKind == JsonValueKind.Number)
                    kind = kindNode.GetInt32();

                string op = "=";
                if (kind == 1) op = ">";
                else if (kind == 2) op = ">=";
                else if (kind == 3) op = "<";
                else if (kind == 4) op = "<=";
                else if (kind == 5) op = "!=";

                return $"{prop} {op} {vals}";
            }
            else if (node.TryGetProperty("Contains", out JsonElement contNode))
            {
                string prop = GetPropertyPath(contNode);
                string vals = GetLiteralValues(contNode);
                return $"{prop} CONTAINS {vals}";
            }
            else if (node.TryGetProperty("IsNotNull", out JsonElement isNotNullNode))
            {
                string prop = GetPropertyPath(isNotNullNode);
                return $"{prop} IS NOT NULL";
            }
            else if (node.TryGetProperty("IsNull", out JsonElement isNullNode))
            {
                string prop = GetPropertyPath(isNullNode);
                return $"{prop} IS NULL";
            }
            else
            {
                // Traverse down for wrapped nodes (like 'Expression')
                foreach (JsonProperty prop in node.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        string res = ParseConditionAst(prop.Value);
                        if (!string.IsNullOrEmpty(res)) parts.Add(res);
                    }
                }
            }

            return parts.Count > 0 ? string.Join(" AND ", parts) : "";
        }

        private string GetPropertyPath(JsonElement node)
        {
            List<string> props = new List<string>();
            SearchForProperty(node, "Property", props);
            return string.Join(", ", props);
        }

        private string GetLiteralValues(JsonElement node)
        {
            List<string> vals = new List<string>();
            SearchForValues(node, vals);
            return string.Join(", ", vals);
        }

        private void SearchForProperty(JsonElement node, string key, List<string> results)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty(key, out JsonElement valNode) && valNode.ValueKind == JsonValueKind.String)
                {
                    string propName = valNode.GetString();
                    string tableName = "";

                    // Attempt to extract the Table Name (Entity) from the AST
                    if (node.TryGetProperty("Expression", out JsonElement exprNode) &&
                        exprNode.ValueKind == JsonValueKind.Object &&
                        exprNode.TryGetProperty("SourceRef", out JsonElement sourceNode) &&
                        sourceNode.ValueKind == JsonValueKind.Object &&
                        sourceNode.TryGetProperty("Entity", out JsonElement entityNode))
                    {
                        tableName = $"[{entityNode.GetString()}].";
                    }

                    string fullName = $"{tableName}[{propName}]";
                    if (!results.Contains(fullName)) results.Add(fullName);
                }
                foreach (JsonProperty prop in node.EnumerateObject())
                    SearchForProperty(prop.Value, key, results);
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in node.EnumerateArray())
                    SearchForProperty(item, key, results);
            }
        }

        private void SearchForValues(JsonElement node, List<string> results)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("Literal", out JsonElement literalNode) &&
                    literalNode.ValueKind == JsonValueKind.Object &&
                    literalNode.TryGetProperty("Value", out JsonElement valNode))
                {
                    string val = valNode.GetString() ?? "null";
                    if (!results.Contains(val)) results.Add(val);
                }
                else
                {
                    foreach (JsonProperty prop in node.EnumerateObject())
                        SearchForValues(prop.Value, results);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in node.EnumerateArray())
                    SearchForValues(item, results);
            }
        }

        // =========================================================
        // EXISTING HELPERS
        // =========================================================

        private string ExtractVisualTitle(JsonDocument doc, string visualId, string visualCategory)
        {
            try
            {
                var root = doc.RootElement;

                // 1. Check NEW PBIR format: visualContainerObjects -> title (Overall Title)
                if (root.TryGetProperty("visualContainerObjects", out JsonElement vcObj) &&
                    vcObj.TryGetProperty("title", out JsonElement titleArr) &&
                    titleArr.ValueKind == JsonValueKind.Array &&
                    titleArr.GetArrayLength() > 0)
                {
                    if (titleArr[0].TryGetProperty("properties", out JsonElement props) &&
                        props.TryGetProperty("text", out JsonElement textObj) &&
                        textObj.TryGetProperty("expr", out JsonElement expr) &&
                        expr.TryGetProperty("Literal", out JsonElement literal) &&
                        literal.TryGetProperty("Value", out JsonElement val))
                    {
                        string rawTitle = val.GetString();
                        if (!string.IsNullOrEmpty(rawTitle)) return rawTitle.Trim('\'', ' ', '\"');
                    }
                }

                if (root.TryGetProperty("visual", out JsonElement visualElement))
                {
                    // 2. Check NEW PBIR format: visual.objects -> header (Slicer Header)
                    if (visualElement.TryGetProperty("objects", out JsonElement objectsElement) &&
                        objectsElement.TryGetProperty("header", out JsonElement headerArr) &&
                        headerArr.ValueKind == JsonValueKind.Array &&
                        headerArr.GetArrayLength() > 0)
                    {
                        if (headerArr[0].TryGetProperty("properties", out JsonElement props) &&
                            props.TryGetProperty("text", out JsonElement textObj) &&
                            textObj.TryGetProperty("expr", out JsonElement expr) &&
                            expr.TryGetProperty("Literal", out JsonElement literal) &&
                            literal.TryGetProperty("Value", out JsonElement val))
                        {
                            string rawHeader = val.GetString();
                            if (!string.IsNullOrEmpty(rawHeader)) return rawHeader.Trim('\'', ' ', '\"');
                        }
                    }

                    // 3. Try finding the "displayName" in the query projections (Field Names)
                    if (visualElement.TryGetProperty("query", out JsonElement queryElement) &&
                        queryElement.TryGetProperty("queryState", out JsonElement queryStateElement))
                    {
                        foreach (JsonProperty stateProp in queryStateElement.EnumerateObject())
                        {
                            if (stateProp.Value.ValueKind == JsonValueKind.Object &&
                                stateProp.Value.TryGetProperty("projections", out JsonElement projectionsElement) &&
                                projectionsElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (JsonElement proj in projectionsElement.EnumerateArray())
                                {
                                    if (proj.TryGetProperty("displayName", out JsonElement dispNameElement))
                                    {
                                        string displayName = dispNameElement.GetString();
                                        if (!string.IsNullOrEmpty(displayName)) return displayName;
                                    }
                                }
                            }
                        }
                    }

                    // 4. OLD PBIX FORMAT (stringified config fallback)
                    if (visualElement.TryGetProperty("config", out JsonElement configElement))
                    {
                        string configStr = configElement.GetString();
                        if (!string.IsNullOrEmpty(configStr))
                        {
                            var match = Regex.Match(configStr, @"\""title\""\s*:\s*\[.*?\""Value\""\s*:\s*\""([^\""]+)\""", RegexOptions.IgnoreCase);
                            if (match.Success)
                            {
                                string rawTitle = match.Groups[1].Value;
                                return rawTitle.Trim('\'', ' ', '\"');
                            }
                        }
                    }
                }
            }
            catch { /* Ignore errors and just use the fallback below */ }

            // Fallback: If absolutely nothing exists, return VisualType + ID
            return $"{visualCategory} ({visualId.Substring(0, Math.Min(visualId.Length, 8))})";
        }
        private Dictionary<string, string> ExtractAllDax(string tablesFolderPath)
        {
            var daxDictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] tmdlFiles = Directory.GetFiles(tablesFolderPath, "*.tmdl");
            Regex itemRegex = new Regex(@"^\s*(measure|column)\s+'?([^'=]+)'?\s*=");

            foreach (string file in tmdlFiles)
            {
                string[] lines = File.ReadAllLines(file);
                string currentItemName = null;
                string currentExpression = "";
                bool isReadingExpression = false;

                foreach (string line in lines)
                {
                    var match = itemRegex.Match(line);

                    if (match.Success)
                    {
                        if (currentItemName != null && !string.IsNullOrWhiteSpace(currentExpression))
                            daxDictionary[currentItemName] = currentExpression.Trim();

                        currentItemName = match.Groups[2].Value.Trim();
                        currentExpression = "";
                        isReadingExpression = true;

                        int equalsIndex = line.IndexOf('=');
                        string afterEquals = line.Substring(equalsIndex + 1).Trim();
                        if (!string.IsNullOrEmpty(afterEquals)) currentExpression += afterEquals + "\n";
                        continue;
                    }

                    if (isReadingExpression && Regex.IsMatch(line, @"^\s*(formatString|displayFolder|lineageTag|column|measure|table|hierarchy|isHidden|dataType|summarizeBy)"))
                        isReadingExpression = false;

                    if (isReadingExpression && !string.IsNullOrWhiteSpace(line))
                        currentExpression += line.TrimStart() + "\n";
                }

                if (currentItemName != null && !string.IsNullOrWhiteSpace(currentExpression))
                    daxDictionary[currentItemName] = currentExpression.Trim();
            }
            return daxDictionary;
        }

        private string ExtractVisualCategory(JsonDocument doc)
        {
            try
            {
                if (doc.RootElement.TryGetProperty("visual", out JsonElement visualElement))
                {
                    if (visualElement.TryGetProperty("visualType", out JsonElement typeElement))
                    {
                        string rawType = typeElement.GetString()?.ToLower() ?? "";

                        if (rawType.Contains("slicer")) return "Slicer";
                        if (rawType.Contains("kpi") || rawType.Contains("card") || rawType.Contains("shape") || rawType.Contains("textbox")) return "KPI";
                        if (rawType.Contains("table") || rawType.Contains("matrix") || rawType.Contains("pivot")) return "Table";

                        return "Chart";
                    }
                }
            }
            catch { }
            return "Chart";
        }


        private void FindQueryRefs(JsonElement element, List<string> foundItems)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    if ((prop.Name == "queryRef" || prop.Name == "Property") && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        string daxRef = prop.Value.GetString();
                        if (!foundItems.Contains(daxRef)) foundItems.Add(daxRef);
                    }
                    else FindQueryRefs(prop.Value, foundItems);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray()) FindQueryRefs(item, foundItems);
            }
        }

        private string FormatCsvRow(params string[] columns)
        {
            for (int i = 0; i < columns.Length; i++)
            {
                if (columns[i] != null && (columns[i].Contains(",") || columns[i].Contains("\"") || columns[i].Contains("\n")))
                {
                    columns[i] = $"\"{columns[i].Replace("\"", "\"\"")}\"";
                }
            }
            return string.Join(",", columns);
        }
    }
}