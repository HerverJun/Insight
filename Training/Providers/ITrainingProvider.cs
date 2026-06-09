namespace Insight.Training.Providers
{
    public interface ITrainingProvider
    {
        TrainingProviderDescriptor Descriptor { get; }

        Task<TrainingProviderJobResult> StartAsync(
            TrainingProviderJobRequest request,
            ITrainingJobObserver observer,
            CancellationToken cancellationToken);

        Task<TrainingProviderJobResult> RecoverAsync(
            TrainingProviderJobRequest request,
            ITrainingJobObserver observer,
            CancellationToken cancellationToken);
    }
}
