namespace Insight.Services.Cloud.Kaggle
{
    public enum KaggleConnectionErrorKind
    {
        None,
        CliUnavailable,
        CredentialsMissing,
        Unauthorized,
        Network,
        RateLimited,
        Unknown
    }

    public enum KaggleKernelState
    {
        Unknown,
        Submitted,
        Queued,
        Running,
        Completed,
        Failed,
        Canceled,
        TimedOut
    }

    public sealed class KaggleRetryPolicy
    {
        public int MaxAttempts { get; init; } = 3;
        public int DelayMilliseconds { get; init; } = 500;
    }

    public sealed class KaggleConnectionTestRequest
    {
        public string Username { get; init; } = "";
        public string ApiKey { get; init; } = "";
        public Action<string, string>? OnLog { get; init; }
    }

    public sealed class KaggleConnectionTestResult
    {
        public bool Success { get; init; }
        public string CliVersion { get; init; } = "";
        public string Message { get; init; } = "";
        public KaggleConnectionErrorKind ErrorKind { get; init; }
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
        public KaggleRetryPolicy RetryPolicy { get; init; } = new();
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
        public KaggleRetryPolicy RetryPolicy { get; init; } = new();
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
        public KaggleKernelState KernelState { get; init; } = KaggleKernelState.Unknown;
        public double Progress { get; init; }
        public string Message { get; init; } = "";
        public bool IsTerminal => KernelState is KaggleKernelState.Completed or
            KaggleKernelState.Failed or
            KaggleKernelState.Canceled or
            KaggleKernelState.TimedOut;
    }

    public sealed class KaggleOutputDownloadRequest
    {
        public string KernelId { get; init; } = "";
        public string OutputDirectory { get; init; } = "";
        public Action<string, string>? OnLog { get; init; }
    }

    public sealed class KaggleArtifactManifest
    {
        public string JobId { get; set; } = "";
        public string KernelId { get; set; } = "";
        public string OutputDirectory { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public List<KaggleArtifactManifestItem> Artifacts { get; set; } = new();
    }

    public sealed class KaggleArtifactManifestItem
    {
        public string Path { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string Format { get; set; } = "";
        public long Length { get; set; }
        public string Sha256 { get; set; } = "";
    }
}
