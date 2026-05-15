using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using Insight.Bridge;
using Insight.Services.Training;
using Insight.Training;
using OpenCvSharp;

namespace Insight.Services
{
    public partial class InsightApplication : IDisposable
    {
        private readonly Random _random = new();
        private readonly IFrontendMessenger _messenger;
        private readonly IUiDispatcher _ui;
        private readonly IAppDialogService _dialogs;
        private readonly TrainingOrchestrator _training;
        private readonly SamLabelingService _samLabeling;

        public InsightApplication(IFrontendMessenger messenger, IUiDispatcher ui, IAppDialogService dialogs)
        {
            _messenger = messenger;
            _ui = ui;
            _dialogs = dialogs;
            _training = new TrainingOrchestrator(_messenger, new YoloTrainingEngine());
            _samLabeling = new SamLabelingService(_messenger, _ui);
        }

        public void Dispose()
        {
            _samLabeling.Dispose();
            _training.Dispose();
        }

        public void HandleGetProjects()
        {
            var projects = ProjectManager.LoadProjects();
            SendToFrontend(new { action = WebViewActions.Outgoing.ProjectsLoaded, projects });
        }

        public void HandleGetSubfolders(JsonElement data)
        {
            if (data.TryGetProperty("path", out var pathProp))
            {
                var sub = ProjectManager.GetSubFolders(pathProp.GetString()!);
                SendToFrontend(new { action = WebViewActions.Outgoing.SubfoldersLoaded, folders = sub });
            }
        }

        public void HandleGetOnnxProjects()
        {
            var projects = OnnxProjectManager.LoadProjects();
            SendToFrontend(new { action = WebViewActions.Outgoing.OnnxProjectsLoaded, projects });
        }

        public void HandleOpenModelFolder(JsonElement data)
        {
            if (!data.TryGetProperty("path", out var modelPathProp)) return;

            var modelPath = modelPathProp.GetString();
            try
            {
                if (!string.IsNullOrEmpty(modelPath) && File.Exists(modelPath))
                {
                    _dialogs.OpenFileInExplorer(modelPath);
                }
                else if (!string.IsNullOrEmpty(modelPath))
                {
                    var folder = Path.GetDirectoryName(modelPath);
                    if (Directory.Exists(folder))
                    {
                        _dialogs.OpenFolder(folder);
                    }
                    else
                    {
                        SendError($"目标文件夹不存在: {folder}");
                    }
                }
            }
            catch (Exception ex)
            {
                SendError($"打开文件夹失败: {ex.Message}");
            }
        }

        public void HandleDeleteProject(JsonElement data)
        {
            try
            {
                if (data.TryGetProperty("index", out var indexProp))
                {
                    ProjectManager.DeleteProject(indexProp.GetInt32());
                    var projects = ProjectManager.LoadProjects();
                    SendToFrontend(new { action = "projects_loaded", projects });
                }
            }
            catch (Exception ex)
            {
                SendError($"删除失败: {ex.Message}");
            }
        }

        public void HandleCreateProject(JsonElement data)
        {
            try
            {
                var name = data.GetProperty("name").GetString();
                var rootPath = data.GetProperty("rootPath").GetString();
                var classStr = data.GetProperty("classes").GetString();

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rootPath))
                {
                    SendError("项目名称和根目录不能为空");
                    return;
                }

                if (!Directory.Exists(rootPath))
                {
                    SendError("指定的根目录不存在");
                    return;
                }

                var classes = classStr?.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(c => c.Trim())
                                      .ToList() ?? new List<string>();

                var newProject = new ProjectConfig
                {
                    Name = name,
                    RootPath = rootPath,
                    Classes = classes
                };

                ProjectManager.AddProject(newProject);

