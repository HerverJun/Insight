using Insight.Infrastructure.Processes;
using Insight.Services.Cloud.Kaggle;
using Insight.Services.Configuration;

namespace Insight.Infrastructure.Cloud.Kaggle
{
    public sealed class KaggleCliClient : IKaggleClient
    {
        private readonly IProcessRunner _processRunner;
        private readonly InsightAppPaths _paths;

        public KaggleCliClient(IProcessRunner processRunner, InsightAppPaths paths)
        {
            _processRunner = processRunner;
            _paths = paths;
        }

        public async Task<KaggleTrainingSubmissionResult> SubmitTrainingAsync(
            KaggleTrainingSubmissionRequest request,
            CancellationToken cancellationToken)
        {
            var result = await KaggleCloudTrainer.StartAsync(
                request.Options,
                request.OnLog,
                request.OnProgress,
                _processRunner,
                _paths,
                cancellationToken);

            return new KaggleTrainingSubmissionResult
            {
                JobId = request.JobId,
                DatasetId = result.DatasetId,
                KernelId = result.KernelId,
                KernelUrl = $"https://www.kaggle.com/code/{result.KernelId}",
                JobRoot = result.JobRoot,
                Submitted = true,
                Message = "Kaggle training submitted."
            };
        }

        public async Task<KaggleTrainingJobStatus> GetTrainingStatusAsync(
            string kernelId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(kernelId) || !kernelId.Contains('/'))
            {
                throw new ArgumentException("Kernel ID must use username/kernel-slug format.", nameof(kernelId));
            }

            var output = new List<string>();
            var result = await _processRunner.RunAsync(
                new ProcessStartSpec
                {
                    FileName = "kaggle",
                    ArgumentList = new[] { "kernels", "status", kernelId },
                    WorkingDirectory = _paths.CloudJobsRoot
                },
                output.Add,
                cancellationToken);

            var message = string.Join(Environment.NewLine, output);
            return new KaggleTrainingJobStatus
            {
                KernelId = kernelId,
                State = result.Succeeded ? "Submitted" : "Unknown",
                Progress = result.Succeeded ? 0.5 : 0,
                Message = message
            };
        }

        public Task<string> DownloadOutputAsync(
            KaggleOutputDownloadRequest request,
            CancellationToken cancellationToken)
        {
            return KaggleCloudTrainer.DownloadOutputAsync(
                request.KernelId,
                request.OutputDirectory,
                request.OnLog,
                _processRunner,
                _paths,
                cancellationToken);
        }
    }
}
