namespace Insight.Training
{
    public enum TrainingRunState
    {
        Idle,
        Preparing,
        Running,
        Stopping,
        Completed,
        Failed
    }

    public sealed class TrainingParams
    {
        public string ModelSize { get; set; } = "s";
        public int ImgSize { get; set; } = 640;
        public int Epochs { get; set; } = 3000;
        public int BatchSize { get; set; } = -1;
        public int Patience { get; set; } = 50;
        public int Workers { get; set; } = 8;
        public string GpuIndex { get; set; } = "0";
        public bool EnableP2 { get; set; }
        public bool AutoFixImage { get; set; } = true;
        public string OnnxName { get; set; } = "";
        public string ModelStoragePath { get; set; } = "";
        public List<string> Classes { get; set; } = new();
    }

    public sealed class TrainingRequest
    {
        public string PythonPath { get; set; } = "python";
        public string WorkDir { get; set; } = "";
        public TrainingParams Params { get; set; } = new();
    }

    public sealed class TrainingProfile
    {
        public string EngineId { get; set; } = "yolo";
        public string DisplayName { get; set; } = "YOLO";
    }

    public sealed class TrainingMetrics
    {
        public double BoxLoss { get; set; }
        public double Map50 { get; set; }
        public double Map5095 { get; set; }
    }

    public sealed class TrainingMetricUpdate
    {
        public string Epoch { get; set; } = "";
        public double? BoxLoss { get; set; }
        public double? Map50 { get; set; }
        public double? Map5095 { get; set; }
        public double? Progress { get; set; }
    }

    public sealed class TrainingArtifact
    {
        public string Path { get; set; } = "";
        public string Format { get; set; } = "";
    }

    public sealed class TrainingLaunchPlan
    {
        public string PythonPath { get; set; } = "python";
        public string Arguments { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
    }
}
