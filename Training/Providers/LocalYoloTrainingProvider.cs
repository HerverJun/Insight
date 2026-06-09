using Insight.Infrastructure.Processes;

namespace Insight.Training.Providers
{
    public sealed class LocalYoloTrainingProvider : ITrainingProvider
    {
        public const string ProviderId = "local-yolo";

        private readonly ITrainingEngine _engine;
        private readonly IProcessRunner _processRunner;

        public LocalYoloTrainingProvider(ITrainingEngine engine, IProcessRunner processRunner)
        {
            _engine = engine;
            _processRunner = processRunner;
        }

        public TrainingProviderDescriptor Descriptor { get; } = new()
        {
            Id = ProviderId,
            DisplayName = "Local YOLO",
            Kind = TrainingProviderKind.LocalProcess,
            Capabilities = new TrainingProviderCapabilities
            {
                SupportsResume = true,
                SupportsStop = true,
                SupportsArtifactDownload = false,
                RequiresSecrets = false
            }
        };

        public async Task<TrainingProviderJobResult> StartAsync(
            TrainingProviderJobRequest request,
            ITrainingJobObserver observer,
            CancellationToken cancellationToken)
        {
            var launchPlan = CreateLaunchPlan(request);

            observer.Log($"Starting provider {Descriptor.Id}", "info");
            var startedAt = DateTime.Now;
            var result = await _processRunner.RunAsync(
                CreateSpec(launchPlan),
                line => ObserveLine(line, observer),
                cancellationToken);

            if (result.WasCanceled)
            {
                return new TrainingProviderJobResult
                {
                    JobId = request.JobId,
                    State = TrainingProviderJobState.Stopped,
                    ExitCode = result.ExitCode,
                    FailureReason = "Training job was canceled."
                };
            }

            if (!result.Succeeded)
            {
                return new TrainingProviderJobResult
                {
                    JobId = request.JobId,
                    State = TrainingProviderJobState.Failed,
                    ExitCode = result.ExitCode,
                    FailureReason = $"Process exited with code {result.ExitCode?.ToString() ?? "unknown"}."
                };
            }

            var artifact = _engine.FindBestArtifact(launchPlan.WorkingDirectory, startedAt) ?? "";
            if (!string.IsNullOrWhiteSpace(artifact))
            {
                observer.Artifact(new TrainingArtifact { Path = artifact, Format = "pt" });
            }

            var artifacts = string.IsNullOrWhiteSpace(artifact)
                ? new List<TrainingArtifact>()
                : new List<TrainingArtifact> { new() { Path = artifact, Format = "pt" } };

            return new TrainingProviderJobResult
            {
                JobId = request.JobId,
                State = string.IsNullOrWhiteSpace(artifact)
                    ? TrainingProviderJobState.Failed
                    : TrainingProviderJobState.Completed,
                ExitCode = result.ExitCode,
                ArtifactPath = artifact,
                Artifacts = artifacts,
                FailureReason = string.IsNullOrWhiteSpace(artifact) ? "Training completed but best.pt was not found." : ""
            };
        }

        public Task<TrainingProviderJobResult> RecoverAsync(
            TrainingProviderJobRequest request,
            ITrainingJobObserver observer,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new TrainingProviderJobResult
            {
                JobId = request.JobId,
                State = TrainingProviderJobState.Failed,
                FailureReason = "Local YOLO process jobs cannot be recovered after application restart."
            });
        }

        private TrainingLaunchPlan CreateLaunchPlan(TrainingProviderJobRequest request)
        {
            if (request.ProviderOptions.TryGetValue("arguments", out var arguments) &&
                !string.IsNullOrWhiteSpace(arguments))
            {
                return new TrainingLaunchPlan
                {
                    PythonPath = string.IsNullOrWhiteSpace(request.PythonPath) ? "python" : request.PythonPath,
                    Arguments = arguments,
                    WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkDir)
                        ? AppDomain.CurrentDomain.BaseDirectory
                        : request.WorkDir
                };
            }

            return _engine.BuildLaunchPlan(new TrainingRequest
            {
                PythonPath = request.PythonPath,
                WorkDir = request.WorkDir,
                Params = request.Params
            });
        }

        private static ProcessStartSpec CreateSpec(TrainingLaunchPlan launchPlan)
        {
            return new ProcessStartSpec
            {
                FileName = launchPlan.PythonPath,
                Arguments = launchPlan.Arguments,
                WorkingDirectory = launchPlan.WorkingDirectory,
                EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PYTHONIOENCODING"] = "utf-8",
                    ["KMP_DUPLICATE_LIB_OK"] = "TRUE"
                }
            };
        }

        private void ObserveLine(string line, ITrainingJobObserver observer)
        {
            observer.Log(line);
            var update = _engine.TryParseMetrics(line);
            if (update != null)
            {
                observer.Metric(update);
            }
        }
    }
}
