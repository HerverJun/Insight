namespace Insight.Training.Providers
{
    public interface ITrainingJobObserver
    {
        void Log(string message, string type = "info");
        void Metric(TrainingMetricUpdate update);
        void Artifact(TrainingArtifact artifact);
    }
}
