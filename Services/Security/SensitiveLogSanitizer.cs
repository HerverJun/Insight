using System.Text.RegularExpressions;

namespace Insight.Services.Security
{
    public static class SensitiveLogSanitizer
    {
        private static readonly Regex[] SecretPatterns =
        {
            new(@"(?i)(kaggle[_-]?(key|token|api[_-]?key)\s*[:=]\s*)[^\s,;]+", RegexOptions.Compiled),
            new(@"(?i)(authorization\s*[:=]\s*bearer\s+)[^\s,;]+", RegexOptions.Compiled),
            new(@"(?i)(""key""\s*:\s*"")[^""]+(""?)", RegexOptions.Compiled),
            new(@"(?i)(""token""\s*:\s*"")[^""]+(""?)", RegexOptions.Compiled)
        };

        public static string Sanitize(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            var sanitized = value;
            foreach (var pattern in SecretPatterns)
            {
                sanitized = pattern.Replace(sanitized, match =>
                {
                    if (match.Groups.Count >= 4)
                    {
                        return match.Groups[1].Value + "***" + match.Groups[3].Value;
                    }

                    return match.Groups[1].Value + "***";
                });
            }

            return sanitized;
        }
    }
}
