using System.Text;

namespace Insight.Infrastructure.Processes
{
    public sealed class ProcessStartSpec
    {
        public string FileName { get; init; } = "";
        public string Arguments { get; init; } = "";
        public IReadOnlyList<string> ArgumentList { get; init; } = Array.Empty<string>();
        public string WorkingDirectory { get; init; } = "";
        public Encoding StandardOutputEncoding { get; init; } = Encoding.UTF8;
        public Encoding StandardErrorEncoding { get; init; } = Encoding.UTF8;
        public Dictionary<string, string> EnvironmentVariables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
