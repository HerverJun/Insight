using System.Drawing;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Insight.Bridge;
using Insight.Infrastructure.Processes;
using Insight.Services.Cloud.Kaggle;
using Insight.Services.Industrial;
using Insight.Training;
using Insight.Training.Jobs;
using Insight.Training.Providers;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Insight.Tests;

public class IndustrialTrainingServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [Fact]
    public void CreateDatasetVersionMaterializesManifestQaAndYaml()
    {
        var projectRoot = CreateTempDirectory();
        try
        {
            var source = Path.Combine(projectRoot, "raw");
            Directory.CreateDirectory(source);
            CreateImage(Path.Combine(source, "ok.png"));
            File.WriteAllText(Path.Combine(source, "ok.txt"), "0 0.5 0.5 0.4 0.4", Encoding.UTF8);
            CreateImage(Path.Combine(source, "missing.png"));
            CreateImage(Path.Combine(source, "badclass.png"));
            File.WriteAllText(Path.Combine(source, "badclass.txt"), "99 0.5 0.5 0.4 0.4", Encoding.UTF8);

            var service = new IndustrialTrainingService(new RecordingFrontendMessenger());
            var version = service.CreateDatasetVersion(new DatasetVersionRequest
            {
                ProjectRoot = projectRoot,
                ProjectName = "tiny",
                SourcePaths = new List<string> { source },
                Classes = new List<string> { "scratch", "dent" },
                SplitSeed = 7
            });

            Assert.Equal(3, version.ImageCount);
            Assert.Equal(1, version.LabelCount);
            Assert.True(version.IssueCount >= 2);
            Assert.True(File.Exists(version.ManifestPath));
            Assert.True(File.Exists(version.QaReportPath));
            Assert.True(File.Exists(version.DataYamlPath));

            var report = JsonSerializer.Deserialize<DatasetQaReport>(File.ReadAllText(version.QaReportPath), JsonOptions)!;
            Assert.Contains(report.Issues, x => x.Code == "missing_label");
            Assert.Contains(report.Issues, x => x.Code == "unknown_class");
        }
        finally
        {
            DeleteTempDirectory(projectRoot);
        }
    }

    [Fact]
    public void BuildYoloTrainArgumentsUsesTypedConfigAndDatasetVersion()
    {
        var dataset = new DatasetVersion
        {
            Id = "ds_test",
            DataYamlPath = @"C:\data version\data.yaml"
        };
        var config = new TrainingRunConfig
        {
            ModelSize = "v11s",
            Epochs = 12,
            BatchSize = 4,
            ImgSize = 512,
            Patience = 3,
            Workers = 0,
            GpuIndex = "cpu",
            Seed = 123,
            AdvancedOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["cache"] = "False",
                ["amp"] = "True"
            }
        };

        var args = IndustrialTrainingService.BuildYoloTrainArguments(config, dataset, @"C:\runs\run_1");

        Assert.Contains("detect train", args);
        Assert.Contains("model=yolo11s.pt", args);
        Assert.Contains("data=\"C:/data version/data.yaml\"", args);
        Assert.Contains("epochs=12", args);
        Assert.Contains("batch=4", args);
        Assert.Contains("imgsz=512", args);
        Assert.Contains("device=cpu", args);
        Assert.Contains("cache=False", args);
        Assert.Contains("amp=True", args);
    }

    [Fact]
    public async Task StartTrainingRunUsesProcessRunnerAndSyncsProviderNeutralJobState()
    {
        var projectRoot = CreateTempDirectory();
        try
        {
            var insightRoot = Path.Combine(projectRoot, ".insight");
            Directory.CreateDirectory(insightRoot);
            var yoloRoot = Path.Combine(insightRoot, "datasets", "ds_1", "yolo");
            Directory.CreateDirectory(yoloRoot);
            var dataYaml = Path.Combine(yoloRoot, "data.yaml");
            File.WriteAllText(dataYaml, "path: .\nnames:\n  0: scratch\n", Encoding.UTF8);

            var index = new IndustrialStoreIndex
            {
                DatasetVersions = new List<DatasetVersion>
                {
                    new()
                    {
                        Id = "ds_1",
                        ProjectRoot = projectRoot,
                        ProjectName = "Surface QA",
                        DataYamlPath = dataYaml,
                        Classes = new List<string> { "scratch" }
                    }
                }
            };
            File.WriteAllText(Path.Combine(insightRoot, "industrial_index.json"), JsonSerializer.Serialize(index, JsonOptions), Encoding.UTF8);

            var runner = new IndustrialFakeProcessRunner();
            var jobStore = new RecordingTrainingJobStore();
            var service = new IndustrialTrainingService(
                new RecordingFrontendMessenger(),
                new YoloTrainingEngine(),
                runner,
                jobStore);

            var run = await service.StartTrainingRunAsync(new TrainingRunConfig
            {
                ProjectRoot = projectRoot,
                DatasetVersionId = "ds_1",
                ExperimentName = "Boundary Test",
                PythonPath = "python",
                ModelSize = "v8s",
                Epochs = 1,
                BatchSize = 1,
                ImgSize = 64,
                Workers = 0,
                GpuIndex = "cpu"
            });

            Assert.Equal("Completed", run.Status);
            Assert.Equal(2, runner.Specs.Count);
            Assert.Contains("detect train", runner.Specs[0].Arguments);
            Assert.Contains("export model=", runner.Specs[1].Arguments);
            Assert.True(File.Exists(run.BestPtPath));
            Assert.True(File.Exists(run.OnnxPath));
            Assert.Contains(jobStore.Records, x => x.JobId == run.Id && x.ProviderId == LocalYoloTrainingProvider.ProviderId && x.State == TrainingProviderJobState.Running);
            Assert.Contains(jobStore.Records, x => x.JobId == run.Id && x.State == TrainingProviderJobState.Completed);
        }
        finally
        {
            DeleteTempDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task StartTrainingRunCanUseKaggleProviderAndRegisterDownloadedArtifacts()
    {
        var projectRoot = CreateTempDirectory();
        try
        {
            var insightRoot = Path.Combine(projectRoot, ".insight");
            Directory.CreateDirectory(insightRoot);
            var yoloRoot = Path.Combine(insightRoot, "datasets", "ds_cloud", "yolo");
            Directory.CreateDirectory(Path.Combine(yoloRoot, "images", "train"));
            Directory.CreateDirectory(Path.Combine(yoloRoot, "labels", "train"));
            var dataYaml = Path.Combine(yoloRoot, "data.yaml");
            File.WriteAllText(dataYaml, "train: images/train\nnames:\n  0: scratch\n", Encoding.UTF8);

            var index = new IndustrialStoreIndex
            {
                DatasetVersions = new List<DatasetVersion>
                {
                    new()
                    {
                        Id = "ds_cloud",
                        ProjectRoot = projectRoot,
                        ProjectName = "Surface QA",
                        YoloRoot = yoloRoot,
                        DataYamlPath = dataYaml,
                        Classes = new List<string> { "scratch" }
                    }
                }
            };
            File.WriteAllText(Path.Combine(insightRoot, "industrial_index.json"), JsonSerializer.Serialize(index, JsonOptions), Encoding.UTF8);

            var runner = new IndustrialFakeProcessRunner();
            var jobStore = new RecordingTrainingJobStore();
            var kaggleClient = new IndustrialFakeKaggleClient();
            var service = new IndustrialTrainingService(
                new RecordingFrontendMessenger(),
                new YoloTrainingEngine(),
                runner,
                jobStore,
                new ITrainingProvider[]
                {
                    new LocalYoloTrainingProvider(new YoloTrainingEngine(), runner),
                    new KaggleYoloTrainingProvider(kaggleClient)
                });

            var run = await service.StartTrainingRunAsync(new TrainingRunConfig
            {
                ProjectRoot = projectRoot,
                DatasetVersionId = "ds_cloud",
                ProviderId = KaggleYoloTrainingProvider.ProviderId,
                ExperimentName = "Cloud Boundary Test",
                PythonPath = "python",
                ModelSize = "v8s",
                Epochs = 1,
                BatchSize = 1,
                ImgSize = 64,
                Workers = 0,
                ProviderOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["kaggleUsername"] = "tester",
                    ["datasetSlug"] = "surface-ds",
                    ["kernelSlug"] = "surface-kernel",
                    ["pollIntervalSeconds"] = "0",
                    ["pollTimeoutMinutes"] = "1"
                }
            });

            Assert.Equal("Completed", run.Status);
            Assert.Equal(KaggleYoloTrainingProvider.ProviderId, run.ProviderId);
            Assert.Equal("tester/surface-kernel", run.ProviderExternalJobId);
            Assert.Empty(runner.Specs);
            Assert.True(File.Exists(run.BestPtPath));
            Assert.True(File.Exists(run.OnnxPath));
            Assert.Contains(jobStore.Records, x =>
                x.JobId == run.Id &&
                x.ProviderId == KaggleYoloTrainingProvider.ProviderId &&
                x.State == TrainingProviderJobState.Completed &&
                x.ExternalJobId == "tester/surface-kernel");

            var persisted = JsonSerializer.Deserialize<IndustrialStoreIndex>(
                File.ReadAllText(Path.Combine(insightRoot, "industrial_index.json"), Encoding.UTF8),
                JsonOptions)!;
            var model = Assert.Single(persisted.ModelRegistry);
            Assert.Equal(run.Id, model.RunId);
            Assert.True(File.Exists(model.PtPath));
            Assert.True(File.Exists(model.OnnxPath));
        }
        finally
        {
            DeleteTempDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task RecoverCloudTrainingRunAfterRestartPollsDownloadsAndRegistersModel()
    {
        var projectRoot = CreateTempDirectory();
        try
        {
            var insightRoot = Path.Combine(projectRoot, ".insight");
            Directory.CreateDirectory(insightRoot);
            var yoloRoot = Path.Combine(insightRoot, "datasets", "ds_recover", "yolo");
            Directory.CreateDirectory(Path.Combine(yoloRoot, "images", "train"));
            Directory.CreateDirectory(Path.Combine(yoloRoot, "labels", "train"));
            var dataYaml = Path.Combine(yoloRoot, "data.yaml");
            File.WriteAllText(dataYaml, "train: images/train\nnames:\n  0: scratch\n", Encoding.UTF8);

            var runRoot = Path.Combine(insightRoot, "runs", "run_recover");
            Directory.CreateDirectory(runRoot);
            var index = new IndustrialStoreIndex
            {
                DatasetVersions = new List<DatasetVersion>
                {
                    new()
                    {
                        Id = "ds_recover",
                        ProjectRoot = projectRoot,
                        ProjectName = "Surface QA",
                        YoloRoot = yoloRoot,
                        DataYamlPath = dataYaml,
                        Classes = new List<string> { "scratch" }
                    }
                },
                TrainingRuns = new List<TrainingRunRecord>
                {
                    new()
                    {
                        Id = "run_recover",
                        ProjectRoot = projectRoot,
                        DatasetVersionId = "ds_recover",
                        ExperimentName = "Recovered Cloud Run",
                        Status = "Running",
                        RunRoot = runRoot,
                        ProviderId = KaggleYoloTrainingProvider.ProviderId,
                        ProviderExternalJobId = "tester/surface-kernel",
                        ProviderMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["kernelId"] = "tester/surface-kernel",
                            ["outputDirectory"] = Path.Combine(runRoot, "kaggle_output")
                        },
                        ConfigPath = Path.Combine(runRoot, "config.json"),
                        EnvironmentPath = Path.Combine(runRoot, "environment.json"),
                        LogPath = Path.Combine(runRoot, "train.log"),
                        MetricsPath = Path.Combine(runRoot, "metrics.jsonl"),
                        Config = new TrainingRunConfig
                        {
                            ProjectRoot = projectRoot,
                            DatasetVersionId = "ds_recover",
                            ProviderId = KaggleYoloTrainingProvider.ProviderId,
                            PythonPath = "python",
                            ModelSize = "v8s",
                            Epochs = 1,
                            BatchSize = 1,
                            ImgSize = 64,
                            ProviderOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["kaggleUsername"] = "tester",
                                ["datasetSlug"] = "surface-ds",
                                ["kernelSlug"] = "surface-kernel",
                                ["pollIntervalSeconds"] = "0",
                                ["pollTimeoutMinutes"] = "1"
                            }
                        }
                    }
                }
            };
            File.WriteAllText(Path.Combine(insightRoot, "industrial_index.json"), JsonSerializer.Serialize(index, JsonOptions), Encoding.UTF8);

            var runner = new IndustrialFakeProcessRunner();
            var jobStore = new RecordingTrainingJobStore();
            var service = new IndustrialTrainingService(
                new RecordingFrontendMessenger(),
                new YoloTrainingEngine(),
                runner,
                jobStore,
                new ITrainingProvider[]
                {
                    new LocalYoloTrainingProvider(new YoloTrainingEngine(), runner),
                    new KaggleYoloTrainingProvider(new IndustrialFakeKaggleClient())
                });

            await service.RecoverCloudTrainingRunsAsync(projectRoot, CancellationToken.None);

            var persisted = JsonSerializer.Deserialize<IndustrialStoreIndex>(
                File.ReadAllText(Path.Combine(insightRoot, "industrial_index.json"), Encoding.UTF8),
                JsonOptions)!;
            var run = Assert.Single(persisted.TrainingRuns);
            Assert.Equal("Completed", run.Status);
            Assert.True(File.Exists(run.BestPtPath));
            Assert.True(File.Exists(run.OnnxPath));
            Assert.Equal("tester/surface-kernel", run.ProviderExternalJobId);
            Assert.Single(persisted.ModelRegistry);
            Assert.Contains(jobStore.Records, x => x.JobId == "run_recover" && x.State == TrainingProviderJobState.Completed);
        }
        finally
        {
            DeleteTempDirectory(projectRoot);
        }
    }

    [Fact]
    public void PromoteModelPinsProductionAndWritesModelCard()
    {
        var projectRoot = CreateTempDirectory();
        try
        {
            var insightRoot = Path.Combine(projectRoot, ".insight");
            var modelRoot = Path.Combine(insightRoot, "models", "m1");
            Directory.CreateDirectory(modelRoot);
            var modelCard = Path.Combine(modelRoot, "model_card.json");
            var index = new IndustrialStoreIndex
            {
                ModelRegistry = new List<ModelRegistryEntry>
                {
                    new()
                    {
                        Id = "m1",
                        Status = "Candidate",
                        ModelCardPath = modelCard,
                        Classes = new List<string> { "scratch" }
                    }
                }
            };
            File.WriteAllText(Path.Combine(insightRoot, "industrial_index.json"), JsonSerializer.Serialize(index, JsonOptions), Encoding.UTF8);

            var service = new IndustrialTrainingService(new RecordingFrontendMessenger());
            var promoted = service.PromoteModel(projectRoot, "m1", "Production");

            Assert.Equal("Production", promoted.Status);
            Assert.True(promoted.IsPinned);
            Assert.True(File.Exists(modelCard));
            Assert.Contains("Production", File.ReadAllText(modelCard));
        }
        finally
        {
            DeleteTempDirectory(projectRoot);
        }
    }

    [Fact]
    public void ExportModelPackageCreatesClearVisionCatalogAndEvidenceBundle()
    {
        var projectRoot = CreateTempDirectory();
        try
        {
            var insightRoot = Path.Combine(projectRoot, ".insight");
            var modelRoot = Path.Combine(insightRoot, "models", "run_1");
            Directory.CreateDirectory(modelRoot);

            var onnxPath = Path.Combine(modelRoot, "best.onnx");
            var ptPath = Path.Combine(modelRoot, "best.pt");
            var modelCard = Path.Combine(modelRoot, "model_card.json");
            var evaluationPath = Path.Combine(insightRoot, "runs", "run_1", "evaluation_report.json");
            var latestEvaluationPath = Path.Combine(insightRoot, "evaluations", "eval_latest", "model_evaluation_report.json");
            Directory.CreateDirectory(Path.GetDirectoryName(evaluationPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(latestEvaluationPath)!);
            File.WriteAllBytes(onnxPath, new byte[] { 1, 2, 3, 4, 5 });
            File.WriteAllBytes(ptPath, new byte[] { 9, 8, 7 });
            File.WriteAllText(evaluationPath, """{"Id":"eval_run_1","Map50":0.91}""", Encoding.UTF8);
            File.WriteAllText(latestEvaluationPath, """{"Id":"eval_latest","ReadinessStatus":"Pass"}""", Encoding.UTF8);

            var index = new IndustrialStoreIndex
            {
                DatasetVersions = new List<DatasetVersion>
                {
                    new()
                    {
                        Id = "ds_1",
                        Classes = new List<string> { "scratch", "dent" },
                        ImageCount = 12,
                        LabelCount = 18,
                        IssueCount = 0
                    }
                },
                TrainingRuns = new List<TrainingRunRecord>
                {
                    new()
                    {
                        Id = "run_1",
                        DatasetVersionId = "ds_1",
                        ExperimentName = "Surface QA",
                        EvaluationReportPath = evaluationPath,
                        Config = new TrainingRunConfig
                        {
                            ModelSize = "v11s",
                            ImgSize = 512,
                            Epochs = 20,
                            BatchSize = 4,
                            Seed = 17
                        }
                    }
                },
                ModelRegistry = new List<ModelRegistryEntry>
                {
                    new()
                    {
                        Id = "model_run_1",
                        RunId = "run_1",
                        DatasetVersionId = "ds_1",
                        Status = "Candidate",
                        PtPath = ptPath,
                        OnnxPath = onnxPath,
                        EvaluationReportPath = evaluationPath,
                        LastEvaluationReportPath = latestEvaluationPath,
                        ModelCardPath = modelCard,
                        Classes = new List<string> { "scratch", "dent" }
                    }
                }
            };
            File.WriteAllText(Path.Combine(insightRoot, "industrial_index.json"), JsonSerializer.Serialize(index, JsonOptions), Encoding.UTF8);

            var outputRoot = Path.Combine(projectRoot, "clearvision-package-out");
            var service = new IndustrialTrainingService(new RecordingFrontendMessenger());
            var package = service.ExportModelPackage(
                projectRoot,
                "model_run_1",
                "ClearVision",
                outputRoot,
                "Proprietary",
                "test-hardware",
                "report-1");

            Assert.True(File.Exists(Path.Combine(package.PackageRoot, "model.onnx")));
            Assert.True(File.Exists(Path.Combine(package.PackageRoot, "model.pt")));
            Assert.Equal(new[] { "scratch", "dent" }, File.ReadAllLines(package.LabelsPath, Encoding.UTF8));
            Assert.True(File.Exists(package.ManifestPath));
            Assert.True(File.Exists(package.CatalogPath));
            Assert.True(File.Exists(package.PlatformDescriptorPath));
            Assert.True(File.Exists(package.ReportPath));
            Assert.Contains("eval_latest", File.ReadAllText(package.ReportPath, Encoding.UTF8));

            using var catalogDoc = JsonDocument.Parse(File.ReadAllText(package.CatalogPath, Encoding.UTF8));
            var model = catalogDoc.RootElement.GetProperty("models")[0];
            Assert.Equal("model_run_1", model.GetProperty("id").GetString());
            Assert.Equal("model.onnx", model.GetProperty("path").GetString());
            Assert.Equal("YOLOv11", model.GetProperty("postprocess").GetProperty("yoloVersion").GetString());
            Assert.Equal("Proprietary", model.GetProperty("license").GetString());
            Assert.Equal("report-1", model.GetProperty("reportId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(model.GetProperty("modelSha256").GetString()));
            Assert.Contains("labels.txt sha256:", model.GetProperty("labelsContract").GetString());

            var persisted = JsonSerializer.Deserialize<IndustrialStoreIndex>(
                File.ReadAllText(Path.Combine(insightRoot, "industrial_index.json"), Encoding.UTF8),
                JsonOptions)!;
            var persistedModel = Assert.Single(persisted.ModelRegistry);
            Assert.Equal(package.PackageRoot, persistedModel.LastPackagePath);
            Assert.Single(persistedModel.PackageHistory);
            Assert.Contains("model_run_1", File.ReadAllText(modelCard, Encoding.UTF8));
        }
        finally
        {
            DeleteTempDirectory(projectRoot);
        }
    }

    [Fact]
    public void ParseYoloDetectionsHandlesFeatureFirstOutputAndClassAwareNms()
    {
        var tensor = new DenseTensor<float>(new[] { 1, 6, 3 });
        SetFeatureFirstCandidate(tensor, 0, cx: 50, cy: 50, w: 40, h: 20, class0: 0.90f, class1: 0.10f);
        SetFeatureFirstCandidate(tensor, 1, cx: 52, cy: 50, w: 40, h: 20, class0: 0.80f, class1: 0.10f);
        SetFeatureFirstCandidate(tensor, 2, cx: 150, cy: 150, w: 30, h: 30, class0: 0.05f, class1: 0.70f);

        var detections = IndustrialTrainingService.ParseYoloDetections(
            tensor,
            new[] { "scratch", "dent" },
            originalWidth: 200,
            originalHeight: 200,
            inputSize: 200,
            scale: 1,
            padX: 0,
            padY: 0,
            confidenceThreshold: 0.25,
            nmsIouThreshold: 0.45);

        Assert.Equal(2, detections.Count);
        Assert.Equal("scratch", detections[0].ClassName);
        Assert.Equal(0.9, detections[0].Confidence, precision: 3);
        Assert.Equal(30, detections[0].X);
        Assert.Equal(40, detections[0].Y);
        Assert.Equal(40, detections[0].Width);
        Assert.Equal(20, detections[0].Height);
        Assert.Equal("dent", detections[1].ClassName);
    }

    [Fact]
    public void ParseYoloDetectionsHandlesCandidateFirstOutputAndLetterboxProjection()
    {
        var tensor = new DenseTensor<float>(new[] { 1, 1, 6 });
        tensor[0, 0, 0] = 100;
        tensor[0, 0, 1] = 100;
        tensor[0, 0, 2] = 40;
        tensor[0, 0, 3] = 40;
        tensor[0, 0, 4] = 0.1f;
        tensor[0, 0, 5] = 0.9f;

        var detections = IndustrialTrainingService.ParseYoloDetections(
            tensor,
            new[] { "scratch", "dent" },
            originalWidth: 100,
            originalHeight: 50,
            inputSize: 200,
            scale: 2,
            padX: 0,
            padY: 50,
            confidenceThreshold: 0.25,
            nmsIouThreshold: 0.45);

        var detection = Assert.Single(detections);
        Assert.Equal("dent", detection.ClassName);
        Assert.Equal(40, detection.X);
        Assert.Equal(15, detection.Y);
        Assert.Equal(20, detection.Width);
        Assert.Equal(20, detection.Height);
    }

    [Fact]
    public void EvaluateDetectionsMatchesByClassAndIou()
    {
        var predictions = new List<DetectionPreviewBox>
        {
            Box(0, "scratch", 0.95, 10, 10, 20, 20),
            Box(0, "scratch", 0.80, 70, 70, 15, 15),
            Box(1, "dent", 0.70, 10, 10, 20, 20)
        };
        var labels = new List<DetectionPreviewBox>
        {
            Box(0, "scratch", 1, 11, 11, 20, 20),
            Box(1, "dent", 1, 40, 40, 10, 10)
        };

        var metrics = IndustrialTrainingService.EvaluateDetections(predictions, labels, 0.5);

        Assert.Equal(1, metrics.TruePositive);
        Assert.Equal(2, metrics.FalsePositive);
        Assert.Equal(1, metrics.FalseNegative);
        Assert.Equal(0.333333, metrics.Precision, precision: 5);
        Assert.Equal(0.5, metrics.Recall, precision: 5);
    }

    private static void CreateImage(string path)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        bitmap.Save(path);
    }

    private static void SetFeatureFirstCandidate(
        DenseTensor<float> tensor,
        int candidate,
        float cx,
        float cy,
        float w,
        float h,
        float class0,
        float class1)
    {
        tensor[0, 0, candidate] = cx;
        tensor[0, 1, candidate] = cy;
        tensor[0, 2, candidate] = w;
        tensor[0, 3, candidate] = h;
        tensor[0, 4, candidate] = class0;
        tensor[0, 5, candidate] = class1;
    }

    private static DetectionPreviewBox Box(int classId, string className, double confidence, double x, double y, double width, double height)
    {
        return new DetectionPreviewBox
        {
            ClassId = classId,
            ClassName = className,
            Confidence = confidence,
            X = x,
            Y = y,
            Width = width,
            Height = height
        };
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "InsightIndustrialTests_" + Guid.NewGuid().ToString("N"));
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

    private sealed class RecordingFrontendMessenger : IFrontendMessenger
    {
        public List<object> Messages { get; } = new();
        public List<string> Errors { get; } = new();

        public void Send(object data) => Messages.Add(data);
        public void Log(string message, string type = "info") => Messages.Add(new { action = "log", message, type });
        public void Error(string message) => Errors.Add(message);
        public void Complete(string message) => Messages.Add(new { action = "complete", message });
    }

    private sealed class IndustrialFakeProcessRunner : IProcessRunner
    {
        public List<ProcessStartSpec> Specs { get; } = new();

        public Task<ProcessRunResult> RunAsync(
            ProcessStartSpec spec,
            Action<string>? onOutput,
            CancellationToken cancellationToken)
        {
            Specs.Add(spec);

            if (Specs.Count == 1)
            {
                onOutput?.Invoke("1/1 2.75G 1.026 1.266 1.18 14 64");
                onOutput?.Invoke("all 1 1 0.91 0.82 0.76 0.55");
                var weights = Path.Combine(spec.WorkingDirectory, "runs", "detect", "train", "weights");
                Directory.CreateDirectory(weights);
                File.WriteAllText(Path.Combine(weights, "best.pt"), "best", Encoding.UTF8);
                File.WriteAllText(Path.Combine(weights, "last.pt"), "last", Encoding.UTF8);
            }
            else
            {
                var best = Directory.GetFiles(spec.WorkingDirectory, "best.pt", SearchOption.AllDirectories).Single();
                File.WriteAllBytes(Path.ChangeExtension(best, ".onnx"), new byte[] { 1, 2, 3, 4 });
                onOutput?.Invoke("export complete");
            }

            return Task.FromResult(new ProcessRunResult(0, cancellationToken.IsCancellationRequested, "ok"));
        }
    }

    private sealed class IndustrialFakeKaggleClient : IKaggleClient
    {
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
            Assert.Equal("ds_cloud", request.DatasetVersionId);
            Assert.True(Directory.Exists(request.DatasetDirectory));
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
            var output = request.OutputDirectory;
            Directory.CreateDirectory(Path.Combine(output, "InsightYOLO", "runs", "train", "weights"));
            var weights = Path.Combine(output, "InsightYOLO", "runs", "train", "weights");
            File.WriteAllText(Path.Combine(weights, "best.pt"), "best", Encoding.UTF8);
            File.WriteAllText(Path.Combine(weights, "last.pt"), "last", Encoding.UTF8);
            File.WriteAllBytes(Path.Combine(output, "detector.onnx"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllText(
                Path.Combine(output, "results.csv"),
                "epoch,train/box_loss,metrics/mAP50(B),metrics/mAP50-95(B)\n1,1.1,0.91,0.77\n",
                Encoding.UTF8);
            return Task.FromResult(output);
        }
    }

    private sealed class RecordingTrainingJobStore : ITrainingJobStore
    {
        public List<TrainingJobRecord> Records { get; } = new();

        public Task UpsertAsync(TrainingJobRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<TrainingJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken)
        {
            return Task.FromResult(Records.LastOrDefault(x => x.JobId == jobId));
        }

        public Task<IReadOnlyList<TrainingJobRecord>> ListAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<TrainingJobRecord>>(Records.ToArray());
        }
    }
}
