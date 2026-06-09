namespace Insight.Bridge
{
    public sealed class SelectPathPayload : IWebViewPayload
    {
        public string Type { get; set; } = "";

        public void Validate()
        {
            Require(Type, nameof(Type));
        }

        private static void Require(string value, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"{propertyName} is required.");
            }
        }
    }

    public sealed class SaveDefaultPythonPathPayload : IWebViewPayload
    {
        public string Path { get; set; } = "";

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Path))
            {
                throw new InvalidOperationException($"{nameof(Path)} is required.");
            }
        }
    }

    public sealed class StartKaggleTrainingPayload : IWebViewPayload
    {
        public List<string> SourcePaths { get; set; } = new();
        public List<string> Classes { get; set; } = new();
        public string ProjectName { get; set; } = "Insight";
        public string KaggleUsername { get; set; } = "";
        public string DatasetSlug { get; set; } = "";
        public string KernelSlug { get; set; } = "";
        public string YoloVersion { get; set; } = "yolov8";
        public string ModelSize { get; set; } = "s";
        public int Epochs { get; set; } = 300;
        public int BatchSize { get; set; } = 16;
        public int ImgSize { get; set; } = 640;
        public int Patience { get; set; } = 50;
        public int Workers { get; set; } = 8;
        public string GpuIndex { get; set; } = "0";
        public double SplitRatio { get; set; } = 0.8;

        public void Validate()
        {
            if (SourcePaths.Count == 0)
            {
                throw new InvalidOperationException($"{nameof(SourcePaths)} requires at least one entry.");
            }

            if (Classes.Count == 0)
            {
                throw new InvalidOperationException($"{nameof(Classes)} requires at least one entry.");
            }

            if (string.IsNullOrWhiteSpace(KaggleUsername))
            {
                throw new InvalidOperationException($"{nameof(KaggleUsername)} is required.");
            }

            Epochs = Math.Max(1, Epochs);
            BatchSize = BatchSize == 0 ? 16 : BatchSize;
            ImgSize = Math.Max(32, ImgSize);
            Patience = Math.Max(1, Patience);
            Workers = Math.Max(0, Workers);
            SplitRatio = Math.Clamp(SplitRatio, 0.05, 0.95);
        }

        public KaggleCloudTrainingOptions ToOptions()
        {
            return new KaggleCloudTrainingOptions
            {
                SourcePaths = SourcePaths,
                Classes = Classes,
                ProjectName = string.IsNullOrWhiteSpace(ProjectName) ? "Insight" : ProjectName,
                KaggleUsername = KaggleUsername,
                DatasetSlug = DatasetSlug,
                KernelSlug = KernelSlug,
                YoloVersion = string.IsNullOrWhiteSpace(YoloVersion) ? "yolov8" : YoloVersion,
                ModelSize = string.IsNullOrWhiteSpace(ModelSize) ? "s" : ModelSize,
                Epochs = Epochs,
                BatchSize = BatchSize,
                ImgSize = ImgSize,
                Patience = Patience,
                Workers = Workers,
                GpuIndex = string.IsNullOrWhiteSpace(GpuIndex) ? "0" : GpuIndex,
                SplitRatio = SplitRatio
            };
        }
    }

    public sealed class DownloadKaggleOutputPayload : IWebViewPayload
    {
        public string KernelId { get; set; } = "";
        public string OutputDir { get; set; } = "";

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(KernelId))
            {
                throw new InvalidOperationException($"{nameof(KernelId)} is required.");
            }
        }
    }

    public sealed class TestKaggleConnectionPayload : IWebViewPayload
    {
        public string KaggleUsername { get; set; } = "";

        public void Validate()
        {
        }
    }

    public sealed class DirectoryPathPayload : IWebViewPayload
    {
        public string Path { get; set; } = "";

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Path))
            {
                throw new InvalidOperationException($"{nameof(Path)} is required.");
            }
        }
    }

    public sealed class ModelFilePayload : IWebViewPayload
    {
        public string Path { get; set; } = "";
        public string FileName { get; set; } = "";

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Path))
            {
                throw new InvalidOperationException($"{nameof(Path)} is required.");
            }

            if (string.IsNullOrWhiteSpace(FileName))
            {
                throw new InvalidOperationException($"{nameof(FileName)} is required.");
            }
        }
    }

    public sealed class RenameModelPayload : IWebViewPayload
    {
        public string Path { get; set; } = "";
        public string OldName { get; set; } = "";
        public string NewName { get; set; } = "";

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Path))
            {
                throw new InvalidOperationException($"{nameof(Path)} is required.");
            }

            if (string.IsNullOrWhiteSpace(OldName))
            {
                throw new InvalidOperationException($"{nameof(OldName)} is required.");
            }

            if (string.IsNullOrWhiteSpace(NewName))
            {
                throw new InvalidOperationException($"{nameof(NewName)} is required.");
            }
        }
    }

    public sealed class ConvertModelPayload : IWebViewPayload
    {
        public string SourcePath { get; set; } = "";
        public string TargetDir { get; set; } = "";
        public string PythonPath { get; set; } = "";

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(SourcePath))
            {
                throw new InvalidOperationException($"{nameof(SourcePath)} is required.");
            }

            if (string.IsNullOrWhiteSpace(TargetDir))
            {
                throw new InvalidOperationException($"{nameof(TargetDir)} is required.");
            }
        }
    }
}
