using System.Collections.Generic;
namespace ReportsAutomationApp.Models
{
    public class ReportGroupUI
    {
        public string GroupName { get; set; } = "";
        public bool IsExpanded { get; set; }
        public List<ReportUI> Reports { get; set; } = new();
    }

    public class SemanticModelUI
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
    }

    public class ReportUI
    {
        public string ReportName { get; set; } = "";
        public string ReportFullPath { get; set; } = "";
        public string SelectedSemanticModelPath { get; set; } = "";
        public bool IsSelected { get; set; }
        public int CompletedStep { get; set; } = 0;
        public bool IsProcessing { get; set; }

        // This stores the results of each step (1-4) so we can open popups later!
        public Dictionary<int, StepResult> StepResults { get; set; } = new();
    }

    public class StepResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; } = "";
        public string DownloadFilePath { get; set; } = "";
        public string ExtractedDataPreview { get; set; } = "";
    }
}
