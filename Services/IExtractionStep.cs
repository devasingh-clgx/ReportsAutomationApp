using System.Threading.Tasks;
using ReportsAutomationApp.Models;

namespace ReportsAutomationApp.Services
{
    public interface IExtractionStep
    {
        int StepId { get; }
        Task<StepResult> ExecuteAsync(string reportPath, string semanticModelPath);
    }
}