using System.Text.Json;
using Insight.Bridge;
using Insight.Infrastructure.Processes;
using Insight.Infrastructure.Training;
using Insight.Services.Cloud.Kaggle;
using Insight.Services.Configuration;
using Insight.Training;
using Insight.Training.Jobs;
using Insight.Training.Providers;

namespace Insight.Tests;

public class ArchitectureGovernanceTests
{
    [Fact]
    public void InsightAppPathsKeepsUserConfigAndProjectArtifactsSeparate()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var paths = new InsightAppPaths(
                Path.Combine(tempRoot, "user"),
                Path.Combine(tempRoot, "local"));

            paths.EnsureUserRoots();

            Assert.True(Directory.Exists(paths.ConfigRoot));
            Assert.True(Directory.Exists(paths.CloudJobsRoot));
            Assert.EndsWith(Path.Combine("config", "python_config.json"), paths.PythonConfigPath);
            Assert.EndsWith(Path.Combine("config", "training_history.json"), paths.TrainingHistoryPath);

            var projectRoot = Path.Combine(tempRoot, "project");
            Directory.CreateDirectory(projectRoot);
            Assert.Equal(
                Path.Combine(Path.GetFullPath(projectRoot), ".insight"),
                paths.GetProjectInsightRoot(projectRoot));
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public void WebViewPayloadBinderBindsCaseInsensitivePayloadsAndReportsInvalidShapes()
    {
        using var valid = JsonDocument.Parse("""{"TYPE":"source"}""");
        var payload = WebViewPayloadBinder.Bind<SelectFolderPayload>(valid.RootElement);

        Assert.Equal("source", payload.Type);

        using var invalid = JsonDocument.Parse("""{"count":"not-a-number"}""");
        var ex = Assert.Throws<InvalidOperationException>(
            () => WebViewPayloadBinder.Bind<NumericPayload>(invalid.RootElement));

        Assert.Contains(nameof(NumericPayload), ex.Message);
    }

    [Fact]
    public void WebViewPayloadBinderRunsPayloadValidation()
    {
        using var invalid = JsonDocument.Parse("""{"sourcePaths":["C:/data"],"classes":["scratch"]}""");
        var ex = Assert.Throws<InvalidOperationException>(
            () => WebViewPayloadBinder.Bind<StartKaggleTrainingPayload>(invalid.RootElement));

        Assert.Contains(nameof(StartKaggleTrainingPayload), ex.Message);
        Assert.Contains(nameof(StartKaggleTrainingPayload.KaggleUsername), ex.Message);

        using var valid = JsonDocument.Parse(
            """
            {
              "sourcePaths":["C:/data"],
              "classes":["scratch"],
              "kaggleUsername":"tester",
              "epochs":0,
              "imgSize":8,
              "splitRatio":0.99
            }
            """);
        var payload = WebViewPayloadBinder.Bind<StartKaggleTrainingPayload>(valid.RootElement);

        Assert.Equal(1, payload.Epochs);
        Assert.Equal(32, payload.ImgSize);
        Assert.Equal(0.95, payload.SplitRatio);
    }

    [Fact]
    public async Task SystemProcessRunnerStreamsOutputAndReturnsExitCode()
    {
        var runner = new SystemProcessRunner();
        var lines = new List<string>();

        var result = await runner.RunAsync(
            new ProcessStartSpec
            {
                FileName = "cmd.exe",
                Arguments = "/c echo insight-process-runner",
                WorkingDirectory = Path.GetTempPath()
            },
            lines.Add,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(lines, x => x.Contains("insight-process-runner"));
        Assert.Contains("insight-process-runner", result.Output);
    }

    [Fact]
    public async Task LocalYoloProviderUsesProcessBoundaryAndReportsMetricsAndArtifacts()
    {
        var workDir = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "训练命令.txt"),
                "yolo detect train model=yolov8s.pt data=\"C:/dataset/data.yaml\" epochs=1");

            var weightsDir = Path.Combine(workDir, "runs", "detect", "train", "weights");
            Directory.CreateDirectory(weightsDir);
            var bestPt = Path.Combine(weightsDir, "best.pt");
            File.WriteAllText(bestPt, "weights");

