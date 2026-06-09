using System.Globalization;
using Insight.Services.Cloud.Kaggle;

namespace Insight.Training.Providers
{
    public sealed class KaggleYoloTrainingProvider : ITrainingProvider
    {
        public const string ProviderId = "kaggle-yolo";

        private readonly IKaggleClient _client;

        public KaggleYoloTrainingProvider(IKaggleClient client)
        {
            _client = client;
        }

        public TrainingProviderDescriptor Descriptor { get; } = new()
        {
            Id = ProviderId,
            DisplayName = "Kaggle YOLO",
            Kind = TrainingProviderKind.CloudApi,
            Capabilities = new TrainingProviderCapabilities
            {
                SupportsResume = false,
                SupportsStop = true,
                SupportsArtifactDownload = true,
                RequiresSecrets = true
            }
        };

        public async Task<TrainingProviderJobResult> StartAsync(
            TrainingProviderJobRequest request,
            ITrainingJobObserver observer,
            CancellationToken cancellationToken)
        {
            try
            {
                var options = KaggleProviderOptions.From(request);
                observer.Log("Testing Kaggle CLI credentials...", "info");
                var connection = await _client.TestConnectionAsync(
                    new KaggleConnectionTestRequest
                    {
                        Username = options.KaggleUsername,
                        OnLog = observer.Log
                    },
                    cancellationToken);

                if (!connection.Success)
                {
                    return Failed(request.JobId, connection.Message);
                }

                observer.Log("Submitting Kaggle cloud training job...", "info");
                var submitted = await _client.SubmitPreparedTrainingAsync(
                    new KagglePreparedTrainingSubmissionRequest
                    {
                        JobId = request.JobId,
                        ProjectRoot = request.ProjectRoot,
                        DatasetVersionId = request.DatasetVersionId,
                        DatasetDirectory = options.DatasetDirectory,
                        ProjectName = options.ProjectName,
                        KaggleUsername = options.KaggleUsername,
                        DatasetSlug = options.DatasetSlug,
                        KernelSlug = options.KernelSlug,
                        YoloVersion = options.YoloVersion,
                        ModelSize = request.Params.ModelSize,
                        Epochs = request.Params.Epochs,
                        BatchSize = request.Params.BatchSize,
                        ImgSize = request.Params.ImgSize,
                        Patience = request.Params.Patience,
                        Workers = request.Params.Workers,
                        GpuIndex = request.Params.GpuIndex,
                        Classes = request.Params.Classes,
                        OnLog = observer.Log,
                        OnProgress = progress => observer.Log($"Kaggle submission progress: {progress}%", "info")
                    },
                    cancellationToken);

                var terminal = await PollUntilTerminalAsync(submitted.KernelId, options, observer, cancellationToken);
                if (terminal.State == TrainingProviderJobState.Stopped)
                {
                    return new TrainingProviderJobResult
                    {
                        JobId = request.JobId,
                        State = TrainingProviderJobState.Stopped,
                        FailureReason = "Kaggle training job was canceled.",
                        Metadata = BuildMetadata(submitted, terminal.StatusMessage, options.OutputDirectory)
                    };
                }

                if (terminal.State == TrainingProviderJobState.Failed)
                {
                    return Failed(
                        request.JobId,
                        string.IsNullOrWhiteSpace(terminal.StatusMessage)
                            ? "Kaggle training failed."
                            : terminal.StatusMessage,
                        BuildMetadata(submitted, terminal.StatusMessage, options.OutputDirectory));
                }

                observer.Log("Downloading Kaggle output artifacts...", "info");
                var outputDirectory = await _client.DownloadOutputAsync(
                    new KaggleOutputDownloadRequest
                    {
                        KernelId = submitted.KernelId,
                        OutputDirectory = options.OutputDirectory,
                        OnLog = observer.Log
                    },
                    cancellationToken);

                var artifacts = DiscoverArtifacts(outputDirectory);
                foreach (var artifact in artifacts)
                {
                    observer.Artifact(artifact);
                }

                var bestPt = artifacts.FirstOrDefault(x => x.Format == "pt")?.Path ?? "";
                var onnx = artifacts.FirstOrDefault(x => x.Format == "onnx")?.Path ?? "";
                if (string.IsNullOrWhiteSpace(bestPt))
                {
                    return Failed(
                        request.JobId,
                        "Kaggle output download completed, but best.pt was not found.",
                        BuildMetadata(submitted, terminal.StatusMessage, outputDirectory));
                }

                if (string.IsNullOrWhiteSpace(onnx))
                {
                    return Failed(
                        request.JobId,
                        "Kaggle output download completed, but an ONNX artifact was not found.",
                        BuildMetadata(submitted, terminal.StatusMessage, outputDirectory));
                }

                return new TrainingProviderJobResult
                {
                    JobId = request.JobId,
                    State = TrainingProviderJobState.Completed,
                    ArtifactPath = bestPt,
                    Artifacts = artifacts,
                    Metadata = BuildMetadata(submitted, terminal.StatusMessage, outputDirectory)
                };
            }
            catch (OperationCanceledException)
            {
                return new TrainingProviderJobResult
                {
                    JobId = request.JobId,
                    State = TrainingProviderJobState.Stopped,
                    FailureReason = "Kaggle training job was canceled."
                };
            }
            catch (Exception ex)
            {
                return Failed(request.JobId, ex.Message);
            }
        }

