using System;
using System.IO;
using System.Linq;

namespace ReportsAutomationApp.Services
{
    internal static class ExportFileNameHelper
    {
        public static string ToSafeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "Unknown";
            }

            var invalidChars = Path.GetInvalidFileNameChars().Concat(new[] { '#', '%', '&', '?', '+' }).ToHashSet();
            var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());

            while (sanitized.Contains("  ", StringComparison.Ordinal))
            {
                sanitized = sanitized.Replace("  ", " ", StringComparison.Ordinal);
            }

            sanitized = sanitized.Trim().Trim('.');
            return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
        }
    }
}
