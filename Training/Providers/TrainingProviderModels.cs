namespace Insight.Training.Providers
{
    public enum TrainingProviderKind
    {
        LocalProcess,
        CloudApi
    }

    public enum TrainingProviderJobState
    {
        Queued,
        Running,
        Completed,
        Failed,
        Stopped
    }

    public sealed class TrainingProviderCapabilities
    {
        public bool SupportsResume { get; init; }
        public bool SupportsStop { get; init; }
        public bool SupportsArtifactDownload { get; init; }
        public bool RequiresSecrets { get; init; }
    }

    public sealed class TrainingProviderDescriptor
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public TrainingProviderKind Kind { get; init; }
        public TrainingProviderCapabilities Capabilities { get; init; } = new();
    }

    public sealed class TrainingProviderJobRequest
    {
        public string JobId { get; init; } = Guid.NewGuid().ToString("N");
        public string ProjectRoot { get; init; } = "";
        public string DatasetVersionId { get; init; } = "";
        public string PythonPath { get; init; } = "python";
        public string WorkDir { get; init; } = "";
        public TrainingParams Params { get; init; } = new();
        public Dictionary<string, string> ProviderOptions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class TrainingProviderJobResult
    {
        public string JobId { get; init; } = "";
        public TrainingProviderJobState State { get; init; }
        public int? ExitCode { get; init; }
        public string ArtifactPath { get; init; } = "";
        public string FailureReason { get; init; } = "";
    }
}
