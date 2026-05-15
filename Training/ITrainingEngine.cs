namespace Insight.Training
{
    public interface ITrainingEngine
    {
        TrainingProfile Profile { get; }
        TrainingLaunchPlan BuildLaunchPlan(TrainingRequest request);
        TrainingMetricUpdate? TryParseMetrics(string cleanOutputLine);
        string? FindBestArtifact(string workDir, DateTime trainingStartTime);
        string BuildExportArguments(string artifactPath);
    }
}