        private async Task<PollResult> PollUntilTerminalAsync(
            string kernelId,
            KaggleProviderOptions options,
            ITrainingJobObserver observer,
            CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.Add(options.PollTimeout);
            while (DateTime.UtcNow <= deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var status = await _client.GetTrainingStatusAsync(kernelId, cancellationToken);
                observer.Log($"Kaggle kernel status: {status.State} {status.Message}".Trim(), "info");

                if (IsCompleted(status.State))
                {
                    return new PollResult(TrainingProviderJobState.Completed, status.Message);
                }

                if (IsFailed(status.State))
                {
                    return new PollResult(TrainingProviderJobState.Failed, status.Message);
                }

                if (options.PollInterval > TimeSpan.Zero)
                {
                    await Task.Delay(options.PollInterval, cancellationToken);
                }
            }

            return new PollResult(
                TrainingProviderJobState.Failed,
                $"Kaggle training did not finish before timeout ({options.PollTimeout.TotalMinutes:0.#} minutes).");
        }

        private static List<TrainingArtifact> DiscoverArtifacts(string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
            {
                return new List<TrainingArtifact>();
            }

            var artifacts = new List<TrainingArtifact>();
            AddNewest(artifacts, outputDirectory, "best.pt", "pt");
            AddNewest(artifacts, outputDirectory, "last.pt", "pt-last");
            AddNewest(artifacts, outputDirectory, "*.onnx", "onnx");
            AddNewest(artifacts, outputDirectory, "results.csv", "metrics");
            AddNewest(artifacts, outputDirectory, "results.png", "report-image");
            AddNewest(artifacts, outputDirectory, "*.json", "report");
            return artifacts;
        }

        private static void AddNewest(List<TrainingArtifact> artifacts, string root, string pattern, string format)
        {
            var path = Directory.GetFiles(root, pattern, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(path) &&
                !artifacts.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                artifacts.Add(new TrainingArtifact { Path = path, Format = format });
            }
        }

        private static bool IsCompleted(string state)
        {
            return state.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("Complete", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("Succeeded", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFailed(string state)
        {
            return state.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("Canceled", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("Cancelled", StringComparison.OrdinalIgnoreCase);
        }

        private static TrainingProviderJobResult Failed(
            string jobId,
            string reason,
            Dictionary<string, string>? metadata = null)
        {
            return new TrainingProviderJobResult
            {
                JobId = jobId,
                State = TrainingProviderJobState.Failed,
                FailureReason = reason,
                Metadata = metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
        }

        private static Dictionary<string, string> BuildMetadata(
            KaggleTrainingSubmissionResult submitted,
            string statusMessage,
            string outputDirectory)
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["datasetId"] = submitted.DatasetId,
                ["kernelId"] = submitted.KernelId,
                ["kernelUrl"] = submitted.KernelUrl,
                ["jobRoot"] = submitted.JobRoot,
                ["datasetDirectory"] = submitted.DatasetDirectory,
                ["kernelDirectory"] = submitted.KernelDirectory,
                ["outputDirectory"] = outputDirectory,
                ["statusMessage"] = statusMessage
            };
            return metadata;
        }

        private readonly record struct PollResult(TrainingProviderJobState State, string StatusMessage);

        private sealed class KaggleProviderOptions
        {
            public string ProjectName { get; init; } = "Insight";
            public string KaggleUsername { get; init; } = "";
            public string DatasetSlug { get; init; } = "";
            public string KernelSlug { get; init; } = "";
            public string DatasetDirectory { get; init; } = "";
            public string OutputDirectory { get; init; } = "";
            public string YoloVersion { get; init; } = "yolov8";
            public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
            public TimeSpan PollTimeout { get; init; } = TimeSpan.FromHours(8);

            public static KaggleProviderOptions From(TrainingProviderJobRequest request)
            {
                var options = request.ProviderOptions;
                var projectName = Get(options, "projectName", "Insight");
                var datasetDirectory = Get(options, "datasetDirectory", "");
                if (string.IsNullOrWhiteSpace(datasetDirectory))
                {
                    datasetDirectory = Path.Combine(
                        Path.GetFullPath(request.ProjectRoot),
                        ".insight",
                        "datasets",
                        request.DatasetVersionId,
                        "yolo");
                }

                var outputDirectory = Get(options, "outputDirectory", "");
                if (string.IsNullOrWhiteSpace(outputDirectory))
                {
                    outputDirectory = Path.Combine(request.WorkDir, "kaggle_output");
                }

                return new KaggleProviderOptions
                {
                    ProjectName = projectName,
                    KaggleUsername = Require(options, "kaggleUsername"),
                    DatasetSlug = Require(options, "datasetSlug"),
                    KernelSlug = Require(options, "kernelSlug"),
                    DatasetDirectory = datasetDirectory,
                    OutputDirectory = outputDirectory,
                    YoloVersion = Get(options, "yoloVersion", "yolov8"),
                    PollInterval = TimeSpan.FromSeconds(GetDouble(options, "pollIntervalSeconds", 30)),
                    PollTimeout = TimeSpan.FromMinutes(GetDouble(options, "pollTimeoutMinutes", 480))
                };
            }

            private static string Require(Dictionary<string, string> options, string key)
            {
                var value = Get(options, key, "");
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException($"Kaggle provider option '{key}' is required.");
                }

                return value;
            }

            private static string Get(Dictionary<string, string> options, string key, string fallback)
            {
                return options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                    ? value.Trim()
                    : fallback;
            }

            private static double GetDouble(Dictionary<string, string> options, string key, double fallback)
            {
                if (options.TryGetValue(key, out var value) &&
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    return Math.Max(0, parsed);
                }

                return fallback;
            }
        }
    }
}
