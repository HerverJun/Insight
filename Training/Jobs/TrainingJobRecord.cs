using Insight.Training.Providers;

namespace Insight.Training.Jobs
{
    public sealed class TrainingJobRecord
    {
        public string JobId { get; init; } = Guid.NewGuid().ToString("N");
        public string ProviderId { get; set; } = "";
        public TrainingProviderJobState State { get; set; } = TrainingProviderJobState.Queued;
        public string ProjectRoot { get; set; } = "";
        public string DatasetVersionId { get; set; } = "";
        public string ExternalJobId { get; set; } = "";
        public string ArtifactRoot { get; set; } = "";
        public string FailureReason { get; set; } = "";
        public DateTime CreatedAt { get; init; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
