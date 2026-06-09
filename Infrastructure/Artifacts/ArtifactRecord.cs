namespace Insight.Infrastructure.Artifacts
{
    public sealed class ArtifactRecord
    {
        public string ArtifactId { get; init; } = Guid.NewGuid().ToString("N");
        public string Kind { get; init; } = "";
        public string Path { get; init; } = "";
        public long SizeBytes { get; init; }
        public string Sha256 { get; init; } = "";
        public DateTime CreatedAt { get; init; } = DateTime.Now;
        public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
