namespace Insight.Infrastructure.Artifacts
{
    public interface IArtifactStore
    {
        Task<ArtifactRecord> PutFileAsync(
            string sourcePath,
            string kind,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken);

        Task<ArtifactRecord?> GetAsync(string artifactId, CancellationToken cancellationToken);
    }
}
