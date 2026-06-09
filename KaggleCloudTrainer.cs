using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Insight.Infrastructure.Processes;
using Insight.Services.Configuration;

namespace Insight
{
    public class KaggleCloudTrainingOptions
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
    }

    public class KaggleCloudTrainingResult
    {
        public string DatasetId { get; set; } = "";
        public string KernelId { get; set; } = "";
        public string JobRoot { get; set; } = "";
    }

    public static class KaggleCloudTrainer
    {
        public static async Task<KaggleCloudTrainingResult> StartAsync(
            KaggleCloudTrainingOptions options,
            Action<string, string>? onLog,
            Action<int>? onProgress)
        {
            return await StartAsync(
                options,
                onLog,
                onProgress,
                new SystemProcessRunner(),
                InsightAppPaths.CreateDefault(),
                CancellationToken.None);
        }

        public static async Task<KaggleCloudTrainingResult> StartAsync(
            KaggleCloudTrainingOptions options,
            Action<string, string>? onLog,
            Action<int>? onProgress,
            IProcessRunner processRunner,
            InsightAppPaths paths,
            CancellationToken cancellationToken)
        {
            ValidateOptions(options);

            var username = Slugify(options.KaggleUsername);
            var datasetSlug = Slugify(string.IsNullOrWhiteSpace(options.DatasetSlug)
                ? $"{options.ProjectName}-yolo-dataset"
                : options.DatasetSlug);
            var kernelSlug = Slugify(string.IsNullOrWhiteSpace(options.KernelSlug)
                ? $"{options.ProjectName}-yolo-train"
                : options.KernelSlug);

            var datasetId = $"{username}/{datasetSlug}";
            var kernelId = $"{username}/{kernelSlug}";
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var jobRoot = Path.Combine(paths.CloudJobsRoot, "kaggle", $"{datasetSlug}_{timestamp}");
            var datasetDir = Path.Combine(jobRoot, "dataset");
            var kernelDir = Path.Combine(jobRoot, "kernel");

            Directory.CreateDirectory(jobRoot);
            Directory.CreateDirectory(kernelDir);

            onLog?.Invoke("检查 Kaggle CLI...", "info");
            await RunKaggleAsync(new[] { "--version" }, jobRoot, onLog, processRunner, cancellationToken);
            EnsureKaggleTokenExists();

            onProgress?.Invoke(5);
            cancellationToken.ThrowIfCancellationRequested();
            await YoloDatasetExporter.ExportToDirectoryAsync(
                sourcePaths: options.SourcePaths,
                targetPath: datasetDir,
                classes: options.Classes,
                yoloVersion: options.YoloVersion,
                modelSize: options.ModelSize,
                epochs: options.Epochs,
                batchSize: options.BatchSize,
                imgSize: options.ImgSize,
                patience: options.Patience,
                workers: options.Workers,
                gpuIndex: options.GpuIndex,
                splitRatio: options.SplitRatio,
                onLog: onLog,
                onProgress: progress => onProgress?.Invoke(Math.Min(60, 5 + (int)(progress * 0.55))));

            cancellationToken.ThrowIfCancellationRequested();
            WriteDatasetMetadata(datasetDir, datasetId, $"{options.ProjectName} YOLO Dataset");

            onProgress?.Invoke(65);
            onLog?.Invoke($"上传 Kaggle Dataset: {datasetId}", "info");
            var create = await RunKaggleAsync(
                new[] { "datasets", "create", "-p", datasetDir, "--dir-mode", "zip", "-q" },
                datasetDir,
                onLog,
                processRunner,
                cancellationToken,
                throwOnError: false);

            if (create.ExitCode != 0)
            {
                onLog?.Invoke("Dataset 已存在或创建失败，尝试发布新版本...", "warning");
                await RunKaggleAsync(
                    new[] { "datasets", "version", "-p", datasetDir, "-m", $"Insight upload {timestamp}", "--dir-mode", "zip", "-q" },
                    datasetDir,
                    onLog,
                    processRunner,
                    cancellationToken);
            }

            onProgress?.Invoke(80);
            PrepareKernelDirectory(kernelDir, kernelId, $"{options.ProjectName} YOLO Cloud Train", datasetId, onLog);

            onLog?.Invoke($"提交 Kaggle Notebook: {kernelId}", "info");
            await RunKaggleAsync(
                new[] { "kernels", "push", "-p", kernelDir },
                kernelDir,
                onLog,
                processRunner,
                cancellationToken);

            onProgress?.Invoke(95);
            await RunKaggleAsync(
                new[] { "kernels", "status", kernelId },
                kernelDir,
                onLog,
                processRunner,
                cancellationToken,
                throwOnError: false);

            onProgress?.Invoke(100);
            return new KaggleCloudTrainingResult
            {
                DatasetId = datasetId,
                KernelId = kernelId,
                JobRoot = jobRoot
            };
        }

        public static async Task<string> DownloadOutputAsync(
            string kernelId,
            string outputDir,
            Action<string, string>? onLog)
        {
            return await DownloadOutputAsync(
                kernelId,
                outputDir,
                onLog,
                new SystemProcessRunner(),
                InsightAppPaths.CreateDefault(),
                CancellationToken.None);
        }

        public static async Task<string> DownloadOutputAsync(
            string kernelId,
            string outputDir,
            Action<string, string>? onLog,
            IProcessRunner processRunner,
            InsightAppPaths paths,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(kernelId) || !kernelId.Contains('/'))
            {
                throw new ArgumentException("Kernel ID 不能为空，格式应为 username/kernel-slug。");
            }

            if (string.IsNullOrWhiteSpace(outputDir))
            {
                outputDir = Path.Combine(paths.CloudJobsRoot, "kaggle-outputs", Slugify(kernelId.Replace('/', '-')));
            }

            Directory.CreateDirectory(outputDir);
            onLog?.Invoke($"下载 Kaggle 输出: {kernelId}", "info");
            await RunKaggleAsync(
                new[] { "kernels", "output", kernelId, "-p", outputDir },
                outputDir,
                onLog,
                processRunner,
                cancellationToken);
            onLog?.Invoke($"Kaggle 输出已保存: {outputDir}", "success");
            return outputDir;
        }

        private static void ValidateOptions(KaggleCloudTrainingOptions options)
        {
            if (options.SourcePaths.Count == 0)
            {
                throw new ArgumentException("请至少选择一个数据源文件夹。");
            }

            if (string.IsNullOrWhiteSpace(options.KaggleUsername))
            {
                throw new ArgumentException("请填写 Kaggle 用户名。");
            }

            if (options.Classes.Count == 0)
            {
                throw new ArgumentException("项目类别为空，请先在项目配置中设置 Classes。");
            }
        }

        private static void EnsureKaggleTokenExists()
        {
            var tokenPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".kaggle",
                "kaggle.json");

            if (!File.Exists(tokenPath))
            {
                throw new InvalidOperationException(
                    $"未找到 Kaggle API Token: {tokenPath}。请在 Kaggle Account 页面创建 API Token，并把 kaggle.json 放到该位置。");
            }
        }

        private static void WriteDatasetMetadata(string datasetDir, string datasetId, string title)
        {
            var metadata = new
            {
                title,
                id = datasetId,
                licenses = new[] { new { name = "CC0-1.0" } }
            };

            File.WriteAllText(
                Path.Combine(datasetDir, "dataset-metadata.json"),
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void PrepareKernelDirectory(
            string kernelDir,
            string kernelId,
            string title,
            string datasetId,
            Action<string, string>? onLog)
        {
            var notebookSource = FindBundledFile("YOLO_TRAIN_KaggleDataset.ipynb");
            var notebookTarget = Path.Combine(kernelDir, "YOLO_TRAIN_KaggleDataset.ipynb");
            File.Copy(notebookSource, notebookTarget, true);

            var metadata = new
            {
                id = kernelId,
                title,
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
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

            onLog?.Invoke($"Notebook 已准备: {notebookTarget}", "info");
        }

        private static string FindBundledFile(string fileName)
        {
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", fileName)),
                Path.Combine(Directory.GetCurrentDirectory(), fileName)
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException($"找不到 Kaggle Notebook 模板: {fileName}");
        }

        private static string Slugify(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            value = Regex.Replace(value, @"[^a-z0-9_-]+", "-");
            value = Regex.Replace(value, @"-+", "-").Trim('-');
            return string.IsNullOrWhiteSpace(value) ? "insight-yolo" : value;
        }

        private static async Task<ProcessResult> RunKaggleAsync(
            IReadOnlyList<string> args,
            string workingDirectory,
            Action<string, string>? onLog,
            IProcessRunner processRunner,
            CancellationToken cancellationToken,
            bool throwOnError = true)
        {
            try
            {
                return await RunProcessAsync("kaggle", args, workingDirectory, onLog, processRunner, cancellationToken, throwOnError);
            }
            catch (Exception ex) when (IsProcessStartFailure(ex))
            {
                onLog?.Invoke("未在 PATH 中找到 kaggle 命令，尝试查找 Python Scripts 里的 kaggle.exe...", "warning");
            }

            var kaggleExe = FindKaggleExecutable();
            if (!string.IsNullOrWhiteSpace(kaggleExe))
            {
                onLog?.Invoke($"使用 Kaggle CLI: {kaggleExe}", "info");
                return await RunProcessAsync(kaggleExe, args, workingDirectory, onLog, processRunner, cancellationToken, throwOnError);
            }

            throw new InvalidOperationException(
                "未找到 Kaggle CLI。请先运行: pip install kaggle，并确认 Python Scripts 目录在 PATH 中，或重启 Insight 后再试。");
        }

        private static string? FindKaggleExecutable()
        {
            var candidates = new List<string>();
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (!string.IsNullOrWhiteSpace(appData))
            {
                var roamingPython = Path.Combine(appData, "Python");
                if (Directory.Exists(roamingPython))
                {
                    candidates.AddRange(Directory.GetFiles(roamingPython, "kaggle.exe", SearchOption.AllDirectories));
                }
            }

            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                var localPython = Path.Combine(localAppData, "Programs", "Python");
                if (Directory.Exists(localPython))
                {
                    candidates.AddRange(Directory.GetFiles(localPython, "kaggle.exe", SearchOption.AllDirectories));
                }
            }

            return candidates.FirstOrDefault(File.Exists);
        }

        private static async Task<ProcessResult> RunProcessAsync(
            string fileName,
            IReadOnlyList<string> args,
            string workingDirectory,
            Action<string, string>? onLog,
            IProcessRunner processRunner,
            CancellationToken cancellationToken,
            bool throwOnError = true)
        {
            var output = new List<string>();
            var result = await processRunner.RunAsync(
                new ProcessStartSpec
                {
                    FileName = fileName,
                    ArgumentList = args.ToArray(),
                    WorkingDirectory = workingDirectory,
                    EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["PYTHONIOENCODING"] = "utf-8"
                    }
                },
                line =>
                {
                    output.Add(line);
                    onLog?.Invoke(line, "info");
                },
                cancellationToken);

            var processResult = new ProcessResult(result.ExitCode ?? -1, string.Join(Environment.NewLine, output));
            if (throwOnError && !result.Succeeded)
            {
                throw new InvalidOperationException($"Kaggle 命令执行失败，退出码: {processResult.ExitCode}\n{processResult.Output}");
            }

            return processResult;
        }

        private static bool IsProcessStartFailure(Exception ex)
        {
            return ex is Win32Exception ||
                ex.InnerException is Win32Exception ||
                ex is InvalidOperationException { InnerException: Win32Exception };
        }

        private readonly record struct ProcessResult(int ExitCode, string Output);
    }
}
