namespace Insight.Training.Jobs
{
    public interface ITrainingJobStore
    {
        Task UpsertAsync(TrainingJobRecord record, CancellationToken cancellationToken);
        Task<TrainingJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken);
        Task<IReadOnlyList<TrainingJobRecord>> ListAsync(CancellationToken cancellationToken);
    }
}
