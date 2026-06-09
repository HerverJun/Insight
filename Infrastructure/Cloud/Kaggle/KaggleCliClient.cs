using Insight.Infrastructure.Processes;
using Insight.Services.Cloud.Kaggle;
using Insight.Services.Configuration;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Insight.Infrastructure.Cloud.Kaggle
{
    public sealed class KaggleCliClient : IKaggleClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly IProcessRunner _processRunner;
        private readonly InsightAppPaths _paths;

        public KaggleCliClient(IProcessRunner processRunner, InsightAppPaths paths)
        {
            _processRunner = processRunner;
            _paths = paths;
        }

        public async Task<KaggleConnectionTestResult> TestConnectionAsync(
            KaggleConnectionTestRequest request,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_paths.CloudJobsRoot);
            var versionLines = new List<string>();
            var version = await RunKaggleAsync(
                new[] { "--version" },
                _paths.CloudJobsRoot,
                versionLines.Add,
                cancellationToken);

            if (!version.Succeeded)
            {
                var message = string.IsNullOrWhiteSpace(version.Output)
                    ? "Kaggle CLI is not available or failed to start."
                    : version.Output;
                request.OnLog?.Invoke(message, "error");
                return new KaggleConnectionTestResult
                {
                    Success = false,
                    Message = message
                };
            }

            var authLines = new List<string>();
            var search = string.IsNullOrWhiteSpace(request.Username) ? "insight" : request.Username.Trim();
            var auth = await RunKaggleAsync(
                new[] { "datasets", "list", "-s", search, "-p", "1" },
                _paths.CloudJobsRoot,
                authLines.Add,
                cancellationToken);

            var cliVersion = string.Join(Environment.NewLine, versionLines).Trim();
            if (!auth.Succeeded)
            {
                var message = string.IsNullOrWhiteSpace(auth.Output)
                    ? "Kaggle credentials could not be verified."
                    : auth.Output;
                request.OnLog?.Invoke(message, "error");
                return new KaggleConnectionTestResult
                {
                    Success = false,
                    CliVersion = cliVersion,
                    Message = message
                };
            }

            var ok = string.IsNullOrWhiteSpace(cliVersion)
                ? "Kaggle CLI and credentials verified."
                : $"Kaggle CLI and credentials verified: {cliVersion}";
            request.OnLog?.Invoke(ok, "success");
            return new KaggleConnectionTestResult
            {
                Success = true,
                CliVersion = cliVersion,
                Message = ok
            };
        }

        public async Task<KaggleTrainingSubmissionResult> SubmitTrainingAsync(
            KaggleTrainingSubmissionRequest request,
            CancellationToken cancellationToken)
        {
            var result = await KaggleCloudTrainer.StartAsync(
                request.Options,
                request.OnLog,
                request.OnProgress,
                _processRunner,
                _paths,
                cancellationToken);

            return new KaggleTrainingSubmissionResult
            {
                JobId = request.JobId,
                DatasetId = result.DatasetId,
                KernelId = result.KernelId,
                KernelUrl = $"https://www.kaggle.com/code/{result.KernelId}",
                JobRoot = result.JobRoot,
                Submitted = true,
                Message = "Kaggle training submitted."
            };
        }

        public async Task<KaggleTrainingSubmissionResult> SubmitPreparedTrainingAsync(
            KagglePreparedTrainingSubmissionRequest request,
            CancellationToken cancellationToken)
        {
            ValidatePreparedSubmission(request);

            var username = Slugify(request.KaggleUsername);
            var datasetSlug = Slugify(request.DatasetSlug);
            var kernelSlug = Slugify(request.KernelSlug);
            var datasetId = $"{username}/{datasetSlug}";
            var kernelId = $"{username}/{kernelSlug}";
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var jobRoot = Path.Combine(_paths.CloudJobsRoot, "kaggle", $"{datasetSlug}_{timestamp}_{request.JobId}");
            var datasetDir = Path.Combine(jobRoot, "dataset");
            var kernelDir = Path.Combine(jobRoot, "kernel");

            Directory.CreateDirectory(datasetDir);
            Directory.CreateDirectory(kernelDir);

            request.OnLog?.Invoke("Staging Kaggle dataset from the selected data version...", "info");
            CopyDirectory(request.DatasetDirectory, datasetDir);
            WriteKaggleDataYaml(Path.Combine(datasetDir, "data.yaml"), request.Classes);
            WriteTrainingConfig(datasetDir, request);
            WriteDatasetMetadata(datasetDir, datasetId, $"{request.ProjectName} {request.DatasetVersionId} YOLO Dataset");
            request.OnProgress?.Invoke(20);

            request.OnLog?.Invoke($"Uploading Kaggle Dataset: {datasetId}", "info");
            var create = await RunKaggleAsync(
                new[] { "datasets", "create", "-p", datasetDir, "--dir-mode", "zip", "-q" },
                datasetDir,
                line => request.OnLog?.Invoke(line, "info"),
                cancellationToken);

            if (!create.Succeeded)
            {
                request.OnLog?.Invoke("Dataset exists or create failed; publishing a new dataset version...", "warning");
                var version = await RunKaggleAsync(
                    new[] { "datasets", "version", "-p", datasetDir, "-m", $"Insight upload {timestamp}", "--dir-mode", "zip", "-q" },
                    datasetDir,
                    line => request.OnLog?.Invoke(line, "info"),
                    cancellationToken);
                if (!version.Succeeded)
                {
                    throw new InvalidOperationException($"Kaggle dataset upload failed: {version.Output}");
                }
            }

            request.OnProgress?.Invoke(55);
            request.OnLog?.Invoke($"Creating or updating Kaggle Notebook: {kernelId}", "info");
            WriteKernelFiles(kernelDir, kernelId, request.ProjectName, datasetId);
            var push = await RunKaggleAsync(
                new[] { "kernels", "push", "-p", kernelDir },
                kernelDir,
                line => request.OnLog?.Invoke(line, "info"),
                cancellationToken);

            if (!push.Succeeded)
            {
                throw new InvalidOperationException($"Kaggle kernel push failed: {push.Output}");
            }

            request.OnProgress?.Invoke(90);
            await RunKaggleAsync(
                new[] { "kernels", "status", kernelId },
                kernelDir,
                line => request.OnLog?.Invoke(line, "info"),
                cancellationToken);

            request.OnProgress?.Invoke(100);
            return new KaggleTrainingSubmissionResult
            {
                JobId = request.JobId,
                DatasetId = datasetId,
                KernelId = kernelId,
                KernelUrl = $"https://www.kaggle.com/code/{kernelId}",
                JobRoot = jobRoot,
                DatasetDirectory = datasetDir,
                KernelDirectory = kernelDir,
                Submitted = true,
                Message = "Kaggle training submitted."
            };
        }

        public async Task<KaggleTrainingJobStatus> GetTrainingStatusAsync(
            string kernelId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(kernelId) || !kernelId.Contains('/'))
            {
                throw new ArgumentException("Kernel ID must use username/kernel-slug format.", nameof(kernelId));
            }

            var output = new List<string>();
            var result = await RunKaggleAsync(
                new[] { "kernels", "status", kernelId },
                _paths.CloudJobsRoot,
                output.Add,
                cancellationToken);

            var message = string.Join(Environment.NewLine, output);
            var state = ParseKernelState(message, result.Succeeded);
            return new KaggleTrainingJobStatus
            {
                KernelId = kernelId,
                State = state,
                Progress = StateProgress(state),
                Message = message
            };
        }

        public Task<string> DownloadOutputAsync(
            KaggleOutputDownloadRequest request,
            CancellationToken cancellationToken)
        {
            return KaggleCloudTrainer.DownloadOutputAsync(
                request.KernelId,
                request.OutputDirectory,
                request.OnLog,
                _processRunner,
                _paths,
                cancellationToken);
        }

        private async Task<ProcessRunResult> RunKaggleAsync(
            IReadOnlyList<string> args,
            string workingDirectory,
            Action<string>? onOutput,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(workingDirectory);
            return await _processRunner.RunAsync(
                new ProcessStartSpec
                {
                    FileName = "kaggle",
                    ArgumentList = args.ToArray(),
                    WorkingDirectory = workingDirectory,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["PYTHONIOENCODING"] = "utf-8"
                    }
                },
                onOutput,
                cancellationToken);
        }

        private static void ValidatePreparedSubmission(KagglePreparedTrainingSubmissionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.DatasetDirectory) || !Directory.Exists(request.DatasetDirectory))
            {
                throw new DirectoryNotFoundException($"Dataset directory not found: {request.DatasetDirectory}");
            }

            if (string.IsNullOrWhiteSpace(request.KaggleUsername))
            {
                throw new ArgumentException("Kaggle username is required.", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.DatasetSlug))
            {
                throw new ArgumentException("Kaggle dataset slug is required.", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.KernelSlug))
            {
                throw new ArgumentException("Kaggle kernel slug is required.", nameof(request));
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }

        private static void WriteKaggleDataYaml(string path, IReadOnlyList<string> classes)
        {
            var names = classes
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();
            var sb = new StringBuilder();
            sb.AppendLine("# YOLO Dataset Configuration");
            sb.AppendLine("# Generated by Insight for Kaggle");
            sb.AppendLine("train: images/train");
            sb.AppendLine("val: images/val");
            if (Directory.Exists(Path.Combine(Path.GetDirectoryName(path)!, "images", "test")))
            {
                sb.AppendLine("test: images/test");
            }
            sb.AppendLine($"nc: {names.Count}");
            sb.AppendLine("names:");
            for (var i = 0; i < names.Count; i++)
            {
                sb.AppendLine($"  {i}: {YamlQuote(names[i])}");
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static void WriteTrainingConfig(string datasetDir, KagglePreparedTrainingSubmissionRequest request)
        {
            var (yoloVersion, modelSize) = ResolveYoloFamilyAndSize(request.YoloVersion, request.ModelSize);
            var cfg = new
            {
                yolo_version = yoloVersion,
                model_size = modelSize,
                epochs = Math.Max(1, request.Epochs),
                batch_size = request.BatchSize == 0 ? 16 : request.BatchSize,
                img_size = Math.Max(32, request.ImgSize),
                patience = Math.Max(1, request.Patience),
                workers = Math.Max(0, request.Workers),
                device = string.IsNullOrWhiteSpace(request.GpuIndex) ? "0" : request.GpuIndex,
                data_yaml = "data.yaml",
                dataset_version_id = request.DatasetVersionId,
                job_id = request.JobId,
                exported_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            };

            File.WriteAllText(
                Path.Combine(datasetDir, "training_config.json"),
                JsonSerializer.Serialize(cfg, JsonOptions),
                Encoding.UTF8);
        }

        private static void WriteDatasetMetadata(string datasetDir, string datasetId, string title)
        {
            var metadata = new
            {
                title,
                id = datasetId,
                licenses = new[]
                {
                    new { name = "CC0-1.0" }
                }
            };

            File.WriteAllText(
                Path.Combine(datasetDir, "dataset-metadata.json"),
                JsonSerializer.Serialize(metadata, JsonOptions),
                Encoding.UTF8);
        }

        private static void WriteKernelFiles(string kernelDir, string kernelId, string projectName, string datasetId)
        {
            var notebookSource = FindBundledFile("YOLO_TRAIN_KaggleDataset.ipynb");
            var notebookTarget = Path.Combine(kernelDir, "YOLO_TRAIN_KaggleDataset.ipynb");
            File.Copy(notebookSource, notebookTarget, overwrite: true);

            var metadata = new
            {
                id = kernelId,
                title = $"{projectName} Insight YOLO Training",
                code_file = "YOLO_TRAIN_KaggleDataset.ipynb",
                language = "python",
                kernel_type = "notebook",
                is_private = true,
                enable_gpu = true,
                enable_internet = true,
                dataset_sources = new[] { datasetId }
            };

            File.WriteAllText(
                Path.Combine(kernelDir, "kernel-metadata.json"),
                JsonSerializer.Serialize(metadata, JsonOptions),
                Encoding.UTF8);
        }

        private static string FindBundledFile(string fileName)
        {
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName),
                Path.Combine(Directory.GetCurrentDirectory(), fileName),
                Path.Combine(AppContext.BaseDirectory, fileName)
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException($"Bundled Kaggle notebook template not found: {fileName}");
        }

        private static (string YoloVersion, string ModelSize) ResolveYoloFamilyAndSize(string yoloVersion, string modelSize)
        {
            var size = (modelSize ?? "s").Trim().ToLowerInvariant();
            var family = (yoloVersion ?? "").Trim().ToLowerInvariant();
            if (size.StartsWith("v11", StringComparison.OrdinalIgnoreCase))
            {
                return ("yolo11", size[3..]);
            }
            if (size.StartsWith("v26", StringComparison.OrdinalIgnoreCase))
            {
                return ("yolo26", size[3..]);
            }
            if (size.StartsWith("v8", StringComparison.OrdinalIgnoreCase))
            {
                return ("yolov8", size[2..]);
            }
            if (string.IsNullOrWhiteSpace(family))
            {
                family = "yolov8";
            }
            if (family == "yolov11")
            {
                family = "yolo11";
            }
            return (family, string.IsNullOrWhiteSpace(size) ? "s" : size);
        }

        private static string ParseKernelState(string message, bool commandSucceeded)
        {
            if (!commandSucceeded) return "Unknown";
            var text = message.ToLowerInvariant();
            if (Regex.IsMatch(text, "\\b(complete|completed|success|succeeded)\\b")) return "Completed";
            if (Regex.IsMatch(text, "\\b(error|failed|failure|cancelled|canceled)\\b")) return "Failed";
            if (Regex.IsMatch(text, "\\b(running|executing)\\b")) return "Running";
            if (Regex.IsMatch(text, "\\b(queued|pending)\\b")) return "Queued";
            return "Submitted";
        }

        private static double StateProgress(string state)
        {
            return state switch
            {
                "Completed" => 1,
                "Failed" => 1,
                "Running" => 0.7,
                "Queued" => 0.2,
                "Submitted" => 0.4,
                _ => 0
            };
        }

        private static string Slugify(string value)
        {
            value = (value ?? "").Trim().ToLowerInvariant();
            value = Regex.Replace(value, "[^a-z0-9_-]+", "-");
            value = Regex.Replace(value, "-{2,}", "-").Trim('-');
            return string.IsNullOrWhiteSpace(value) ? "insight-yolo" : value;
        }

        private static string YamlQuote(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }
    }
}
