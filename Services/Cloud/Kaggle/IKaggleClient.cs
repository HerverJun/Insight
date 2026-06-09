namespace Insight.Services.Cloud.Kaggle
{
    public interface IKaggleClient
    {
        Task<KaggleConnectionTestResult> TestConnectionAsync(
            KaggleConnectionTestRequest request,
            CancellationToken cancellationToken);

        Task<KaggleTrainingSubmissionResult> SubmitTrainingAsync(
            KaggleTrainingSubmissionRequest request,
            CancellationToken cancellationToken);

        Task<KaggleTrainingSubmissionResult> SubmitPreparedTrainingAsync(
            KagglePreparedTrainingSubmissionRequest request,
            CancellationToken cancellationToken);

        Task<KaggleTrainingJobStatus> GetTrainingStatusAsync(
            string kernelId,
            CancellationToken cancellationToken);

        Task<string> DownloadOutputAsync(
            KaggleOutputDownloadRequest request,
            CancellationToken cancellationToken);
    }
}