                // Refresh list
                var projects = ProjectManager.LoadProjects();
                SendToFrontend(new { action = "projects_loaded", projects });
                SendComplete("项目创建成功！");
            }
            catch (Exception ex)
            {
                SendError($"创建项目失败: {ex.Message}");
            }
        }

        public async Task HandleDefectAugmentationAsync(JsonElement data)
        {
            try
            {
                var sourcePath = data.GetProperty("sourcePath").GetString();
                var holeAssetsPath = data.GetProperty("holeAssetsPath").GetString();
                var mutationRate = data.GetProperty("mutationRate").GetDouble();

                if (string.IsNullOrEmpty(sourcePath) || !Directory.Exists(sourcePath))
                {
                    SendError("源数据路径无效");
                    return;
                }

                if (string.IsNullOrEmpty(holeAssetsPath) || !Directory.Exists(holeAssetsPath))
                {
                    SendError("素材路径无效");
                    return;
                }

                var outputDir = Path.Combine(Path.GetDirectoryName(sourcePath) ?? sourcePath, Path.GetFileName(sourcePath) + "_Augmented");

                await Task.Run(() =>
                {
                    try
                    {
                        SendLog($"开始缺陷增强处理...", "info");
                        SendLog($"源目录: {sourcePath}");
                        SendLog($"素材库: {holeAssetsPath}");
                        SendLog($"输出目录: {outputDir}");

                        DefectAugmentationService.ProcessDataset(
                            sourcePath, // 图片目录
                            sourcePath, // 标签目录（与图片同目录）
                            holeAssetsPath,
                            outputDir,
                            (msg) => SendLog(msg),
                            (current, total) =>
                            {
                                int progress = (int)((double)current / total * 100);
                                SendToFrontend(new { action = "augment_progress", progress = progress });
                            },
                            mutationRate
                        );

                        SendComplete($"缺陷增强完成！输出至: {outputDir}");
                        SendToFrontend(new { action = "augment_complete", path = outputDir });
                    }
                    catch (Exception ex)
                    {
                        SendError($"增强处理失败: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                SendError($"参数解析失败: {ex.Message}");
            }
        }

        public void HandleSelectFolder(string type)
        {
            try
            {
                var selectedPath = _dialogs.SelectFolder(type);
                if (!string.IsNullOrEmpty(selectedPath))
                {
                    SendToFrontend(new
                    {
                        action = "folder_selected",
                        type,
                        path = selectedPath
                    });
                }
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                _dialogs.ShowWarning($"文件夹选择器发生错误，请重试。\n{ex.Message}", "错误");
            }
            catch (Exception ex)
            {
                _dialogs.ShowError($"打开文件夹选择器失败: {ex.Message}", "错误");
            }
        }

        public void HandleSelectFile(string type)
        {
            try
            {
                var selectedPath = _dialogs.SelectPythonFile(type);
                if (!string.IsNullOrEmpty(selectedPath))
                {
                    SendToFrontend(new
                    {
                        action = "folder_selected",
                        type,
                        path = selectedPath
                    });
                }
            }
            catch (Exception ex)
            {
                _dialogs.ShowError($"打开文件选择器失败: {ex.Message}", "错误");
            }
        }

        public async Task HandleGenerateAsync(JsonElement data)
        {
            try
            {
                var sourcePaths = data.GetProperty("sourcePaths").EnumerateArray().Select(x => x.GetString()!).ToList();
                var targetPath = data.GetProperty("targetPath").GetString()!;
                var classes = data.GetProperty("classes").EnumerateArray().Select(x => x.GetString()!).ToList();
                // classNames 从前端传递 (Project Config)

                var paramsData = new TrainingParams
                {
                    ModelSize = data.GetProperty("modelSize").GetString()!,
                    ImgSize = data.GetProperty("imgSize").GetInt32(),
                    Epochs = data.GetProperty("epochs").GetInt32(),
                    BatchSize = data.GetProperty("batchSize").GetInt32(),
                    Patience = data.GetProperty("patience").GetInt32(),
                    Workers = data.GetProperty("workers").GetInt32(),
                    GpuIndex = data.GetProperty("gpuIndex").GetString()!,
                    EnableP2 = data.GetProperty("enableP2").GetBoolean(),
                    AutoFixImage = data.GetProperty("autoFixImage").GetBoolean(),
                    OnnxName = data.TryGetProperty("onnxName", out var onnxNameEl) ? onnxNameEl.GetString() ?? "" : "",
                    ModelStoragePath = data.TryGetProperty("modelStoragePath", out var storageEl) ? storageEl.GetString() ?? "" : ""
                };

                await Task.Run(() => GenerateDataset(sourcePaths, targetPath, classes, paramsData));
            }
            catch (Exception ex)
            {
                SendError($"生成失败: {ex.Message}");
            }
        }

        private void GenerateDataset(List<string> sourcePaths, string targetPath, List<string> classes, TrainingParams paramsData)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    SendError("目标保存路径不能为空");
                    return;
                }

                var normalizedTargetPath = Path.GetFullPath(targetPath);
                if (sourcePaths.Any(p => Directory.Exists(p) &&
                    string.Equals(Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                  normalizedTargetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                  StringComparison.OrdinalIgnoreCase)))
                {
                    SendError("目标保存路径不能与源数据文件夹相同，请选择独立输出目录。");
                    return;
                }

                // 1. 扫描所有源文件夹的图片
                SendLog("正在扫描源文件夹...");
                var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp" };
                var imageFiles = new List<string>();

                foreach (var path in sourcePaths)
                {
                    if (Directory.Exists(path))
                    {
                        var files = Directory.GetFiles(path)
                            .Where(f => imageExtensions.Contains(Path.GetExtension(f).ToLower()));
                        imageFiles.AddRange(files);
                    }
                }

                if (imageFiles.Count == 0)
                {
                    SendError("选定的文件夹中未找到任何图片文件");
                    return;
                }

                SendLog($"共找到 {imageFiles.Count} 张图片");

                // 2. 使用项目配置的类别
                classes = classes
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (classes.Count == 0)
                {
                    SendError("项目配置中未定义任何类别！请检查 projects.json");
                    return;
                }
                SendLog($"使用项目类别配置: {string.Join(", ", classes)}");

                // 建立类别名称到索引的映射
                var classMap = classes.Select((name, index) => new { name, index })
                                      .ToDictionary(x => x.name, x => x.index);

                Directory.CreateDirectory(targetPath);
                ClearGeneratedDatasetDirs(targetPath);

                // 3. 创建目录结构
                SendLog("正在创建目录结构...");
                var trainImagesDir = Path.Combine(targetPath, "images", "train");
                var valImagesDir = Path.Combine(targetPath, "images", "val");
                var trainLabelsDir = Path.Combine(targetPath, "labels", "train");
                var valLabelsDir = Path.Combine(targetPath, "labels", "val");

                Directory.CreateDirectory(trainImagesDir);
                Directory.CreateDirectory(valImagesDir);
                Directory.CreateDirectory(trainLabelsDir);
                Directory.CreateDirectory(valLabelsDir);

                // 4. 随机打乱并切分 (9:1)
                SendLog("正在切分数据集 (9:1)...");
                var shuffled = imageFiles.OrderBy(_ => _random.Next()).ToList();
                var splitIndex = (int)(shuffled.Count * 0.9);
                var trainFiles = shuffled.Take(splitIndex).ToList();
                var valFiles = shuffled.Skip(splitIndex).ToList();

                // 5. 处理训练集
                SendLog("正在处理训练集...");
                ProcessFiles(trainFiles, trainImagesDir, trainLabelsDir, classMap);

                // 6. 处理验证集
                SendLog("正在处理验证集...");
                ProcessFiles(valFiles, valImagesDir, valLabelsDir, classMap);

                // 7. 生成 data.yaml
                SendLog("正在生成 data.yaml...");
                GenerateDataYaml(targetPath, classes);

                // 7.1 生成 P2 模型配置 (如果启用)
                if (paramsData.EnableP2)
                {
                    SendLog("正在生成 P2 模型配置...");
                    GenerateP2Yaml(targetPath, paramsData.ModelSize);
                }

                // 8. 生成训练命令
                SendLog("正在生成训练命令...");
                GenerateTrainCommand(targetPath, paramsData);

                // 完成
                SendComplete($"✓ 数据集生成完成！共处理 {imageFiles.Count} 张图片");
            }
            catch (Exception ex)
            {
                SendError($"生成过程中出错: {ex.Message}");
            }
        }

        private void ClearGeneratedDatasetDirs(string targetPath)
        {
            foreach (var child in new[] { "images", "labels" })
            {
                var dir = Path.Combine(targetPath, child);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
        }

        public async Task HandleExportDatasetZipAsync(JsonElement data)
        {
            try
            {
                var sourcePaths = data.GetProperty("sourcePaths").EnumerateArray().Select(x => x.GetString()!).ToList();
                var yoloVersion = data.GetProperty("yoloVersion").GetString() ?? "yolov8";
                var modelSize = data.TryGetProperty("modelSize", out var modelSizeProp) ? modelSizeProp.GetString() ?? "n" : "n";
                var epochs = data.TryGetProperty("epochs", out var epochsProp) && epochsProp.ValueKind == JsonValueKind.Number ? epochsProp.GetInt32() : 300;
                var batchSize = data.TryGetProperty("batchSize", out var batchProp) && batchProp.ValueKind == JsonValueKind.Number ? batchProp.GetInt32() : 16;
                var imgSize = data.TryGetProperty("imgSize", out var imgProp) && imgProp.ValueKind == JsonValueKind.Number ? imgProp.GetInt32() : 640;
                var patience = data.TryGetProperty("patience", out var patProp) && patProp.ValueKind == JsonValueKind.Number ? patProp.GetInt32() : 50;
                var workers = data.TryGetProperty("workers", out var workersProp) && workersProp.ValueKind == JsonValueKind.Number ? workersProp.GetInt32() : 8;
                var gpuIndex = data.TryGetProperty("gpuIndex", out var gpuProp) ? gpuProp.GetString() ?? "0" : "0";
                var splitRatio = data.GetProperty("splitRatio").GetDouble();
                var classes = data.GetProperty("classes").EnumerateArray().Select(x => x.GetString()!).ToList();
                var projectName = data.TryGetProperty("projectName", out var pName) ? pName.GetString() : "Untitled";

                if (sourcePaths.Count == 0)
                {
                    SendError("请至少选择一个数据集文件夹");
                    return;
                }

                var targetZipPath = _dialogs.SelectDatasetZipPath(projectName, yoloVersion);

                if (string.IsNullOrEmpty(targetZipPath))
                {
                    // 用户取消
                    SendToFrontend(new { action = "export_zip_cancelled" });
                    return;
                }

                await Task.Run(async () =>
                {
                    try
                    {
                        await YoloDatasetExporter.ExportToZipAsync(
                            sourcePaths: sourcePaths,
                            targetZipPath: targetZipPath,
                            classes: classes,
                            yoloVersion: yoloVersion,
                            modelSize: modelSize,
                            epochs: epochs,
                            batchSize: batchSize,
                            imgSize: imgSize,
                            patience: patience,
                            workers: workers,
                            gpuIndex: gpuIndex,
                            splitRatio: splitRatio,
                            onLog: (msg, type) => SendLog(msg, type),
                            onProgress: (progress) =>
                            {
                                SendToFrontend(new { action = "export_zip_progress", progress = progress });
                            }
                        );

                        SendToFrontend(new { action = "export_zip_complete", path = targetZipPath });
                    }
                    catch (Exception ex)
                    {
                        SendError($"导出 ZIP 失败: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                SendError($"参数解析失败: {ex.Message}");
            }
        }

        public async Task HandleStartKaggleTrainingAsync(JsonElement data)
        {
            try
            {
                var options = new KaggleCloudTrainingOptions
                {
                    SourcePaths = data.GetProperty("sourcePaths").EnumerateArray().Select(x => x.GetString()!).ToList(),
                    Classes = data.GetProperty("classes").EnumerateArray().Select(x => x.GetString() ?? "").ToList(),
                    ProjectName = data.TryGetProperty("projectName", out var pName) ? pName.GetString() ?? "Insight" : "Insight",
                    KaggleUsername = data.TryGetProperty("kaggleUsername", out var userProp) ? userProp.GetString() ?? "" : "",
                    DatasetSlug = data.TryGetProperty("datasetSlug", out var datasetProp) ? datasetProp.GetString() ?? "" : "",
                    KernelSlug = data.TryGetProperty("kernelSlug", out var kernelProp) ? kernelProp.GetString() ?? "" : "",
                    YoloVersion = data.TryGetProperty("yoloVersion", out var yoloProp) ? yoloProp.GetString() ?? "yolov8" : "yolov8",
                    ModelSize = data.TryGetProperty("modelSize", out var modelProp) ? modelProp.GetString() ?? "s" : "s",
                    Epochs = data.TryGetProperty("epochs", out var epochsProp) && epochsProp.ValueKind == JsonValueKind.Number ? epochsProp.GetInt32() : 300,
                    BatchSize = data.TryGetProperty("batchSize", out var batchProp) && batchProp.ValueKind == JsonValueKind.Number ? batchProp.GetInt32() : 16,
                    ImgSize = data.TryGetProperty("imgSize", out var imgProp) && imgProp.ValueKind == JsonValueKind.Number ? imgProp.GetInt32() : 640,
                    Patience = data.TryGetProperty("patience", out var patProp) && patProp.ValueKind == JsonValueKind.Number ? patProp.GetInt32() : 50,
                    Workers = data.TryGetProperty("workers", out var workersProp) && workersProp.ValueKind == JsonValueKind.Number ? workersProp.GetInt32() : 8,
                    GpuIndex = data.TryGetProperty("gpuIndex", out var gpuProp) ? gpuProp.GetString() ?? "0" : "0",
                    SplitRatio = data.TryGetProperty("splitRatio", out var splitProp) && splitProp.ValueKind == JsonValueKind.Number ? splitProp.GetDouble() : 0.8
                };

                SendLog("准备提交 Kaggle 云端训练...", "info");
                SendToFrontend(new { action = "kaggle_progress", progress = 0, status = "准备 Kaggle 云训练" });

                await Task.Run(async () =>
                {
                    try
                    {
                        var result = await KaggleCloudTrainer.StartAsync(
                            options,
                            (msg, type) => SendLog(msg, type),
                            progress => SendToFrontend(new { action = "kaggle_progress", progress, status = "Kaggle 云训练提交中" }));

                        SendToFrontend(new
                        {
                            action = "kaggle_training_started",
                            datasetId = result.DatasetId,
                            kernelId = result.KernelId,
                            jobRoot = result.JobRoot
                        });
                        SendLog($"Kaggle Notebook 已提交: https://www.kaggle.com/code/{result.KernelId}", "success");
                    }
                    catch (Exception ex)
                    {
                        SendError($"Kaggle 云训练提交失败: {ex.Message}");
                        SendToFrontend(new { action = "kaggle_training_failed" });
                    }
                });
            }
            catch (Exception ex)
            {
                SendError($"Kaggle 云训练参数解析失败: {ex.Message}");
                SendToFrontend(new { action = "kaggle_training_failed" });
            }
        }

        public async Task HandleDownloadKaggleOutputAsync(JsonElement data)
        {
            try
            {
                var kernelId = data.TryGetProperty("kernelId", out var kernelProp) ? kernelProp.GetString() ?? "" : "";
                var outputDir = data.TryGetProperty("outputDir", out var outputProp) ? outputProp.GetString() ?? "" : "";

                SendLog("准备下载 Kaggle 输出...", "info");
                await Task.Run(async () =>
                {
                    try
                    {
                        var resolvedOutputDir = await KaggleCloudTrainer.DownloadOutputAsync(kernelId, outputDir, (msg, type) => SendLog(msg, type));
                        SendToFrontend(new { action = "kaggle_output_downloaded", outputDir = resolvedOutputDir });
                    }
                    catch (Exception ex)
                    {
                        SendError($"下载 Kaggle 输出失败: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                SendError($"Kaggle 输出下载参数解析失败: {ex.Message}");
            }
        }

        private List<string> ScanClasses(string sourcePath)
        {
            var classes = new HashSet<string>();
            var jsonFiles = Directory.GetFiles(sourcePath, "*.json");

            // 优先从 JSON 提取
            foreach (var jsonFile in jsonFiles)
            {
                try
                {
                    var content = File.ReadAllText(jsonFile);
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("shapes", out var shapes))
                    {
                        foreach (var shape in shapes.EnumerateArray())
                        {
                            if (shape.TryGetProperty("label", out var labelProp))
                            {
                                classes.Add(labelProp.GetString()!);
                            }
                        }
                    }
                }
                catch { /* 跳过无效的 JSON 文件 */ }
            }

            // 如果没有 JSON，尝试查找 classes.txt
            if (classes.Count == 0)
            {
                var classesTxt = Path.Combine(sourcePath, "classes.txt");
                if (File.Exists(classesTxt))
                {
                    var lines = File.ReadAllLines(classesTxt)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrEmpty(l));
                    foreach (var line in lines) classes.Add(line);
                }
            }

            return classes.OrderBy(c => c).ToList();
        }

        private void ProcessFiles(List<string> files, string imagesDir, string labelsDir, Dictionary<string, int> classMap)
        {
            var usedOutputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var imageFile in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(imageFile);
                var outputBaseName = GetUniqueOutputBaseName(fileName, imageFile, usedOutputNames);
                var extension = Path.GetExtension(imageFile).ToLower();
                var sourcePath = Path.GetDirectoryName(imageFile)!;

                // 1. 处理图片
                var targetImagePath = Path.Combine(imagesDir, outputBaseName + ".jpg");
                if (extension == ".jpg" || extension == ".jpeg")
                {
                    File.Copy(imageFile, targetImagePath, true);
                }
                else
                {
                    using var image = Image.FromFile(imageFile);
                    image.Save(targetImagePath, ImageFormat.Jpeg);
                }

                // 2. 处理标签 (优先 JSON 转 YOLO，其次 TXT 复制)
                var targetLabelPath = Path.Combine(labelsDir, outputBaseName + ".txt");
                var jsonPath = Path.Combine(sourcePath, fileName + ".json");
                var txtPath = Path.Combine(sourcePath, fileName + ".txt");

                if (File.Exists(jsonPath))
                {
                    ConvertJsonToYolo(jsonPath, targetLabelPath, classMap);
                }
                else if (File.Exists(txtPath))
                {
                    File.Copy(txtPath, targetLabelPath, true);
                }
                else
                {
                    // 无标注文件，创建空标签文件（负样本）
                    File.WriteAllText(targetLabelPath, string.Empty);
                }
            }
        }

        private static string GetUniqueOutputBaseName(string originalBaseName, string sourcePath, HashSet<string> usedOutputNames)
        {
            if (usedOutputNames.Add(originalBaseName))
            {
                return originalBaseName;
            }

            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sourcePath)))
                .Substring(0, 8)
                .ToLowerInvariant();

            var candidate = $"{originalBaseName}_{hash}";
            var suffix = 2;
            while (!usedOutputNames.Add(candidate))
            {
                candidate = $"{originalBaseName}_{hash}_{suffix++}";
            }

            return candidate;
        }

        private void ConvertJsonToYolo(string jsonPath, string targetPath, Dictionary<string, int> classMap)
        {
            try
            {
                var content = File.ReadAllText(jsonPath);
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                var width = root.GetProperty("imageWidth").GetInt32();
                var height = root.GetProperty("imageHeight").GetInt32();
                if (width <= 0 || height <= 0) throw new InvalidDataException("JSON imageWidth/imageHeight 无效");
                var shapes = root.GetProperty("shapes");

                var sb = new System.Text.StringBuilder();

                foreach (var shape in shapes.EnumerateArray())
                {
                    var label = shape.GetProperty("label").GetString();
                    if (label == null || !classMap.ContainsKey(label)) continue;

                    var classId = classMap[label];
                    var points = shape.GetProperty("points");

                    // 计算多边形的外接矩形
                    double minX = double.MaxValue, minY = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue;

                    foreach (var point in points.EnumerateArray())
                    {
                        var x = point[0].GetDouble();
                        var y = point[1].GetDouble();
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }

                    // 转换为 YOLO 中心点坐标 (归一化)
                    var dw = 1.0 / width;
                    var dh = 1.0 / height;

                    var xCenter = (minX + maxX) / 2.0;
                    var yCenter = (minY + maxY) / 2.0;
                    var w = maxX - minX;
                    var h = maxY - minY;

                    xCenter *= dw;
                    w *= dw;
                    yCenter *= dh;
                    h *= dh;

                    sb.AppendLine($"{classId} {xCenter:F6} {yCenter:F6} {w:F6} {h:F6}");
                }

                File.WriteAllText(targetPath, sb.ToString());
            }
            catch (Exception ex)
            {
                File.WriteAllText(targetPath, string.Empty);
                SendLog($"标签转换失败，已写入空标签: {Path.GetFileName(jsonPath)} - {ex.Message}", "warning");
            }
        }

        private void GenerateDataYaml(string targetPath, List<string> classes)
        {
            // 构建 names 字典格式
            var namesDict = new System.Text.StringBuilder();
            for (int i = 0; i < classes.Count; i++)
            {
                namesDict.AppendLine($"  {i}: {YamlQuote(classes[i])}");
            }

            var yamlContent = $@"# YOLO Dataset Configuration
# Generated by Insight
# {DateTime.Now}

path: {YamlQuote(targetPath.Replace("\\", "/"))}
train: images/train
val: images/val

# Classes
nc: {classes.Count}
names:
{namesDict}";

            var yamlPath = Path.Combine(targetPath, "data.yaml");
            File.WriteAllText(yamlPath, yamlContent);
            SendLog($"已生成: {yamlPath}");
        }

        private static string YamlQuote(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }

        private void GenerateP2Yaml(string targetPath, string modelSize)
        {
            var yamlContent = @"# Ultralytics YOLO 🚀, AGPL-3.0 license
# YOLOv8-p2 object detection model with P2-P5 outputs. For Usage examples see https://docs.ultralytics.com/tasks/detect

# Parameters
nc: 80  # number of classes
scales: # model compound scaling constants, i.e. 'model=yolov8n.yaml' will call yolov8.yaml with scale 'n'
  # [depth, width, max_channels]
  n: [0.33, 0.25, 1024]
  s: [0.33, 0.50, 1024]
  m: [0.67, 0.75, 768]
  l: [1.00, 1.00, 512]
  x: [1.00, 1.25, 512]

backbone:
  # [from, repeats, module, args]
  - [-1, 1, Conv, [64, 3, 2]]  # 0-P1/2
  - [-1, 1, Conv, [128, 3, 2]]  # 1-P2/4
  - [-1, 3, C2f, [128, true]]
  - [-1, 1, Conv, [256, 3, 2]]  # 3-P3/8
  - [-1, 6, C2f, [256, true]]
  - [-1, 1, Conv, [512, 3, 2]]  # 5-P4/16
  - [-1, 6, C2f, [512, true]]
  - [-1, 1, Conv, [1024, 3, 2]]  # 7-P5/32
  - [-1, 3, C2f, [1024, true]]
  - [-1, 1, SPPF, [1024, 5]]  # 9

head:
  - [-1, 1, nn.Upsample, [None, 2, 'nearest']]
  - [[-1, 6], 1, Concat, [1]]  # cat backbone P4
  - [-1, 3, C2f, [512]]  # 12

  - [-1, 1, nn.Upsample, [None, 2, 'nearest']]
  - [[-1, 4], 1, Concat, [1]]  # cat backbone P3
  - [-1, 3, C2f, [256]]  # 15

  - [-1, 1, nn.Upsample, [None, 2, 'nearest']]
  - [[-1, 2], 1, Concat, [1]]  # cat backbone P2
  - [-1, 3, C2f, [128]]  # 18 (P2/4-xsmall)

  - [-1, 1, Conv, [128, 3, 2]]
  - [[-1, 15], 1, Concat, [1]]  # cat head P3
  - [-1, 3, C2f, [256]]  # 21 (P3/8-small)

  - [-1, 1, Conv, [256, 3, 2]]
  - [[-1, 12], 1, Concat, [1]]  # cat head P4
  - [-1, 3, C2f, [512]]  # 24 (P4/16-medium)

  - [-1, 1, Conv, [512, 3, 2]]
  - [[-1, 9], 1, Concat, [1]]  # cat head P5
  - [-1, 3, C2f, [1024]]  # 27 (P5/32-large)

  - [[18, 21, 24, 27], 1, Detect, [nc]]  # Detect(P2, P3, P4, P5)
";
            // 解析模型版本和大小
            var modelVersion = modelSize.StartsWith("v26") ? "yolo26"
                             : modelSize.StartsWith("v11") ? "yolo11"
                             : "yolov8";
            var sizeCode = modelSize.Length > 2 ? modelSize.Substring(modelSize.Length - 1) : modelSize;
            var fileName = $"{modelVersion}{sizeCode}-p2.yaml";
            File.WriteAllText(Path.Combine(targetPath, fileName), yamlContent);
            SendLog($"已生成 P2 模型配置: {fileName}");
        }

        private void GenerateTrainCommand(string targetPath, TrainingParams p)
        {
            // 解析模型版本和大小 (v8s -> yolov8s, v11m -> yolo11m, v26s -> yolo26s)
            var modelVersion = p.ModelSize.StartsWith("v26") ? "yolo26"
                             : p.ModelSize.StartsWith("v11") ? "yolo11"
                             : p.ModelSize.StartsWith("v8") ? "yolov8"
                             : "yolov8";
            var sizeCode = p.ModelSize.Length > 2 ? p.ModelSize.Substring(p.ModelSize.Length - 1) : "s";
            var modelName = p.EnableP2 ? $"{modelVersion}{sizeCode}-p2.yaml" : $"{modelVersion}{sizeCode}.pt";
            var dataYamlPath = Path.Combine(targetPath, "data.yaml").Replace("\\", "/");

            // 构建训练命令
            var device = string.IsNullOrEmpty(p.GpuIndex) ? "" : $"device={p.GpuIndex}";
            var command = $"yolo detect train model={modelName} data=\"{dataYamlPath}\" " +
                          $"epochs={p.Epochs} batch={p.BatchSize} imgsz={p.ImgSize} " +
                          $"patience={p.Patience} workers={p.Workers} {device}";

            var commandFilePath = Path.Combine(targetPath, "训练命令.txt");
            var content = $@"# YOLO 训练命令
# Generated by Insight
# ==================

{command}

# 参数说明：
# model: 模型配置 ({modelName})
# data: 数据集配置文件路径
# epochs: 训练轮数 ({p.Epochs})
# batch: 批次大小 ({p.BatchSize}，-1为自动)
# patience: 早停轮数 ({p.Patience})
# workers: 数据加载线程数 ({p.Workers})
# device: GPU设备索引 ({p.GpuIndex})
# imgsz: 图像尺寸 ({p.ImgSize})
{(p.EnableP2 ? "# P2: 已启用小目标增强层" : "")}

# 请确保已安装 ultralytics 包：
# pip install ultralytics

# ==================
# PT 转 ONNX 指令
# ==================
# 训练完成后，使用以下命令将 best.pt 转换为 ONNX 格式：

yolo export model=runs/detect/train/weights/best.pt format=onnx imgsz={p.ImgSize} simplify=True

# 参数说明：
# model: 训练好的权重文件路径（根据实际路径修改）
# format: 导出格式 (onnx)
# imgsz: 图像尺寸（与训练时保持一致）
# simplify: 简化 ONNX 模型
";

            File.WriteAllText(commandFilePath, content);
            SendLog($"已生成: {commandFilePath}");
            SendLog($"训练命令: {command}");
        }

        public Task HandleStartTrainingAsync(JsonElement data)
        {
            return _training.StartAsync(data);
        }

        public void HandleStopTraining()
        {
            _training.Stop();
        }

        #region 前端通信辅助方法

        private void SendToFrontend(object data)
        {
            _messenger.Send(data);
        }

        private void SendLog(string message, string type = "info")
        {
            _messenger.Log(message, type);
        }

        private void SendError(string message)
        {
            _messenger.Error(message);
        }

        private void SendComplete(string message)
        {
            _messenger.Complete(message);
        }

        #endregion

        #region Python 路径配置

        private string GetPythonConfigPath()
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(appDir, "python_config.json");
        }

        public void HandleSaveDefaultPythonPath(JsonElement data)
        {
            try
            {
                if (data.TryGetProperty("path", out var pathProp))
                {
                    var pythonPath = pathProp.GetString();
                    if (!string.IsNullOrWhiteSpace(pythonPath))
                    {
                        var configPath = GetPythonConfigPath();
                        var config = new { defaultPythonPath = pythonPath };
                        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(configPath, json);

                        SendToFrontend(new { action = "python_path_saved", success = true, path = pythonPath });
                        SendLog($"默认 Python 路径已保存: {pythonPath}", "success");
                    }
                    else
                    {
                        SendToFrontend(new { action = "python_path_saved", success = false, error = "路径为空" });
                    }
                }
            }
            catch (Exception ex)
            {
                SendToFrontend(new { action = "python_path_saved", success = false, error = ex.Message });
                SendError($"保存默认路径失败: {ex.Message}");
            }
        }

        public void HandleGetDefaultPythonPath()
        {
            try
            {
                var configPath = GetPythonConfigPath();
                string defaultPath = @"C:\conda_envs\yolo\python.exe"; // 代码中的初始默认值

                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("defaultPythonPath", out var pathProp))
                    {
                        var savedPath = pathProp.GetString();
                        if (!string.IsNullOrWhiteSpace(savedPath))
                        {
                            defaultPath = savedPath;
                        }
                    }
                }

                SendToFrontend(new { action = "default_python_path_loaded", path = defaultPath });
            }
            catch
            {
                SendToFrontend(new { action = "default_python_path_loaded", path = @"C:\conda_envs\yolo\python.exe" });
            }
        }

        #endregion

        public void HandleGetTrainingHistory()
        {
            _training.LoadHistory();
        }

        public void HandleGetModels(JsonElement data)
        {
            try
            {
                var path = data.GetProperty("path").GetString();
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                {
                    SendToFrontend(new { action = "models_loaded", models = new List<object>() });
                    return;
                }

                // Get all ONNX/PT files
                var modelFiles = Directory.GetFiles(path, "*.*")
                    .Where(f => f.EndsWith(".pt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();

                var modelsList = new List<object>();

                foreach (var f in modelFiles)
                {
                    // Check for metadata JSON
                    var jsonPath = Path.ChangeExtension(f.FullName, ".json");
                    object? metadataVal = null;

                    if (File.Exists(jsonPath))
                    {
                        try
                        {
                            var jsonStr = File.ReadAllText(jsonPath);
                            using var metaObj = JsonDocument.Parse(jsonStr);
                            metadataVal = metaObj.RootElement.Clone(); // Clone before disposing JsonDocument.
                                                               // Actually it's better to deserialize to object or dynamic, but JsonElement serializes fine in object usually.
                                                               // Let's manually parse common fields if needed, or just pass the whole thing.
                                                               // To avoid serialization issues with JsonElement in some older .NET, let's just pass the string or deserialized dict.
                                                               // But usually anonymous object is fine.
                                                               // Let's use deserialized anonymous object or JsonNode (if available).
                                                               // Simplest: just pass the Parsed RootElement.
                        }
                        catch { }
                    }

                    modelsList.Add(new
                    {
                        name = f.Name,
                        size = (f.Length / 1024.0 / 1024.0).ToString("F2") + " MB",
                        date = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                        path = f.FullName,
                        metadata = metadataVal
                    });
                }

                SendToFrontend(new { action = "models_loaded", models = modelsList });
            }
            catch (Exception ex)
            {
                SendError($"加载模型列表失败: {ex.Message}");
            }
        }

        public void HandleDeleteModel(JsonElement data)
        {
            try
            {
                var path = data.GetProperty("path").GetString();
                var fileName = data.GetProperty("fileName").GetString();
                var fullPath = ResolveModelFilePath(path, fileName, requireExisting: true);
                if (fullPath == null)
                {
                    SendError("模型文件路径无效");
                    return;
                }

                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    var metadataPath = Path.ChangeExtension(fullPath, ".json");
                    if (File.Exists(metadataPath)) File.Delete(metadataPath);
                    SendToFrontend(new { action = "model_operation_complete" });
                }
                else
                {
                    SendError("文件不存在");
                }
            }
            catch (Exception ex)
            {
                SendError($"删除失败: {ex.Message}");
            }
        }

        public void HandleRenameModel(JsonElement data)
        {
            try
            {
                var path = data.GetProperty("path").GetString();
                var oldName = data.GetProperty("oldName").GetString();
                var newName = data.GetProperty("newName").GetString();

                var oldPath = ResolveModelFilePath(path, oldName, requireExisting: true);
                var newPath = ResolveModelFilePath(path, newName, requireExisting: false);
                if (oldPath == null || newPath == null)
                {
                    SendError("模型文件名无效");
                    return;
                }

                if (!string.Equals(Path.GetExtension(oldPath), Path.GetExtension(newPath), StringComparison.OrdinalIgnoreCase))
                {
                    SendError("重命名不能改变模型文件类型");
                    return;
                }

                if (File.Exists(newPath))
                {
                    SendError("目标文件名已存在");
                    return;
                }

                if (File.Exists(oldPath))
                {
                    File.Move(oldPath, newPath);
                    var oldMetadataPath = Path.ChangeExtension(oldPath, ".json");
                    var newMetadataPath = Path.ChangeExtension(newPath, ".json");
                    if (File.Exists(oldMetadataPath) && !File.Exists(newMetadataPath))
                    {
                        File.Move(oldMetadataPath, newMetadataPath);
                    }
                    SendToFrontend(new { action = "model_operation_complete" });
                }
                else
                {
                    SendError("原文件不存在");
                }
            }
            catch (Exception ex)
            {
                SendError($"重命名失败: {ex.Message}");
            }
        }

        private static string? ResolveModelFilePath(string? directory, string? fileName, bool requireExisting)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            if (Path.GetFileName(fileName) != fileName)
            {
                return null;
            }

            var ext = Path.GetExtension(fileName);
            if (!ext.Equals(".pt", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".onnx", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var baseDir = Path.GetFullPath(directory);
            var fullPath = Path.GetFullPath(Path.Combine(baseDir, fileName));
            var baseWithSeparator = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(baseWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (requireExisting && !File.Exists(fullPath))
            {
                return null;
            }

            return fullPath;
        }

        public async Task HandleConvertModelAsync(JsonElement data)
        {
            // 手动转换调用
            try
            {
                var sourcePath = data.GetProperty("sourcePath").GetString(); // full path to .pt
                var targetDir = data.GetProperty("targetDir").GetString();

                if (string.IsNullOrWhiteSpace(sourcePath) ||
                    !Path.GetExtension(sourcePath).Equals(".pt", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(sourcePath))
                {
                    SendError("源 PT 文件不存在或类型无效");
                    return;
                }

                if (string.IsNullOrWhiteSpace(targetDir))
                {
                    SendError("目标目录不能为空");
                    return;
                }

                string pythonPath = data.TryGetProperty("pythonPath", out var pyEl) ? pyEl.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(pythonPath))
                {
                    pythonPath = @"C:\conda_envs\yolo\python.exe"; // Fallback
                }

                SendLog("开始手动转换...", "info");

                var workDir = Path.GetDirectoryName(sourcePath);
                if (string.IsNullOrWhiteSpace(workDir))
                {
                    SendError("源文件目录无效");
                    return;
                }

                await Task.Run(() =>
                {
                    try
                    {
                        RunExportCommand(pythonPath, workDir, sourcePath, targetDir, Path.GetFileNameWithoutExtension(sourcePath) + ".onnx");
                    }
                    catch (Exception ex)
                    {
                        SendError($"转换失败: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                SendError($"转换请求失败: {ex.Message}");
            }
        }

        private void RunExportCommand(string pythonPath, string workDir, string ptPath, string targetDir, string onnxName)
        {
            var exportArgs = $"-c \"from ultralytics.cfg import entrypoint; entrypoint()\" export model=\"{ptPath}\" format=onnx simplify=True";

            var p = new System.Diagnostics.Process();
            p.StartInfo.FileName = pythonPath;
            p.StartInfo.Arguments = exportArgs;
            p.StartInfo.WorkingDirectory = workDir;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            p.StartInfo.EnvironmentVariables["KMP_DUPLICATE_LIB_OK"] = "TRUE";

            p.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) SendLog(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) SendLog(e.Data); };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();

            if (p.ExitCode == 0)
            {
                var exportedOnnx = ptPath.Replace(".pt", ".onnx"); // Ultralytics saves in same folder
                if (File.Exists(exportedOnnx))
                {
                    if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                    var finalPath = Path.Combine(targetDir, onnxName);
                    File.Move(exportedOnnx, finalPath, true);
                    SendLog($"★ 转换成功! 保存至: {finalPath}", "success");
                    SendToFrontend(new { action = "model_operation_complete" });
                }
            }
        }

        public void HandleToolConvert(JsonElement data)
        {
            try
            {
                var sourcePath = data.GetProperty("sourcePath").GetString();
                var targetPath = data.GetProperty("targetPath").GetString();

                if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
                {
                    SendError("源文件夹不存在");
                    return;
                }
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    SendError("请指定目标保存路径");
                    return;
                }

                Task.Run(() =>
                {
                    try
                    {
                        SendLog("=== 开始转换 LabelMe -> YOLO ===", "info");

                        // 1. 自动扫描类别
                        SendLog("正在扫描类别...", "info");
                        var classes = ScanClasses(sourcePath);
                        if (classes.Count == 0)
                        {
                            SendError("未在源文件夹中找到任何类别 (JSON/txt)");
                            return;
                        }
                        SendLog($"识别到 {classes.Count} 个类别: {string.Join(", ", classes)}", "success");

                        // 2. 复用 GenerateDataset 的逻辑部分 (需要构造伪参数)
                        // 为了复用代码，我们这里手动调用 ProcessFiles 等核心逻辑，而不走 GenerateDataset 的完整流程(它包含 data.yaml 等生成)
                        // 用户只需要 labelme -> yolo 转换，结构: images/train, labels/train...?
                        // 用户说是 "LabelMe转YOLO"，通常意味着标准化目录结构 + TXT标签。
                        // 我们直接复用 GenerateDataset 的逻辑，因为它是最全的。

                        var dummyParams = new TrainingParams { ModelSize = "s", Epochs = 1 }; // 仅用于占位
                        GenerateDataset(new List<string> { sourcePath }, targetPath, classes, dummyParams);
                    }
                    catch (Exception ex)
                    {
                        SendError($"转换失败: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                SendError($"参数错误: {ex.Message}");
            }
        }
        public void HandleOpenOutput(JsonElement data)
        {
            try
            {
                if (data.TryGetProperty("path", out var pathElement))
                {
                    var p = pathElement.GetString();
                    if (string.IsNullOrWhiteSpace(p)) return;

                    if (!Directory.Exists(p))
                    {
                        // 尝试解析相对路径
                        if (!Path.IsPathRooted(p))
                        {
                            p = Path.GetFullPath(p);
                        }
                    }

                    if (Directory.Exists(p))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", p);
                        SendLog($"已打开文件夹: {p}");
                    }
                    else
                    {
                        SendError($"文件夹不存在: {p}");
                    }
                }
            }
            catch (Exception ex)
            {
                SendError($"无法打开文件夹: {ex.Message}");
            }
        }
        public void HandleGetSAMModels()
        {
            _samLabeling.HandleGetSAMModels();
        }

        public Task HandleLoadSAMModelAsync(JsonElement data)
        {
            return _samLabeling.HandleLoadSAMModelAsync(data);
        }

        public Task HandleEncodeImageAsync(JsonElement data)
        {
            return _samLabeling.HandleEncodeImageAsync(data);
        }

        public Task HandleSAMInferenceAsync(JsonElement data)
        {
            return _samLabeling.HandleSAMInferenceAsync(data);
        }

        public void HandleSaveAnnotations(JsonElement data)
        {
            _samLabeling.HandleSaveAnnotations(data);
        }

        public void HandleGetImagesInFolder(JsonElement data)
        {
            _samLabeling.HandleGetImagesInFolder(data);
        }

        public void HandleGetImageData(JsonElement data)
        {
            _samLabeling.HandleGetImageData(data);
        }

        public void HandleCreateOnnxProject(JsonElement data)
        {
            try
            {
                var name = data.GetProperty("name").GetString();
                var rootPath = data.GetProperty("rootPath").GetString();

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rootPath))
                {
                    SendError("ONNX 项目名称和根目录不能为空");
                    return;
                }

                if (!Directory.Exists(rootPath))
                {
                    SendError("指定的根目录不存在");
                    return;
                }

                var newProject = new OnnxProjectConfig
                {
                    Name = name,
                    RootPath = rootPath
                };

                OnnxProjectManager.AddProject(newProject);

                // Refresh list
                var projects = OnnxProjectManager.LoadProjects();
                SendToFrontend(new { action = "onnx_projects_loaded", projects });
                SendComplete("ONNX 项目创建成功！");
            }
            catch (Exception ex)
            {
                SendError($"创建 ONNX 项目失败: {ex.Message}");
            }
        }

        public void HandleDeleteOnnxProject(JsonElement data)
        {
            try
            {
                if (data.TryGetProperty("index", out var indexProp))
                {
                    OnnxProjectManager.DeleteProject(indexProp.GetInt32());
                    var projects = OnnxProjectManager.LoadProjects();
                    SendToFrontend(new { action = "onnx_projects_loaded", projects });
                }
            }
            catch (Exception ex)
            {
                SendError($"删除分离 ONNX 项目失败: {ex.Message}");
            }
        }

        public void HandleGetOnnxModels(JsonElement root)
        {
            try
            {
                if (!root.TryGetProperty("projectName", out var nameProp)) return;
                var projectName = nameProp.GetString();
                if (string.IsNullOrEmpty(projectName)) return;

                var project = OnnxProjectManager.LoadProjects().FirstOrDefault(p => p.Name == projectName);
                if (project == null)
                {
                    SendToFrontend(new { action = "onnx_models_loaded", models = new object[0] });
                    return;
                }

                string modelPath = project.RootPath;

                if (!Directory.Exists(modelPath))
                {
                    SendToFrontend(new { action = "onnx_models_loaded", models = new object[0] });
                    return;
                }

                // 获取所有 .onnx 文件
                var files = Directory.GetFiles(modelPath, "*.onnx", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Select(f => new
                    {
                        Name = f.Name,
                        SizeBytes = f.Length,
                        LastWriteTime = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        FullPath = f.FullName
                    })
                    .ToList();

                SendToFrontend(new { action = "onnx_models_loaded", models = files });
            }
            catch (Exception ex)
            {
                SendError($"Failed to load ONNX models: {ex.Message}");
            }
        }
    }
}
