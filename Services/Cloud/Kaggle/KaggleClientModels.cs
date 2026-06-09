namespace Insight.Services.Cloud.Kaggle
{
    public sealed class KaggleConnectionTestRequest
    {
        public string Username { get; init; } = "";
        public Action<string, string>? OnLog { get; init; }
    }

    public sealed class KaggleConnectionTestResult
    {
        public bool Success { get; init; }
        public string CliVersion { get; init; } = "";
        public string Message { get; init; } = "";
    }

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

    public sealed class KagglePreparedTrainingSubmissionRequest
    {
        public string JobId { get; init; } = Guid.NewGuid().ToString("N");
        public string ProjectRoot { get; init; } = "";
        public string DatasetVersionId { get; init; } = "";
        public string DatasetDirectory { get; init; } = "";
        public string ProjectName { get; init; } = "Insight";
        public string KaggleUsername { get; init; } = "";
        public string DatasetSlug { get; init; } = "";
        public string KernelSlug { get; init; } = "";
        public string YoloVersion { get; init; } = "yolov8";
        public string ModelSize { get; init; } = "s";
        public int Epochs { get; init; } = 100;
        public int BatchSize { get; init; } = 16;
        public int ImgSize { get; init; } = 640;
        public int Patience { get; init; } = 50;
        public int Workers { get; init; } = 8;
        public string GpuIndex { get; init; } = "0";
        public IReadOnlyList<string> Classes { get; init; } = Array.Empty<string>();
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
        public string DatasetDirectory { get; init; } = "";
        public string KernelDirectory { get; init; } = "";
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
