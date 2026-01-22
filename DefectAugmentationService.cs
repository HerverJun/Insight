using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes; // Required for mutable JSON
using System.Threading.Tasks;
using OpenCvSharp;

namespace Insight
{
    /// <summary>
    /// 缺陷增强服务：用于在数据集中合成缺陷图像，提升模型对缺陷的识别能力
    /// </summary>
    public static class DefectAugmentationService
    {
        /// <summary>
        /// YOLO 标签格式: class_id x_center y_center width height (归一化坐标)
        /// </summary>
        public class YoloLabel
        {
            public int ClassId { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public double W { get; set; }
            public double H { get; set; }
            public string OriginalLine { get; set; } = string.Empty;
            public int OriginalJsonIndex { get; set; } = -1; // JSON shapes 数组中的原始索引
        }

        /// <summary>
        /// 处理数据集，在图像上合成缺陷素材
        /// </summary>
        /// <param name="sourceImagesDir">源图片目录</param>
        /// <param name="sourceLabelsDir">源标签目录</param>
        /// <param name="holeAssetsDir">缺陷素材目录 (PNG 透明图片)</param>
        /// <param name="outputDir">输出目录</param>
        /// <param name="logger">日志回调</param>
        /// <param name="progressCallback">进度回调</param>
        /// <param name="mutationRate">变异率 (0-1)</param>
        public static void ProcessDataset(
            string sourceImagesDir,
            string sourceLabelsDir,
            string holeAssetsDir,
            string outputDir,
            Action<string> logger,
            Action<int, int> progressCallback,
            double mutationRate = 0.3)
        {
            // 验证输入路径
            if (!Directory.Exists(sourceImagesDir)) throw new DirectoryNotFoundException($"源图片目录不存在: {sourceImagesDir}");
            if (!Directory.Exists(sourceLabelsDir)) throw new DirectoryNotFoundException($"源标签目录不存在: {sourceLabelsDir}");
            if (!Directory.Exists(holeAssetsDir)) throw new DirectoryNotFoundException($"素材目录不存在: {holeAssetsDir}");

            // 创建输出目录
            Directory.CreateDirectory(outputDir);

            // 加载缺陷素材库
            var holeFiles = Directory.GetFiles(holeAssetsDir, "*.png");
            if (holeFiles.Length == 0) throw new FileNotFoundException("素材目录中未找到 PNG 图片 (需带透明通道)");

            logger($"加载素材库: 找到 {holeFiles.Length} 个素材");

            // 扫描类别定义
            logger("正在扫描类别定义...");
            var classMap = ScanClasses(sourceLabelsDir);

            // 扫描源文件
            var imageFiles = Directory.GetFiles(sourceImagesDir)
                .Where(f => f.EndsWith(".jpg") || f.EndsWith(".png") || f.EndsWith(".bmp") || f.EndsWith(".jpeg"))
                .ToArray();

            logger($"开始处理，共发现 {imageFiles.Length} 张原图，变异率: {mutationRate:P0}，输出格式: LabelMe JSON");

            int processedCount = 0;
            int totalFiles = imageFiles.Length;
            var random = new Random();

            // 并行处理图像
            Parallel.ForEach(imageFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, (imgFile) =>
            {
                try
                {
                    string fileName = Path.GetFileNameWithoutExtension(imgFile);
                    string txtFile = Path.Combine(sourceLabelsDir, fileName + ".txt");
                    string jsonFile = Path.Combine(sourceLabelsDir, fileName + ".json");

                    // 输出路径
                    string outImgPath = Path.Combine(outputDir, Path.GetFileName(imgFile));
                    string outJsonPath = Path.Combine(outputDir, fileName + ".json");

                    // 决定是否进行变异
                    bool shouldMutate = false;
                    lock (random)
                    {
                        shouldMutate = random.NextDouble() < mutationRate;
                    }

                    // 加载标签
                    var labels = LoadLabels(txtFile, jsonFile, classMap, out int imgWidth, out int imgHeight);

                    if (!shouldMutate || labels.Count == 0)
                    {
                        // 不变异：直接复制图像和标签
                        File.Copy(imgFile, outImgPath, true);
                        SaveAsJson(outJsonPath, jsonFile, labels, imgWidth > 0 ? imgWidth : 640, imgHeight > 0 ? imgHeight : 640, Path.GetFileName(imgFile), classMap);
                    }
                    else
                    {
                        // 应用缺陷合成
                        ProcessSingleImage(imgFile, jsonFile, labels, holeFiles, outImgPath, outJsonPath, logger, classMap);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"处理失败 {Path.GetFileName(imgFile)}: {ex.Message}");
                }
                finally
                {
                    int current = System.Threading.Interlocked.Increment(ref processedCount);
                    if (current % 10 == 0 || current == totalFiles)
                    {
                        progressCallback(current, totalFiles);
                    }
                }
            });

            logger("处理完成！");
        }

