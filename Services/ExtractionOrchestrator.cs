using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ReportsAutomationApp.Models;

namespace ReportsAutomationApp.Services
{
    public class ExtractionOrchestrator
    {
        // This holds ALL of our step services
        private readonly IEnumerable<IExtractionStep> _steps;

        public ExtractionOrchestrator(IEnumerable<IExtractionStep> steps)
        {
            _steps = steps;
        }

        public async Task<StepResult> ExecuteProcessAsync(string reportPath, string semanticModelPath, int processId)
        {
            // Find the service where StepId matches the processId
            var stepToRun = _steps.FirstOrDefault(s => s.StepId == processId);

            if (stepToRun == null)
            {
                return new StepResult { IsSuccess = false, Message = $"No service found for Step {processId}." };
            }

            // Execute the specific service!
            return await stepToRun.ExecuteAsync(reportPath, semanticModelPath);
        }
    }
}