            var fakeRunner = new FakeProcessRunner(
                0,
                "1/1 2.75G 1.026 1.266 1.18 14 640",
                "all 20 20 0.91 0.82 0.76 0.55");
            var observer = new RecordingTrainingObserver();
            var provider = new LocalYoloTrainingProvider(new YoloTrainingEngine(), fakeRunner);

            var result = await provider.StartAsync(
                new TrainingProviderJobRequest
                {
                    JobId = "job_1",
                    PythonPath = "python",
                    WorkDir = workDir,
                    Params = new TrainingParams()
                },
                observer,
                CancellationToken.None);

            Assert.Equal(TrainingProviderJobState.Completed, result.State);
            Assert.Equal(bestPt, result.ArtifactPath);
            Assert.NotNull(fakeRunner.LastSpec);
            Assert.Equal("python", fakeRunner.LastSpec.FileName);
            Assert.Contains("detect train", fakeRunner.LastSpec.Arguments);
            Assert.Contains(observer.Metrics, x => x.BoxLoss == 1.026);
            Assert.Contains(observer.Metrics, x => x.Map50 == 0.76);
            Assert.Contains(observer.Artifacts, x => x.Path == bestPt && x.Format == "pt");
        }
        finally
        {
            DeleteTempDirectory(workDir);
        }
    }

    [Fact]
    public async Task KaggleYoloProviderPollsDownloadsAndReturnsProviderArtifacts()
    {
        var workDir = CreateTempDirectory();
        try
        {
            var datasetDir = Path.Combine(workDir, "dataset");
            Directory.CreateDirectory(datasetDir);
            var outputDir = Path.Combine(workDir, "output");
            var client = new FakeKaggleClient(outputDir);
            var observer = new RecordingTrainingObserver();
            var provider = new KaggleYoloTrainingProvider(client);

            var result = await provider.StartAsync(
                new TrainingProviderJobRequest
                {
                    JobId = "run_cloud",
                    ProjectRoot = workDir,
                    DatasetVersionId = "ds_1",
                    WorkDir = workDir,
                    Params = new TrainingParams
                    {
                        ModelSize = "v8s",
                        Epochs = 1,
                        BatchSize = 1,
                        Classes = new List<string> { "scratch" }
                    },
                    ProviderOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["projectName"] = "Surface QA",
                        ["kaggleUsername"] = "tester",
                        ["datasetSlug"] = "surface-ds",
                        ["kernelSlug"] = "surface-kernel",
                        ["datasetDirectory"] = datasetDir,
                        ["outputDirectory"] = outputDir,
                        ["pollIntervalSeconds"] = "0",
                        ["pollTimeoutMinutes"] = "1"
                    }
                },
                observer,
                CancellationToken.None);

            Assert.Equal(TrainingProviderJobState.Completed, result.State);
            Assert.Equal("tester/surface-kernel", result.Metadata["kernelId"]);
            Assert.Contains(result.Artifacts, x => x.Format == "pt" && Path.GetFileName(x.Path) == "best.pt");
            Assert.Contains(result.Artifacts, x => x.Format == "onnx" && Path.GetExtension(x.Path) == ".onnx");
            Assert.Contains(observer.Logs, x => x.Contains("Kaggle kernel status"));
        }
        finally
        {
            DeleteTempDirectory(workDir);
        }
    }

    [Fact]
    public void TrainingProviderCatalogRejectsDuplicateProviderIds()
    {
        var first = new StaticTrainingProvider("local-yolo");
        var second = new StaticTrainingProvider("LOCAL-YOLO");

        var ex = Assert.Throws<InvalidOperationException>(
            () => new TrainingProviderCatalog(new[] { first, second }));

        Assert.Contains("Duplicate training provider id", ex.Message);
    }

    [Fact]
    public async Task FileTrainingJobStorePersistsProviderNeutralJobState()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var paths = new InsightAppPaths(
                Path.Combine(tempRoot, "user"),
                Path.Combine(tempRoot, "local"));
            var store = new FileTrainingJobStore(paths);

            await store.UpsertAsync(
                new TrainingJobRecord
                {
                    JobId = "job_1",
                    ProviderId = LocalYoloTrainingProvider.ProviderId,
                    State = TrainingProviderJobState.Running,
                    ProjectRoot = Path.Combine(tempRoot, "project"),
                    DatasetVersionId = "ds_1",
                    ExternalJobId = "local-process-1",
                    Metadata =
                    {
                        ["source"] = "unit-test"
                    }
                },
                CancellationToken.None);

            var loaded = await store.GetAsync("job_1", CancellationToken.None);
            var all = await store.ListAsync(CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(TrainingProviderJobState.Running, loaded.State);
            Assert.Equal("unit-test", loaded.Metadata["source"]);
            Assert.Single(all);
            Assert.Equal("job_1", all[0].JobId);
            Assert.True(File.Exists(Path.Combine(paths.LocalDataRoot, "jobs", "job_1.json")));
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "InsightArchitectureTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class SelectFolderPayload
    {
        public string Type { get; set; } = "";
    }

    private sealed class NumericPayload
    {
        public int Count { get; set; }
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly int _exitCode;
        private readonly string[] _lines;

        public FakeProcessRunner(int exitCode, params string[] lines)
        {
            _exitCode = exitCode;
            _lines = lines;
        }

        public ProcessStartSpec? LastSpec { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            ProcessStartSpec spec,
            Action<string>? onOutput,
            CancellationToken cancellationToken)
        {
            LastSpec = spec;
            foreach (var line in _lines)
            {
                onOutput?.Invoke(line);
            }

            return Task.FromResult(new ProcessRunResult(_exitCode, cancellationToken.IsCancellationRequested, string.Join(Environment.NewLine, _lines)));
        }
    }

    private sealed class RecordingTrainingObserver : ITrainingJobObserver
    {
        public List<string> Logs { get; } = new();
        public List<TrainingMetricUpdate> Metrics { get; } = new();
        public List<TrainingArtifact> Artifacts { get; } = new();

        public void Log(string message, string type = "info") => Logs.Add(message);
        public void Metric(TrainingMetricUpdate update) => Metrics.Add(update);
        public void Artifact(TrainingArtifact artifact) => Artifacts.Add(artifact);
    }

    private sealed class StaticTrainingProvider : ITrainingProvider
    {
        public StaticTrainingProvider(string id)
        {
            Descriptor = new TrainingProviderDescriptor
            {
                Id = id,
                DisplayName = id,
                Kind = TrainingProviderKind.LocalProcess
            };
        }

        public TrainingProviderDescriptor Descriptor { get; }

        public Task<TrainingProviderJobResult> StartAsync(
            TrainingProviderJobRequest request,
            ITrainingJobObserver observer,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new TrainingProviderJobResult
            {
                JobId = request.JobId,
                State = TrainingProviderJobState.Completed
            });
        }
    }

    private sealed class FakeKaggleClient : IKaggleClient
    {
        private readonly string _outputDir;

        public FakeKaggleClient(string outputDir)
        {
            _outputDir = outputDir;
        }

        public Task<KaggleConnectionTestResult> TestConnectionAsync(
            KaggleConnectionTestRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new KaggleConnectionTestResult { Success = true, Message = "ok" });
        }

        public Task<KaggleTrainingSubmissionResult> SubmitTrainingAsync(
            KaggleTrainingSubmissionRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new KaggleTrainingSubmissionResult());
        }

        public Task<KaggleTrainingSubmissionResult> SubmitPreparedTrainingAsync(
            KagglePreparedTrainingSubmissionRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new KaggleTrainingSubmissionResult
            {
                JobId = request.JobId,
                DatasetId = $"{request.KaggleUsername}/{request.DatasetSlug}",
                KernelId = $"{request.KaggleUsername}/{request.KernelSlug}",
                KernelUrl = $"https://www.kaggle.com/code/{request.KaggleUsername}/{request.KernelSlug}",
                Submitted = true
            });
        }

        public Task<KaggleTrainingJobStatus> GetTrainingStatusAsync(
            string kernelId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new KaggleTrainingJobStatus
            {
                KernelId = kernelId,
                State = "Completed",
                Message = "complete"
            });
        }

        public Task<string> DownloadOutputAsync(
            KaggleOutputDownloadRequest request,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.Combine(_outputDir, "weights"));
            File.WriteAllText(Path.Combine(_outputDir, "weights", "best.pt"), "best");
            File.WriteAllBytes(Path.Combine(_outputDir, "detector.onnx"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllText(Path.Combine(_outputDir, "results.csv"), "epoch,metrics/mAP50(B)\n1,0.9\n");
            return Task.FromResult(_outputDir);
        }
    }
}
