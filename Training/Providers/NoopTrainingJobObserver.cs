namespace Insight.Training.Providers
{
    public sealed class NoopTrainingJobObserver : ITrainingJobObserver
    {
        public static NoopTrainingJobObserver Instance { get; } = new();

        private NoopTrainingJobObserver()
        {
        }

        public void Log(string message, string type = "info")
        {
        }

        public void Metric(TrainingMetricUpdate update)
        {
        }

        public void Artifact(TrainingArtifact artifact)
        {
        }
    }
}