        /// <summary>
        /// 扫描目录中的 LabelMe JSON 文件，提取所有类别名称
        /// </summary>
        private static Dictionary<string, int> ScanClasses(string dir)
        {
            var classes = new HashSet<string>();
            var jsonFiles = Directory.GetFiles(dir, "*.json");
            foreach (var f in jsonFiles.Take(1000))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    if (doc.RootElement.TryGetProperty("shapes", out var shapes))
                    {
                        foreach (var s in shapes.EnumerateArray())
                        {
                            if (s.TryGetProperty("label", out var l)) classes.Add(l.GetString()!);
                        }
                    }
                }
                catch { }
            }
            return classes.OrderBy(x => x).Select((name, i) => new { name, i }).ToDictionary(x => x.name, x => x.i);
        }

        /// <summary>
        /// 加载标签文件 (JSON 或 TXT 格式)
        /// </summary>
        private static List<YoloLabel> LoadLabels(string txtPath, string jsonPath, Dictionary<string, int> classMap, out int w, out int h)
        {
            w = 0; h = 0;
            var result = new List<YoloLabel>();

            if (File.Exists(jsonPath))
            {
                try
                {
                    var content = File.ReadAllText(jsonPath);
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("imageWidth", out var wProp)) w = wProp.GetInt32();
                    if (root.TryGetProperty("imageHeight", out var hProp)) h = hProp.GetInt32();

                    if (root.TryGetProperty("shapes", out var shapes))
                    {
                        int idx = 0;
                        foreach (var shape in shapes.EnumerateArray())
                        {
                            var label = shape.GetProperty("label").GetString();
                            if (!string.IsNullOrEmpty(label) && classMap.ContainsKey(label))
                            {
                                int cid = classMap[label];
                                var points = shape.GetProperty("points");
                                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                                foreach (var p in points.EnumerateArray())
                                {
                                    var px = p[0].GetDouble(); var py = p[1].GetDouble();
                                    if (px < minX) minX = px; if (px > maxX) maxX = px;
                                    if (py < minY) minY = py; if (py > maxY) maxY = py;
                                }

                                double dw = 1.0 / w; double dh = 1.0 / h;
                                double xCenter = (minX + maxX) / 2.0 * dw;
                                double yCenter = (minY + maxY) / 2.0 * dh;
                                double width = (maxX - minX) * dw;
                                double height = (maxY - minY) * dh;

                                result.Add(new YoloLabel
                                {
                                    ClassId = cid,
                                    X = xCenter,
                                    Y = yCenter,
                                    W = width,
                                    H = height,
                                    OriginalJsonIndex = idx
                                });
                            }
                            idx++;
                        }
                    }
                }
                catch { }
                return result;
            }

