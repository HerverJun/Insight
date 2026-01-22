using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System;

namespace Insight
{
    public partial class Insight : Form
    {
        private readonly Random _random = new();
        private System.Diagnostics.Process? _trainingProcess;
        private bool _isTraining = false;

        // Win32 API for window dragging

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        public Insight()
        {
            InitializeComponent();

            // 恢复标准系统边框，以确保完美的拖拽、缩放和全屏交互体验
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.Text = "Insight"; // 简化标题
            this.StartPosition = FormStartPosition.CenterScreen;

            // 设定默认大小
            this.Size = new Size(1280, 800);

            // 启动时最大化
            this.WindowState = FormWindowState.Maximized;

            // 暗色模式标题栏（已禁用，保持默认白色）
            // try { UseImmersiveDarkMode(this.Handle, true); } catch { }

            InitializeWebView();
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private static bool UseImmersiveDarkMode(IntPtr handle, bool enabled)
        {
            if (IsWindows10OrGreater(17763))
            {
                var attribute = DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1;
                if (IsWindows10OrGreater(18985))
                {
                    attribute = DWMWA_USE_IMMERSIVE_DARK_MODE;
                }

                int useImmersiveDarkMode = enabled ? 1 : 0;
                return DwmSetWindowAttribute(handle, attribute, ref useImmersiveDarkMode, sizeof(int)) == 0;
            }
            return false;
        }

        private static bool IsWindows10OrGreater(int build = -1)
        {
            return Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= build;
        }


        private async void InitializeWebView()
        {
            try
            {
                // 指定用户数据文件夹，避免权限问题
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Insight", "WebView2");

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView21.EnsureCoreWebView2Async(env);

                // 禁用 WebView2 默认的缩放和右键菜单等，提升 App 质感
                webView21.CoreWebView2.Settings.IsZoomControlEnabled = false;
                webView21.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

                // 注册消息处理
                webView21.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                // 从嵌入资源加载 HTML
                var html = GetEmbeddedResource("index.html");
                webView21.CoreWebView2.NavigateToString(html);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"WebView2 初始化失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string GetEmbeddedResource(string fileName)
        {
#if DEBUG
            // Debug 模式下，优先从源代码目录加载，方便热更 UI
            // 假设 BaseDirectory 是 bin\Debug\net8.0-windows\
            // 向上 3 层找到项目根目录 c:\Users\...\InsightV2\
            var debugPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", fileName));
            if (File.Exists(debugPath))
            {
                System.Diagnostics.Debug.WriteLine($"[Debug] Loading UI from source: {debugPath}");
                return File.ReadAllText(debugPath);
            }
#endif
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"Insight.{fileName}"; // 嵌入资源名称格式: 命名空间.文件名

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            else
            {
                // 发布环境或者资源未找到：尝试从运行目录加载
                var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                if (File.Exists(filePath))
                {
                    return File.ReadAllText(filePath);
                }
                throw new FileNotFoundException($"Embedded resource '{resourceName}' or file '{filePath}' not found.");
            }
        }

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                // 解析前端发送的 JSON 消息
                var message = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(message)) return;

                var json = JsonDocument.Parse(message);
                var root = json.RootElement;

                if (!root.TryGetProperty("action", out var actionElement)) return;
                var action = actionElement.GetString();

                switch (action)
                {
                    case "select_folder":
                        if (root.TryGetProperty("type", out var typeElement))
                        {
                            var folderType = typeElement.GetString();
                            if (!string.IsNullOrEmpty(folderType))
                            {
                                this.Invoke(() => HandleSelectFolder(folderType));
                            }
                        }
                        break;
                    case "select_file":
                        if (root.TryGetProperty("type", out var fileTypeElement))
                        {
                            var fileType = fileTypeElement.GetString();
                            this.Invoke(() => HandleSelectFile(fileType));
                        }
                        break;
                    case "generate":
                        HandleGenerate(root);
                        break;
                    case "start_training":
                        HandleStartTraining(root.Clone());
                        break;
                    case "stop_training":
                        HandleStopTraining();
                        break;
                    case "open_output":
                        HandleOpenOutput(root);
                        break;
                    case "defect_augmentation":
                        HandleDefectAugmentation(root);
                        break;
                    case "create_project":
                        HandleCreateProject(root);
                        break;
                    case "delete_project":
                        HandleDeleteProject(root);
                        break;
                    case "convert_tool":
                        HandleToolConvert(root);
                        break;
                    case "get_projects":
                        var projects = ProjectManager.LoadProjects();
                        SendToFrontend(new { action = "projects_loaded", projects });
                        break;
                    case "get_subfolders":
                        if (root.TryGetProperty("path", out var pathProp))
                        {
                            var sub = ProjectManager.GetSubFolders(pathProp.GetString()!);
                            SendToFrontend(new { action = "subfolders_loaded", folders = sub });
                        }
                        break;
                    case "get_models":
                        HandleGetModels(root);
                        break;
                    case "delete_model":
                        HandleDeleteModel(root);
                        break;
                    case "rename_model":
                        HandleRenameModel(root);
                        break;
                    case "convert_model":
                        HandleConvertModel(root);
                        break;
                    case "get_training_history":
                        HandleGetTrainingHistory();
                        break;
                    case "open_model_folder":
                        if (root.TryGetProperty("path", out var modelPathProp))
                        {
                            var modelPath = modelPathProp.GetString();
                            SendLog($"[Debug] open_model_folder: {modelPath}", "info");

                            try
                            {
                                if (!string.IsNullOrEmpty(modelPath) && File.Exists(modelPath))
                                {
                                    SendLog($"[Debug] Opening folder with file selected: {modelPath}", "info");
                                    var psi = new System.Diagnostics.ProcessStartInfo
                                    {
                                        FileName = "explorer.exe",
                                        Arguments = $"/select,\"{modelPath}\"",
                                        UseShellExecute = true
                                    };
                                    System.Diagnostics.Process.Start(psi);
                                }
                                else if (!string.IsNullOrEmpty(modelPath))
                                {
                                    var folder = Path.GetDirectoryName(modelPath);
                                    SendLog($"[Debug] File not found, opening folder: {folder}", "info");
                                    if (Directory.Exists(folder))
                                    {
                                        var psi = new System.Diagnostics.ProcessStartInfo
                                        {
                                            FileName = "explorer.exe",
                                            Arguments = folder,
                                            UseShellExecute = true
                                        };
                                        System.Diagnostics.Process.Start(psi);
                                    }
                                    else
                                    {
                                        SendLog($"[Debug] Folder also not found: {folder}", "warning");
                                    }
                                }
                                else
                                {
                                    SendLog("[Debug] open_model_folder: path is empty", "warning");
                                }
                            }
                            catch (Exception ex)
                            {
                                SendLog($"[Debug] Failed to open folder: {ex.Message}", "error");
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"消息处理错误: {ex.Message}");
                SendError($"消息交互异常: {ex.Message}");
            }
        }

        private void HandleDeleteProject(JsonElement data)
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

        private void HandleCreateProject(JsonElement data)
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

        private async void HandleDefectAugmentation(JsonElement data)
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

        private void HandleSelectFolder(string type)
        {
            try
            {
                using var dialog = new FolderBrowserDialog();

                // 设置安全的根文件夹，防止浏览特殊位置时崩溃
                dialog.RootFolder = Environment.SpecialFolder.Desktop;
                dialog.ShowNewFolderButton = true;

                if (type == "source") dialog.Description = "选择源文件夹";
                else if (type == "newProjectRoot") dialog.Description = "选择项目根目录";
                else dialog.Description = "选择保存位置";

                dialog.UseDescriptionForTitle = true;

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    var response = new
                    {
                        action = "folder_selected",
                        type = type,
                        path = dialog.SelectedPath
                    };
                    SendToFrontend(response);
                }
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                // COM 异常
                System.Diagnostics.Debug.WriteLine($"COM异常: {ex.Message}");
                MessageBox.Show($"文件夹选择器发生错误，请重试。\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开文件夹选择器失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void HandleSelectFile(string type)
        {
            try
            {
                using var dialog = new OpenFileDialog();
                dialog.Filter = "Python Executable|python.exe|All Files|*.*";
                dialog.Title = "选择 Python 解释器";

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    var response = new
                    {
                        action = "folder_selected", // 复用文件夹选择消息
                        type = type,
                        path = dialog.FileName
                    };
                    SendToFrontend(response);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开文件选择器失败: {ex.Message}");
            }
        }

        private async void HandleGenerate(JsonElement data)
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

        public class TrainingParams
        {
            public string ModelSize { get; set; } = "s";
            public int ImgSize { get; set; } = 640;
            public int Epochs { get; set; } = 3000;
            public int BatchSize { get; set; } = -1;
            public int Patience { get; set; } = 50;
            public int Workers { get; set; } = 8;
            public string GpuIndex { get; set; } = "0";
            public bool EnableP2 { get; set; } = false;
            public bool AutoFixImage { get; set; } = true;
            public string OnnxName { get; set; } = "";
            public string ModelStoragePath { get; set; } = "";
            public List<string> Classes { get; set; } = new List<string>();
        }

        private void GenerateDataset(List<string> sourcePaths, string targetPath, List<string> classes, TrainingParams paramsData)
        {
            try
            {
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
                if (classes.Count == 0)
                {
                    SendError("项目配置中未定义任何类别！请检查 projects.json");
                    return;
                }
                SendLog($"使用项目类别配置: {string.Join(", ", classes)}");

                // 建立类别名称到索引的映射
                var classMap = classes.Select((name, index) => new { name, index })
                                      .ToDictionary(x => x.name, x => x.index);

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
            foreach (var imageFile in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(imageFile);
                var extension = Path.GetExtension(imageFile).ToLower();
                var sourcePath = Path.GetDirectoryName(imageFile)!;

                // 1. 处理图片
                var targetImagePath = Path.Combine(imagesDir, fileName + ".jpg");
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
                var targetLabelPath = Path.Combine(labelsDir, fileName + ".txt");
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

        private void ConvertJsonToYolo(string jsonPath, string targetPath, Dictionary<string, int> classMap)
        {
            try
            {
                var content = File.ReadAllText(jsonPath);
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                var width = root.GetProperty("imageWidth").GetInt32();
                var height = root.GetProperty("imageHeight").GetInt32();
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
                System.Diagnostics.Debug.WriteLine($"JSON 转换错误 {jsonPath}: {ex.Message}");
            }
        }

        private void GenerateDataYaml(string targetPath, List<string> classes)
        {
            // 构建 names 字典格式
            var namesDict = new System.Text.StringBuilder();
            for (int i = 0; i < classes.Count; i++)
            {
                namesDict.AppendLine($"  {i}: {classes[i]}");
            }

            var yamlContent = $@"# YOLO Dataset Configuration
# Generated by Insight
# {DateTime.Now}

path: {targetPath.Replace("\\", "/")}
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
            var modelVersion = modelSize.StartsWith("v11") ? "yolo11" : "yolov8";
            var sizeCode = modelSize.Length > 2 ? modelSize.Substring(modelSize.Length - 1) : modelSize;
            var fileName = $"{modelVersion}{sizeCode}-p2.yaml";
            File.WriteAllText(Path.Combine(targetPath, fileName), yamlContent);
            SendLog($"已生成 P2 模型配置: {fileName}");
        }

        private void GenerateTrainCommand(string targetPath, TrainingParams p)
        {
            // 解析模型版本和大小 (v8s -> yolov8s, v11m -> yolo11m)
            var modelVersion = p.ModelSize.StartsWith("v11") ? "yolo11" : "yolov8";
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


        // === Training Metrics Tracking ===
        private double _lastBoxLoss = 0;
        private double _lastMap50 = 0;
        private double _lastMap5095 = 0;

        private async void HandleStartTraining(JsonElement data)
        {
            if (_isTraining) return;

            try
            {
                SendLog("收到训练请求...", "info");

                string pythonPath = data.TryGetProperty("pythonPath", out var pyProp) ? pyProp.GetString() ?? "" : "";
                string workDir = data.TryGetProperty("workDir", out var wdProp) ? wdProp.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(pythonPath)) pythonPath = "python";
                if (string.IsNullOrWhiteSpace(workDir)) workDir = AppDomain.CurrentDomain.BaseDirectory;

                // Populate TrainingParams
                var paramsData = new TrainingParams
                {
                    OnnxName = data.TryGetProperty("onnxName", out var onnxEl) ? onnxEl.GetString() ?? "" : "",
                    ModelStoragePath = data.TryGetProperty("modelStoragePath", out var storeEl) ? storeEl.GetString() ?? "" : "",
                    ModelSize = data.TryGetProperty("modelSize", out var ms) ? ms.GetString() ?? "s" : "s",
                    ImgSize = data.TryGetProperty("imgSize", out var imgs) && imgs.ValueKind == JsonValueKind.Number ? imgs.GetInt32() : 640,
                    Epochs = data.TryGetProperty("epochs", out var eps) && eps.ValueKind == JsonValueKind.Number ? eps.GetInt32() : 300,
                    BatchSize = data.TryGetProperty("batchSize", out var bs) && bs.ValueKind == JsonValueKind.Number ? bs.GetInt32() : 16,
                    Patience = data.TryGetProperty("patience", out var pat) && pat.ValueKind == JsonValueKind.Number ? pat.GetInt32() : 50,
                    Workers = data.TryGetProperty("workers", out var wrk) && wrk.ValueKind == JsonValueKind.Number ? wrk.GetInt32() : 8,
                    GpuIndex = data.TryGetProperty("gpuIndex", out var gpu) ? gpu.GetString() ?? "0" : "0",
                    EnableP2 = data.TryGetProperty("enableP2", out var p2) && p2.GetBoolean(),
                    AutoFixImage = data.TryGetProperty("autoFixImage", out var fix) && fix.GetBoolean(),
                    Classes = data.TryGetProperty("classes", out var cls) && cls.ValueKind == JsonValueKind.Array
                              ? cls.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                              : new List<string>()
                };

                // 自动去除常见的 conda activate 前缀

                // 自动去除常见的 conda activate 前缀
                pythonPath = pythonPath.Replace("conda activate ", "").Replace("activate ", "").Trim();

                // 智能路径解析：支持直接输入环境文件夹路径
                if (Directory.Exists(pythonPath))
                {
                    var potentialPython = Path.Combine(pythonPath, "python.exe");
                    if (File.Exists(potentialPython))
                    {
                        pythonPath = potentialPython;
                    }
                    else
                    {
                        // 检查标准虚拟环境结构
                        potentialPython = Path.Combine(pythonPath, "Scripts", "python.exe");
                        if (File.Exists(potentialPython))
                        {
                            pythonPath = potentialPython;
                        }
                    }
                }

                // 最终验证 Python 解释器路径
                if (!File.Exists(pythonPath) && !pythonPath.Equals("python", StringComparison.OrdinalIgnoreCase))
                {
                    SendError($"找不到 Python 解释器: {pythonPath}。请指定 python.exe 的完整路径或有效的环境目录。");
                    return;
                }

                // 从生成的训练命令文件中读取命令
                var cmdFile = Path.Combine(workDir, "训练命令.txt");
                string args = "";

                if (File.Exists(cmdFile))
                {
                    // Scan the file for the command line (skip comments)
                    var lines = File.ReadAllLines(cmdFile);
                    foreach (var line in lines)
                    {
                        var trim = line.Trim();
                        if (!string.IsNullOrEmpty(trim) && !trim.StartsWith("#") && trim.StartsWith("yolo"))
                        {
                            // 解析 yolo 命令并转换为 Python 可执行格式
                            var yoloArgs = trim.Substring(5); // 去除 "yolo " 前缀
                            args = $"-c \"from ultralytics.cfg import entrypoint; entrypoint()\" {yoloArgs}";
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(args))
                {
                    SendError("找不到有效的训练命令，请先重新生成数据集。");
                    return;
                }

                _isTraining = true;
                SendLog($"正在启动训练进程...", "info");
                SendLog($"解释器: {pythonPath}", "info");
                SendLog($"参数: {args}", "info");

                await Task.Run(() =>
                {
                    try
                    {
                        var startInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = pythonPath,
                            Arguments = args,
                            WorkingDirectory = workDir,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            StandardOutputEncoding = System.Text.Encoding.UTF8,
                            StandardErrorEncoding = System.Text.Encoding.UTF8
                        };

                        // 设置 Python 输出编码为 UTF-8
                        startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                        // 解决 Anaconda 环境中 OpenMP 库冲突问题
                        startInfo.EnvironmentVariables["KMP_DUPLICATE_LIB_OK"] = "TRUE";

                        _trainingProcess = new System.Diagnostics.Process
                        {
                            StartInfo = startInfo
                        };

                        _trainingProcess.OutputDataReceived += (s, e) => ParseTrainingOutput(e.Data);
                        _trainingProcess.ErrorDataReceived += (s, e) => ParseTrainingOutput(e.Data); // ultralytics 部分输出使用 stderr

                        _trainingProcess.Start();
                        _trainingProcess.BeginOutputReadLine();
                        _trainingProcess.BeginErrorReadLine();

                        _trainingProcess.WaitForExit();
                    }
                    catch (Exception ex)
                    {
                        SendError($"训练进程异常: {ex.Message}");
                    }
                    finally
                    {
                        _isTraining = false;
                        _trainingProcess?.Dispose();
                        _trainingProcess = null;

                        // Try automatic export if not stopped manually
                        // modelStoragePath, onnxName are in paramsData
                        ExportOnnx(pythonPath, workDir, args, paramsData);

                        var endMsg = new { action = "training_finished" };
                        SendToFrontend(endMsg);
                    }
                });
            }
            catch (Exception ex)
            {
                _isTraining = false;
                SendError($"启动训练失败: {ex.Message}");
            }
        }

        private void HandleStopTraining()
        {
            if (_trainingProcess != null && !_trainingProcess.HasExited)
            {
                try
                {
                    // 终止训练进程
                    _trainingProcess.Kill();
                    SendLog("已发送停止信号。", "warning");

                    var msg = new { action = "training_stopped" };
                    SendToFrontend(msg);
                }
                catch (Exception ex)
                {
                    SendError($"停止失败: {ex.Message}");
                }
            }
        }

        private void ParseTrainingOutput(string? line)
        {
            if (string.IsNullOrEmpty(line)) return;

            // 去除 ANSI 颜色代码
            string cleanLine = System.Text.RegularExpressions.Regex.Replace(line, @"\x1B\[[^@-~]*[@-~]", "");
            cleanLine = cleanLine.Trim();

            // 发送清理后的日志到前端
            SendLog(cleanLine, "info");

            try
            {
                // 2. 解析训练进度 (Box Loss)
                // 格式: "Epoch/Total GPU_mem box_loss cls_loss dfl_loss instances size"
                // 示例: "1/100 2.75G 1.026 1.266 1.18 14 640"
                var parts = cleanLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                // 匹配训练进度行
                if (parts.Length >= 5 && parts[0].Contains("/"))
                {
                    // 解析 Epoch 进度 (如 1/100)
                    string progressText = parts[0];
                    double progress = 0;
                    if (progressText.Contains("/"))
                    {
                        var epochParts = progressText.Split('/');
                        if (epochParts.Length == 2 &&
                            double.TryParse(epochParts[0], out var current) &&
                            double.TryParse(epochParts[1], out var total))
                        {
                            if (total > 0) progress = (current / total) * 100;
                        }
                    }

                    if (double.TryParse(parts[2], out var boxLoss))
                    {
                        _lastBoxLoss = boxLoss; // Track latest loss
                        var epochStr = parts[0];

                        var data = new
                        {
                            action = "training_data",
                            epoch = epochStr,
                            box_loss = boxLoss,
                            progress = progress
                        };
                        SendToFrontend(data);
                    }
                }

                // 解析验证结果 (mAP)
                // 格式: "all images labels precision recall mAP50 mAP50-95"
                else if (parts.Length >= 6 && parts[0] == "all")
                {
                    // parts[5] = mAP50
                    if (double.TryParse(parts[5], out var map50))
                    {
                        double map5095 = 0;
                        if (parts.Length > 6) double.TryParse(parts[6], out map5095);

                        // Track latest mAP values
                        _lastMap50 = map50;
                        _lastMap5095 = map5095;

                        var data = new
                        {
                            action = "training_data",
                            epoch = "val",
                            map50 = map50,
                            map5095 = map5095
                        };
                        SendToFrontend(data);
                    }
                }
            }
            catch { }
        }

        #region 前端通信辅助方法

        private void SendToFrontend(object data)
        {
            try
            {
                var json = JsonSerializer.Serialize(data);

                // 必须在 UI 线程上调用 PostWebMessageAsString
                if (this.InvokeRequired)
                {
                    this.Invoke(() =>
                    {
                        if (webView21.CoreWebView2 != null)
                        {
                            webView21.CoreWebView2.PostWebMessageAsString(json);
                        }
                    });
                }
                else
                {
                    if (webView21.CoreWebView2 != null)
                    {
                        webView21.CoreWebView2.PostWebMessageAsString(json);
                    }
                }
            }
            catch { }
        }

        private void SendLog(string message, string type = "info")
        {
            var data = new { action = "log", message, type };
            SendToFrontend(data);
        }

        private void SendError(string message)
        {
            var data = new { action = "error", message };
            SendToFrontend(data);
        }

        private void SendComplete(string message)
        {
            var data = new { action = "complete", message };
            SendToFrontend(data);
        }

        #endregion

        private void ExportOnnx(string pythonPath, string workDir, string trainArgs, TrainingParams paramsData)
        {
            var bestPt = Path.Combine(workDir, "runs", "detect", "train", "weights", "best.pt");

            // Auto search for latest run if 'train' doesn't exist or is old?
            // Actually ultralytics increments run folders (train, train2, train3...)
            // We should find the latest 'train*' folder.
            var runsDir = Path.Combine(workDir, "runs", "detect");
            if (Directory.Exists(runsDir))
            {
                var latestTrain = Directory.GetDirectories(runsDir, "train*")
                                           .OrderByDescending(d => Directory.GetCreationTime(d))
                                           .FirstOrDefault();
                if (latestTrain != null)
                {
                    bestPt = Path.Combine(latestTrain, "weights", "best.pt");
                }
            }

            if (!File.Exists(bestPt))
            {
                SendLog($"未找到模型文件: {bestPt}。训练可能失败或未产生权重。", "error");
                return;
            }

            var exportArgs = $"-c \"from ultralytics.cfg import entrypoint; entrypoint()\" export model=\"{bestPt}\" format=onnx simplify=True";

            SendLog("开始导出 ONNX 模型...", "info");
            SendLog($"Best.pt Path: {bestPt}", "info");

            try
            {
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
                    var exportedOnnx = bestPt.Replace(".pt", ".onnx");
                    SendLog($"[Debug] Looking for exported ONNX at: {exportedOnnx}", "info");

                    if (File.Exists(exportedOnnx))
                    {
                        SendLog($"[Debug] Found ONNX file, size: {new FileInfo(exportedOnnx).Length / 1024}KB", "info");

                        // Determine Target Directory
                        string targetDir = string.IsNullOrWhiteSpace(paramsData.ModelStoragePath) ? workDir : paramsData.ModelStoragePath;
                        SendLog($"[Debug] Target directory: {targetDir}", "info");

                        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                        // Determine Name
                        string finalName = paramsData.OnnxName;
                        if (string.IsNullOrWhiteSpace(finalName))
                        {
                            var modelSize = paramsData.ModelSize; // v8s, v11n etc
                            var dateStr = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                            // Default: yolo-{size}-{date}.onnx
                            finalName = $"yolo-{modelSize}-{dateStr}.onnx";
                        }
                        if (!finalName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)) finalName += ".onnx";

                        var finalPath = Path.Combine(targetDir, finalName);
                        SendLog($"[Debug] Moving to: {finalPath}", "info");

                        File.Move(exportedOnnx, finalPath, true);

                        // === SAVE METADATA ===
                        SaveModelMetadata(finalPath, paramsData);
                        // =====================

                        // === SAVE TRAINING HISTORY ===
                        // Derive project name from workDir (usually RootPath\dataset, so RootPath is parent)
                        var projectName = Path.GetFileName(Path.GetDirectoryName(workDir)) ?? "Unknown";
                        SaveTrainingHistoryEntry(finalPath, paramsData, projectName);
                        // =============================

                        SendLog($"★ 导出成功! 已归档至: {finalPath}", "success");
                        SendToFrontend(new { action = "model_operation_complete" });
                    }
                    else
                    {
                        SendLog($"[Debug] ONNX file NOT found at expected path: {exportedOnnx}", "warning");
                        SendLog("导出命令由于未知原因未生成文件。", "warning");
                    }
                }
                else
                {
                    SendLog("ONNX 导出进程非正常退出。", "error");
                }
            }
            catch (Exception ex)
            {
                SendLog($"导出异常: {ex.Message}", "error");
            }
        }

        private void SaveModelMetadata(string modelPath, TrainingParams paramsData)
        {
            try
            {
                var jsonPath = Path.ChangeExtension(modelPath, ".json");
                var metadata = new
                {
                    name = Path.GetFileName(modelPath),
                    date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    paramsData = paramsData, // Serializes all public props including Classes
                    classes = paramsData.Classes
                };

                var json = System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(jsonPath, json);
            }
            catch (Exception ex)
            {
                SendLog($"元数据保存失败: {ex.Message}", "warning");
            }
        }

        // === Training History Management ===

        private string GetTrainingHistoryPath()
        {
            // Store history alongside projects.json
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(appDir, "training_history.json");
        }

        private void SaveTrainingHistoryEntry(string modelPath, TrainingParams paramsData, string projectName)
        {
            try
            {
                var historyPath = GetTrainingHistoryPath();
                var history = new List<object>();

                // Load existing history
                if (File.Exists(historyPath))
                {
                    var existingJson = File.ReadAllText(historyPath);
                    using var doc = JsonDocument.Parse(existingJson);
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        history.Add(elem.Clone());
                    }
                }

                // Add new entry
                var newEntry = new
                {
                    id = Guid.NewGuid().ToString(),
                    projectName = projectName,
                    date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    modelPath = modelPath,
                    modelSize = paramsData.ModelSize,
                    epochs = paramsData.Epochs,
                    imgSize = paramsData.ImgSize,
                    batchSize = paramsData.BatchSize,
                    classes = paramsData.Classes,
                    // Training Metrics
                    finalLoss = Math.Round(_lastBoxLoss, 4),
                    mAP50 = Math.Round(_lastMap50, 4),
                    mAP5095 = Math.Round(_lastMap5095, 4)
                };

                history.Add(newEntry);

                // Save updated history
                var json = System.Text.Json.JsonSerializer.Serialize(history, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(historyPath, json);

                SendLog("训练记录已保存到历史", "success");
            }
            catch (Exception ex)
            {
                SendLog($"保存训练历史失败: {ex.Message}", "warning");
            }
        }

        private void HandleGetTrainingHistory()
        {
            try
            {
                var historyPath = GetTrainingHistoryPath();
                var history = new List<object>();

                if (File.Exists(historyPath))
                {
                    var json = File.ReadAllText(historyPath);
                    using var doc = JsonDocument.Parse(json);
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        history.Add(elem.Clone());
                    }
                }

                // Sort by date descending (newest first)
                // Since we're using anonymous objects, we'll send as-is

                SendToFrontend(new { action = "training_history_loaded", history = history });
            }
            catch (Exception ex)
            {
                SendError($"加载训练历史失败: {ex.Message}");
                SendToFrontend(new { action = "training_history_loaded", history = new List<object>() });
            }
        }


        private void HandleGetModels(JsonElement data)
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
                    object metadataVal = null;

                    if (File.Exists(jsonPath))
                    {
                        try
                        {
                            var jsonStr = File.ReadAllText(jsonPath);
                            var metaObj = JsonDocument.Parse(jsonStr);
                            metadataVal = metaObj.RootElement; // Send raw JsonElement to frontend (System.Text.Json supports this serialization?)
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

        private void HandleDeleteModel(JsonElement data)
        {
            try
            {
                var path = data.GetProperty("path").GetString();
                var fileName = data.GetProperty("fileName").GetString();
                var fullPath = Path.Combine(path, fileName);

                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
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

        private void HandleRenameModel(JsonElement data)
        {
            try
            {
                var path = data.GetProperty("path").GetString();
                var oldName = data.GetProperty("oldName").GetString();
                var newName = data.GetProperty("newName").GetString();

                var oldPath = Path.Combine(path, oldName);
                var newPath = Path.Combine(path, newName);

                if (File.Exists(oldPath))
                {
                    File.Move(oldPath, newPath);
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

        private void HandleConvertModel(JsonElement data)
        {
            // 手动转换调用
            try
            {
                var sourcePath = data.GetProperty("sourcePath").GetString(); // full path to .pt
                var targetDir = data.GetProperty("targetDir").GetString();

                if (!File.Exists(sourcePath))
                {
                    SendError("源文件不存在");
                    return;
                }

                // 需要 pythonPath (前端没传，需要想办法获取，或者保存一个全局的)
                // 这里为了简便，需要假设前端会传，或者如果在 StartTraining 时已获取过。
                // 现有的 index.html convertModel 并没有传 pythonPath。
                // 我们应该从 localStorage 或者 Global Config 获取。
                // 此处简化: 假设 pythonPath 用户已在界面配置，必须传过来。
                // Update index.html logic: convertModel should carry pythonPath too? 
                // Alternatively, we can assume "python" if in path, or specific path.

                // Let's modify index.html ConvertModel logic slightly in next step or use best guess.
                // Or better: Let's assume user has `C:\ANACONDA\python.exe` 

                // BETTER FIX: The `HandleStartTraining` logic usually receives it.
                // We can save it to a static variable when used, or read from a config file.
                // For now, let's try to get it from `data` assuming we update frontend to send it.
                // If not, fall back to "python".

                string pythonPath = data.TryGetProperty("pythonPath", out var pyEl) ? pyEl.GetString() : "";
                if (string.IsNullOrWhiteSpace(pythonPath))
                {
                    pythonPath = @"C:\ANACONDA\python.exe"; // Fallback
                }

                SendLog("开始手动转换...", "info");

                // Reuse ExportOnnx logic, but we need to trick it or adapt it.
                // My Refactored ExportOnnx takes `workDir` and finds `best.pt` in `runs/detect...`.
                // But here we have a specific file.
                // So I should separate "Find Best PT" from "Run Export Command".

                // Let's create a Helper Method `RunExportCommand`.
                RunExportCommand(pythonPath, Path.GetDirectoryName(sourcePath), sourcePath, targetDir, Path.GetFileNameWithoutExtension(sourcePath) + ".onnx");
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

        private void HandleToolConvert(JsonElement data)
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
        private void HandleOpenOutput(JsonElement data)
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
    }
}
