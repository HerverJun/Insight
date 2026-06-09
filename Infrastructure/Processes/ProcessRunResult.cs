namespace Insight.Infrastructure.Processes
{
    public sealed class ProcessRunResult
    {
        public ProcessRunResult(int? exitCode, bool wasCanceled, string output)
        {
            ExitCode = exitCode;
            WasCanceled = wasCanceled;
            Output = output;
        }

        public int? ExitCode { get; }
        public bool WasCanceled { get; }
        public string Output { get; }
        public bool Succeeded => !WasCanceled && ExitCode == 0;
    }
}
