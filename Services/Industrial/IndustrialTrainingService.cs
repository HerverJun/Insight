using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Insight.Bridge;
using Insight.Infrastructure.Processes;
using Insight.Infrastructure.Training;
using Insight.Services.Configuration;
using Insight.Training;
using Insight.Training.Jobs;
using Insight.Training.Providers;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Insight.Services.Industrial
{
    public sealed class IndustrialTrainingService : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

        private readonly IFrontendMessenger _messenger;
        private readonly ITrainingEngine _metricParser;
        private readonly IProcessRunner _processRunner;
        private readonly ITrainingJobStore _jobStore;
        private readonly object _processLock = new();
        private CancellationTokenSource? _activeRunCts;
        private string _activeRunId = "";
        private bool _stopRequested;

        public IndustrialTrainingService(IFrontendMessenger messenger, ITrainingEngine? metricParser = null)
            : this(
                messenger,
                metricParser,
                new SystemProcessRunner(),
                new FileTrainingJobStore(InsightAppPaths.CreateDefault()))
        {
        }

        public IndustrialTrainingService(
            IFrontendMessenger messenger,
            ITrainingEngine? metricParser,
            IProcessRunner processRunner,
            ITrainingJobStore jobStore)
        {
            _messenger = messenger;
            _metricParser = metricParser ?? new YoloTrainingEngine();
            _processRunner = processRunner;
            _jobStore = jobStore;
        }

        public void Dispose()
        {
            StopTrainingRun();
            _activeRunCts?.Dispose();
        }

        public async Task HandleCreateDatasetVersionAsync(JsonElement data)
        {
            try
            {
                var request = ParseDatasetVersionRequest(data);
                var version = await Task.Run(() => CreateDatasetVersion(request));
                var report = LoadJson<DatasetQaReport>(version.QaReportPath) ?? new DatasetQaReport();
                _messenger.Send(new { action = "dataset_version_created", version, report });
                _messenger.Log($"数据版本已创建: {version.Id}，样本 {version.ImageCount}，问题 {version.IssueCount}", version.IssueCount == 0 ? "success" : "warning");
            }
            catch (Exception ex)
            {
                _messenger.Error($"创建数据版本失败: {ex.Message}");
            }
        }

        public void HandleValidateDataset(JsonElement data)
        {
            try
            {
                var projectRoot = GetRequiredString(data, "projectRoot");
                var versionId = GetRequiredString(data, "datasetVersionId");
                var report = ValidateDatasetVersion(projectRoot, versionId);
                _messenger.Send(new { action = "dataset_validation_completed", report });
            }
            catch (Exception ex)
            {
                _messenger.Error($"数据集校验失败: {ex.Message}");
            }
        }

        public async Task HandleStartTrainingRunAsync(JsonElement data)
        {
            try
            {
                var config = ParseTrainingRunConfig(data);
                await StartTrainingRunAsync(config);
            }
            catch (Exception ex)
            {
                _messenger.Error($"启动专业训练失败: {ex.Message}");
            }
        }

        public async Task HandleResumeTrainingRunAsync(JsonElement data)
        {
            try
            {
                var config = ParseTrainingRunConfig(data);
                config.Resume = true;
                config.ResumeRunId = GetOptionalString(data, "resumeRunId");
                await StartTrainingRunAsync(config);
            }
            catch (Exception ex)
            {
                _messenger.Error($"续训失败: {ex.Message}");
            }
        }

        public void HandleStopTrainingRun()
        {
            StopTrainingRun();
        }

        public void HandleGetTrainingRuns(JsonElement data)
        {
            try
            {
                var projectRoot = GetRequiredString(data, "projectRoot");
                var index = LoadIndex(projectRoot);
                _messenger.Send(new
                {
                    action = "training_runs_loaded",
                    datasetVersions = index.DatasetVersions.OrderByDescending(x => x.CreatedAt).ToList(),
                    runs = index.TrainingRuns.OrderByDescending(x => x.CreatedAt).ToList(),
                    models = index.ModelRegistry.OrderByDescending(x => x.CreatedAt).ToList()
                });
            }
            catch (Exception ex)
            {
                _messenger.Error($"加载实验列表失败: {ex.Message}");
            }
        }

        public void HandleGetTrainingRunDetail(JsonElement data)
        {
            try
            {
                var projectRoot = GetRequiredString(data, "projectRoot");
                var runId = GetRequiredString(data, "runId");
                var run = FindRun(projectRoot, runId);
                var evaluation = string.IsNullOrWhiteSpace(run.EvaluationReportPath)
                    ? null
                    : LoadJson<EvaluationReport>(run.EvaluationReportPath);
                _messenger.Send(new { action = "training_run_detail_loaded", run, evaluation });
            }
            catch (Exception ex)
            {
                _messenger.Error($"加载实验详情失败: {ex.Message}");
            }
        }

        public void HandleGetEvaluationReport(JsonElement data)
        {
            try
            {
                var projectRoot = GetRequiredString(data, "projectRoot");
                var runId = GetRequiredString(data, "runId");
                var run = FindRun(projectRoot, runId);
                if (string.IsNullOrWhiteSpace(run.EvaluationReportPath) || !File.Exists(run.EvaluationReportPath))
                {
                    throw new FileNotFoundException("该训练尚未生成评估报告。");
                }

                var report = LoadJson<EvaluationReport>(run.EvaluationReportPath);
                _messenger.Send(new { action = "evaluation_report_loaded", report });
            }
            catch (Exception ex)
            {
                _messenger.Error($"加载评估报告失败: {ex.Message}");
            }
        }

        public void HandlePromoteModel(JsonElement data)
        {
            try
            {
                var projectRoot = GetRequiredString(data, "projectRoot");
                var modelId = GetRequiredString(data, "modelId");
                var status = GetOptionalString(data, "status");
                if (string.IsNullOrWhiteSpace(status)) status = "Production";
                var entry = PromoteModel(projectRoot, modelId, status);
                _messenger.Send(new { action = "model_promoted", model = entry });
                _messenger.Log($"模型状态已更新: {entry.Id} -> {entry.Status}", "success");
            }
            catch (Exception ex)
            {
                _messenger.Error($"模型状态更新失败: {ex.Message}");
            }
        }

        public void HandleBenchmarkModel(JsonElement data)
        {
            try
            {
                var projectRoot = GetRequiredString(data, "projectRoot");
                var modelId = GetRequiredString(data, "modelId");
                var index = LoadIndex(projectRoot);
                var model = index.ModelRegistry.FirstOrDefault(x => x.Id == modelId)
                    ?? throw new InvalidOperationException($"未找到模型: {modelId}");

                model.SmokeTest = RunOnnxSmokeTest(model.OnnxPath);
                model.UpdatedAt = DateTime.Now;
                SaveIndex(projectRoot, index);
                _messenger.Send(new { action = "benchmark_completed", model, benchmark = model.SmokeTest });
            }
            catch (Exception ex)
            {
                _messenger.Error($"模型 benchmark 失败: {ex.Message}");
            }
        }

        public void HandleRunInferencePreview(JsonElement data)
        {
            try
            {
                var projectRoot = GetRequiredString(data, "projectRoot");
                var modelId = GetRequiredString(data, "modelId");
                var imagePath = GetOptionalString(data, "imagePath");
                var confidenceThreshold = GetOptionalDouble(data, "confidenceThreshold", 0.25);
                var nmsIouThreshold = GetOptionalDouble(data, "nmsIouThreshold", 0.45);
                var index = LoadIndex(projectRoot);
                var model = index.ModelRegistry.FirstOrDefault(x => x.Id == modelId)
                    ?? throw new InvalidOperationException($"未找到模型: {modelId}");

                var smoke = RunOnnxSmokeTest(model.OnnxPath);
                InferencePreviewResult? preview = null;
                if (!string.IsNullOrWhiteSpace(imagePath))
                {
                    preview = RunInferencePreview(projectRoot, modelId, imagePath, confidenceThreshold, nmsIouThreshold);
                    model.LastPreviewPath = preview.AnnotatedImagePath;
                    model.LastPreviewReportPath = preview.ReportPath;
                    model.UpdatedAt = DateTime.Now;
                    SaveIndex(projectRoot, index);
                    if (!string.IsNullOrWhiteSpace(model.ModelCardPath))
                    {
                        SaveJson(model.ModelCardPath, BuildModelCard(model));
                    }
                }

                _messenger.Send(new
                {
                    action = "inference_preview_completed",
                    modelId,
                    imagePath,
                    ready = smoke.Success && (preview == null || preview.Success),
                    smoke,
                    preview
                });
            }
            catch (Exception ex)
            {
                _messenger.Error($"推理预览失败: {ex.Message}");
            }
        }

        public InferencePreviewResult RunInferencePreview(
            string projectRoot,
            string modelId,
            string imagePath,
            double confidenceThreshold = 0.25,
            double nmsIouThreshold = 0.45)
        {
            projectRoot = Path.GetFullPath(projectRoot);
            imagePath = Path.GetFullPath(imagePath);
            if (!File.Exists(imagePath))
            {
                throw new FileNotFoundException("预览图片不存在。", imagePath);
            }

            var index = LoadIndex(projectRoot);
            var model = index.ModelRegistry.FirstOrDefault(x => x.Id == modelId)
                ?? throw new InvalidOperationException($"未找到模型: {modelId}");
            if (string.IsNullOrWhiteSpace(model.OnnxPath) || !File.Exists(model.OnnxPath))
            {
                throw new FileNotFoundException("注册模型缺少可用于推理的 ONNX 文件。", model.OnnxPath);
            }

            var run = index.TrainingRuns.FirstOrDefault(x => x.Id == model.RunId);
            var classes = (model.Classes ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();
            if (classes.Count == 0)
            {
                var dataset = index.DatasetVersions.FirstOrDefault(x => x.Id == model.DatasetVersionId);
                classes = (dataset?.Classes ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .ToList();
            }

            var total = Stopwatch.StartNew();
            var sessionStopwatch = Stopwatch.StartNew();
            using var session = new InferenceSession(model.OnnxPath);
            sessionStopwatch.Stop();

            var input = session.InputMetadata.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(input.Key))
            {
                throw new InvalidOperationException("ONNX 模型没有可用输入。");
            }

            var inputSize = ResolvePreviewInputSize(input.Value.Dimensions, run?.Config.ImgSize ?? 640);
            var prepared = PrepareImageTensor(imagePath, inputSize, input.Key);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(input.Key, prepared.Tensor)
            };

            var inferenceStopwatch = Stopwatch.StartNew();
            using var outputs = session.Run(inputs);
            inferenceStopwatch.Stop();

            var outputTensor = outputs
                .Select(x => TryGetFloatTensor(x))
                .FirstOrDefault(x => x != null)
                ?? throw new InvalidOperationException("ONNX 输出中没有 float 张量，暂无法按 YOLO 结果解析。");

            var detections = ParseYoloDetections(
                outputTensor,
                classes,
                prepared.OriginalWidth,
                prepared.OriginalHeight,
                prepared.InputSize,
                prepared.Scale,
                prepared.PadX,
                prepared.PadY,
                confidenceThreshold,
                nmsIouThreshold);

            var previewRoot = Path.Combine(GetInsightRoot(projectRoot), "previews", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(previewRoot);
            var previewId = "preview_" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..6];
            var annotatedPath = Path.Combine(previewRoot, previewId + ".jpg");
            DrawDetections(imagePath, annotatedPath, detections);

            total.Stop();
            var result = new InferencePreviewResult
            {
                Success = true,
                Message = $"检测到 {detections.Count} 个目标。",
                ModelId = modelId,
                ImagePath = imagePath,
                AnnotatedImagePath = annotatedPath,
                OriginalWidth = prepared.OriginalWidth,
                OriginalHeight = prepared.OriginalHeight,
                InputSize = prepared.InputSize,
                Scale = prepared.Scale,
                PadX = prepared.PadX,
                PadY = prepared.PadY,
                SessionCreateMs = Math.Round(sessionStopwatch.Elapsed.TotalMilliseconds, 2),
                InferenceMs = Math.Round(inferenceStopwatch.Elapsed.TotalMilliseconds, 2),
                TotalMs = Math.Round(total.Elapsed.TotalMilliseconds, 2),
                Inputs = session.InputMetadata.Select(x => $"{x.Key}:{FormatDims(x.Value.Dimensions)}").ToList(),
                Outputs = session.OutputMetadata.Select(x => $"{x.Key}:{FormatDims(x.Value.Dimensions)}").ToList(),
                Detections = detections
            };

            result.ReportPath = Path.Combine(previewRoot, previewId + ".json");
            SaveJson(result.ReportPath, result);
            return result;
        }

        public void HandleRunModelEvaluation(JsonElement data)
        {
            try
            {
                var request = ParseModelEvaluationRequest(data);
                var report = RunModelEvaluation(request);
                _messenger.Send(new { action = "model_evaluation_completed", report });
                _messenger.Log($"模型验收评估完成: {report.Id}，F1={report.Metrics.F1:0.###}，状态={report.ReadinessStatus}", report.ReadinessStatus == "Pass" ? "success" : "warning");
            }
            catch (Exception ex)
            {
                _messenger.Error($"模型验收评估失败: {ex.Message}");
            }
        }

        public ModelEvaluationReport RunModelEvaluation(ModelEvaluationRequest request)
        {
            NormalizeModelEvaluationRequest(request);
            var index = LoadIndex(request.ProjectRoot);
            var model = index.ModelRegistry.FirstOrDefault(x => x.Id == request.ModelId)
                ?? throw new InvalidOperationException($"未找到模型: {request.ModelId}");
            if (string.IsNullOrWhiteSpace(model.OnnxPath) || !File.Exists(model.OnnxPath))
            {
                throw new FileNotFoundException("注册模型缺少可用于评估的 ONNX 文件。", model.OnnxPath);
            }

            var datasetVersionId = string.IsNullOrWhiteSpace(request.DatasetVersionId)
                ? model.DatasetVersionId
                : request.DatasetVersionId;
            var dataset = index.DatasetVersions.FirstOrDefault(x => x.Id == datasetVersionId)
                ?? throw new InvalidOperationException($"未找到数据版本: {datasetVersionId}");
            var manifest = LoadJson<DatasetManifest>(dataset.ManifestPath)
                ?? throw new FileNotFoundException("数据版本 manifest 不存在。", dataset.ManifestPath);
            var run = index.TrainingRuns.FirstOrDefault(x => x.Id == model.RunId);
            var classes = ResolveModelClasses(model, dataset);

            var split = ResolveEvaluationSplit(request.Split, manifest);
            var candidates = manifest.Items
                .Where(x => split == "all" || string.Equals(x.Split, split, StringComparison.OrdinalIgnoreCase))
                .Take(request.MaxSamples)
                .ToList();
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException($"数据版本中没有可评估样本，split={split}。");
            }

            var evalId = "eval_" + MakeSafeFileName(model.Id) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..6];
            var reportRoot = Path.Combine(GetInsightRoot(request.ProjectRoot), "evaluations", evalId);
            var galleryRoot = Path.Combine(reportRoot, "error_gallery");
            Directory.CreateDirectory(galleryRoot);

            using var session = new InferenceSession(model.OnnxPath);
            var input = session.InputMetadata.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(input.Key))
            {
                throw new InvalidOperationException("ONNX 模型没有可用输入。");
            }

            var inputSize = ResolvePreviewInputSize(input.Value.Dimensions, run?.Config.ImgSize ?? 640);
            var minThreshold = request.ConfidenceThresholds.Min();
            var basePredictions = new Dictionary<string, List<DetectionPreviewBox>>(StringComparer.OrdinalIgnoreCase);
            var groundTruth = new Dictionary<string, List<DetectionPreviewBox>>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in candidates)
            {
                var imagePath = File.Exists(item.ImagePath) ? item.ImagePath : item.SourcePath;
                if (!File.Exists(imagePath)) continue;

                var labels = item.Labels
                    .Select(label => ToGroundTruthBox(label, item.Width, item.Height, classes))
                    .Where(x => x.Width > 0 && x.Height > 0)
                    .ToList();
                groundTruth[imagePath] = labels;
                basePredictions[imagePath] = RunInferenceWithSession(
                    session,
                    input.Key,
                    imagePath,
                    classes,
                    inputSize,
                    minThreshold,
                    request.NmsIouThreshold);
            }

            if (basePredictions.Count == 0)
            {
                throw new InvalidOperationException("没有成功完成推理的评估样本。");
            }

            var sweep = request.ConfidenceThresholds
                .Select(threshold => BuildThresholdSweepPoint(basePredictions, groundTruth, threshold, request.IouThreshold))
                .OrderByDescending(x => x.F1)
                .ThenByDescending(x => x.Precision)
                .ThenBy(x => x.ConfidenceThreshold)
                .ToList();
            var best = sweep.First();
            var sortedSweep = sweep.OrderBy(x => x.ConfidenceThreshold).ToList();

            var samples = new List<EvaluationSampleResult>();
            foreach (var pair in basePredictions)
            {
                var predictions = FilterPredictions(pair.Value, best.ConfidenceThreshold);
                var labels = groundTruth.TryGetValue(pair.Key, out var gt) ? gt : new List<DetectionPreviewBox>();
                var metrics = EvaluateDetections(predictions, labels, request.IouThreshold);
                var sample = new EvaluationSampleResult
                {
                    SourcePath = pair.Key,
                    Split = split,
                    GroundTruthCount = labels.Count,
                    PredictionCount = predictions.Count,
                    TruePositive = metrics.TruePositive,
                    FalsePositive = metrics.FalsePositive,
                    FalseNegative = metrics.FalseNegative,
                    Severity = metrics.FalsePositive > 0 || metrics.FalseNegative > 0 ? "review" : "ok",
                    Predictions = predictions,
                    GroundTruth = labels
                };

                if (sample.Severity != "ok" && samples.Count(x => x.Severity != "ok") < request.MaxGallerySamples)
                {
                    var galleryPath = Path.Combine(galleryRoot, MakeSafeFileName(Path.GetFileNameWithoutExtension(pair.Key)) + "_" + Guid.NewGuid().ToString("N")[..6] + ".jpg");
                    DrawEvaluationSample(pair.Key, galleryPath, predictions, labels);
                    sample.AnnotatedImagePath = galleryPath;
                }

                samples.Add(sample);
            }

            var report = new ModelEvaluationReport
            {
                Id = evalId,
                ModelId = model.Id,
                RunId = model.RunId,
                DatasetVersionId = dataset.Id,
                Split = split,
                ReportRoot = reportRoot,
                ErrorGalleryPath = galleryRoot,
                ImageCount = basePredictions.Count,
                GroundTruthCount = groundTruth.Values.Sum(x => x.Count),
                PredictionCount = samples.Sum(x => x.PredictionCount),
                IouThreshold = request.IouThreshold,
                NmsIouThreshold = request.NmsIouThreshold,
                BestConfidenceThreshold = best.ConfidenceThreshold,
                Metrics = best,
                ThresholdSweep = sortedSweep,
                Samples = samples.OrderByDescending(x => x.FalseNegative + x.FalsePositive).ThenBy(x => x.SourcePath).ToList(),
                PerClass = BuildPerClassMetrics(basePredictions, groundTruth, classes, best.ConfidenceThreshold, request.IouThreshold)
            };

            model.SmokeTest = RunOnnxSmokeTest(model.OnnxPath);
            report.Gates = BuildReadinessGates(report, dataset, model);
            report.ReadinessStatus = ResolveReadinessStatus(report.Gates);
            report.Recommendation = BuildReadinessRecommendation(report);
            report.ReportPath = Path.Combine(reportRoot, "model_evaluation_report.json");
            SaveJson(report.ReportPath, report);

            model.LastEvaluationReportPath = report.ReportPath;
            model.LastReadinessStatus = report.ReadinessStatus;
            model.UpdatedAt = DateTime.Now;
            SaveIndex(request.ProjectRoot, index);
            if (!string.IsNullOrWhiteSpace(model.ModelCardPath))
            {
                SaveJson(model.ModelCardPath, BuildModelCard(model));
            }

            return report;
        }

        public void HandleExportModelPackage(JsonElement data)
        {
            try
            {
                var projectRoot = GetRequiredString(data, "projectRoot");
                var modelId = GetRequiredString(data, "modelId");
                var profile = GetOptionalString(data, "profile");
                var outputRoot = GetOptionalString(data, "outputRoot");
                var license = GetOptionalString(data, "license");
                var hardwareProfile = GetOptionalString(data, "hardwareProfile");
                var reportId = GetOptionalString(data, "reportId");

                var package = ExportModelPackage(projectRoot, modelId, profile, outputRoot, license, hardwareProfile, reportId);
                _messenger.Send(new { action = "model_package_exported", package });
                _messenger.Log($"平台模型包已导出: {package.PackageRoot}", "success");
            }
            catch (Exception ex)
            {
                _messenger.Error($"导出平台模型包失败: {ex.Message}");
            }
        }

        public ModelPackageRecord ExportModelPackage(
            string projectRoot,
            string modelId,
            string? profile = null,
            string? outputRoot = null,
            string? license = null,
            string? hardwareProfile = null,
            string? reportId = null)
        {
            projectRoot = Path.GetFullPath(projectRoot);
            if (!Directory.Exists(projectRoot))
            {
                throw new DirectoryNotFoundException($"项目根目录不存在: {projectRoot}");
            }

            profile = string.IsNullOrWhiteSpace(profile) ? "ClearVision" : profile.Trim();
            outputRoot = string.IsNullOrWhiteSpace(outputRoot)
                ? Path.Combine(GetInsightRoot(projectRoot), "packages")
                : Path.GetFullPath(outputRoot);
            Directory.CreateDirectory(outputRoot);

            var index = LoadIndex(projectRoot);
            var model = index.ModelRegistry.FirstOrDefault(x => x.Id == modelId)
                ?? throw new InvalidOperationException($"未找到模型: {modelId}");
            if (string.IsNullOrWhiteSpace(model.OnnxPath) || !File.Exists(model.OnnxPath))
            {
                throw new FileNotFoundException("注册模型缺少可导出的 ONNX 文件。", model.OnnxPath);
            }

            var run = index.TrainingRuns.FirstOrDefault(x => x.Id == model.RunId);
            var dataset = index.DatasetVersions.FirstOrDefault(x => x.Id == model.DatasetVersionId);
            var modelClasses = model.Classes ?? new List<string>();
            var datasetClasses = dataset?.Classes ?? new List<string>();
            var classes = modelClasses.Count > 0 ? modelClasses : datasetClasses;
            classes = classes.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
            if (classes.Count == 0)
            {
                throw new InvalidOperationException("模型缺少类别列表，无法生成平台标签契约。");
            }

            var packageId = "pkg_" + MakeSafeFileName(model.Id) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..6];
            var packageRoot = Path.Combine(outputRoot, packageId);
            Directory.CreateDirectory(packageRoot);
            Directory.CreateDirectory(Path.Combine(packageRoot, "reports"));

            var modelPath = Path.Combine(packageRoot, "model.onnx");
            File.Copy(model.OnnxPath, modelPath, overwrite: true);

            if (!string.IsNullOrWhiteSpace(model.PtPath) && File.Exists(model.PtPath))
            {
                File.Copy(model.PtPath, Path.Combine(packageRoot, "model.pt"), overwrite: true);
            }

            var labelsPath = Path.Combine(packageRoot, "labels.txt");
            File.WriteAllLines(labelsPath, classes, Encoding.UTF8);

            var evidenceReportPath = !string.IsNullOrWhiteSpace(model.LastEvaluationReportPath) && File.Exists(model.LastEvaluationReportPath)
                ? model.LastEvaluationReportPath
                : model.EvaluationReportPath;
            var reportPath = CopyIfExists(evidenceReportPath, Path.Combine(packageRoot, "reports", "evaluation_report.json"));
            CopyIfExists(dataset?.QaReportPath, Path.Combine(packageRoot, "reports", "dataset_qa_report.json"));
            CopyIfExists(run?.ConfigPath, Path.Combine(packageRoot, "reports", "training_config.json"));
            CopyIfExists(run?.EnvironmentPath, Path.Combine(packageRoot, "reports", "environment.json"));

            var modelSha = ComputeSha256(modelPath);
            var labelsSha = ComputeSha256(labelsPath);
            var imgSize = Math.Max(32, run?.Config.ImgSize ?? 640);
            var yoloVersion = ResolveYoloVersion(run?.Config.ModelSize ?? "");
            var licenseText = string.IsNullOrWhiteSpace(license) ? "REVIEW_REQUIRED" : license.Trim();
            var hardwareText = string.IsNullOrWhiteSpace(hardwareProfile)
                ? $"{Environment.MachineName}; {Environment.OSVersion}; .NET {Environment.Version}"
                : hardwareProfile.Trim();
            var reportText = string.IsNullOrWhiteSpace(reportId)
                ? ResolveReportId(reportPath, run)
                : reportId.Trim();
            var labelsContract = $"labels.txt sha256:{labelsSha}; class order matches YOLO class ids 0..{classes.Count - 1}";
            var providerFallback = "OnnxRuntime CPU fallback required; CUDA/TensorRT optional and must be recorded in release evidence.";

            var manifestPath = Path.Combine(packageRoot, "clearvision.model.manifest.json");
            SaveJson(manifestPath, BuildClearVisionManifest(
                model,
                run,
                dataset,
                classes,
                modelSha,
                labelsSha,
                imgSize,
                yoloVersion,
                licenseText,
                labelsContract,
                providerFallback,
                hardwareText,
                reportText));

            var catalogPath = Path.Combine(packageRoot, "model_catalog.json");
            SaveJson(catalogPath, BuildClearVisionModelCatalog(
                model,
                run,
                classes,
                modelSha,
                imgSize,
                yoloVersion,
                licenseText,
                labelsContract,
                providerFallback,
                dataset?.Id ?? model.DatasetVersionId,
                hardwareText,
                reportText));

            var descriptorPath = Path.Combine(packageRoot, "platform-package.json");
            SaveJson(descriptorPath, BuildPlatformDescriptor(
                packageId,
                profile,
                model,
                classes,
                modelSha,
                labelsSha,
                imgSize,
                yoloVersion,
                labelsContract));

            var readmePath = Path.Combine(packageRoot, "README.md");
            File.WriteAllText(readmePath, BuildPackageReadme(packageId, model, classes, modelSha, labelsSha, yoloVersion), Encoding.UTF8);

            var packageRecord = new ModelPackageRecord
            {
                Id = packageId,
                ModelId = model.Id,
                Profile = profile,
                PackageRoot = packageRoot,
                OutputRoot = outputRoot,
                ModelSha256 = modelSha,
                LabelsSha256 = labelsSha,
                ManifestPath = manifestPath,
                CatalogPath = catalogPath,
                PlatformDescriptorPath = descriptorPath,
                LabelsPath = labelsPath,
                ModelPath = modelPath,
                ReportPath = reportPath,
                ReadmePath = readmePath
            };

            model.LastPackagePath = packageRoot;
            model.UpdatedAt = DateTime.Now;
            model.PackageHistory ??= new List<ModelPackageRecord>();
            model.PackageHistory.Insert(0, packageRecord);
            if (model.PackageHistory.Count > 20)
            {
                model.PackageHistory = model.PackageHistory.Take(20).ToList();
            }

            SaveIndex(projectRoot, index);
            if (!string.IsNullOrWhiteSpace(model.ModelCardPath))
            {
                SaveJson(model.ModelCardPath, BuildModelCard(model));
            }

            return packageRecord;
        }

        public DatasetVersion CreateDatasetVersion(DatasetVersionRequest request)
        {
            NormalizeDatasetRequest(request);
            var versionId = "ds_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..6];
            var versionRoot = Path.Combine(GetInsightRoot(request.ProjectRoot), "datasets", versionId);
            var yoloRoot = Path.Combine(versionRoot, "yolo");

            Directory.CreateDirectory(versionRoot);
            foreach (var split in new[] { "train", "val", "test" })
            {
                Directory.CreateDirectory(Path.Combine(yoloRoot, "images", split));
                Directory.CreateDirectory(Path.Combine(yoloRoot, "labels", split));
            }

            var imageFiles = EnumerateImages(request.SourcePaths).ToList();
            if (imageFiles.Count == 0)
            {
                throw new InvalidOperationException("选定的数据源中没有找到图片。");
            }

            var classMap = request.Classes
                .Select((name, index) => new { name, index })
                .ToDictionary(x => x.name, x => x.index, StringComparer.Ordinal);

            var splitRandom = new Random(request.SplitSeed);
            var shuffled = imageFiles.OrderBy(_ => splitRandom.Next()).ToList();
            var trainCount = Math.Clamp((int)Math.Round(shuffled.Count * request.TrainRatio), 1, shuffled.Count);
            var valCount = Math.Clamp((int)Math.Round(shuffled.Count * request.ValRatio), 0, Math.Max(0, shuffled.Count - trainCount));

            var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var manifest = new DatasetManifest { VersionId = versionId };
            var classCounts = request.Classes.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);

            for (var i = 0; i < shuffled.Count; i++)
            {
                var sourcePath = shuffled[i];
                var split = i < trainCount ? "train" : i < trainCount + valCount ? "val" : "test";
                var item = MaterializeSample(sourcePath, split, yoloRoot, classMap, seenHashes);
                foreach (var label in item.Labels)
                {
                    if (classCounts.ContainsKey(label.ClassName)) classCounts[label.ClassName]++;
                }

                manifest.Items.Add(item);
            }

            var manifestPath = Path.Combine(versionRoot, "manifest.json");
            SaveJson(manifestPath, manifest);
            var report = BuildQaReport(versionId, manifest, classCounts);
            var qaPath = Path.Combine(versionRoot, "qa_report.json");
            SaveJson(qaPath, report);
            var yamlPath = Path.Combine(yoloRoot, "data.yaml");
            WriteDataYaml(yamlPath, yoloRoot, request.Classes);

            var version = new DatasetVersion
            {
                Id = versionId,
                ProjectRoot = request.ProjectRoot,
                ProjectName = request.ProjectName,
                SourcePaths = request.SourcePaths,
                Classes = request.Classes,
                SplitSeed = request.SplitSeed,
                TrainRatio = request.TrainRatio,
                ValRatio = request.ValRatio,
                TestRatio = request.TestRatio,
                VersionRoot = versionRoot,
                YoloRoot = yoloRoot,
                ManifestPath = manifestPath,
                QaReportPath = qaPath,
                DataYamlPath = yamlPath,
                ImageCount = manifest.Items.Count,
                LabelCount = manifest.Items.Sum(x => x.Labels.Count),
                IssueCount = report.IssueCount,
                TrainCount = manifest.Items.Count(x => x.Split == "train"),
                ValCount = manifest.Items.Count(x => x.Split == "val"),
                TestCount = manifest.Items.Count(x => x.Split == "test"),
                ClassCounts = classCounts
            };

            var index = LoadIndex(request.ProjectRoot);
            index.DatasetVersions.RemoveAll(x => x.Id == version.Id);
            index.DatasetVersions.Add(version);
            SaveIndex(request.ProjectRoot, index);
            return version;
        }

        public DatasetQaReport ValidateDatasetVersion(string projectRoot, string versionId)
        {
            var version = FindDatasetVersion(projectRoot, versionId);
            var manifest = LoadJson<DatasetManifest>(version.ManifestPath)
                ?? throw new FileNotFoundException("manifest.json 不存在。", version.ManifestPath);
            var report = BuildQaReport(versionId, manifest, version.ClassCounts);
            SaveJson(version.QaReportPath, report);

            var index = LoadIndex(projectRoot);
            var current = index.DatasetVersions.FirstOrDefault(x => x.Id == versionId);
            if (current != null)
            {
                current.IssueCount = report.IssueCount;
                current.LabelCount = report.LabelCount;
                current.ClassCounts = report.ClassCounts;
                SaveIndex(projectRoot, index);
            }

            return report;
        }

        public async Task<TrainingRunRecord> StartTrainingRunAsync(TrainingRunConfig config)
        {
            NormalizeTrainingConfig(config);
            var dataset = FindDatasetVersion(config.ProjectRoot, config.DatasetVersionId);
            var runId = "run_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..6];
            var runRoot = Path.Combine(GetInsightRoot(config.ProjectRoot), "runs", runId);
            var cancellationToken = ReserveActiveRun(runId);

            try
            {
                Directory.CreateDirectory(runRoot);
                var run = new TrainingRunRecord
                {
                    Id = runId,
                    ProjectRoot = config.ProjectRoot,
                    DatasetVersionId = config.DatasetVersionId,
                    ExperimentName = config.ExperimentName,
                    Status = "Preparing",
                    RunRoot = runRoot,
                    ConfigPath = Path.Combine(runRoot, "config.json"),
                    EnvironmentPath = Path.Combine(runRoot, "environment.json"),
                    LogPath = Path.Combine(runRoot, "train.log"),
                    MetricsPath = Path.Combine(runRoot, "metrics.jsonl"),
                    Config = config
                };

                SaveJson(run.ConfigPath, config);
                SaveJson(run.EnvironmentPath, CaptureEnvironment(config.PythonPath, runRoot));
                UpsertRun(config.ProjectRoot, run);
                TrySyncTrainingJob(run);
                _messenger.Send(new { action = "industrial_training_started", run });

                await ExecuteTrainingRunAsync(run, dataset, cancellationToken);
                return run;
            }
            catch
            {
                ClearActiveRun(runId);
                throw;
            }
        }

        public void StopTrainingRun()
        {
            lock (_processLock)
            {
                if (string.IsNullOrWhiteSpace(_activeRunId) && _activeRunCts == null) return;

                try
                {
                    _stopRequested = true;
                    _activeRunCts?.Cancel();

                    _messenger.Log($"已发送停止信号: {_activeRunId}", "warning");
                }
                catch (Exception ex)
                {
                    _messenger.Error($"停止训练失败: {ex.Message}");
                }
            }
        }

        public ModelRegistryEntry PromoteModel(string projectRoot, string modelId, string status)
        {
            var index = LoadIndex(projectRoot);
            var entry = index.ModelRegistry.FirstOrDefault(x => x.Id == modelId)
                ?? throw new InvalidOperationException($"未找到模型: {modelId}");

            if (string.Equals(status, "Production", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var model in index.ModelRegistry.Where(x => x.IsPinned && x.Id != entry.Id))
                {
                    model.IsPinned = false;
                    model.UpdatedAt = DateTime.Now;
                }

                entry.IsPinned = true;
            }
            else if (string.Equals(status, "Archived", StringComparison.OrdinalIgnoreCase))
            {
                entry.IsPinned = false;
            }

            entry.Status = status;
            entry.UpdatedAt = DateTime.Now;
            SaveIndex(projectRoot, index);
            SaveJson(entry.ModelCardPath, BuildModelCard(entry));
            return entry;
        }

        public static string BuildYoloTrainArguments(TrainingRunConfig config, DatasetVersion dataset, string runRoot)
        {
            var modelName = ResolveYoloModelName(config.ModelSize);
            if (config.Resume && !string.IsNullOrWhiteSpace(config.ResumeRunId))
            {
                var last = Path.Combine(Path.GetDirectoryName(runRoot) ?? "", config.ResumeRunId, "train", "weights", "last.pt");
                if (File.Exists(last)) modelName = QuotePath(last);
            }

            var args = new StringBuilder();
            args.Append("-c \"from ultralytics.cfg import entrypoint; entrypoint()\" detect train ");
            args.Append($"model={modelName} ");
            args.Append($"data={QuotePath(dataset.DataYamlPath)} ");
            args.Append($"project={QuotePath(runRoot)} name=train exist_ok=True ");
            args.Append($"epochs={config.Epochs} batch={config.BatchSize} imgsz={config.ImgSize} ");
            args.Append($"patience={config.Patience} workers={config.Workers} seed={config.Seed} ");
            if (!string.IsNullOrWhiteSpace(config.GpuIndex)) args.Append($"device={config.GpuIndex} ");
            if (config.Resume) args.Append("resume=True ");

            foreach (var pair in config.AdvancedOptions.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
                args.Append($"{pair.Key}={QuoteCliValue(pair.Value)} ");
            }

            return args.ToString().Trim();
        }

        private async Task ExecuteTrainingRunAsync(
            TrainingRunRecord run,
            DatasetVersion dataset,
            CancellationToken cancellationToken)
        {
            run.Status = "Running";
            run.StartedAt = DateTime.Now;
            UpsertRun(run.ProjectRoot, run);
            TrySyncTrainingJob(run);
            AppendLog(run, "训练进程启动。");

            if (IsStopRequested(run.Id))
            {
                MarkRunStopped(run);
                return;
            }

            if (!IsValidPython(run.Config.PythonPath))
            {
                FailRun(run, $"Python 路径无效: {run.Config.PythonPath}");
                return;
            }

            var arguments = BuildYoloTrainArguments(run.Config, dataset, run.RunRoot);
            File.WriteAllText(Path.Combine(run.RunRoot, "train_command.txt"), arguments + Environment.NewLine, Encoding.UTF8);
            AppendLog(run, "训练命令: " + arguments);

            try
            {
                var result = await _processRunner.RunAsync(
                    new ProcessStartSpec
                    {
                        FileName = run.Config.PythonPath,
                        Arguments = arguments,
                        WorkingDirectory = run.RunRoot,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                        EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["PYTHONIOENCODING"] = "utf-8",
                            ["KMP_DUPLICATE_LIB_OK"] = "TRUE"
                        }
                    },
                    line => HandleTrainingOutput(run, line),
                    cancellationToken);

                var wasStopped = false;
                lock (_processLock)
                {
                    wasStopped = result.WasCanceled || (_stopRequested && _activeRunId == run.Id);
                }

                if (wasStopped)
                {
                    MarkRunStopped(run);
                    return;
                }

                if (!result.Succeeded)
                {
                    var exitCode = result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
                    FailRun(run, $"训练进程退出码: {exitCode}。请检查 Python 环境、ultralytics 安装、GPU device 和数据路径。");
                    return;
                }

                await CompleteRunAsync(run, dataset, cancellationToken);
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    MarkRunStopped(run);
                }
                else
                {
                    FailRun(run, ex.Message);
                }
            }
        }

        private async Task CompleteRunAsync(
            TrainingRunRecord run,
            DatasetVersion dataset,
            CancellationToken cancellationToken)
        {
            LoadMetricsFromResultsCsv(run);
            run.BestPtPath = FindArtifact(run.RunRoot, "best.pt");
            run.LastPtPath = FindArtifact(run.RunRoot, "last.pt");

            if (string.IsNullOrWhiteSpace(run.BestPtPath))
            {
                FailRun(run, "训练完成但未找到 best.pt。");
                return;
            }

            run.Status = "Evaluating";
            UpsertRun(run.ProjectRoot, run);
            TrySyncTrainingJob(run);
            var report = BuildEvaluationReport(run, dataset);
            run.EvaluationReportPath = Path.Combine(run.RunRoot, "evaluation_report.json");
            SaveJson(run.EvaluationReportPath, report);

            run.Status = "Exporting";
            UpsertRun(run.ProjectRoot, run);
            TrySyncTrainingJob(run);
            run.OnnxPath = await ExportOnnxAsync(run, cancellationToken);

            if (!string.IsNullOrWhiteSpace(run.OnnxPath))
            {
                RegisterModel(run, dataset);
            }
            else
            {
                FailRun(run, "训练完成，但 ONNX 导出失败；请查看 train.log 和 Python/ultralytics export 环境。");
                return;
            }

            run.Status = "Completed";
            run.FinishedAt = DateTime.Now;
            UpsertRun(run.ProjectRoot, run);
            TrySyncTrainingJob(run);
            _messenger.Send(new { action = "industrial_training_finished", success = true, run });
            _messenger.Log($"专业训练完成: {run.Id}", "success");
        }

        private async Task<string> ExportOnnxAsync(TrainingRunRecord run, CancellationToken cancellationToken)
        {
            try
            {
                AppendLog(run, "开始导出 ONNX。");
                var result = await _processRunner.RunAsync(
                    new ProcessStartSpec
                    {
                        FileName = run.Config.PythonPath,
                        Arguments = $"-c \"from ultralytics.cfg import entrypoint; entrypoint()\" export model={QuotePath(run.BestPtPath)} format=onnx imgsz={run.Config.ImgSize} simplify=True",
                        WorkingDirectory = run.RunRoot,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                        EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["PYTHONIOENCODING"] = "utf-8"
                        }
                    },
                    line => AppendLog(run, line),
                    cancellationToken);

                if (!result.Succeeded)
                {
                    var exitCode = result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
                    AppendLog(run, $"ONNX 导出失败，退出码: {exitCode}");
                    return "";
                }

                var exported = Path.ChangeExtension(run.BestPtPath, ".onnx");
                if (!File.Exists(exported))
                {
                    AppendLog(run, "ONNX 导出命令完成，但未找到导出文件。");
                    return "";
                }

                return exported;
            }
            catch (Exception ex)
            {
                AppendLog(run, $"ONNX 导出异常: {ex.Message}");
                return "";
            }
        }

        private void RegisterModel(TrainingRunRecord run, DatasetVersion dataset)
        {
            var modelRoot = Path.Combine(GetInsightRoot(run.ProjectRoot), "models", run.Id);
            Directory.CreateDirectory(modelRoot);

            var ptPath = Path.Combine(modelRoot, Path.GetFileName(run.BestPtPath));
            File.Copy(run.BestPtPath, ptPath, overwrite: true);
            var onnxPath = Path.Combine(modelRoot, Path.GetFileName(run.OnnxPath));
            File.Copy(run.OnnxPath, onnxPath, overwrite: true);

            var entry = new ModelRegistryEntry
            {
                Id = "model_" + run.Id,
                RunId = run.Id,
                DatasetVersionId = dataset.Id,
                Status = "Candidate",
                PtPath = ptPath,
                OnnxPath = onnxPath,
                EvaluationReportPath = run.EvaluationReportPath,
                Classes = dataset.Classes,
                ModelCardPath = Path.Combine(modelRoot, "model_card.json"),
                SmokeTest = RunOnnxSmokeTest(onnxPath)
            };

            SaveJson(entry.ModelCardPath, BuildModelCard(entry));
            var index = LoadIndex(run.ProjectRoot);
            index.ModelRegistry.RemoveAll(x => x.Id == entry.Id);
            index.ModelRegistry.Add(entry);
            SaveIndex(run.ProjectRoot, index);
            _messenger.Send(new { action = "model_registered", model = entry });
        }

        private void FailRun(TrainingRunRecord run, string reason)
        {
            run.Status = "Failed";
            run.FailureReason = reason;
            run.FinishedAt = DateTime.Now;
            UpsertRun(run.ProjectRoot, run);
            TrySyncTrainingJob(run);
            AppendLog(run, "失败原因: " + reason);
            _messenger.Send(new { action = "industrial_training_finished", success = false, run });
            _messenger.Error($"专业训练失败: {reason}");
            ClearActiveRun(run.Id);
        }

        private void MarkRunStopped(TrainingRunRecord run)
        {
            run.Status = "Stopped";
            run.FailureReason = "用户停止训练。";
            run.FinishedAt = DateTime.Now;
            UpsertRun(run.ProjectRoot, run);
            TrySyncTrainingJob(run);
            AppendLog(run, run.FailureReason);
            _messenger.Send(new { action = "industrial_training_finished", success = false, run });
            ClearActiveRun(run.Id);
        }

        private void TrySyncTrainingJob(TrainingRunRecord run)
        {
            try
            {
                var record = new TrainingJobRecord
                {
                    JobId = run.Id,
                    ProviderId = LocalYoloTrainingProvider.ProviderId,
                    State = MapTrainingRunState(run.Status),
                    ProjectRoot = run.ProjectRoot,
                    DatasetVersionId = run.DatasetVersionId,
                    ExternalJobId = run.Id,
                    ArtifactRoot = string.IsNullOrWhiteSpace(run.OnnxPath) ? run.RunRoot : run.OnnxPath,
                    FailureReason = run.FailureReason,
                    Metadata =
                    {
                        ["experimentName"] = run.ExperimentName,
                        ["runRoot"] = run.RunRoot,
                        ["status"] = run.Status
                    }
                };

                _jobStore.UpsertAsync(record, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _messenger.Log($"同步训练任务状态失败: {ex.Message}", "warning");
            }
        }

        private static TrainingProviderJobState MapTrainingRunState(string status)
        {
            return status switch
            {
                "Completed" => TrainingProviderJobState.Completed,
                "Failed" => TrainingProviderJobState.Failed,
                "Stopped" => TrainingProviderJobState.Stopped,
                "Running" or "Evaluating" or "Exporting" => TrainingProviderJobState.Running,
                _ => TrainingProviderJobState.Queued
            };
        }

        private CancellationToken ReserveActiveRun(string runId)
        {
            lock (_processLock)
            {
                if (!string.IsNullOrWhiteSpace(_activeRunId) || _activeRunCts != null)
                {
                    throw new InvalidOperationException($"已有专业训练运行中: {_activeRunId}。请先停止或等待完成。");
                }

                _activeRunCts = new CancellationTokenSource();
                _activeRunId = runId;
                _stopRequested = false;
                return _activeRunCts.Token;
            }
        }

        private bool IsStopRequested(string runId)
        {
            lock (_processLock)
            {
                return _activeRunId == runId && _stopRequested;
            }
        }

        private void ClearActiveRun(string runId)
        {
            lock (_processLock)
            {
                if (_activeRunId != runId) return;
                _activeRunCts?.Dispose();
                _activeRunCts = null;
                _activeRunId = "";
                _stopRequested = false;
            }
        }

        private void HandleTrainingOutput(TrainingRunRecord run, string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            var clean = line.Trim();
            AppendLog(run, clean);
            _messenger.Log(clean, "info");

            var update = _metricParser.TryParseMetrics(clean);
            if (update == null) return;

            var metric = new MetricPoint
            {
                Epoch = update.Epoch,
                BoxLoss = update.BoxLoss,
                Map50 = update.Map50,
                Map5095 = update.Map5095,
                Progress = update.Progress
            };
            run.Metrics.Add(metric);
            AppendJsonLine(run.MetricsPath, metric);
            UpsertRun(run.ProjectRoot, run);
            _messenger.Send(new { action = "industrial_training_data", runId = run.Id, metric });
        }

        private DatasetManifestItem MaterializeSample(
            string sourcePath,
            string split,
            string yoloRoot,
            Dictionary<string, int> classMap,
            HashSet<string> seenHashes)
        {
            var sha = ComputeSha256(sourcePath);
            var item = new DatasetManifestItem
            {
                SourcePath = sourcePath,
                Split = split,
                Sha256 = sha
            };

            if (!seenHashes.Add(sha))
            {
                item.Issues.Add("duplicate_image");
            }

            try
            {
                using var image = System.Drawing.Image.FromFile(sourcePath);
                item.Width = image.Width;
                item.Height = image.Height;
            }
            catch
            {
                item.Issues.Add("bad_image");
            }

            var outputBase = MakeSafeFileName(Path.GetFileNameWithoutExtension(sourcePath)) + "_" + sha[..8];
            var outputImage = Path.Combine(yoloRoot, "images", split, outputBase + Path.GetExtension(sourcePath).ToLowerInvariant());
            var outputLabel = Path.Combine(yoloRoot, "labels", split, outputBase + ".txt");
            File.Copy(sourcePath, outputImage, overwrite: true);
            item.ImagePath = outputImage;
            item.LabelPath = outputLabel;

            item.Labels = LoadLabels(sourcePath, item.Width, item.Height, classMap, item.Issues);
            if (item.Labels.Count == 0 && !item.Issues.Contains("missing_label"))
            {
                item.Issues.Add("empty_label");
            }

            File.WriteAllLines(outputLabel, item.Labels.Select(ToYoloLine), Encoding.UTF8);
            return item;
        }

        private static List<DatasetLabel> LoadLabels(
            string imagePath,
            int imageWidth,
            int imageHeight,
            Dictionary<string, int> classMap,
            List<string> issues)
        {
            var txtPath = Path.ChangeExtension(imagePath, ".txt");
            var jsonPath = Path.ChangeExtension(imagePath, ".json");

            if (File.Exists(txtPath))
            {
                return LoadYoloTxtLabels(txtPath, classMap, issues);
            }

            if (File.Exists(jsonPath))
            {
                return LoadLabelMeLabels(jsonPath, imageWidth, imageHeight, classMap, issues);
            }

            issues.Add("missing_label");
            return new List<DatasetLabel>();
        }

        private static List<DatasetLabel> LoadYoloTxtLabels(string txtPath, Dictionary<string, int> classMap, List<string> issues)
        {
            var labels = new List<DatasetLabel>();
            var classNames = classMap.OrderBy(x => x.Value).Select(x => x.Key).ToArray();

            foreach (var line in File.ReadAllLines(txtPath))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5)
                {
                    issues.Add("invalid_label_line");
                    continue;
                }

                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var classId) ||
                    classId < 0 ||
                    classId >= classNames.Length)
                {
                    issues.Add("unknown_class");
                    continue;
                }

                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                    !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                    !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ||
                    !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
                {
                    issues.Add("invalid_label_number");
                    continue;
                }

                if (w <= 0 || h <= 0)
                {
                    issues.Add("zero_area_box");
                    continue;
                }

                if (x < 0 || x > 1 || y < 0 || y > 1 || w > 1 || h > 1)
                {
                    issues.Add("box_out_of_bounds");
                }

                labels.Add(new DatasetLabel
                {
                    ClassId = classId,
                    ClassName = classNames[classId],
                    X = Clamp01(x),
                    Y = Clamp01(y),
                    W = Clamp01(w),
                    H = Clamp01(h)
                });
            }

            if (labels.Count == 0) issues.Add("empty_label");
            return labels;
        }

        private static List<DatasetLabel> LoadLabelMeLabels(
            string jsonPath,
            int imageWidth,
            int imageHeight,
            Dictionary<string, int> classMap,
            List<string> issues)
        {
            var labels = new List<DatasetLabel>();
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath, Encoding.UTF8));
            var root = doc.RootElement;
            var width = root.TryGetProperty("imageWidth", out var widthEl) && widthEl.ValueKind == JsonValueKind.Number
                ? widthEl.GetInt32()
                : imageWidth;
            var height = root.TryGetProperty("imageHeight", out var heightEl) && heightEl.ValueKind == JsonValueKind.Number
                ? heightEl.GetInt32()
                : imageHeight;

            if (width <= 0 || height <= 0)
            {
                issues.Add("bad_image_size");
                return labels;
            }

            if (!root.TryGetProperty("shapes", out var shapes) || shapes.ValueKind != JsonValueKind.Array)
            {
                issues.Add("empty_label");
                return labels;
            }

            foreach (var shape in shapes.EnumerateArray())
            {
                var labelName = shape.TryGetProperty("label", out var labelEl) ? labelEl.GetString() ?? "" : "";
                if (!classMap.TryGetValue(labelName, out var classId))
                {
                    issues.Add("unknown_class");
                    continue;
                }

                if (!shape.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
                {
                    issues.Add("invalid_label_line");
                    continue;
                }

                var xs = new List<double>();
                var ys = new List<double>();
                foreach (var point in points.EnumerateArray())
                {
                    if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2) continue;
                    xs.Add(point[0].GetDouble());
                    ys.Add(point[1].GetDouble());
                }

                if (xs.Count == 0 || ys.Count == 0)
                {
                    issues.Add("invalid_label_line");
                    continue;
                }

                var minX = xs.Min();
                var maxX = xs.Max();
                var minY = ys.Min();
                var maxY = ys.Max();
                var boxW = (maxX - minX) / width;
                var boxH = (maxY - minY) / height;
                if (boxW <= 0 || boxH <= 0)
                {
                    issues.Add("zero_area_box");
                    continue;
                }

                labels.Add(new DatasetLabel
                {
                    ClassId = classId,
                    ClassName = labelName,
                    X = Clamp01((minX + maxX) / 2 / width),
                    Y = Clamp01((minY + maxY) / 2 / height),
                    W = Clamp01(boxW),
                    H = Clamp01(boxH)
                });
            }

            if (labels.Count == 0) issues.Add("empty_label");
            return labels;
        }

        private static DatasetQaReport BuildQaReport(
            string versionId,
            DatasetManifest manifest,
            Dictionary<string, int> classCounts)
        {
            var issueGroups = manifest.Items
                .SelectMany(x => x.Issues.Distinct(StringComparer.Ordinal))
                .GroupBy(x => x, StringComparer.Ordinal)
                .Select(g => new QaIssueSummary
                {
                    Code = g.Key,
                    Count = g.Count(),
                    Severity = g.Key is "bad_image" or "unknown_class" or "box_out_of_bounds" ? "error" : "warning"
                })
                .OrderByDescending(x => x.Severity == "error")
                .ThenByDescending(x => x.Count)
                .ToList();

            if (classCounts.Count > 0)
            {
                var zeroClassCount = classCounts.Count(x => x.Value == 0);
                var nonZero = classCounts.Where(x => x.Value > 0).Select(x => x.Value).ToList();
                if (zeroClassCount > 0)
                {
                    issueGroups.Add(new QaIssueSummary { Code = "class_imbalance", Count = zeroClassCount, Severity = "warning" });
                }
                else if (nonZero.Count > 1 && nonZero.Max() / (double)nonZero.Min() >= 10)
                {
                    issueGroups.Add(new QaIssueSummary { Code = "class_imbalance", Count = 1, Severity = "warning" });
                }
            }

            var report = new DatasetQaReport
            {
                VersionId = versionId,
                ImageCount = manifest.Items.Count,
                LabelCount = manifest.Items.Sum(x => x.Labels.Count),
                IssueCount = issueGroups.Sum(x => x.Count),
                ClassCounts = new Dictionary<string, int>(classCounts, StringComparer.Ordinal),
                Issues = issueGroups,
                ProblemSamples = manifest.Items.Where(x => x.Issues.Count > 0).Take(100).ToList()
            };

            if (report.ImageCount == 0)
            {
                report.Recommendation = "没有可训练图片，请重新选择数据源。";
            }
            else if (report.Issues.Any(x => x.Severity == "error"))
            {
                report.Recommendation = "存在阻断级数据问题，请先修复坏图、未知类别或越界框。";
            }
            else if (classCounts.Any(x => x.Value == 0))
            {
                report.Recommendation = "部分类别没有样本，建议补充样本后再训练。";
            }
            else
            {
                report.Recommendation = "数据集可进入训练，建议先跑快速冒烟预设。";
            }

            return report;
        }

        private EvaluationReport BuildEvaluationReport(TrainingRunRecord run, DatasetVersion dataset)
        {
            var last = run.Metrics.LastOrDefault(x => x.Map50.HasValue || x.Map5095.HasValue);
            var report = new EvaluationReport
            {
                Id = "eval_" + run.Id,
                RunId = run.Id,
                DatasetVersionId = dataset.Id,
                Map50 = last?.Map50,
                Map5095 = last?.Map5095,
                Artifacts = Directory.Exists(Path.Combine(run.RunRoot, "train"))
                    ? Directory.GetFiles(Path.Combine(run.RunRoot, "train"), "*.*", SearchOption.AllDirectories)
                        .Where(x => x.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                        .ToList()
                    : new List<string>()
            };

            foreach (var cls in dataset.Classes)
            {
                report.PerClass.Add(new PerClassMetric
                {
                    ClassName = cls,
                    Support = dataset.ClassCounts.TryGetValue(cls, out var support) ? support : 0,
                    Map50 = report.Map50
                });
            }

            return report;
        }

        private void LoadMetricsFromResultsCsv(TrainingRunRecord run)
        {
            var resultsCsv = Directory.GetFiles(run.RunRoot, "results.csv", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(resultsCsv)) return;

            var lines = File.ReadAllLines(resultsCsv);
            if (lines.Length < 2) return;

            var headers = lines[0].Split(',').Select(x => x.Trim()).ToArray();
            foreach (var line in lines.Skip(1))
            {
                var values = line.Split(',').Select(x => x.Trim()).ToArray();
                var metric = new MetricPoint
                {
                    Epoch = GetCsvValue(headers, values, "epoch")
                };
                metric.BoxLoss = TryGetCsvDouble(headers, values, "train/box_loss");
                metric.Map50 = TryGetCsvDouble(headers, values, "metrics/mAP50(B)") ?? TryGetCsvDouble(headers, values, "metrics/mAP50");
                metric.Map5095 = TryGetCsvDouble(headers, values, "metrics/mAP50-95(B)") ?? TryGetCsvDouble(headers, values, "metrics/mAP50-95");
                run.Metrics.Add(metric);
                AppendJsonLine(run.MetricsPath, metric);
            }
        }

        private OnnxSmokeTest RunOnnxSmokeTest(string onnxPath)
        {
            var result = new OnnxSmokeTest();
            if (string.IsNullOrWhiteSpace(onnxPath) || !File.Exists(onnxPath))
            {
                result.Message = "ONNX 文件不存在。";
                return result;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var session = new InferenceSession(onnxPath);
                stopwatch.Stop();
                result.Success = true;
                result.SessionCreateMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2);
                result.Inputs = session.InputMetadata.Select(x => $"{x.Key}:{FormatDims(x.Value.Dimensions)}").ToList();
                result.Outputs = session.OutputMetadata.Select(x => $"{x.Key}:{FormatDims(x.Value.Dimensions)}").ToList();
                result.Message = "ONNX 加载成功。";
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.Success = false;
                result.SessionCreateMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2);
                result.Message = ex.Message;
            }

            return result;
        }

        private static List<string> ResolveModelClasses(ModelRegistryEntry model, DatasetVersion? dataset)
        {
            var classes = (model.Classes ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();
            if (classes.Count > 0)
            {
                return classes;
            }

            return (dataset?.Classes ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();
        }

        private static string ResolveEvaluationSplit(string requestedSplit, DatasetManifest manifest)
        {
            var split = string.IsNullOrWhiteSpace(requestedSplit) ? "test" : requestedSplit.Trim().ToLowerInvariant();
            if (split == "auto")
            {
                if (manifest.Items.Any(x => x.Split == "test")) return "test";
                if (manifest.Items.Any(x => x.Split == "val")) return "val";
                return "all";
            }

            if (split is "train" or "val" or "test" or "all")
            {
                return split;
            }

            return "test";
        }

        private static List<DetectionPreviewBox> RunInferenceWithSession(
            InferenceSession session,
            string inputName,
            string imagePath,
            IReadOnlyList<string> classes,
            int inputSize,
            double confidenceThreshold,
            double nmsIouThreshold)
        {
            var prepared = PrepareImageTensor(imagePath, inputSize, inputName);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, prepared.Tensor)
            };

            using var outputs = session.Run(inputs);
            var outputTensor = outputs
                .Select(x => TryGetFloatTensor(x))
                .FirstOrDefault(x => x != null)
                ?? throw new InvalidOperationException("ONNX 输出中没有 float 张量，暂无法按 YOLO 结果解析。");

            return ParseYoloDetections(
                outputTensor,
                classes,
                prepared.OriginalWidth,
                prepared.OriginalHeight,
                prepared.InputSize,
                prepared.Scale,
                prepared.PadX,
                prepared.PadY,
                confidenceThreshold,
                nmsIouThreshold);
        }

        private static DetectionPreviewBox ToGroundTruthBox(DatasetLabel label, int imageWidth, int imageHeight, IReadOnlyList<string> classes)
        {
            if (imageWidth <= 0 || imageHeight <= 0)
            {
                return new DetectionPreviewBox();
            }

            var width = label.W * imageWidth;
            var height = label.H * imageHeight;
            var x = label.X * imageWidth - width / 2;
            var y = label.Y * imageHeight - height / 2;
            var className = !string.IsNullOrWhiteSpace(label.ClassName)
                ? label.ClassName
                : label.ClassId >= 0 && label.ClassId < classes.Count
                    ? classes[label.ClassId]
                    : $"class_{label.ClassId}";

            return new DetectionPreviewBox
            {
                ClassId = label.ClassId,
                ClassName = className,
                Confidence = 1,
                X = Math.Round(Math.Clamp(x, 0, imageWidth), 2),
                Y = Math.Round(Math.Clamp(y, 0, imageHeight), 2),
                Width = Math.Round(Math.Clamp(width, 0, imageWidth), 2),
                Height = Math.Round(Math.Clamp(height, 0, imageHeight), 2)
            };
        }

        private static ThresholdSweepPoint BuildThresholdSweepPoint(
            Dictionary<string, List<DetectionPreviewBox>> basePredictions,
            Dictionary<string, List<DetectionPreviewBox>> groundTruth,
            double confidenceThreshold,
            double iouThreshold)
        {
            var total = new DetectionQualityMetrics();
            foreach (var pair in basePredictions)
            {
                var predictions = FilterPredictions(pair.Value, confidenceThreshold);
                var labels = groundTruth.TryGetValue(pair.Key, out var gt) ? gt : new List<DetectionPreviewBox>();
                var metrics = EvaluateDetections(predictions, labels, iouThreshold);
                total.TruePositive += metrics.TruePositive;
                total.FalsePositive += metrics.FalsePositive;
                total.FalseNegative += metrics.FalseNegative;
            }

            FillRates(total);
            return new ThresholdSweepPoint
            {
                ConfidenceThreshold = Math.Round(confidenceThreshold, 4),
                TruePositive = total.TruePositive,
                FalsePositive = total.FalsePositive,
                FalseNegative = total.FalseNegative,
                Precision = total.Precision,
                Recall = total.Recall,
                F1 = total.F1
            };
        }

        private static List<DetectionPreviewBox> FilterPredictions(IEnumerable<DetectionPreviewBox> predictions, double confidenceThreshold)
        {
            return predictions
                .Where(x => x.Confidence >= confidenceThreshold)
                .OrderByDescending(x => x.Confidence)
                .ToList();
        }

        public static DetectionQualityMetrics EvaluateDetections(
            IReadOnlyList<DetectionPreviewBox> predictions,
            IReadOnlyList<DetectionPreviewBox> groundTruth,
            double iouThreshold)
        {
            var matchedGroundTruth = new bool[groundTruth.Count];
            var truePositive = 0;

            foreach (var prediction in predictions.OrderByDescending(x => x.Confidence))
            {
                var bestIndex = -1;
                var bestIou = 0.0;
                for (var i = 0; i < groundTruth.Count; i++)
                {
                    if (matchedGroundTruth[i] || groundTruth[i].ClassId != prediction.ClassId) continue;
                    var iou = CalculateIou(prediction, groundTruth[i]);
                    if (iou > bestIou)
                    {
                        bestIou = iou;
                        bestIndex = i;
                    }
                }

                if (bestIndex >= 0 && bestIou >= iouThreshold)
                {
                    matchedGroundTruth[bestIndex] = true;
                    truePositive++;
                }
            }

            var metrics = new DetectionQualityMetrics
            {
                TruePositive = truePositive,
                FalsePositive = Math.Max(0, predictions.Count - truePositive),
                FalseNegative = Math.Max(0, groundTruth.Count - truePositive)
            };
            FillRates(metrics);
            return metrics;
        }

        private static List<PerClassEvaluationMetric> BuildPerClassMetrics(
            Dictionary<string, List<DetectionPreviewBox>> basePredictions,
            Dictionary<string, List<DetectionPreviewBox>> groundTruth,
            IReadOnlyList<string> classes,
            double confidenceThreshold,
            double iouThreshold)
        {
            var result = new List<PerClassEvaluationMetric>();
            for (var classId = 0; classId < classes.Count; classId++)
            {
                var total = new DetectionQualityMetrics();
                var support = 0;
                foreach (var pair in basePredictions)
                {
                    var predictions = FilterPredictions(pair.Value, confidenceThreshold)
                        .Where(x => x.ClassId == classId)
                        .ToList();
                    var labels = groundTruth.TryGetValue(pair.Key, out var gt)
                        ? gt.Where(x => x.ClassId == classId).ToList()
                        : new List<DetectionPreviewBox>();
                    support += labels.Count;
                    var metrics = EvaluateDetections(predictions, labels, iouThreshold);
                    total.TruePositive += metrics.TruePositive;
                    total.FalsePositive += metrics.FalsePositive;
                    total.FalseNegative += metrics.FalseNegative;
                }

                FillRates(total);
                result.Add(new PerClassEvaluationMetric
                {
                    ClassId = classId,
                    ClassName = classes[classId],
                    Support = support,
                    TruePositive = total.TruePositive,
                    FalsePositive = total.FalsePositive,
                    FalseNegative = total.FalseNegative,
                    Precision = total.Precision,
                    Recall = total.Recall,
                    F1 = total.F1
                });
            }

            return result;
        }

        private static List<ReadinessGateResult> BuildReadinessGates(
            ModelEvaluationReport report,
            DatasetVersion dataset,
            ModelRegistryEntry model)
        {
            var gates = new List<ReadinessGateResult>
            {
                BuildGate("dataset_qa", "数据 QA", dataset.IssueCount == 0, dataset.IssueCount <= 5,
                    dataset.IssueCount == 0 ? "数据版本无 QA 问题。" : $"数据版本仍有 {dataset.IssueCount} 个 QA 问题。"),
                BuildGate("onnx_smoke", "ONNX 加载", model.SmokeTest.Success, false,
                    model.SmokeTest.Success ? "ONNX Runtime 可加载模型。" : $"ONNX smoke 未通过: {model.SmokeTest.Message}"),
                BuildGate("precision", "Precision", report.Metrics.Precision >= 0.9, report.Metrics.Precision >= 0.75,
                    $"Precision={report.Metrics.Precision:0.###}"),
                BuildGate("recall", "Recall", report.Metrics.Recall >= 0.9, report.Metrics.Recall >= 0.75,
                    $"Recall={report.Metrics.Recall:0.###}"),
                BuildGate("error_gallery", "误检漏检样本墙", true, false,
                    report.Samples.Any(x => x.Severity != "ok")
                        ? "已生成误检/漏检样本墙。"
                        : "当前阈值下未发现误检/漏检样本。")
            };

            if (string.IsNullOrWhiteSpace(model.LastPackagePath))
            {
                gates.Add(new ReadinessGateResult
                {
                    Code = "platform_package",
                    Name = "平台交付包",
                    Status = "Review",
                    Message = "尚未导出 ClearVision/ONNX Runtime 平台包。"
                });
            }
            else
            {
                gates.Add(new ReadinessGateResult
                {
                    Code = "platform_package",
                    Name = "平台交付包",
                    Status = "Pass",
                    Message = $"最近交付包: {model.LastPackagePath}"
                });
            }

            return gates;
        }

        private static ReadinessGateResult BuildGate(string code, string name, bool pass, bool warn, string message)
        {
            return new ReadinessGateResult
            {
                Code = code,
                Name = name,
                Status = pass ? "Pass" : warn ? "Review" : "Fail",
                Message = message
            };
        }

        private static string ResolveReadinessStatus(IEnumerable<ReadinessGateResult> gates)
        {
            var list = gates.ToList();
            if (list.Any(x => x.Status == "Fail")) return "Fail";
            if (list.Any(x => x.Status == "Review")) return "Review";
            return "Pass";
        }

        private static string BuildReadinessRecommendation(ModelEvaluationReport report)
        {
            if (report.ReadinessStatus == "Pass")
            {
                return $"模型通过当前门禁。推荐阈值 {report.BestConfidenceThreshold:0.###}，仍需现场签核后进入生产。";
            }

            var failed = report.Gates.Where(x => x.Status != "Pass").Select(x => x.Name).ToList();
            return $"暂不建议生产钉选。需处理: {string.Join(", ", failed)}。推荐阈值 {report.BestConfidenceThreshold:0.###}。";
        }

        private static void FillRates(DetectionQualityMetrics metrics)
        {
            metrics.Precision = metrics.TruePositive + metrics.FalsePositive == 0
                ? 0
                : Math.Round(metrics.TruePositive / (double)(metrics.TruePositive + metrics.FalsePositive), 6);
            metrics.Recall = metrics.TruePositive + metrics.FalseNegative == 0
                ? 0
                : Math.Round(metrics.TruePositive / (double)(metrics.TruePositive + metrics.FalseNegative), 6);
            metrics.F1 = metrics.Precision + metrics.Recall == 0
                ? 0
                : Math.Round(2 * metrics.Precision * metrics.Recall / (metrics.Precision + metrics.Recall), 6);
        }

        private static Tensor<float>? TryGetFloatTensor(DisposableNamedOnnxValue value)
        {
            try
            {
                return value.AsTensor<float>();
            }
            catch
            {
                return null;
            }
        }

        private static int ResolvePreviewInputSize(IReadOnlyList<int> dims, int fallback)
        {
            if (dims.Count >= 4)
            {
                var h = dims[2];
                var w = dims[3];
                if (h > 0 && w > 0)
                {
                    return Math.Max(32, Math.Max(h, w));
                }
            }

            return Math.Max(32, fallback);
        }

        private static PreparedImageTensor PrepareImageTensor(string imagePath, int inputSize, string inputName)
        {
            using var source = new Bitmap(imagePath);
            var scale = Math.Min(inputSize / (double)source.Width, inputSize / (double)source.Height);
            var resizedWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
            var resizedHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
            var padX = (inputSize - resizedWidth) / 2;
            var padY = (inputSize - resizedHeight) / 2;

            using var canvas = new Bitmap(inputSize, inputSize);
            using (var graphics = Graphics.FromImage(canvas))
            {
                graphics.Clear(Color.FromArgb(114, 114, 114));
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, padX, padY, resizedWidth, resizedHeight);
            }

            var tensor = new DenseTensor<float>(new[] { 1, 3, inputSize, inputSize });
            for (var y = 0; y < inputSize; y++)
            {
                for (var x = 0; x < inputSize; x++)
                {
                    var color = canvas.GetPixel(x, y);
                    tensor[0, 0, y, x] = color.R / 255f;
                    tensor[0, 1, y, x] = color.G / 255f;
                    tensor[0, 2, y, x] = color.B / 255f;
                }
            }

            return new PreparedImageTensor
            {
                InputName = inputName,
                Tensor = tensor,
                OriginalWidth = source.Width,
                OriginalHeight = source.Height,
                InputSize = inputSize,
                Scale = scale,
                PadX = padX,
                PadY = padY
            };
        }

        public static List<DetectionPreviewBox> ParseYoloDetections(
            Tensor<float> outputTensor,
            IReadOnlyList<string> classes,
            int originalWidth,
            int originalHeight,
            int inputSize,
            double scale,
            int padX,
            int padY,
            double confidenceThreshold,
            double nmsIouThreshold)
        {
            var dims = outputTensor.Dimensions.ToArray();
            if (dims.Length != 3 || dims[0] <= 0 || dims[1] <= 0 || dims[2] <= 0)
            {
                return new List<DetectionPreviewBox>();
            }

            var values = outputTensor.ToArray();
            var dim1 = dims[1];
            var dim2 = dims[2];
            var knownClassCount = classes.Count;
            var featureSizes = knownClassCount > 0
                ? new HashSet<int> { knownClassCount + 4, knownClassCount + 5 }
                : new HashSet<int>();
            var featuresFirst = featureSizes.Contains(dim1)
                ? true
                : featureSizes.Contains(dim2)
                    ? false
                    : dim1 < dim2;
            var featureCount = featuresFirst ? dim1 : dim2;
            var candidateCount = featuresFirst ? dim2 : dim1;
            if (featureCount < 5)
            {
                return new List<DetectionPreviewBox>();
            }

            var hasObjectness = knownClassCount > 0 && featureCount == knownClassCount + 5;
            var classOffset = hasObjectness ? 5 : 4;
            var availableClassCount = featureCount - classOffset;
            var classCount = knownClassCount > 0
                ? Math.Min(knownClassCount, availableClassCount)
                : availableClassCount;
            if (classCount <= 0)
            {
                return new List<DetectionPreviewBox>();
            }

            float GetValue(int candidate, int feature)
            {
                return featuresFirst
                    ? values[feature * dim2 + candidate]
                    : values[candidate * dim2 + feature];
            }

            var boxes = new List<DetectionPreviewBox>();
            for (var candidate = 0; candidate < candidateCount; candidate++)
            {
                var cx = GetValue(candidate, 0);
                var cy = GetValue(candidate, 1);
                var w = GetValue(candidate, 2);
                var h = GetValue(candidate, 3);
                if (cx <= 1.5f && cy <= 1.5f && w <= 1.5f && h <= 1.5f)
                {
                    cx *= inputSize;
                    cy *= inputSize;
                    w *= inputSize;
                    h *= inputSize;
                }

                if (w <= 0 || h <= 0)
                {
                    continue;
                }

                var objectness = hasObjectness ? Math.Clamp(GetValue(candidate, 4), 0, 1) : 1f;
                var bestClass = -1;
                var bestScore = 0f;
                for (var classIndex = 0; classIndex < classCount; classIndex++)
                {
                    var score = Math.Clamp(GetValue(candidate, classOffset + classIndex), 0, 1) * objectness;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestClass = classIndex;
                    }
                }

                if (bestClass < 0 || bestScore < confidenceThreshold)
                {
                    continue;
                }

                var x1 = (cx - w / 2 - padX) / scale;
                var y1 = (cy - h / 2 - padY) / scale;
                var x2 = (cx + w / 2 - padX) / scale;
                var y2 = (cy + h / 2 - padY) / scale;
                x1 = Math.Clamp(x1, 0, originalWidth);
                y1 = Math.Clamp(y1, 0, originalHeight);
                x2 = Math.Clamp(x2, 0, originalWidth);
                y2 = Math.Clamp(y2, 0, originalHeight);
                if (x2 <= x1 || y2 <= y1)
                {
                    continue;
                }

                boxes.Add(new DetectionPreviewBox
                {
                    ClassId = bestClass,
                    ClassName = bestClass < classes.Count ? classes[bestClass] : $"class_{bestClass}",
                    Confidence = Math.Round(bestScore, 6),
                    X = Math.Round(x1, 2),
                    Y = Math.Round(y1, 2),
                    Width = Math.Round(x2 - x1, 2),
                    Height = Math.Round(y2 - y1, 2)
                });
            }

            return ApplyClassAwareNms(boxes, nmsIouThreshold);
        }

        private static List<DetectionPreviewBox> ApplyClassAwareNms(List<DetectionPreviewBox> boxes, double iouThreshold)
        {
            var kept = new List<DetectionPreviewBox>();
            foreach (var group in boxes.GroupBy(x => x.ClassId))
            {
                var ordered = group.OrderByDescending(x => x.Confidence).ToList();
                while (ordered.Count > 0)
                {
                    var current = ordered[0];
                    kept.Add(current);
                    ordered.RemoveAt(0);
                    ordered = ordered
                        .Where(candidate => CalculateIou(current, candidate) <= iouThreshold)
                        .ToList();
                }
            }

            return kept.OrderByDescending(x => x.Confidence).ToList();
        }

        public static double CalculateIou(DetectionPreviewBox a, DetectionPreviewBox b)
        {
            var ax2 = a.X + a.Width;
            var ay2 = a.Y + a.Height;
            var bx2 = b.X + b.Width;
            var by2 = b.Y + b.Height;
            var ix1 = Math.Max(a.X, b.X);
            var iy1 = Math.Max(a.Y, b.Y);
            var ix2 = Math.Min(ax2, bx2);
            var iy2 = Math.Min(ay2, by2);
            var iw = Math.Max(0, ix2 - ix1);
            var ih = Math.Max(0, iy2 - iy1);
            var intersection = iw * ih;
            var union = a.Width * a.Height + b.Width * b.Height - intersection;
            return union <= 0 ? 0 : intersection / union;
        }

        private static void DrawDetections(string imagePath, string outputPath, IReadOnlyList<DetectionPreviewBox> detections)
        {
            using var image = new Bitmap(imagePath);
            using var graphics = Graphics.FromImage(image);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var font = new Font("Segoe UI", 10, FontStyle.Bold);

            foreach (var detection in detections)
            {
                var color = ColorFromClass(detection.ClassId);
                using var pen = new Pen(color, Math.Max(2, image.Width / 600f));
                var rect = new RectangleF((float)detection.X, (float)detection.Y, (float)detection.Width, (float)detection.Height);
                graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);

                var label = $"{detection.ClassName} {detection.Confidence:0.00}";
                var labelSize = graphics.MeasureString(label, font);
                var labelX = Math.Max(0, rect.X);
                var labelY = Math.Max(0, rect.Y - labelSize.Height - 2);
                using var brush = new SolidBrush(Color.FromArgb(220, color));
                using var textBrush = new SolidBrush(Color.White);
                graphics.FillRectangle(brush, labelX, labelY, labelSize.Width + 6, labelSize.Height + 2);
                graphics.DrawString(label, font, textBrush, labelX + 3, labelY + 1);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            image.Save(outputPath, ImageFormat.Jpeg);
        }

        private static void DrawEvaluationSample(
            string imagePath,
            string outputPath,
            IReadOnlyList<DetectionPreviewBox> predictions,
            IReadOnlyList<DetectionPreviewBox> groundTruth)
        {
            using var image = new Bitmap(imagePath);
            using var graphics = Graphics.FromImage(image);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var font = new Font("Segoe UI", 10, FontStyle.Bold);

            foreach (var label in groundTruth)
            {
                var rect = new RectangleF((float)label.X, (float)label.Y, (float)label.Width, (float)label.Height);
                using var pen = new Pen(Color.FromArgb(220, 38, 38), Math.Max(2, image.Width / 600f));
                pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                DrawBoxLabel(graphics, font, rect, "GT " + label.ClassName, Color.FromArgb(220, 38, 38));
            }

            foreach (var prediction in predictions)
            {
                var rect = new RectangleF((float)prediction.X, (float)prediction.Y, (float)prediction.Width, (float)prediction.Height);
                using var pen = new Pen(Color.FromArgb(0, 137, 123), Math.Max(2, image.Width / 600f));
                graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                DrawBoxLabel(graphics, font, rect, $"{prediction.ClassName} {prediction.Confidence:0.00}", Color.FromArgb(0, 137, 123));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            image.Save(outputPath, ImageFormat.Jpeg);
        }

        private static void DrawBoxLabel(Graphics graphics, Font font, RectangleF rect, string label, Color color)
        {
            var labelSize = graphics.MeasureString(label, font);
            var labelX = Math.Max(0, rect.X);
            var labelY = Math.Max(0, rect.Y - labelSize.Height - 2);
            using var brush = new SolidBrush(Color.FromArgb(220, color));
            using var textBrush = new SolidBrush(Color.White);
            graphics.FillRectangle(brush, labelX, labelY, labelSize.Width + 6, labelSize.Height + 2);
            graphics.DrawString(label, font, textBrush, labelX + 3, labelY + 1);
        }

        private static Color ColorFromClass(int classId)
        {
            var palette = new[]
            {
                Color.FromArgb(0, 137, 123),
                Color.FromArgb(31, 111, 235),
                Color.FromArgb(245, 158, 11),
                Color.FromArgb(220, 38, 38),
                Color.FromArgb(124, 58, 237),
                Color.FromArgb(5, 150, 105)
            };
            return palette[Math.Abs(classId) % palette.Length];
        }

        private object BuildModelCard(ModelRegistryEntry entry)
        {
            return new
            {
                entry.Id,
                entry.RunId,
                entry.DatasetVersionId,
                entry.Status,
                entry.IsPinned,
                entry.PtPath,
                entry.OnnxPath,
                entry.EvaluationReportPath,
                entry.Classes,
                entry.SmokeTest,
                entry.LastPackagePath,
                entry.PackageHistory,
                entry.LastPreviewPath,
                entry.LastPreviewReportPath,
                entry.LastEvaluationReportPath,
                entry.LastReadinessStatus,
                updatedAt = DateTime.Now
            };
        }

        private sealed class PreparedImageTensor
        {
            public string InputName { get; init; } = "";
            public DenseTensor<float> Tensor { get; init; } = null!;
            public int OriginalWidth { get; init; }
            public int OriginalHeight { get; init; }
            public int InputSize { get; init; }
            public double Scale { get; init; }
            public int PadX { get; init; }
            public int PadY { get; init; }
        }

        private static object BuildClearVisionManifest(
            ModelRegistryEntry model,
            TrainingRunRecord? run,
            DatasetVersion? dataset,
            List<string> classes,
            string modelSha,
            string labelsSha,
            int imgSize,
            string yoloVersion,
            string licenseText,
            string labelsContract,
            string providerFallback,
            string hardwareProfile,
            string reportId)
        {
            return new
            {
                schemaVersion = "2026-04-30.deep-learning-model-manifest.v1",
                modelId = model.Id,
                modelSha256 = modelSha,
                artifactPath = "model.onnx",
                source = $"Insight industrial trainer; run={model.RunId}; dataset={model.DatasetVersionId}",
                license = licenseText,
                labelsContract,
                labelsSha256 = labelsSha,
                providerFallback,
                datasetVersion = dataset?.Id ?? model.DatasetVersionId,
                hardwareProfile,
                reportId,
                classes,
                inputShape = new[] { 1, 3, imgSize, imgSize },
                preprocess = new
                {
                    resize = "letterbox",
                    colorOrder = "RGB",
                    normalization = "float32_0_1",
                    padValue = 114
                },
                postprocess = new
                {
                    yoloVersion,
                    confidenceThreshold = 0.25,
                    nmsIouThreshold = 0.45,
                    classAwareNms = true
                },
                training = new
                {
                    runId = model.RunId,
                    experimentName = run?.ExperimentName ?? "",
                    epochs = run?.Config.Epochs,
                    batchSize = run?.Config.BatchSize,
                    imgSize,
                    seed = run?.Config.Seed,
                    datasetImageCount = dataset?.ImageCount,
                    datasetLabelCount = dataset?.LabelCount,
                    datasetIssueCount = dataset?.IssueCount
                },
                claimBoundary = new
                {
                    realModelOutput = "Runner must use ONNX Runtime output tensors; annotation-seeded tensors are forbidden for this manifest.",
                    fieldSignoff = "Insight training/evaluation evidence is not production-line sign-off until a field acceptance report is attached.",
                    modelArtifactPolicy = "Keep this manifest, hash, labels, IO schema and report together with the exact ONNX artifact."
                }
            };
        }

        private static object BuildClearVisionModelCatalog(
            ModelRegistryEntry model,
            TrainingRunRecord? run,
            List<string> classes,
            string modelSha,
            int imgSize,
            string yoloVersion,
            string licenseText,
            string labelsContract,
            string providerFallback,
            string datasetVersion,
            string hardwareProfile,
            string reportId)
        {
            return new
            {
                releaseGate = new
                {
                    appliesToOperatorTypes = new[] { "DeepLearning", "SurfaceDefectDetection" },
                    requiredExternalModelFields = new[]
                    {
                        "modelSha256",
                        "license",
                        "labelsContract",
                        "providerFallback",
                        "datasetVersion",
                        "hardwareProfile",
                        "reportId"
                    },
                    claimBoundary = "Model smoke or offline evidence must not be described as production-line sign-off without a field acceptance report."
                },
                models = new[]
                {
                    new
                    {
                        id = model.Id,
                        name = string.IsNullOrWhiteSpace(run?.ExperimentName) ? model.Id : run!.ExperimentName,
                        type = "object_detection",
                        path = "model.onnx",
                        version = model.RunId,
                        execution_provider = "cpu",
                        source = "insight-industrial-trainer",
                        license = licenseText,
                        model_sha256 = modelSha,
                        modelSha256 = modelSha,
                        labelsContract,
                        providerFallback,
                        datasetVersion,
                        hardwareProfile,
                        reportId,
                        input_size = new[] { imgSize, imgSize },
                        input_shape = new[] { 1, 3, imgSize, imgSize },
                        num_classes = classes.Count,
                        class_names = classes,
                        classes,
                        preprocess = new
                        {
                            resize = "letterbox",
                            colorOrder = "RGB",
                            normalization = "float32_0_1",
                            padValue = 114
                        },
                        postprocess = new
                        {
                            yoloVersion,
                            confidenceThreshold = 0.25,
                            nmsIouThreshold = 0.45,
                            classAwareNms = true
                        }
                    }
                }
            };
        }

        private static object BuildPlatformDescriptor(
            string packageId,
            string profile,
            ModelRegistryEntry model,
            List<string> classes,
            string modelSha,
            string labelsSha,
            int imgSize,
            string yoloVersion,
            string labelsContract)
        {
            return new
            {
                schemaVersion = "2026-05-16.insight-platform-package.v1",
                packageId,
                profile,
                createdAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                model = new
                {
                    model.Id,
                    model.RunId,
                    model.DatasetVersionId,
                    modelSha256 = modelSha,
                    labelsSha256 = labelsSha,
                    classes,
                    inputShape = new[] { 1, 3, imgSize, imgSize },
                    yoloVersion
                },
                artifacts = new
                {
                    onnx = "model.onnx",
                    pt = "model.pt",
                    labels = "labels.txt",
                    manifest = "clearvision.model.manifest.json",
                    catalog = "model_catalog.json",
                    evaluationReport = "reports/evaluation_report.json"
                },
                clearVision = new
                {
                    compatibleOperators = new[] { "DeepLearning", "SurfaceDefectDetection" },
                    operatorParameters = new
                    {
                        ModelId = model.Id,
                        ModelCatalogPath = "model_catalog.json",
                        ModelPath = "model.onnx",
                        LabelsPath = "labels.txt",
                        ModelVersion = yoloVersion,
                        ConfidenceThreshold = 0.25,
                        NmsIouThreshold = 0.45,
                        ExecutionProvider = "cpu"
                    },
                    labelsContract
                },
                genericOnnxRuntime = new
                {
                    task = "object_detection",
                    inputLayout = "NCHW",
                    colorOrder = "RGB",
                    normalization = "float32_0_1",
                    postprocess = "YOLO boxes + class scores + NMS"
                }
            };
        }

        private static string BuildPackageReadme(
            string packageId,
            ModelRegistryEntry model,
            List<string> classes,
            string modelSha,
            string labelsSha,
            string yoloVersion)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Insight Platform Model Package: {packageId}");
            sb.AppendLine();
            sb.AppendLine("This package is generated by Insight for ClearVision and other ONNX Runtime based vision platforms.");
            sb.AppendLine();
            sb.AppendLine("## Files");
            sb.AppendLine();
            sb.AppendLine("- `model.onnx`: deployable ONNX artifact.");
            sb.AppendLine("- `labels.txt`: one class per line, matching YOLO class ids.");
            sb.AppendLine("- `model_catalog.json`: ClearVision `ModelCatalog` compatible catalog.");
            sb.AppendLine("- `clearvision.model.manifest.json`: release-gate manifest with hash, labels contract, provider fallback and evidence pointers.");
            sb.AppendLine("- `platform-package.json`: portable integration descriptor.");
            sb.AppendLine("- `reports/`: evaluation, dataset QA and environment evidence when available.");
            sb.AppendLine();
            sb.AppendLine("## ClearVision Parameters");
            sb.AppendLine();
            sb.AppendLine("| Parameter | Value |");
            sb.AppendLine("| --- | --- |");
            sb.AppendLine($"| `ModelId` | `{model.Id}` |");
            sb.AppendLine("| `ModelCatalogPath` | `model_catalog.json` |");
            sb.AppendLine("| `ModelPath` | `model.onnx` |");
            sb.AppendLine("| `LabelsPath` | `labels.txt` |");
            sb.AppendLine($"| `ModelVersion` | `{yoloVersion}` |");
            sb.AppendLine();
            sb.AppendLine("## Evidence");
            sb.AppendLine();
            sb.AppendLine($"- Model SHA-256: `{modelSha}`");
            sb.AppendLine($"- Labels SHA-256: `{labelsSha}`");
            sb.AppendLine($"- Classes: {string.Join(", ", classes)}");
            sb.AppendLine();
            sb.AppendLine("Field acceptance is still a separate step: attach real station data, hardware profile, threshold review and sign-off before calling the model production-validated.");
            return sb.ToString();
        }

        private static string CopyIfExists(string? sourcePath, string targetPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return "";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
            return targetPath;
        }

        private static string ResolveYoloVersion(string modelSize)
        {
            var value = (modelSize ?? "").Trim().ToLowerInvariant();
            if (value.Contains("11")) return "YOLOv11";
            if (value.Contains("26")) return "YOLOv26";
            if (value.Contains("5")) return "YOLOv5";
            if (value.Contains("6")) return "YOLOv6";
            return "YOLOv8";
        }

        private static string ResolveReportId(string reportPath, TrainingRunRecord? run)
        {
            if (File.Exists(reportPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(reportPath, Encoding.UTF8));
                    if (doc.RootElement.TryGetProperty("Id", out var idProp) ||
                        doc.RootElement.TryGetProperty("id", out idProp))
                    {
                        return idProp.GetString() ?? "";
                    }
                }
                catch
                {
                    // Fall through to run id.
                }
            }

            return string.IsNullOrWhiteSpace(run?.Id) ? "REVIEW_REQUIRED" : run!.Id;
        }

        private static DatasetVersionRequest ParseDatasetVersionRequest(JsonElement data)
        {
            return new DatasetVersionRequest
            {
                ProjectRoot = GetRequiredString(data, "projectRoot"),
                ProjectName = GetOptionalString(data, "projectName"),
                SourcePaths = GetStringArray(data, "sourcePaths"),
                Classes = GetStringArray(data, "classes"),
                SplitSeed = GetOptionalInt(data, "splitSeed", 42),
                TrainRatio = GetOptionalDouble(data, "trainRatio", 0.8),
                ValRatio = GetOptionalDouble(data, "valRatio", 0.1),
                TestRatio = GetOptionalDouble(data, "testRatio", 0.1)
            };
        }

        private static ModelEvaluationRequest ParseModelEvaluationRequest(JsonElement data)
        {
            var request = new ModelEvaluationRequest
            {
                ProjectRoot = GetRequiredString(data, "projectRoot"),
                ModelId = GetRequiredString(data, "modelId"),
                DatasetVersionId = GetOptionalString(data, "datasetVersionId"),
                Split = GetOptionalString(data, "split"),
                IouThreshold = GetOptionalDouble(data, "iouThreshold", 0.5),
                NmsIouThreshold = GetOptionalDouble(data, "nmsIouThreshold", 0.45),
                MaxSamples = GetOptionalInt(data, "maxSamples", 500),
                MaxGallerySamples = GetOptionalInt(data, "maxGallerySamples", 80)
            };

            if (data.TryGetProperty("confidenceThresholds", out var thresholds) && thresholds.ValueKind == JsonValueKind.Array)
            {
                request.ConfidenceThresholds = thresholds.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.Number)
                    .Select(x => x.GetDouble())
                    .ToList();
            }

            return request;
        }

        private static TrainingRunConfig ParseTrainingRunConfig(JsonElement data)
        {
            var config = new TrainingRunConfig
            {
                ProjectRoot = GetRequiredString(data, "projectRoot"),
                DatasetVersionId = GetRequiredString(data, "datasetVersionId"),
                ExperimentName = GetOptionalString(data, "experimentName"),
                PythonPath = GetOptionalString(data, "pythonPath"),
                ModelSize = GetOptionalString(data, "modelSize"),
                ImgSize = GetOptionalInt(data, "imgSize", 640),
                Epochs = GetOptionalInt(data, "epochs", 100),
                BatchSize = GetOptionalInt(data, "batchSize", 16),
                Patience = GetOptionalInt(data, "patience", 50),
                Workers = GetOptionalInt(data, "workers", 8),
                GpuIndex = GetOptionalString(data, "gpuIndex"),
                Resume = GetOptionalBool(data, "resume", false),
                ResumeRunId = GetOptionalString(data, "resumeRunId"),
                Seed = GetOptionalInt(data, "seed", 42)
            };

            if (data.TryGetProperty("advancedOptions", out var advanced) && advanced.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in advanced.EnumerateObject())
                {
                    config.AdvancedOptions[prop.Name] = prop.Value.ToString();
                }
            }

            return config;
        }

        private static void NormalizeDatasetRequest(DatasetVersionRequest request)
        {
            request.ProjectRoot = Path.GetFullPath(request.ProjectRoot);
            request.ProjectName = string.IsNullOrWhiteSpace(request.ProjectName)
                ? Path.GetFileName(request.ProjectRoot)
                : request.ProjectName.Trim();
            request.SourcePaths = request.SourcePaths
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            request.Classes = request.Classes
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (!Directory.Exists(request.ProjectRoot)) throw new DirectoryNotFoundException($"项目根目录不存在: {request.ProjectRoot}");
            if (request.SourcePaths.Count == 0) throw new InvalidOperationException("至少需要一个数据源目录。");
            if (request.Classes.Count == 0) throw new InvalidOperationException("至少需要一个类别。");

            request.TrainRatio = Math.Clamp(request.TrainRatio, 0.05, 0.95);
            request.ValRatio = Math.Clamp(request.ValRatio, 0.0, 0.9);
            request.TestRatio = Math.Max(0, 1 - request.TrainRatio - request.ValRatio);
        }

        private static void NormalizeModelEvaluationRequest(ModelEvaluationRequest request)
        {
            request.ProjectRoot = Path.GetFullPath(request.ProjectRoot);
            if (string.IsNullOrWhiteSpace(request.ModelId)) throw new InvalidOperationException("缺少模型 ID。");
            if (!Directory.Exists(request.ProjectRoot)) throw new DirectoryNotFoundException($"项目根目录不存在: {request.ProjectRoot}");
            request.Split = string.IsNullOrWhiteSpace(request.Split) ? "auto" : request.Split.Trim();
            request.IouThreshold = Math.Clamp(request.IouThreshold, 0.05, 0.95);
            request.NmsIouThreshold = Math.Clamp(request.NmsIouThreshold, 0.05, 0.95);
            request.MaxSamples = Math.Clamp(request.MaxSamples, 1, 10000);
            request.MaxGallerySamples = Math.Clamp(request.MaxGallerySamples, 0, 500);
            request.ConfidenceThresholds = request.ConfidenceThresholds
                .Where(x => !double.IsNaN(x) && !double.IsInfinity(x))
                .Select(x => Math.Clamp(x, 0.001, 0.999))
                .Distinct()
                .OrderBy(x => x)
                .ToList();
            if (request.ConfidenceThresholds.Count == 0)
            {
                request.ConfidenceThresholds = new List<double> { 0.1, 0.2, 0.25, 0.35, 0.5, 0.65, 0.8 };
            }
        }

        private static void NormalizeTrainingConfig(TrainingRunConfig config)
        {
            config.ProjectRoot = Path.GetFullPath(config.ProjectRoot);
            if (string.IsNullOrWhiteSpace(config.DatasetVersionId)) throw new InvalidOperationException("缺少数据版本。");
            if (string.IsNullOrWhiteSpace(config.ExperimentName)) config.ExperimentName = "Industrial Detection";
            if (string.IsNullOrWhiteSpace(config.PythonPath)) config.PythonPath = "python";
            if (string.IsNullOrWhiteSpace(config.ModelSize)) config.ModelSize = "v8s";
            if (string.IsNullOrWhiteSpace(config.GpuIndex)) config.GpuIndex = "0";
            config.ImgSize = Math.Max(32, config.ImgSize);
            config.Epochs = Math.Max(1, config.Epochs);
            config.BatchSize = config.BatchSize == 0 ? 16 : config.BatchSize;
            config.Patience = Math.Max(1, config.Patience);
            config.Workers = Math.Max(0, config.Workers);
        }

        private static IEnumerable<string> EnumerateImages(IEnumerable<string> sourcePaths)
        {
            foreach (var path in sourcePaths)
            {
                if (!Directory.Exists(path)) continue;
                foreach (var file in Directory.GetFiles(path, "*.*", SearchOption.AllDirectories))
                {
                    if (ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                    {
                        yield return file;
                    }
                }
            }
        }

        private static EnvironmentSnapshot CaptureEnvironment(string pythonPath, string workingDirectory)
        {
            var drive = new DriveInfo(Path.GetPathRoot(workingDirectory) ?? AppDomain.CurrentDomain.BaseDirectory);
            return new EnvironmentSnapshot
            {
                PythonPath = pythonPath,
                PythonPathExists = IsValidPython(pythonPath),
                WorkingDirectory = workingDirectory,
                FreeDiskBytes = drive.AvailableFreeSpace
            };
        }

        private static bool IsValidPython(string pythonPath)
        {
            if (string.IsNullOrWhiteSpace(pythonPath)) return false;
            if (pythonPath.Equals("python", StringComparison.OrdinalIgnoreCase)) return true;
            if (File.Exists(pythonPath)) return true;
            if (Directory.Exists(pythonPath) && File.Exists(Path.Combine(pythonPath, "python.exe"))) return true;
            return false;
        }

        private static string FindArtifact(string runRoot, string fileName)
        {
            if (!Directory.Exists(runRoot)) return "";
            return Directory.GetFiles(runRoot, fileName, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault() ?? "";
        }

        private void UpsertRun(string projectRoot, TrainingRunRecord run)
        {
            var index = LoadIndex(projectRoot);
            index.TrainingRuns.RemoveAll(x => x.Id == run.Id);
            index.TrainingRuns.Add(run);
            SaveIndex(projectRoot, index);
        }

        private DatasetVersion FindDatasetVersion(string projectRoot, string versionId)
        {
            var index = LoadIndex(projectRoot);
            return index.DatasetVersions.FirstOrDefault(x => x.Id == versionId)
                ?? throw new InvalidOperationException($"未找到数据版本: {versionId}");
        }

        private TrainingRunRecord FindRun(string projectRoot, string runId)
        {
            var index = LoadIndex(projectRoot);
            return index.TrainingRuns.FirstOrDefault(x => x.Id == runId)
                ?? throw new InvalidOperationException($"未找到训练实验: {runId}");
        }

        private static IndustrialStoreIndex LoadIndex(string projectRoot)
        {
            var path = GetIndexPath(projectRoot);
            if (!File.Exists(path)) return new IndustrialStoreIndex();
            return LoadJson<IndustrialStoreIndex>(path) ?? new IndustrialStoreIndex();
        }

        private static void SaveIndex(string projectRoot, IndustrialStoreIndex index)
        {
            Directory.CreateDirectory(GetInsightRoot(projectRoot));
            SaveJson(GetIndexPath(projectRoot), index);
        }

        private static string GetInsightRoot(string projectRoot)
        {
            return Path.Combine(Path.GetFullPath(projectRoot), ".insight");
        }

        private static string GetIndexPath(string projectRoot)
        {
            return Path.Combine(GetInsightRoot(projectRoot), "industrial_index.json");
        }

        private static void SaveJson<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(value, JsonOptions);
            File.WriteAllText(path, json, Encoding.UTF8);
        }

        private static T? LoadJson<T>(string path)
        {
            if (!File.Exists(path)) return default;
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }

        private static void AppendJsonLine<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, Encoding.UTF8);
        }

        private void AppendLog(TrainingRunRecord run, string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(run.LogPath)!);
            File.AppendAllText(run.LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}", Encoding.UTF8);
        }

        private static void WriteDataYaml(string yamlPath, string yoloRoot, List<string> classes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# YOLO Dataset Configuration");
            sb.AppendLine("# Generated by Insight industrial trainer");
            sb.AppendLine($"path: {YamlQuote(yoloRoot.Replace("\\", "/"))}");
            sb.AppendLine("train: images/train");
            sb.AppendLine("val: images/val");
            sb.AppendLine("test: images/test");
            sb.AppendLine($"nc: {classes.Count}");
            sb.AppendLine("names:");
            for (var i = 0; i < classes.Count; i++)
            {
                sb.AppendLine($"  {i}: {YamlQuote(classes[i])}");
            }

            File.WriteAllText(yamlPath, sb.ToString(), Encoding.UTF8);
        }

        private static string ToYoloLine(DatasetLabel label)
        {
            return string.Join(" ", new[]
            {
                label.ClassId.ToString(CultureInfo.InvariantCulture),
                label.X.ToString("0.######", CultureInfo.InvariantCulture),
                label.Y.ToString("0.######", CultureInfo.InvariantCulture),
                label.W.ToString("0.######", CultureInfo.InvariantCulture),
                label.H.ToString("0.######", CultureInfo.InvariantCulture)
            });
        }

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string ResolveYoloModelName(string modelSize)
        {
            var value = (modelSize ?? "v8s").Trim().ToLowerInvariant();
            if (value.EndsWith(".pt", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (value.StartsWith("v11", StringComparison.OrdinalIgnoreCase)) return "yolo11" + value[3..] + ".pt";
            if (value.StartsWith("v26", StringComparison.OrdinalIgnoreCase)) return "yolo26" + value[3..] + ".pt";
            if (value.StartsWith("v8", StringComparison.OrdinalIgnoreCase)) return "yolov8" + value[2..] + ".pt";
            if (value.Length == 1) return "yolov8" + value + ".pt";
            return value + ".pt";
        }

        private static string QuotePath(string path)
        {
            return "\"" + path.Replace("\\", "/").Replace("\"", "\\\"") + "\"";
        }

        private static string QuoteCliValue(string value)
        {
            return value.Contains(' ') || value.Contains('\\') || value.Contains('/')
                ? QuotePath(value)
                : value;
        }

        private static string YamlQuote(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }

        private static string MakeSafeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
            var safe = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(safe) ? "sample" : safe;
        }

        private static double Clamp01(double value)
        {
            return Math.Max(0, Math.Min(1, value));
        }

        private static string FormatDims(IEnumerable<int> dims)
        {
            return "[" + string.Join(",", dims.Select(x => x.ToString(CultureInfo.InvariantCulture))) + "]";
        }

        private static string GetRequiredString(JsonElement data, string name)
        {
            var value = GetOptionalString(data, name);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"缺少参数: {name}");
            return value;
        }

        private static string GetOptionalString(JsonElement data, string name)
        {
            return data.TryGetProperty(name, out var prop) && prop.ValueKind != JsonValueKind.Null
                ? prop.GetString() ?? ""
                : "";
        }

        private static int GetOptionalInt(JsonElement data, string name, int fallback)
        {
            return data.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
                ? prop.GetInt32()
                : fallback;
        }

        private static double GetOptionalDouble(JsonElement data, string name, double fallback)
        {
            return data.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
                ? prop.GetDouble()
                : fallback;
        }

        private static bool GetOptionalBool(JsonElement data, string name, bool fallback)
        {
            return data.TryGetProperty(name, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? prop.GetBoolean()
                : fallback;
        }

        private static List<string> GetStringArray(JsonElement data, string name)
        {
            if (!data.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
            {
                return new List<string>();
            }

            return prop.EnumerateArray()
                .Select(x => x.GetString() ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        private static string GetCsvValue(string[] headers, string[] values, string header)
        {
            var index = Array.FindIndex(headers, x => string.Equals(x, header, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index < values.Length ? values[index] : "";
        }

        private static double? TryGetCsvDouble(string[] headers, string[] values, string header)
        {
            var text = GetCsvValue(headers, values, header);
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        }
    }
}
