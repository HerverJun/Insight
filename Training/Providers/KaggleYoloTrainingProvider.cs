using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Insight.Services.Cloud.Kaggle;

namespace Insight.Training.Providers
{
    public sealed class KaggleYoloTrainingProvider : ITrainingProvider
    {
        public const string ProviderId = "kaggle-yolo";

        private readonly IKaggleClient _client;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

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

                return await MonitorAndDownloadAsync(request.JobId, submitted, options, observer, cancellationToken);
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

        public async Task<TrainingProviderJobResult> RecoverAsync(
            TrainingProviderJobRequest request,
            ITrainingJobObserver observer,
            CancellationToken cancellationToken)
        {
            try
            {
                var options = KaggleProviderOptions.From(request);
                if (string.IsNullOrWhiteSpace(options.KernelId))
                {
                    return Failed(request.JobId, "Cannot recover Kaggle job because kernelId is missing.");
                }

                observer.Log($"Recovering Kaggle job state: {options.KernelId}", "info");
                var submitted = new KaggleTrainingSubmissionResult
                {
                    JobId = request.JobId,
                    DatasetId = options.DatasetId,
                    KernelId = options.KernelId,
                    KernelUrl = $"https://www.kaggle.com/code/{options.KernelId}",
                    JobRoot = options.JobRoot,
                    DatasetDirectory = options.DatasetDirectory,
                    KernelDirectory = options.KernelDirectory,
                    Submitted = true,
                    Message = "Recovered from persisted Insight job state."
                };

                return await MonitorAndDownloadAsync(request.JobId, submitted, options, observer, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new TrainingProviderJobResult
                {
                    JobId = request.JobId,
                    State = TrainingProviderJobState.Stopped,
                    FailureReason = "Kaggle recovery was canceled."
                };
            }
            catch (Exception ex)
            {
                return Failed(request.JobId, ex.Message);
            }
        }

        private async Task<TrainingProviderJobResult> MonitorAndDownloadAsync(
            string jobId,
            KaggleTrainingSubmissionResult submitted,
            KaggleProviderOptions options,
            ITrainingJobObserver observer,
            CancellationToken cancellationToken)
        {
            var terminal = await PollUntilTerminalAsync(submitted.KernelId, options, observer, cancellationToken);
            if (terminal.State == TrainingProviderJobState.Stopped)
            {
                return new TrainingProviderJobResult
                {
                    JobId = jobId,
                    State = TrainingProviderJobState.Stopped,
                    FailureReason = "Kaggle training job was canceled.",
                    Metadata = BuildMetadata(submitted, terminal.StatusMessage, options.OutputDirectory)
                };
            }

            if (terminal.State == TrainingProviderJobState.Failed)
            {
                return Failed(
                    jobId,
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
            var manifestPath = WriteArtifactManifest(jobId, submitted.KernelId, outputDirectory, artifacts);
            artifacts.Add(new TrainingArtifact { Path = manifestPath, Format = "manifest" });

            foreach (var artifact in artifacts)
            {
                observer.Artifact(artifact);
            }

            var validation = ValidateArtifacts(artifacts, manifestPath);
            var metadata = BuildMetadata(submitted, terminal.StatusMessage, outputDirectory);
            metadata["artifactManifestPath"] = manifestPath;
            foreach (var pair in validation.Checksums)
            {
                metadata[pair.Key] = pair.Value;
            }

            if (!validation.Success)
            {
                return Failed(jobId, validation.Message, metadata);
            }

            return new TrainingProviderJobResult
            {
                JobId = jobId,
                State = TrainingProviderJobState.Completed,
                ArtifactPath = validation.BestPtPath,
                Artifacts = artifacts,
                Metadata = metadata
            };
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

                if (IsCompleted(status))
                {
                    return new PollResult(TrainingProviderJobState.Completed, status.Message);
                }

                if (IsFailed(status))
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

        private static string WriteArtifactManifest(
            string jobId,
            string kernelId,
            string outputDirectory,
            List<TrainingArtifact> artifacts)
        {
            var manifest = new KaggleArtifactManifest
            {
                JobId = jobId,
                KernelId = kernelId,
                OutputDirectory = outputDirectory,
                CreatedAtUtc = DateTime.UtcNow,
                Artifacts = artifacts
                    .Where(x => !string.IsNullOrWhiteSpace(x.Path) && File.Exists(x.Path))
                    .Select(x => new KaggleArtifactManifestItem
                    {
                        Path = x.Path,
                        RelativePath = Path.GetRelativePath(outputDirectory, x.Path),
                        Format = x.Format,
                        Length = new FileInfo(x.Path).Length,
                        Sha256 = ComputeSha256(x.Path)
                    })
                    .OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            var manifestPath = Path.Combine(outputDirectory, "insight-kaggle-artifacts.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
            return manifestPath;
        }

        private static ArtifactValidationResult ValidateArtifacts(
            IReadOnlyList<TrainingArtifact> artifacts,
            string manifestPath)
        {
            var bestPt = artifacts.FirstOrDefault(x => x.Format == "pt")?.Path ?? "";
            var onnx = artifacts.FirstOrDefault(x => x.Format == "onnx")?.Path ?? "";
            var report = artifacts.FirstOrDefault(x => x.Format is "metrics" or "report" or "report-image")?.Path ?? "";
            if (string.IsNullOrWhiteSpace(bestPt) || !File.Exists(bestPt))
            {
                return ArtifactValidationResult.Fail("Kaggle output download completed, but best.pt was not found.");
            }

            if (string.IsNullOrWhiteSpace(onnx) || !File.Exists(onnx))
            {
                return ArtifactValidationResult.Fail("Kaggle output download completed, but an ONNX artifact was not found.");
            }

            if (string.IsNullOrWhiteSpace(report) || !File.Exists(report))
            {
                return ArtifactValidationResult.Fail("Kaggle output download completed, but no metrics or report artifact was found.");
            }

            var manifestJson = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<KaggleArtifactManifest>(manifestJson, JsonOptions);
            if (manifest == null || manifest.Artifacts.Count == 0)
            {
                return ArtifactValidationResult.Fail("Kaggle artifact manifest is empty or invalid.");
            }

            foreach (var item in manifest.Artifacts)
            {
                if (!File.Exists(item.Path))
                {
                    return ArtifactValidationResult.Fail($"Kaggle artifact manifest references a missing file: {item.RelativePath}");
                }

                var info = new FileInfo(item.Path);
                if (info.Length != item.Length)
                {
                    return ArtifactValidationResult.Fail($"Kaggle artifact length mismatch: {item.RelativePath}");
                }

                var sha = ComputeSha256(item.Path);
                if (!sha.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return ArtifactValidationResult.Fail($"Kaggle artifact checksum mismatch: {item.RelativePath}");
                }
            }

            return ArtifactValidationResult.Ok(
                bestPt,
                onnx,
                report,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["bestPtSha256"] = ComputeSha256(bestPt),
                    ["onnxSha256"] = ComputeSha256(onnx),
                    ["reportSha256"] = ComputeSha256(report)
                });
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

        private static bool IsCompleted(KaggleTrainingJobStatus status)
        {
            return status.KernelState == KaggleKernelState.Completed ||
                status.State.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
                status.State.Equals("Complete", StringComparison.OrdinalIgnoreCase) ||
                status.State.Equals("Succeeded", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFailed(KaggleTrainingJobStatus status)
        {
            return status.KernelState is KaggleKernelState.Failed or KaggleKernelState.Canceled or KaggleKernelState.TimedOut ||
                status.State.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                status.State.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
                status.State.Equals("TimedOut", StringComparison.OrdinalIgnoreCase) ||
                status.State.Equals("Canceled", StringComparison.OrdinalIgnoreCase) ||
                status.State.Equals("Cancelled", StringComparison.OrdinalIgnoreCase);
        }

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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

        private sealed class ArtifactValidationResult
        {
            public bool Success { get; init; }
            public string Message { get; init; } = "";
            public string BestPtPath { get; init; } = "";
            public string OnnxPath { get; init; } = "";
            public string ReportPath { get; init; } = "";
            public Dictionary<string, string> Checksums { get; init; } = new(StringComparer.OrdinalIgnoreCase);

            public static ArtifactValidationResult Ok(
                string bestPtPath,
                string onnxPath,
                string reportPath,
                Dictionary<string, string> checksums)
            {
                return new ArtifactValidationResult
                {
                    Success = true,
                    BestPtPath = bestPtPath,
                    OnnxPath = onnxPath,
                    ReportPath = reportPath,
                    Checksums = checksums
                };
            }

            public static ArtifactValidationResult Fail(string message)
            {
                return new ArtifactValidationResult
                {
                    Success = false,
                    Message = message
                };
            }
        }

        private sealed class KaggleProviderOptions
        {
            public string ProjectName { get; init; } = "Insight";
            public string KaggleUsername { get; init; } = "";
            public string DatasetSlug { get; init; } = "";
            public string KernelSlug { get; init; } = "";
            public string DatasetId { get; init; } = "";
            public string KernelId { get; init; } = "";
            public string JobRoot { get; init; } = "";
            public string DatasetDirectory { get; init; } = "";
            public string KernelDirectory { get; init; } = "";
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
                    DatasetId = Get(options, "datasetId", ""),
                    KernelId = Get(options, "kernelId", ""),
                    JobRoot = Get(options, "jobRoot", ""),
                    DatasetDirectory = datasetDirectory,
                    KernelDirectory = Get(options, "kernelDirectory", ""),
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
