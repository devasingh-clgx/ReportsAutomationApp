using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                string bookmarksPath = Path.Combine(reportPath, "definition", "bookmarks");
                string reportExtensionsPath = Path.Combine(reportPath, "definition", "reportExtensions.json");

                string reportName = new DirectoryInfo(reportPath).Name;
                string safeReportName = ExportFileNameHelper.ToSafeToken(reportName);
                string modelName = new DirectoryInfo(semanticModelPath).Name;

                if (!Directory.Exists(tablesFolderPath))
                    return new StepResult { IsSuccess = false, Message = $"Semantic Model tables folder not found: {tablesFolderPath}" };

                if (!Directory.Exists(pagesPath))
                    return new StepResult { IsSuccess = false, Message = $"Report pages folder not found: {pagesPath}" };

                var daxDictionary = ExtractAllDax(tablesFolderPath);

                var reportLevelDax = ExtractReportExtensionsDax(reportExtensionsPath);
                foreach (var kvp in reportLevelDax)
                {
                    daxDictionary[kvp.Key] = kvp.Value;
                }

                // Safely extract bookmarks, page assignments, and their specific Date Filters!
                var allBookmarks = ExtractAllBookmarks(bookmarksPath);

                var extractedRows = new List<ExportRow>();

                int visualsProcessed = 0;

                foreach (var pageFolder in Directory.GetDirectories(pagesPath))
                {
                    string pageId = new DirectoryInfo(pageFolder).Name;
                    string pageName = pageId;
                    string pageJsonPath = Path.Combine(pageFolder, "page.json");
                    string basePageFiltersFormatted = "None";

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
                                if (pageFilters.Count > 0) basePageFiltersFormatted = string.Join(" | ", pageFilters);
                            }
                        }
                        catch { }
                    }

                    // Find bookmarks specifically for this page
                    var pageBookmarks = allBookmarks.Where(b => b.PageId == pageId).ToList();

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
                                string baseVisualName = ExtractVisualTitle(doc, visualId, visualCategory);

                                List<string> visualFilters = new List<string>();
                                FindFilters(doc.RootElement, visualFilters);
                                string visualFiltersFormatted = visualFilters.Count > 0 ? string.Join(" | ", visualFilters) : "None";

                                List<string> rawQueryRefs = new List<string>();
                                FindQueryRefs(doc.RootElement, rawQueryRefs); // Safe extraction (keeps Tooltips)

                                if (rawQueryRefs.Count > 0)
                                {
                                    List<string> columnsUsed = new List<string>();
                                    var seededMeasures = new List<string>();

                                    foreach (var qRef in rawQueryRefs)
                                    {
                                        string cleanName = qRef;
                                        int dotIndex = qRef.IndexOf('.');
                                        if (dotIndex >= 0) cleanName = qRef.Substring(dotIndex + 1);

                                        if (daxDictionary.ContainsKey(cleanName))
                                        {
                                            if (!seededMeasures.Contains(cleanName, StringComparer.OrdinalIgnoreCase))
                                            {
                                                seededMeasures.Add(cleanName);
                                            }
                                        }
                                        else
                                        {
                                            if (!columnsUsed.Contains(qRef)) columnsUsed.Add(qRef);
                                        }
                                    }

                                    string columnsStr = columnsUsed.Count > 0 ? string.Join("\n", columnsUsed) : "None";

                                    var exportDefinitions = BuildExportDefinitions(doc, visualCategory, baseVisualName, seededMeasures, daxDictionary);

                                    foreach (var exportDefinition in exportDefinitions)
                                    {
                                        extractedRows.Add(new ExportRow(
                                            modelName,
                                            pageName,
                                            visualId,
                                            exportDefinition.VisualName,
                                            visualCategory,
                                            visualFiltersFormatted,
                                            basePageFiltersFormatted,
                                            columnsStr,
                                            exportDefinition.MeasuresUsed,
                                            exportDefinition.Dax));

                                        // --- 2. Iterate Bookmark Scenarios for this Page ---
                                        foreach (var bm in pageBookmarks)
                                        {
                                            // If the bookmark explicitly hides this chart, skip generating a row for it!
                                            if (bm.HiddenVisuals.Contains(visualId))
                                                continue;

                                            // Name the visual cleanly (e.g., "Item Category - DEP Created DT Last Yr")
                                            string bmkVisualName = $"{exportDefinition.VisualName} - {bm.Name}";

                                            // Merge Date Filters into the global PageFilters string
                                            string combinedPageFilters = basePageFiltersFormatted;
                                            if (bm.Filters.Count > 0)
                                            {
                                                string extraFilters = string.Join(" | ", bm.Filters);
                                                combinedPageFilters = (combinedPageFilters == "None" || string.IsNullOrWhiteSpace(combinedPageFilters))
                                                    ? extraFilters
                                                    : $"{combinedPageFilters} | {extraFilters}";
                                            }

                                            extractedRows.Add(new ExportRow(
                                                modelName,
                                                pageName,
                                                visualId,
                                                bmkVisualName,
                                                visualCategory,
                                                visualFiltersFormatted,
                                                combinedPageFilters,
                                                columnsStr,
                                                exportDefinition.MeasuresUsed,
                                                exportDefinition.Dax));
                                        }
                                    }
                                    visualsProcessed++;
                                }
                            }
                        }
                    }
                }

                var deduplicatedRows = DeduplicateRows(extractedRows);
                int duplicatesRemoved = extractedRows.Count - deduplicatedRows.Count;

                StringBuilder csvContent = new StringBuilder();
                csvContent.AppendLine("ScenarioId,ModelName,PageName,VisualId,VisualName,VisualType,VisualFilters,PageFilters,ColumnsUsed,MeasuresUsed,Dax");

                for (int i = 0; i < deduplicatedRows.Count; i++)
                {
                    var row = deduplicatedRows[i];
                    csvContent.AppendLine(FormatCsvRow(
                        (i + 1).ToString(),
                        row.ModelName,
                        row.PageName,
                        row.VisualId,
                        row.VisualName,
                        row.VisualType,
                        row.VisualFilters,
                        row.PageFilters,
                        row.ColumnsUsed,
                        row.MeasuresUsed,
                        row.Dax));
                }

                string exportsFolder = Path.Combine(_env.WebRootPath, "Exports");
                if (!Directory.Exists(exportsFolder)) Directory.CreateDirectory(exportsFolder);

                string fileName = $"DAX_{safeReportName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                string filePath = Path.Combine(exportsFolder, fileName);

                await File.WriteAllTextAsync(filePath, csvContent.ToString());

                return new StepResult
                {
                    IsSuccess = true,
                    Message = $"Successfully extracted {visualsProcessed} visuals with all Bookmark Date scenarios. Removed {duplicatesRemoved} duplicate row(s).",
                    DownloadFilePath = $"/Exports/{fileName}",
                    ExtractedDataPreview = csvContent.ToString().Substring(0, Math.Min(csvContent.Length, 500)) + "...\n[Data Truncated]"
                };
            }
            catch (Exception ex)
            {
                return new StepResult { IsSuccess = false, Message = $"Error extracting DAX: {ex.Message}" };
            }
        }


        // =========================================================
        // PARSERS: Report Extensions & Bookmarks
        // =========================================================

        public class BookmarkData
        {
            public string Name { get; set; } = "";
            public string PageId { get; set; } = "";
            public bool ApplyOnlyToTargetVisuals { get; set; }
            public HashSet<string> TargetVisuals { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> HiddenVisuals { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Filters { get; set; } = new List<string>();
        }

        private sealed class ExportRow
        {
            public ExportRow(string modelName, string pageName, string visualId, string visualName, string visualType, string visualFilters, string pageFilters, string columnsUsed, string measuresUsed, string dax)
            {
                ModelName = modelName;
                PageName = pageName;
                VisualId = visualId;
                VisualName = visualName;
                VisualType = visualType;
                VisualFilters = visualFilters;
                PageFilters = pageFilters;
                ColumnsUsed = columnsUsed;
                MeasuresUsed = measuresUsed;
                Dax = dax;
            }

            public string ModelName { get; }
            public string PageName { get; }
            public string VisualId { get; }
            public string VisualName { get; }
            public string VisualType { get; }
            public string VisualFilters { get; }
            public string PageFilters { get; }
            public string ColumnsUsed { get; }
            public string MeasuresUsed { get; }
            public string Dax { get; }
        }

        private sealed class ExportDefinition
        {
            public ExportDefinition(string visualName, string measuresUsed, string dax)
            {
                VisualName = visualName;
                MeasuresUsed = measuresUsed;
                Dax = dax;
            }

            public string VisualName { get; }
            public string MeasuresUsed { get; }
            public string Dax { get; }
        }

        private Dictionary<string, string> ExtractReportExtensionsDax(string jsonPath)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(jsonPath)) return dict;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                if (doc.RootElement.TryGetProperty("entities", out var entitiesArray) && entitiesArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entity in entitiesArray.EnumerateArray())
                    {
                        if (entity.TryGetProperty("measures", out var measuresArray) && measuresArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var measure in measuresArray.EnumerateArray())
                            {
                                if (measure.TryGetProperty("name", out var nameElement) &&
                                    measure.TryGetProperty("expression", out var exprElement))
                                {
                                    dict[nameElement.GetString()] = exprElement.GetString();
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return dict;
        }

        private List<ExportDefinition> BuildExportDefinitions(JsonDocument doc, string visualCategory, string baseVisualName, List<string> seededMeasures, Dictionary<string, string> daxDictionary)
        {
            var definitions = new List<ExportDefinition>();
            var kpiProjections = ExtractKpiProjectionMeasures(doc);

            if (visualCategory == "KPI" && kpiProjections.Count > 1)
            {
                foreach (var projection in kpiProjections)
                {
                    var exportDefinition = BuildExportDefinition(projection.DisplayName, new List<string> { projection.MeasureName }, daxDictionary);
                    definitions.Add(exportDefinition);
                }

                return definitions;
            }

            definitions.Add(BuildExportDefinition(baseVisualName, seededMeasures, daxDictionary));
            return definitions;
        }

        private ExportDefinition BuildExportDefinition(string visualName, List<string> seedMeasures, Dictionary<string, string> daxDictionary)
        {
            List<string> measuresUsed = new List<string>();
            StringBuilder combinedDax = new StringBuilder();
            HashSet<string> resolvedMeasures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Queue<string> measureQueue = new Queue<string>();

            foreach (string seedMeasure in seedMeasures)
            {
                if (!daxDictionary.ContainsKey(seedMeasure) || !resolvedMeasures.Add(seedMeasure))
                    continue;

                measureQueue.Enqueue(seedMeasure);
                measuresUsed.Add(seedMeasure);
            }

            while (measureQueue.Count > 0)
            {
                string currentMeasure = measureQueue.Dequeue();
                string currentDax = daxDictionary[currentMeasure];

                combinedDax.AppendLine($"--- {currentMeasure} ---");
                combinedDax.AppendLine(currentDax);
                combinedDax.AppendLine();

                var matches = Regex.Matches(currentDax, @"\[([^\]]+)\]");
                foreach (Match match in matches)
                {
                    string nestedItem = match.Groups[1].Value.Trim();

                    if (daxDictionary.ContainsKey(nestedItem) && resolvedMeasures.Add(nestedItem))
                    {
                        measureQueue.Enqueue(nestedItem);
                        measuresUsed.Add(nestedItem);
                    }
                }
            }

            string measuresStr = measuresUsed.Count > 0 ? string.Join("\n", measuresUsed) : "None";
            string finalDax = combinedDax.ToString().Trim();

            if (string.IsNullOrEmpty(finalDax)) finalDax = "No DAX Formulas";

            return new ExportDefinition(visualName, measuresStr, finalDax);
        }

        private List<(string DisplayName, string MeasureName)> ExtractKpiProjectionMeasures(JsonDocument doc)
        {
            var results = new List<(string DisplayName, string MeasureName)>();

            if (!doc.RootElement.TryGetProperty("visual", out JsonElement visualElement)
                || !visualElement.TryGetProperty("query", out JsonElement queryElement)
                || !queryElement.TryGetProperty("queryState", out JsonElement queryStateElement)
                || !queryStateElement.TryGetProperty("Data", out JsonElement dataElement)
                || !dataElement.TryGetProperty("projections", out JsonElement projectionsElement)
                || projectionsElement.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (JsonElement projection in projectionsElement.EnumerateArray())
            {
                if (!projection.TryGetProperty("field", out JsonElement fieldElement)
                    || !fieldElement.TryGetProperty("Measure", out JsonElement measureElement)
                    || !measureElement.TryGetProperty("Property", out JsonElement propertyElement))
                {
                    continue;
                }

                string? measureName = propertyElement.GetString();
                if (string.IsNullOrWhiteSpace(measureName))
                    continue;

                string displayName = projection.TryGetProperty("displayName", out JsonElement displayNameElement)
                    ? displayNameElement.GetString() ?? measureName
                    : projection.TryGetProperty("nativeQueryRef", out JsonElement nativeQueryRefElement)
                        ? nativeQueryRefElement.GetString() ?? measureName
                        : measureName;

                results.Add((displayName, measureName));
            }

            return results;
        }

        private List<BookmarkData> ExtractAllBookmarks(string bookmarksPath)
        {
            var bookmarks = new List<BookmarkData>();
            if (!Directory.Exists(bookmarksPath)) return bookmarks;

            foreach (var file in Directory.GetFiles(bookmarksPath, "*.bookmark.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    var bookmark = new BookmarkData();

                    if (root.TryGetProperty("displayName", out var nameElement))
                        bookmark.Name = nameElement.GetString();
                    else if (root.TryGetProperty("name", out var internalName))
                        bookmark.Name = internalName.GetString();

                    if (root.TryGetProperty("options", out var options))
                    {
                        if (options.TryGetProperty("applyOnlyToTargetVisuals", out var applyOnlyTargetVisuals)
                            && (applyOnlyTargetVisuals.ValueKind == JsonValueKind.True || applyOnlyTargetVisuals.ValueKind == JsonValueKind.False))
                        {
                            bookmark.ApplyOnlyToTargetVisuals = applyOnlyTargetVisuals.GetBoolean();
                        }

                        if (options.TryGetProperty("targetVisualNames", out var targetVisualNames)
                            && targetVisualNames.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var targetVisualName in targetVisualNames.EnumerateArray())
                            {
                                var visualName = targetVisualName.GetString();
                                if (!string.IsNullOrWhiteSpace(visualName))
                                    bookmark.TargetVisuals.Add(visualName);
                            }
                        }
                    }

                    if (root.TryGetProperty("explorationState", out var expState))
                    {
                        if (expState.TryGetProperty("activeSection", out var activeSec))
                            bookmark.PageId = activeSec.GetString();

                        List<string> rawFilters = new List<string>();
                        FindFilters(expState, rawFilters);
                        ExtractSlicerStateFilters(expState, rawFilters);
                        if (rawFilters.Count > 0)
                            bookmark.Filters = new HashSet<string>(rawFilters).ToList();
                    }

                    ExtractHiddenVisuals(root, bookmark.HiddenVisuals);

                    bookmarks.Add(bookmark);
                }
                catch { }
            }
            return bookmarks;
        }

        private void ExtractHiddenVisuals(JsonElement element, HashSet<string> hiddenVisuals)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (IsBookmarkVisualHidden(prop.Value))
                            hiddenVisuals.Add(prop.Name);

                        if (prop.Value.TryGetProperty("isHidden", out JsonElement hiddenEl) && hiddenEl.ValueKind == JsonValueKind.True)
                            hiddenVisuals.Add(prop.Name);

                        ExtractHiddenVisuals(prop.Value, hiddenVisuals);
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        ExtractHiddenVisuals(prop.Value, hiddenVisuals);
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    ExtractHiddenVisuals(item, hiddenVisuals);
                }
            }
        }

        private List<ExportRow> DeduplicateRows(List<ExportRow> rows)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var deduplicated = new List<ExportRow>();

            foreach (var row in rows)
            {
                string key = string.Join("\u001F",
                    NormalizeForDedup(row.ModelName),
                    NormalizeForDedup(row.PageName),
                    NormalizeForDedup(row.VisualName),
                    NormalizeForDedup(row.VisualType),
                    NormalizeForDedup(row.VisualFilters),
                    NormalizeForDedup(row.PageFilters),
                    NormalizeForDedup(row.ColumnsUsed),
                    NormalizeForDedup(row.MeasuresUsed),
                    NormalizeForDedup(row.Dax));

                if (seen.Add(key))
                {
                    deduplicated.Add(row);
                }
            }

            return deduplicated;
        }

        private static string NormalizeForDedup(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        }

        private static bool IsBookmarkVisualHidden(JsonElement visualState)
        {
            if (visualState.TryGetProperty("display", out JsonElement displayState)
                && displayState.ValueKind == JsonValueKind.Object
                && displayState.TryGetProperty("mode", out JsonElement displayMode)
                && string.Equals(displayMode.GetString(), "hidden", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (visualState.TryGetProperty("singleVisual", out JsonElement singleVisual)
                && singleVisual.ValueKind == JsonValueKind.Object
                && singleVisual.TryGetProperty("display", out JsonElement singleVisualDisplay)
                && singleVisualDisplay.ValueKind == JsonValueKind.Object
                && singleVisualDisplay.TryGetProperty("mode", out JsonElement singleVisualMode)
                && string.Equals(singleVisualMode.GetString(), "hidden", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private void ExtractSlicerStateFilters(JsonElement element, List<string> filterExpressions)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (TryBuildSlicerStateFilter(element, out string slicerFilter)
                    && !string.IsNullOrWhiteSpace(slicerFilter)
                    && !filterExpressions.Contains(slicerFilter))
                {
                    filterExpressions.Add(slicerFilter);
                }

                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    ExtractSlicerStateFilters(prop.Value, filterExpressions);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    ExtractSlicerStateFilters(item, filterExpressions);
                }
            }
        }

        private bool TryBuildSlicerStateFilter(JsonElement visualContainerState, out string filterExpression)
        {
            filterExpression = "";

            if (!visualContainerState.TryGetProperty("singleVisual", out JsonElement singleVisual)
                || singleVisual.ValueKind != JsonValueKind.Object
                || !string.Equals(singleVisual.TryGetProperty("visualType", out JsonElement visualType) ? visualType.GetString() : null, "slicer", StringComparison.OrdinalIgnoreCase)
                || HasExplicitVisualFilter(singleVisual))
            {
                return false;
            }

            string targetField = ResolveSlicerTargetField(visualContainerState, singleVisual);
            if (string.IsNullOrWhiteSpace(targetField))
            {
                return false;
            }

            if (!singleVisual.TryGetProperty("objects", out JsonElement objects)
                || objects.ValueKind != JsonValueKind.Object
                || !objects.TryGetProperty("merge", out JsonElement merge)
                || merge.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            JsonElement dataProperties = GetFirstPropertyBag(merge, "data");
            JsonElement dateRangeProperties = GetFirstPropertyBag(merge, "dateRange");

            string mode = GetLiteralPropertyValue(dataProperties, "mode");
            string relativeRange = GetLiteralPropertyValue(dataProperties, "relativeRange");
            string relativePeriod = GetLiteralPropertyValue(dataProperties, "relativePeriod");
            string relativeDuration = GetLiteralPropertyValue(dataProperties, "relativeDuration");
            string startDate = NormalizeDateLiteral(GetLiteralPropertyValue(dataProperties, "startDate"));
            string endDate = NormalizeDateLiteral(GetLiteralPropertyValue(dataProperties, "endDate"));
            bool includeToday = string.Equals(GetLiteralPropertyValue(dateRangeProperties, "includeToday"), "true", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(relativeRange)
                && !string.IsNullOrWhiteSpace(relativePeriod)
                && TryParsePowerBiDuration(relativeDuration, out int durationValue))
            {
                string normalizedPeriod = NormalizeRelativePeriod(relativePeriod);
                string currentDate = includeToday ? "CURRENT_DATE" : "DATEADD(DAY, -1, CURRENT_DATE)";

                if (string.Equals(relativeRange, "Last", StringComparison.OrdinalIgnoreCase))
                {
                    filterExpression = $"{targetField} BETWEEN DATEADD({normalizedPeriod}, -{durationValue}, {currentDate}) AND {currentDate}";
                    return true;
                }

                if (string.Equals(relativeRange, "Next", StringComparison.OrdinalIgnoreCase))
                {
                    string rangeStart = includeToday ? "CURRENT_DATE" : "DATEADD(DAY, 1, CURRENT_DATE)";
                    filterExpression = $"{targetField} BETWEEN {rangeStart} AND DATEADD({normalizedPeriod}, {durationValue}, {rangeStart})";
                    return true;
                }

                if (string.Equals(relativeRange, "This", StringComparison.OrdinalIgnoreCase))
                {
                    filterExpression = $"{targetField} IN CURRENT {normalizedPeriod}";
                    return true;
                }
            }

            if (string.Equals(mode, "Between", StringComparison.OrdinalIgnoreCase)
                && (!string.IsNullOrWhiteSpace(startDate) || !string.IsNullOrWhiteSpace(endDate) || includeToday))
            {
                string lowerBound = !string.IsNullOrWhiteSpace(startDate) ? startDate : "MIN_DATE";
                string upperBound = !string.IsNullOrWhiteSpace(endDate)
                    ? endDate
                    : includeToday
                        ? "CURRENT_DATE"
                        : "MAX_DATE";

                filterExpression = $"{targetField} BETWEEN {lowerBound} AND {upperBound}";
                return true;
            }

            return false;
        }

        private static bool HasExplicitVisualFilter(JsonElement singleVisual)
        {
            if (!singleVisual.TryGetProperty("objects", out JsonElement objects)
                || objects.ValueKind != JsonValueKind.Object
                || !objects.TryGetProperty("merge", out JsonElement merge)
                || merge.ValueKind != JsonValueKind.Object
                || !merge.TryGetProperty("general", out JsonElement general)
                || general.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement item in general.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("properties", out JsonElement properties)
                    && properties.ValueKind == JsonValueKind.Object
                    && properties.TryGetProperty("filter", out JsonElement filter)
                    && filter.ValueKind == JsonValueKind.Object)
                {
                    return true;
                }
            }

            return false;
        }

        private string ResolveSlicerTargetField(JsonElement visualContainerState, JsonElement singleVisual)
        {
            if (visualContainerState.TryGetProperty("filters", out JsonElement filters)
                && filters.ValueKind == JsonValueKind.Object
                && filters.TryGetProperty("byExpr", out JsonElement byExpr)
                && byExpr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in byExpr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("expression", out JsonElement expression))
                    {
                        string propertyPath = GetPropertyPath(expression);
                        if (!string.IsNullOrWhiteSpace(propertyPath))
                        {
                            return propertyPath;
                        }
                    }
                }
            }

            if (singleVisual.TryGetProperty("activeProjections", out JsonElement activeProjections)
                && activeProjections.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty projection in activeProjections.EnumerateObject())
                {
                    if (projection.Value.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (JsonElement item in projection.Value.EnumerateArray())
                    {
                        string propertyPath = GetPropertyPath(item);
                        if (!string.IsNullOrWhiteSpace(propertyPath))
                        {
                            return propertyPath;
                        }
                    }
                }
            }

            return "";
        }

        private static JsonElement GetFirstPropertyBag(JsonElement merge, string propertyName)
        {
            if (merge.TryGetProperty(propertyName, out JsonElement propertyArray)
                && propertyArray.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in propertyArray.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("properties", out JsonElement properties)
                        && properties.ValueKind == JsonValueKind.Object)
                    {
                        return properties;
                    }
                }
            }

            return default;
        }

        private static string GetLiteralPropertyValue(JsonElement propertyBag, string propertyName)
        {
            if (propertyBag.ValueKind == JsonValueKind.Object
                && propertyBag.TryGetProperty(propertyName, out JsonElement property)
                && property.ValueKind == JsonValueKind.Object
                && property.TryGetProperty("expr", out JsonElement expr)
                && expr.ValueKind == JsonValueKind.Object
                && expr.TryGetProperty("Literal", out JsonElement literal)
                && literal.ValueKind == JsonValueKind.Object
                && literal.TryGetProperty("Value", out JsonElement value))
            {
                return TrimPowerBiLiteral(value.GetString());
            }

            return "";
        }

        private static string TrimPowerBiLiteral(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim(' ', '\'', '"');
        }

        private static bool TryParsePowerBiDuration(string duration, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(duration))
                return false;

            return int.TryParse(duration.TrimEnd('D'), out value);
        }

        private static string NormalizeRelativePeriod(string period)
        {
            return period.TrimEnd('s', 'S') switch
            {
                "Day" => "DAY",
                "Week" => "WEEK",
                "Month" => "MONTH",
                "Quarter" => "QUARTER",
                "Year" => "YEAR",
                _ => period.ToUpperInvariant()
            };
        }

        private static string NormalizeDateLiteral(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            const string prefix = "datetime'";
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && value.EndsWith("'", StringComparison.Ordinal))
            {
                return $"'{value.Substring(prefix.Length, value.Length - prefix.Length - 1).Split('T')[0]}'";
            }

            return value.StartsWith("'", StringComparison.Ordinal) ? value : $"'{value}'";
        }
        // =========================================================
        // POWER BI AST FILTER PARSER 
        // =========================================================

        private void FindFilters(JsonElement element, List<string> filterExpressions)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    if (prop.Name == "filter" && prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        ExtractFilterExpressions(prop.Value, filterExpressions);
                    }
                    else if (prop.Name == "filters" && prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement filterItem in prop.Value.EnumerateArray())
                        {
                            if (filterItem.ValueKind == JsonValueKind.Object && filterItem.TryGetProperty("filter", out JsonElement filterDef))
                                ExtractFilterExpressions(filterDef, filterExpressions);
                            else
                                ExtractFilterExpressions(filterItem, filterExpressions);
                        }
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        string strVal = prop.Value.GetString();
                        if (strVal != null && strVal.Contains("\"filter"))
                        {
                            try
                            {
                                using var innerDoc = JsonDocument.Parse(strVal);
                                FindFilters(innerDoc.RootElement, filterExpressions);
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

        private string ParseConditionAst(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return "";
            List<string> parts = new List<string>();

            if (node.TryGetProperty("Between", out JsonElement betNode))
            {
                string prop = GetPropertyPath(betNode);
                string lower = GetExpressionValue(betNode.TryGetProperty("LowerBound", out var lb) ? lb : default);
                string upper = GetExpressionValue(betNode.TryGetProperty("UpperBound", out var ub) ? ub : default);
                return $"{prop} BETWEEN {lower} AND {upper}";
            }
            else if (node.TryGetProperty("Not", out JsonElement notNode))
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
                foreach (JsonProperty prop in node.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        string res = ParseConditionAst(prop.Value);
                        if (!string.IsNullOrEmpty(res)) parts.Add(res);
                    }
                    // FIX: This is the missing piece! It must drill into Arrays to find the Date constraints!
                    else if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in prop.Value.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object)
                            {
                                string res = ParseConditionAst(item);
                                if (!string.IsNullOrEmpty(res)) parts.Add(res);
                            }
                        }
                    }
                }
            }

            return parts.Count > 0 ? string.Join(" AND ", parts) : "";
        }

        private string GetExpressionValue(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return "";

            if (node.TryGetProperty("Literal", out JsonElement literal))
            {
                return literal.TryGetProperty("Value", out JsonElement val) ? (val.GetString() ?? "null") : "null";
            }
            if (node.TryGetProperty("DateTime", out JsonElement dt))
            {
                return dt.TryGetProperty("Date", out JsonElement dVal) ? $"'{dVal.GetString()}'" : "datetime";
            }
            if (node.TryGetProperty("DateSpan", out JsonElement ds))
            {
                return ds.TryGetProperty("Expression", out JsonElement exp) ? GetExpressionValue(exp) : "";
            }
            if (node.TryGetProperty("DateAdd", out JsonElement da))
            {
                string inner = da.TryGetProperty("Expression", out JsonElement exp) ? GetExpressionValue(exp) : "";
                int amount = da.TryGetProperty("Amount", out JsonElement amt) ? amt.GetInt32() : 0;
                int timeUnit = da.TryGetProperty("TimeUnit", out JsonElement tu) ? tu.GetInt32() : 0;

                string unitStr = timeUnit == 0 ? "Day" : (timeUnit == 1 ? "Week" : (timeUnit == 2 ? "Month" : "Year"));
                return $"DATEADD({unitStr}, {amount}, {inner})";
            }
            if (node.TryGetProperty("Now", out _))
            {
                return "NOW()";
            }

            return GetLiteralValues(node);
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
            catch { }

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
                    else
                    {
                        FindQueryRefs(prop.Value, foundItems);
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    FindQueryRefs(item, foundItems);
                }
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