using System.Text.Json.Serialization;

namespace Insight.Services.Industrial
{
    public sealed class DatasetVersionRequest
    {
        public string ProjectRoot { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public List<string> SourcePaths { get; set; } = new();
        public List<string> Classes { get; set; } = new();
        public int SplitSeed { get; set; } = 42;
        public double TrainRatio { get; set; } = 0.8;
        public double ValRatio { get; set; } = 0.1;
        public double TestRatio { get; set; } = 0.1;
    }

    public sealed class DatasetVersion
    {
        public string Id { get; set; } = "";
        public string ProjectRoot { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public List<string> SourcePaths { get; set; } = new();
        public List<string> Classes { get; set; } = new();
        public int SplitSeed { get; set; }
        public double TrainRatio { get; set; }
        public double ValRatio { get; set; }
        public double TestRatio { get; set; }
        public string VersionRoot { get; set; } = "";
        public string YoloRoot { get; set; } = "";
        public string ManifestPath { get; set; } = "";
        public string QaReportPath { get; set; } = "";
        public string DataYamlPath { get; set; } = "";
        public int ImageCount { get; set; }
        public int LabelCount { get; set; }
        public int IssueCount { get; set; }
        public int TrainCount { get; set; }
        public int ValCount { get; set; }
        public int TestCount { get; set; }
        public Dictionary<string, int> ClassCounts { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed class DatasetManifest
    {
        public string VersionId { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public List<DatasetManifestItem> Items { get; set; } = new();
    }

    public sealed class DatasetManifestItem
    {
        public string SourcePath { get; set; } = "";
        public string ImagePath { get; set; } = "";
        public string LabelPath { get; set; } = "";
        public string Split { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public List<DatasetLabel> Labels { get; set; } = new();
        public List<string> Issues { get; set; } = new();
    }

    public sealed class DatasetLabel
    {
        public int ClassId { get; set; }
        public string ClassName { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
    }

    public sealed class DatasetQaReport
    {
        public string VersionId { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public int ImageCount { get; set; }
        public int LabelCount { get; set; }
        public int IssueCount { get; set; }
        public Dictionary<string, int> ClassCounts { get; set; } = new(StringComparer.Ordinal);
        public List<QaIssueSummary> Issues { get; set; } = new();
        public List<DatasetManifestItem> ProblemSamples { get; set; } = new();
        public string Recommendation { get; set; } = "";
    }

    public sealed class QaIssueSummary
    {
        public string Code { get; set; } = "";
        public string Severity { get; set; } = "warning";
        public int Count { get; set; }
    }

    public sealed class TrainingRunConfig
    {
        public string ProjectRoot { get; set; } = "";
        public string DatasetVersionId { get; set; } = "";
        public string ProviderId { get; set; } = "local-yolo";
        public string ExperimentName { get; set; } = "Industrial Detection";
        public string PythonPath { get; set; } = "python";
        public string ModelSize { get; set; } = "v8s";
        public int ImgSize { get; set; } = 640;
        public int Epochs { get; set; } = 100;
        public int BatchSize { get; set; } = 16;
        public int Patience { get; set; } = 50;
        public int Workers { get; set; } = 8;
        public string GpuIndex { get; set; } = "0";
        public bool Resume { get; set; }
        public string ResumeRunId { get; set; } = "";
        public int Seed { get; set; } = 42;
        public Dictionary<string, string> AdvancedOptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ProviderOptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class TrainingRunRecord
    {
        public string Id { get; set; } = "";
        public string ProjectRoot { get; set; } = "";
        public string DatasetVersionId { get; set; } = "";
        public string ExperimentName { get; set; } = "";
        public string Status { get; set; } = "Preparing";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public string RunRoot { get; set; } = "";
        public string ProviderId { get; set; } = "local-yolo";
        public string ProviderExternalJobId { get; set; } = "";
        public Dictionary<string, string> ProviderMetadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string ConfigPath { get; set; } = "";
        public string EnvironmentPath { get; set; } = "";
        public string LogPath { get; set; } = "";
        public string MetricsPath { get; set; } = "";
        public string EvaluationReportPath { get; set; } = "";
        public string BestPtPath { get; set; } = "";
        public string LastPtPath { get; set; } = "";
        public string OnnxPath { get; set; } = "";
        public string FailureReason { get; set; } = "";
        public TrainingRunConfig Config { get; set; } = new();
        public List<MetricPoint> Metrics { get; set; } = new();
    }

    public sealed class MetricPoint
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Epoch { get; set; } = "";
        public double? BoxLoss { get; set; }
        public double? Map50 { get; set; }
        public double? Map5095 { get; set; }
        public double? Progress { get; set; }
    }

    public sealed class EvaluationReport
    {
        public string Id { get; set; } = "";
        public string RunId { get; set; } = "";
        public string DatasetVersionId { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public double? Map50 { get; set; }
        public double? Map5095 { get; set; }
        public double? Precision { get; set; }
        public double? Recall { get; set; }
        public List<PerClassMetric> PerClass { get; set; } = new();
        public List<string> Artifacts { get; set; } = new();
        public string ThresholdRecommendation { get; set; } = "Use the validation confidence sweep before production pinning.";
    }

    public sealed class PerClassMetric
    {
        public string ClassName { get; set; } = "";
        public int Support { get; set; }
        public double? Map50 { get; set; }
        public double? Precision { get; set; }
        public double? Recall { get; set; }
    }

    public sealed class ModelRegistryEntry
    {
        public string Id { get; set; } = "";
        public string RunId { get; set; } = "";
        public string DatasetVersionId { get; set; } = "";
        public string Status { get; set; } = "Draft";
        public bool IsPinned { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? UpdatedAt { get; set; }
        public string PtPath { get; set; } = "";
        public string OnnxPath { get; set; } = "";
        public string ModelCardPath { get; set; } = "";
        public string EvaluationReportPath { get; set; } = "";
        public List<string> Classes { get; set; } = new();
        public OnnxSmokeTest SmokeTest { get; set; } = new();
        public string LastPackagePath { get; set; } = "";
        public List<ModelPackageRecord> PackageHistory { get; set; } = new();
        public string LastPreviewPath { get; set; } = "";
        public string LastPreviewReportPath { get; set; } = "";
        public string LastEvaluationReportPath { get; set; } = "";
        public string LastReadinessStatus { get; set; } = "";
    }

    public sealed class ModelPackageRecord
    {
        public string Id { get; set; } = "";
        public string ModelId { get; set; } = "";
        public string Profile { get; set; } = "ClearVision";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string PackageRoot { get; set; } = "";
        public string OutputRoot { get; set; } = "";
        public string ModelSha256 { get; set; } = "";
        public string LabelsSha256 { get; set; } = "";
        public string ManifestPath { get; set; } = "";
        public string CatalogPath { get; set; } = "";
        public string PlatformDescriptorPath { get; set; } = "";
        public string LabelsPath { get; set; } = "";
        public string ModelPath { get; set; } = "";
        public string ReportPath { get; set; } = "";
        public string ReadmePath { get; set; } = "";
    }

    public sealed class OnnxSmokeTest
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public double SessionCreateMs { get; set; }
        public List<string> Inputs { get; set; } = new();
        public List<string> Outputs { get; set; } = new();
    }

    public sealed class InferencePreviewResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string ModelId { get; set; } = "";
        public string ImagePath { get; set; } = "";
        public string AnnotatedImagePath { get; set; } = "";
        public string ReportPath { get; set; } = "";
        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }
        public int InputSize { get; set; }
        public double Scale { get; set; }
        public int PadX { get; set; }
        public int PadY { get; set; }
        public double SessionCreateMs { get; set; }
        public double InferenceMs { get; set; }
        public double TotalMs { get; set; }
        public List<string> Inputs { get; set; } = new();
        public List<string> Outputs { get; set; } = new();
        public List<DetectionPreviewBox> Detections { get; set; } = new();
    }

    public sealed class DetectionPreviewBox
    {
        public int ClassId { get; set; }
        public string ClassName { get; set; } = "";
        public double Confidence { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public sealed class ModelEvaluationRequest
    {
        public string ProjectRoot { get; set; } = "";
        public string ModelId { get; set; } = "";
        public string DatasetVersionId { get; set; } = "";
        public string Split { get; set; } = "test";
        public double IouThreshold { get; set; } = 0.5;
        public double NmsIouThreshold { get; set; } = 0.45;
        public List<double> ConfidenceThresholds { get; set; } = new();
        public int MaxSamples { get; set; } = 500;
        public int MaxGallerySamples { get; set; } = 80;
    }

    public sealed class ModelEvaluationReport
    {
        public string Id { get; set; } = "";
        public string ModelId { get; set; } = "";
        public string RunId { get; set; } = "";
        public string DatasetVersionId { get; set; } = "";
        public string Split { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string ReportRoot { get; set; } = "";
        public string ReportPath { get; set; } = "";
        public string ErrorGalleryPath { get; set; } = "";
        public int ImageCount { get; set; }
        public int GroundTruthCount { get; set; }
        public int PredictionCount { get; set; }
        public double IouThreshold { get; set; }
        public double NmsIouThreshold { get; set; }
        public double BestConfidenceThreshold { get; set; }
        public DetectionQualityMetrics Metrics { get; set; } = new();
        public List<ThresholdSweepPoint> ThresholdSweep { get; set; } = new();
        public List<PerClassEvaluationMetric> PerClass { get; set; } = new();
        public List<EvaluationSampleResult> Samples { get; set; } = new();
        public List<ReadinessGateResult> Gates { get; set; } = new();
        public string ReadinessStatus { get; set; } = "Review";
        public string Recommendation { get; set; } = "";
    }

    public class DetectionQualityMetrics
    {
        public int TruePositive { get; set; }
        public int FalsePositive { get; set; }
        public int FalseNegative { get; set; }
        public double Precision { get; set; }
        public double Recall { get; set; }
        public double F1 { get; set; }
    }

    public sealed class ThresholdSweepPoint : DetectionQualityMetrics
    {
        public double ConfidenceThreshold { get; set; }
    }

    public sealed class PerClassEvaluationMetric : DetectionQualityMetrics
    {
        public int ClassId { get; set; }
        public string ClassName { get; set; } = "";
        public int Support { get; set; }
    }

    public sealed class EvaluationSampleResult
    {
        public string SourcePath { get; set; } = "";
        public string Split { get; set; } = "";
        public int GroundTruthCount { get; set; }
        public int PredictionCount { get; set; }
        public int TruePositive { get; set; }
        public int FalsePositive { get; set; }
        public int FalseNegative { get; set; }
        public string Severity { get; set; } = "ok";
        public string AnnotatedImagePath { get; set; } = "";
        public List<DetectionPreviewBox> Predictions { get; set; } = new();
        public List<DetectionPreviewBox> GroundTruth { get; set; } = new();
    }

    public sealed class ReadinessGateResult
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "Review";
        public string Message { get; set; } = "";
    }

    public sealed class EnvironmentSnapshot
    {
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string MachineName { get; set; } = Environment.MachineName;
        public string OSVersion { get; set; } = Environment.OSVersion.ToString();
        public string DotNetVersion { get; set; } = Environment.Version.ToString();
        public string PythonPath { get; set; } = "";
        public bool PythonPathExists { get; set; }
        public string WorkingDirectory { get; set; } = "";
        public long FreeDiskBytes { get; set; }
    }

    public sealed class IndustrialStoreIndex
    {
        public List<DatasetVersion> DatasetVersions { get; set; } = new();
        public List<TrainingRunRecord> TrainingRuns { get; set; } = new();
        public List<ModelRegistryEntry> ModelRegistry { get; set; } = new();
    }
}
