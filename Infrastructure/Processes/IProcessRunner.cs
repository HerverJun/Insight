namespace Insight.Infrastructure.Processes
{
    public interface IProcessRunner
    {
        Task<ProcessRunResult> RunAsync(
            ProcessStartSpec spec,
            Action<string>? onOutput,
            CancellationToken cancellationToken);
    }
}
