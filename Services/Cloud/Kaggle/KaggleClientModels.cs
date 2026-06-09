namespace Insight.Services.Cloud.Kaggle
{
    public sealed class KaggleTrainingSubmissionRequest
    {
        public string JobId { get; init; } = Guid.NewGuid().ToString("N");
        public string ProjectRoot { get; init; } = "";
        public string DatasetZipPath { get; init; } = "";
        public string NotebookPath { get; init; } = "";
        public string KernelSlug { get; init; } = "";
        public global::Insight.KaggleCloudTrainingOptions Options { get; init; } = new();
        public Action<string, string>? OnLog { get; init; }
        public Action<int>? OnProgress { get; init; }
        public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class KaggleTrainingSubmissionResult
    {
        public string JobId { get; init; } = "";
        public string DatasetId { get; init; } = "";
        public string KernelId { get; init; } = "";
        public string KernelUrl { get; init; } = "";
        public string JobRoot { get; init; } = "";
        public bool Submitted { get; init; }
        public string Message { get; init; } = "";
    }

    public sealed class KaggleTrainingJobStatus
    {
        public string KernelId { get; init; } = "";
        public string State { get; init; } = "";
        public double Progress { get; init; }
        public string Message { get; init; } = "";
    }

    public sealed class KaggleOutputDownloadRequest
    {
        public string KernelId { get; init; } = "";
        public string OutputDirectory { get; init; } = "";
        public Action<string, string>? OnLog { get; init; }
    }
}
