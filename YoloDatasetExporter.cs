using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Drawing;
using System.Threading.Tasks;

namespace Insight
{
    public class YoloDatasetExporter
    {
        private static readonly Random _random = new Random();

        /// <summary>
        /// 规范化 YOLO 版本名称，使其匹配 Ultralytics 官方权重文件命名：
        /// yolov8/v9/v10 -> 保持不变 (权重文件带 v，如 yolov8s.pt)
        /// yolov11/yolo11 -> yolo11 (权重文件不带 v，如 yolo11s.pt)
        /// yolo26 -> yolo26 (权重文件不带 v，如 yolo26s.pt)
        /// </summary>
        private static string NormalizeYoloVersion(string yoloVersion, Action<string, string>? onLog = null)
        {
            var raw = (yoloVersion ?? string.Empty).Trim().ToLower();
            if (string.IsNullOrWhiteSpace(raw)) return "yolov8";

            // Ultralytics 官方命名：YOLO11 和 YOLO26 的权重文件不带 'v'
            // 前端可能传 'yolov11'，需要转为 'yolo11'
            if (raw == "yolov11")
            {
                raw = "yolo11";
            }

            if (!raw.Equals(yoloVersion?.Trim().ToLower()))
            {
                onLog?.Invoke($"YOLO 版本已规范化: {yoloVersion} -> {raw}", "info");
            }

            return raw;
        }

        public static async Task ExportToZipAsync(
            List<string> sourcePaths,
            string targetZipPath,
            List<string> classes,
            string yoloVersion,
            string modelSize,
            int epochs,
            int batchSize,
            int imgSize,
            int patience,
            int workers,
            string gpuIndex,
            double splitRatio,
            Action<string, string>? onLog,
            Action<int>? onProgress)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "Insight_YoloExport_" + Guid.NewGuid().ToString("N"));

            try
            {
                var normalizedYoloVersion = NormalizeYoloVersion(yoloVersion, onLog);

                onLog?.Invoke($"开始导出 YOLO 数据集至 ZIP...", "info");
                onLog?.Invoke($"目标模型版本: {normalizedYoloVersion}", "info");
                onLog?.Invoke($"训练/验证比例: {splitRatio:P0} / {1 - splitRatio:P0}", "info");
                onLog?.Invoke($"训练参数: epochs={epochs}, batch={batchSize}, imgsz={imgSize}, patience={patience}, workers={workers}, device={gpuIndex}", "info");

                // 1. 扫描源文件夹
                onProgress?.Invoke(5);
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
                    throw new Exception("选定的文件夹中未找到任何图片文件");
                }

                onLog?.Invoke($"共找到 {imageFiles.Count} 张图片", "info");

                if (classes.Count == 0)
                {
                    throw new Exception("项目配置中未定义任何类别！");
                }

                // 建立类别映射
                var classMap = classes.Select((name, index) => new { name, index })
                                      .ToDictionary(x => x.name, x => x.index);

                // 2. 创建临时目录结构
                onProgress?.Invoke(10);
                var trainImagesDir = Path.Combine(tempDir, "images", "train");
                var valImagesDir = Path.Combine(tempDir, "images", "val");
                var trainLabelsDir = Path.Combine(tempDir, "labels", "train");
                var valLabelsDir = Path.Combine(tempDir, "labels", "val");

                Directory.CreateDirectory(trainImagesDir);
                Directory.CreateDirectory(valImagesDir);
                Directory.CreateDirectory(trainLabelsDir);
                Directory.CreateDirectory(valLabelsDir);

                // 3. 随机打乱并切分
                var shuffled = imageFiles.OrderBy(_ => _random.Next()).ToList();
                var splitIndex = (int)(shuffled.Count * splitRatio);
                var trainFiles = shuffled.Take(splitIndex).ToList();
                var valFiles = shuffled.Skip(splitIndex).ToList();

                // 4. 处理文件
                onLog?.Invoke($"正在处理训练集 ({trainFiles.Count} 张)...", "info");
                await ProcessFilesAsync(trainFiles, trainImagesDir, trainLabelsDir, classMap, 15, 50, onProgress);

