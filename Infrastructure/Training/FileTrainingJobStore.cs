using System.Text.Json;
using Insight.Services.Configuration;
using Insight.Training.Jobs;

namespace Insight.Infrastructure.Training
{
    public sealed class FileTrainingJobStore : ITrainingJobStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly string _jobRoot;

        public FileTrainingJobStore(InsightAppPaths paths)
        {
            _jobRoot = Path.Combine(paths.LocalDataRoot, "jobs");
        }

        public async Task UpsertAsync(TrainingJobRecord record, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(record.JobId))
            {
                throw new ArgumentException("Training job id is required.", nameof(record));
            }

            record.UpdatedAt = DateTime.Now;
            Directory.CreateDirectory(_jobRoot);
            var json = JsonSerializer.Serialize(record, JsonOptions);
            await File.WriteAllTextAsync(GetJobPath(record.JobId), json, cancellationToken);
        }

        public async Task<TrainingJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken)
        {
            var path = GetJobPath(jobId);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<TrainingJobRecord>(json, JsonOptions);
        }

        public async Task<IReadOnlyList<TrainingJobRecord>> ListAsync(CancellationToken cancellationToken)
        {
            if (!Directory.Exists(_jobRoot))
            {
                return Array.Empty<TrainingJobRecord>();
            }

            var records = new List<TrainingJobRecord>();
            foreach (var path in Directory.GetFiles(_jobRoot, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var json = await File.ReadAllTextAsync(path, cancellationToken);
                var record = JsonSerializer.Deserialize<TrainingJobRecord>(json, JsonOptions);
                if (record != null)
                {
                    records.Add(record);
                }
            }

            return records
                .OrderByDescending(x => x.UpdatedAt)
                .ToArray();
        }

        private string GetJobPath(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId) || Path.GetFileName(jobId) != jobId)
            {
                throw new ArgumentException("Training job id must be a file-safe name.", nameof(jobId));
            }

            return Path.Combine(_jobRoot, jobId + ".json");
        }
    }
}