            if (File.Exists(txtPath))
            {
                // 回退处理 TXT 格式
                var lines = File.ReadAllLines(txtPath);
                foreach (var line in lines)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5 && int.TryParse(parts[0], out int cid) &&
                        double.TryParse(parts[1], out double x) && double.TryParse(parts[2], out double y) &&
                        double.TryParse(parts[3], out double width) && double.TryParse(parts[4], out double height))
                    {
                        result.Add(new YoloLabel { ClassId = cid, X = x, Y = y, W = width, H = height, OriginalLine = line });
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 处理单张图像：在指定标签区域叠加缺陷素材
        /// </summary>
        private static void ProcessSingleImage(
            string imgPath,
            string jsonInputPath,
            List<YoloLabel> labels,
            string[] holeFiles,
            string outImgPath,
            string outJsonPath,
            Action<string> logger,
            Dictionary<string, int> classMap)
        {
            using var srcMat = Cv2.ImRead(imgPath, ImreadModes.Color);
            if (srcMat.Empty()) return;

            int imgW = srcMat.Width;
            int imgH = srcMat.Height;

            // 随机选择一个标签作为目标
            var rnd = new Random(Guid.NewGuid().GetHashCode());
            int targetIndex = rnd.Next(labels.Count);
            var targetLabel = labels[targetIndex];

            // 计算目标区域 ROI
            int wPx = (int)(targetLabel.W * imgW);
            int hPx = (int)(targetLabel.H * imgH);
            int xPx = (int)((targetLabel.X * imgW) - (wPx / 2.0));
            int yPx = (int)((targetLabel.Y * imgH) - (hPx / 2.0));

            // 边界检查
            if (xPx < 0) xPx = 0; if (yPx < 0) yPx = 0;
            if (xPx + wPx > imgW) wPx = imgW - xPx;
            if (yPx + hPx > imgH) hPx = imgH - yPx;

            if (wPx > 0 && hPx > 0)
            {
                string holeFile = holeFiles[rnd.Next(holeFiles.Length)];
                using var holeMat = Cv2.ImRead(holeFile, ImreadModes.Unchanged);
                if (!holeMat.Empty())
                {
                    using var resizedHole = new Mat();
                    Cv2.Resize(holeMat, resizedHole, new OpenCvSharp.Size(wPx, hPx));
                    BlendImages(srcMat, resizedHole, xPx, yPx, logger);
                }
            }

            srcMat.SaveImage(outImgPath);

            // 保存 JSON (删除目标标签)
            SaveAsJson(outJsonPath, jsonInputPath, labels.Where((_, i) => i != targetIndex).ToList(), imgW, imgH, Path.GetFileName(outImgPath), classMap);
        }

        /// <summary>
        /// 保存标签为 LabelMe JSON 格式
        /// </summary>
        private static void SaveAsJson(
            string outputPath,
            string inputJsonPath,
            List<YoloLabel> labelsToKeep,
            int w,
            int h,
            string imageName,
            Dictionary<string, int> classMap)
        {
            if (File.Exists(inputJsonPath))
            {
                // 基于现有 JSON 修改，保留多边形信息
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(inputJsonPath));
                    if (node != null)
                    {
                        node["imagePath"] = imageName;
                        node["imageData"] = null; // Clear base64 to save space

                        // Filter shapes
                        var shapes = node["shapes"] as JsonArray;
                        if (shapes != null)
                        {
                            // 根据要保留的标签索引构建集合
                            var indicesToKeep = new HashSet<int>(labelsToKeep.Select(l => l.OriginalJsonIndex));

                            // 从后向前删除以避免索引偏移
                            for (int i = shapes.Count - 1; i >= 0; i--)
                            {
                                if (!indicesToKeep.Contains(i))
                                {
                                    shapes.RemoveAt(i);
                                }
                            }
                        }

                        File.WriteAllText(outputPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        return;
                    }
                }
                catch { }
            }

            // 为 TXT 源合成 JSON
            var idToName = classMap.ToDictionary(k => k.Value, k => k.Key);

            var jsonObj = new JsonObject();
            jsonObj["version"] = "5.0.1";
            jsonObj["flags"] = new JsonObject();
            var shapesArr = new JsonArray();

            foreach (var l in labelsToKeep)
            {
                if (idToName.TryGetValue(l.ClassId, out var name))
                {
                    var shape = new JsonObject();
                    shape["label"] = name;

                    // 将 YOLO 归一化坐标转换为像素坐标
                    double cx = l.X * w; double cy = l.Y * h;
                    double bw = l.W * w; double bh = l.H * h;
                    double x1 = cx - bw / 2; double y1 = cy - bh / 2;
                    double x2 = cx + bw / 2; double y2 = cy + bh / 2;

                    var points = new JsonArray();
                    points.Add(new JsonArray(JsonValue.Create(x1), JsonValue.Create(y1)));
                    points.Add(new JsonArray(JsonValue.Create(x2), JsonValue.Create(y2)));

                    shape["points"] = points;
                    shape["group_id"] = null;
                    shape["shape_type"] = "rectangle";
                    shape["flags"] = new JsonObject();
                    shapesArr.Add(shape);
                }
            }

            jsonObj["shapes"] = shapesArr;
            jsonObj["imagePath"] = imageName;
            jsonObj["imageData"] = null;
            jsonObj["imageHeight"] = h;
            jsonObj["imageWidth"] = w;

            File.WriteAllText(outputPath, jsonObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>
        /// 图像混合：将素材图片叠加到背景图像上
        /// </summary>
        private static void BlendImages(Mat bg, Mat overlay, int x, int y, Action<string> logger)
        {
            if (overlay.Channels() != 4)
            {
                if (overlay.Channels() == 3)
                {
                    var roiRectFb = new Rect(x, y, overlay.Width, overlay.Height);
                    using var roiFb = new Mat(bg, roiRectFb);
                    overlay.CopyTo(roiFb);
                }
                return;
            }

            var roiRect = new Rect(x, y, overlay.Width, overlay.Height);
            using var roi = new Mat(bg, roiRect);

            var overlayChannels = Cv2.Split(overlay);
            var bChannel = overlayChannels[0];
            var gChannel = overlayChannels[1];
            var rChannel = overlayChannels[2];
            var aChannel = overlayChannels[3];

            using var alphaMask = new Mat();
            aChannel.ConvertTo(alphaMask, MatType.CV_32F, 1.0 / 255.0);

            using var betaMask = new Mat();
            var one = new Scalar(1.0);
            Cv2.Subtract(one, alphaMask, betaMask);

            var bgChannels = Cv2.Split(roi);

            ConvertAndBlend(bChannel, bgChannels[0], alphaMask, betaMask);
            ConvertAndBlend(gChannel, bgChannels[1], alphaMask, betaMask);
            ConvertAndBlend(rChannel, bgChannels[2], alphaMask, betaMask);

            using var merged = new Mat();
            Cv2.Merge(bgChannels, merged);
            merged.CopyTo(roi);

            foreach (var m in overlayChannels) m.Dispose();
            foreach (var m in bgChannels) m.Dispose();
        }

        /// <summary>
        /// 单通道混合计算
        /// </summary>
        private static void ConvertAndBlend(Mat fg, Mat bg, Mat alpha, Mat beta)
        {
            using var fgFloat = new Mat();
            using var bgFloat = new Mat();
            fg.ConvertTo(fgFloat, MatType.CV_32F);
            bg.ConvertTo(bgFloat, MatType.CV_32F);
            using var part1 = new Mat();
            using var part2 = new Mat();
            Cv2.Multiply(fgFloat, alpha, part1);
            Cv2.Multiply(bgFloat, beta, part2);
            using var sum = new Mat();
            Cv2.Add(part1, part2, sum);
            sum.ConvertTo(bg, MatType.CV_8U);
        }
    }
}