                onLog?.Invoke($"正在处理验证集 ({valFiles.Count} 张)...", "info");
                await ProcessFilesAsync(valFiles, valImagesDir, valLabelsDir, classMap, 50, 85, onProgress);

                // 5. 生成 data.yaml
                onProgress?.Invoke(85);
                GenerateDataYaml(tempDir, classes, normalizedYoloVersion);
                GenerateTrainingConfig(tempDir, normalizedYoloVersion, modelSize, epochs, batchSize, imgSize, patience, workers, gpuIndex);
                GenerateColabNotebook(tempDir);

                // 6. 打包 ZIP
                onLog?.Invoke("正在压缩文件...", "info");
                if (File.Exists(targetZipPath))
                {
                    File.Delete(targetZipPath);
                }

                // 确保目标目录存在
                var targetDir = Path.GetDirectoryName(targetZipPath);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                ZipFile.CreateFromDirectory(tempDir, targetZipPath, CompressionLevel.Fastest, false);
                onProgress?.Invoke(100);

                onLog?.Invoke($"✓ ZIP 导出成功！路径: {targetZipPath}", "success");
            }
            finally
            {
                // 清理临时目录
                if (Directory.Exists(tempDir))
                {
                    try
                    {
                        Directory.Delete(tempDir, true);
                    }
                    catch { /* 忽略清理错误 */ }
                }
            }
        }

        private static async Task ProcessFilesAsync(
            List<string> files,
            string imagesDir,
            string labelsDir,
            Dictionary<string, int> classMap,
            int startProgress,
            int endProgress,
            Action<int>? onProgress)
        {
            int total = files.Count;
            if (total == 0) return;

            await Task.Run(() =>
            {
                var usedOutputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Count; i++)
                {
                    var imageFile = files[i];
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

                    // 2. 处理标签
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
                        // 负样本
                        File.WriteAllText(targetLabelPath, string.Empty);
                    }

                    // 更新进度
                    if (i % Math.Max(1, total / 20) == 0)
                    {
                        int currentProgress = startProgress + (int)((double)i / total * (endProgress - startProgress));
                        onProgress?.Invoke(currentProgress);
                    }
                }
            });
        }

        private static string GetUniqueOutputBaseName(string originalBaseName, string sourcePath, HashSet<string> usedOutputNames)
        {
            if (usedOutputNames.Add(originalBaseName))
            {
                return originalBaseName;
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath))).Substring(0, 8).ToLowerInvariant();
            var candidate = $"{originalBaseName}_{hash}";
            var suffix = 2;
            while (!usedOutputNames.Add(candidate))
            {
                candidate = $"{originalBaseName}_{hash}_{suffix++}";
            }

            return candidate;
        }

        private static void ConvertJsonToYolo(string jsonPath, string targetPath, Dictionary<string, int> classMap)
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
            catch
            {
                /* 忽略个别解析错误 */
            }
        }

        private static void GenerateDataYaml(string targetPath, List<string> classes, string yoloVersion)
        {
            var namesDict = new System.Text.StringBuilder();
            for (int i = 0; i < classes.Count; i++)
            {
                namesDict.AppendLine($"  {i}: {classes[i]}");
            }

            // 使用相对 split 路径，不写 path 字段，避免在不同运行目录下解析错位
            var yamlContent = $@"# YOLO Dataset Configuration
# Generated by Insight V4 for Google Colab
# Recommended Model: {yoloVersion}n.pt
# Date: {DateTime.Now}

train: images/train
val: images/val

# Classes
nc: {classes.Count}
names:
{namesDict}";

            var yamlPath = Path.Combine(targetPath, "data.yaml");
            File.WriteAllText(yamlPath, yamlContent);
        }

        private static void GenerateTrainingConfig(
            string targetPath,
            string yoloVersion,
            string modelSize,
            int epochs,
            int batchSize,
            int imgSize,
            int patience,
            int workers,
            string gpuIndex)
        {
            var cfg = new
            {
                yolo_version = yoloVersion,
                model_size = string.IsNullOrWhiteSpace(modelSize) ? "n" : modelSize.ToLower(),
                epochs = epochs,
                batch_size = batchSize,
                img_size = imgSize,
                patience = patience,
                workers = workers,
                device = string.IsNullOrWhiteSpace(gpuIndex) ? "0" : gpuIndex,
                data_yaml = "data.yaml",
                exported_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(Path.Combine(targetPath, "training_config.json"), json);
        }

        private static void GenerateColabNotebook(string targetPath)
        {
            var notebook = new
            {
                nbformat = 4,
                nbformat_minor = 5,
                metadata = new
                {
                    kernelspec = new
                    {
                        name = "python3",
                        display_name = "Python 3"
                    },
                    language_info = new
                    {
                        name = "python"
                    }
                },
                cells = new object[]
                {
                    new
                    {
                        cell_type = "markdown",
                        metadata = new { },
                        source = new[]
                        {
                            "# Insight 导出训练 Notebook（Drive 持久化 + 自动续训）\n",
                            "该 notebook 会读取 `training_config.json`，并将训练结果写入 Google Drive，支持断点续训。\n"
                        }
                    },
                    new
                    {
                        cell_type = "code",
                        execution_count = (int?)null,
                        metadata = new { },
                        outputs = new object[] { },
                        source = new[]
                        {
                            "!pip install -q -U ultralytics pyyaml\n",
                            "from google.colab import drive\n",
                            "import os, json, yaml\n",
                            "from pathlib import Path\n",
                            "\n",
                            "drive.mount('/content/drive', force_remount=False)\n",
                            "DRIVE_ROOT = '/content/drive/MyDrive/InsightYOLO'\n",
                            "RUNS_PROJECT = os.path.join(DRIVE_ROOT, 'runs')\n",
                            "os.makedirs(RUNS_PROJECT, exist_ok=True)\n",
                            "\n",
                            "cfg_path = Path('training_config.json')\n",
                            "if not cfg_path.exists():\n",
                            "    raise FileNotFoundError('training_config.json not found')\n",
                            "\n",
                            "cfg = json.loads(cfg_path.read_text(encoding='utf-8'))\n",
                            "model_family = str(cfg.get('yolo_version', 'yolov8'))\n",
                            "# Ultralytics 官方权重命名: yolov8/v9/v10 带v, yolo11/yolo26 不带v\n",
                            "WEIGHT_MAP = {'yolov11': 'yolo11', 'yolo11': 'yolo11', 'yolo26': 'yolo26'}\n",
                            "weight_prefix = WEIGHT_MAP.get(model_family, model_family)\n",
                            "model_size = str(cfg.get('model_size', 'n')).lower()\n",
                            "model_name = f\"{weight_prefix}{model_size}.pt\"\n",
                            "epochs = int(cfg.get('epochs', 300))\n",
                            "batch_size = int(cfg.get('batch_size', 16))\n",
                            "img_size = int(cfg.get('img_size', 640))\n",
                            "patience = int(cfg.get('patience', 50))\n",
                            "workers = int(cfg.get('workers', 8))\n",
                            "device = cfg.get('device', '0')\n",
                            "data_yaml = cfg.get('data_yaml', 'data.yaml')\n",
                            "\n",
                            "dataset_path = os.getcwd()\n",
                            "raw_yaml = data_yaml if os.path.isabs(str(data_yaml)) else os.path.join(dataset_path, str(data_yaml))\n",
                            "if not os.path.exists(raw_yaml):\n",
                            "    raise FileNotFoundError(f'data.yaml not found: {raw_yaml}')\n",
                            "\n",
                            "def build_resolved_data_yaml(dataset_path, raw_yaml):\n",
                            "    with open(raw_yaml, 'r', encoding='utf-8') as f:\n",
                            "        ycfg = yaml.safe_load(f) or {}\n",
                            "\n",
                            "    base = ycfg.get('path')\n",
                            "    if not base or str(base).strip() in ('.', './'):\n",
                            "        base_dir = dataset_path\n",
                            "    else:\n",
                            "        base = str(base).strip()\n",
                            "        base_dir = base if os.path.isabs(base) else os.path.normpath(os.path.join(dataset_path, base))\n",
                            "\n",
                            "    for key in ('train', 'val', 'test'):\n",
                            "        if key in ycfg and ycfg.get(key):\n",
                            "            p = str(ycfg[key]).strip()\n",
                            "            if not os.path.isabs(p):\n",
                            "                if p.startswith('./'):\n",
                            "                    p = p[2:]\n",
                            "                p = os.path.normpath(os.path.join(base_dir, p))\n",
                            "            ycfg[key] = p.replace('\\\\', '/')\n",
                            "\n",
                            "    ycfg.pop('path', None)\n",
                            "    fixed_yaml = os.path.join(dataset_path, 'data.resolved.yaml')\n",
                            "    with open(fixed_yaml, 'w', encoding='utf-8') as f:\n",
                            "        yaml.safe_dump(ycfg, f, allow_unicode=True, sort_keys=False)\n",
                            "    return fixed_yaml\n",
                            "\n",
                            "fixed_yaml = build_resolved_data_yaml(dataset_path, raw_yaml)\n",
                            "run_name = f\"{weight_prefix}{model_size}_train\"\n",
                            "resume_ckpt = os.path.join(RUNS_PROJECT, run_name, 'weights', 'last.pt')\n",
                            "\n",
                            "print('Model:', model_name)\n",
                            "print('Data YAML:', fixed_yaml)\n",
                            "print('Runs dir:', RUNS_PROJECT)\n",
                            "print('Resume CKPT:', resume_ckpt)\n"
                        }
                    },
                    new
                    {
                        cell_type = "code",
                        execution_count = (int?)null,
                        metadata = new { },
                        outputs = new object[] { },
                        source = new[]
                        {
                            "from ultralytics import YOLO\n",
                            "\n",
                            "if os.path.exists(resume_ckpt):\n",
                            "    print(f'Resume from: {resume_ckpt}')\n",
                            "    model = YOLO(resume_ckpt)\n",
                            "    results = model.train(resume=True)\n",
                            "else:\n",
                            "    print('Start new training run')\n",
                            "    model = YOLO(model_name)\n",
                            "    results = model.train(\n",
                            "        data=fixed_yaml,\n",
                            "        epochs=epochs,\n",
                            "        batch=batch_size,\n",
                            "        imgsz=img_size,\n",
                            "        patience=patience,\n",
                            "        workers=workers,\n",
                            "        device=device,\n",
                            "        project=RUNS_PROJECT,\n",
                            "        name=run_name,\n",
                            "        exist_ok=True,\n",
                            "        save=True,\n",
                            "        save_period=1\n",
                            "    )\n",
                            "\n",
                            "print('Results:', results.save_dir)\n"
                        }
                    },
                    new
                    {
                        cell_type = "code",
                        execution_count = (int?)null,
                        metadata = new { },
                        outputs = new object[] { },
                        source = new[]
                        {
                            "from ultralytics import YOLO\n",
                            "import shutil\n",
                            "\n",
                            "best_pt = os.path.join(str(results.save_dir), 'weights', 'best.pt')\n",
                            "export_dir = os.path.join(DRIVE_ROOT, 'exports')\n",
                            "os.makedirs(export_dir, exist_ok=True)\n",
                            "onnx_name = f'detector_{weight_prefix}{model_size}.onnx'\n",
                            "onnx_path = os.path.join(export_dir, onnx_name)\n",
                            "exported = YOLO(best_pt).export(format='onnx', imgsz=img_size, simplify=True, opset=12, dynamic=False)\n",
                            "shutil.copy(exported, onnx_path)\n",
                            "print('ONNX:', onnx_path)\n"
                        }
                    }
                }
            };

            var notebookJson = JsonSerializer.Serialize(notebook, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(Path.Combine(targetPath, "YOLO_train.ipynb"), notebookJson);
        }
    }
}
