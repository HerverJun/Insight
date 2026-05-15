using System.Text.RegularExpressions;

namespace Insight.Training
{
    public sealed class YoloTrainingEngine : ITrainingEngine
    {
        public TrainingProfile Profile { get; } = new()
        {
            EngineId = "yolo",
            DisplayName = "YOLO"
        };

        public TrainingLaunchPlan BuildLaunchPlan(TrainingRequest request)
        {
            var pythonPath = ResolvePythonPath(request.PythonPath);
            var workDir = string.IsNullOrWhiteSpace(request.WorkDir)
                ? AppDomain.CurrentDomain.BaseDirectory
                : request.WorkDir;

            var commandFile = Path.Combine(workDir, "训练命令.txt");
            var arguments = ReadYoloArguments(commandFile);
            if (string.IsNullOrWhiteSpace(arguments))
            {
                throw new InvalidOperationException("找不到有效的训练命令，请先重新生成数据集。");
            }

            return new TrainingLaunchPlan
            {
                PythonPath = pythonPath,
                Arguments = arguments,
                WorkingDirectory = workDir
            };
        }

        public TrainingMetricUpdate? TryParseMetrics(string cleanOutputLine)
        {
            var cleanLine = Regex.Replace(cleanOutputLine, @"\x1B\[[^@-~]*[@-~]", "").Trim();
            var parts = cleanLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 5 && parts[0].Contains("/"))
            {
                var progress = TryParseProgress(parts[0]);
                if (double.TryParse(parts[2], out var boxLoss))
                {
                    return new TrainingMetricUpdate
                    {
                        Epoch = parts[0],
                        BoxLoss = boxLoss,
                        Progress = progress
                    };
                }
            }

            if (parts.Length >= 6 && parts[0] == "all" && double.TryParse(parts[5], out var map50))
            {
                double.TryParse(parts.Length > 6 ? parts[6] : "0", out var map5095);
                return new TrainingMetricUpdate
                {
                    Epoch = "val",
                    Map50 = map50,
                    Map5095 = map5095
                };
            }

            return null;
        }

        public string? FindBestArtifact(string workDir, DateTime trainingStartTime)
        {
            var runsDir = Path.Combine(workDir, "runs", "detect");
            if (!Directory.Exists(runsDir)) return null;

            var cutoff = trainingStartTime.AddSeconds(-10);
            return Directory.GetDirectories(runsDir, "train*")
                .Select(d => new
                {
                    Dir = d,
                    BestPt = Path.Combine(d, "weights", "best.pt"),
                    LastWriteTime = Directory.GetLastWriteTime(d)
                })
                .Where(x => x.LastWriteTime >= cutoff && File.Exists(x.BestPt))
                .OrderByDescending(x => x.LastWriteTime)
                .Select(x => x.BestPt)
                .FirstOrDefault();
        }

        public string BuildExportArguments(string artifactPath)
        {
            return $"-c \"from ultralytics.cfg import entrypoint; entrypoint()\" export model=\"{artifactPath}\" format=onnx simplify=True";
        }

        private static string ResolvePythonPath(string pythonPath)
        {
            if (string.IsNullOrWhiteSpace(pythonPath)) pythonPath = "python";

            pythonPath = pythonPath.Replace("conda activate ", "").Replace("activate ", "").Trim();
            if (Directory.Exists(pythonPath))
            {
                var potentialPython = Path.Combine(pythonPath, "python.exe");
                if (File.Exists(potentialPython)) return potentialPython;

                potentialPython = Path.Combine(pythonPath, "Scripts", "python.exe");
                if (File.Exists(potentialPython)) return potentialPython;
            }

            if (!File.Exists(pythonPath) && !pythonPath.Equals("python", StringComparison.OrdinalIgnoreCase))
            {
                throw new FileNotFoundException($"找不到 Python 解释器: {pythonPath}。请指定 python.exe 的完整路径或有效的环境目录。");
            }

            return pythonPath;
        }

        private static string ReadYoloArguments(string commandFile)
        {
            if (!File.Exists(commandFile)) return "";

            foreach (var line in File.ReadAllLines(commandFile))
            {
                var trim = line.Trim();
                if (!string.IsNullOrEmpty(trim) && !trim.StartsWith("#") && trim.StartsWith("yolo"))
                {
                    var yoloArgs = trim.Substring(5);
                    return $"-c \"from ultralytics.cfg import entrypoint; entrypoint()\" {yoloArgs}";
                }
            }

            return "";
        }

        private static double TryParseProgress(string epochText)
        {
            var parts = epochText.Split('/');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], out var current) &&
                double.TryParse(parts[1], out var total) &&
                total > 0)
            {
                return current / total * 100;
            }

            return 0;
        }
    }
}
