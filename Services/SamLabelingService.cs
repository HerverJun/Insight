using System.Text.Json;
using Insight.Bridge;
using OpenCvSharp;

namespace Insight.Services
{
    public sealed class SamLabelingService : IDisposable
    {
        private readonly IFrontendMessenger _messenger;
        private readonly IUiDispatcher _ui;
        private SAM2Service? _sam2Service;
        private readonly object _sam2Lock = new();
        private string? _currentLabelingImagePath;

        public SamLabelingService(IFrontendMessenger messenger, IUiDispatcher ui)
        {
            _messenger = messenger;
            _ui = ui;
        }

        public void Dispose()
        {
            lock (_sam2Lock)
            {
                _sam2Service?.Dispose();
                _sam2Service = null;
            }
        }

        public void HandleGetSAMModels()
        {
            try
            {
                string modelDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
                if (!Directory.Exists(modelDir))
                {
                    Directory.CreateDirectory(modelDir);
                    SendToFrontend(new { action = "sam_models_loaded", models = new string[] { } });
                    return;
                }

                // 寻找所有以 .encoder.onnx 结尾的文件
                var encoders = Directory.GetFiles(modelDir, "*.encoder.onnx");
                var modelList = new List<string>();

                foreach (var encoder in encoders)
                {
                    string fileName = Path.GetFileName(encoder);
                    string modelName = fileName.Substring(0, fileName.Length - ".encoder.onnx".Length);
                    string decoderPath = Path.Combine(modelDir, modelName + ".decoder.onnx");

                    // 如果对齐的解码器也存在，则这是一个可用的 SAM2 模型对
                    if (File.Exists(decoderPath))
                    {
                        modelList.Add(modelName);
                    }
                }

                SendToFrontend(new { action = "sam_models_loaded", models = modelList.ToArray() });
            }
            catch (Exception ex)
            {
                SendError($"扫描模型目录失败: {ex.Message}");
            }
        }

        public async Task HandleLoadSAMModelAsync(JsonElement data)
        {
            try
            {
                // 增加对自定义模型名称的支持
                string modelName = "sam2.1_hiera_tiny"; // 默认名
                if (data.TryGetProperty("modelName", out var nameProp)) modelName = nameProp.GetString()!;
                else if (data.TryGetProperty("modelType", out var typeProp)) modelName = typeProp.GetString()!;

                bool useGpu = true;
                if (data.TryGetProperty("useGpu", out var gpuProp)) useGpu = gpuProp.GetBoolean();

                SendLog($"正在加载模型: {modelName} (GPU: {useGpu})...", "info");

                await Task.Run(() =>
                {
                    string modelDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
                    string encoderPath = Path.Combine(modelDir, $"{modelName}.encoder.onnx");
                    string decoderPath = Path.Combine(modelDir, $"{modelName}.decoder.onnx");

                    if (!File.Exists(encoderPath) || !File.Exists(decoderPath))
                    {
                        _ui.Invoke(() => SendError($"模型文件未找到: {modelName}，请检查 models 文件夹"));
                        return;
                    }

                    lock (_sam2Lock)
                    {
                        if (_sam2Service != null) _sam2Service.Dispose();
                        _sam2Service = new SAM2Service();
                        _sam2Service.LoadModels(encoderPath, decoderPath, useGpu);
                    }

                    _ui.Invoke(() =>
                    {
                        SendLog($"SAM2 模型 [{modelName}] 加载成功！", "success");
                        SendToFrontend(new { action = "sam_model_loaded", modelName = modelName });
                    });
                });
            }
            catch (Exception ex)
            {
                SendError($"加载模型失败: {ex.Message}");
            }
        }

        public async Task HandleEncodeImageAsync(JsonElement data)
        {
            try
            {
                var path = data.GetProperty("path").GetString();
                Console.WriteLine($"[Insight] HandleEncodeImage called, path: {path}");

                if (string.IsNullOrEmpty(path))
                {
                    Console.WriteLine("[Insight] Path is empty!");
                    return;
                }

                if (!File.Exists(path))
                {
                    Console.WriteLine($"[Insight] File not found: {path}");
                    return;
                }

                if (_sam2Service == null)
                {
                    Console.WriteLine("[Insight] SAM2 service is null! Model not loaded?");
                    SendError("SAM2 模型未加载，请先点击'加载模型'按钮");
                    return;
                }

                _currentLabelingImagePath = path;

                await Task.Run(() =>
                {
                    try
                    {
                        Console.WriteLine("[Insight] Calling SAM2 EncodeImage...");
                        lock (_sam2Lock)
                        {
                            _sam2Service?.EncodeImage(path);
                        }
                        Console.WriteLine("[Insight] EncodeImage completed, loading annotations...");

                        // Load existing annotations if any (YOLO txt)
                        var txtPath = Path.ChangeExtension(path, ".txt");
                        var existingAnns = new List<object>();

                        if (File.Exists(txtPath))
                        {
                            var lines = File.ReadAllLines(txtPath);
                            // Need image size for denormalization? Frontend has size.
                            // Better: Send raw YOLO lines and let Frontend denormalize?
                            // Or denormalize here if we read image size.
                            // Since we just encoded, we know the size?
                            // Using OpenCvSharp to peek size.
                            using var img = Cv2.ImRead(path);
                            int w = img.Width;
                            int h = img.Height;

                            foreach (var line in lines)
                            {
                                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length >= 5)
                                {
                                    if (int.TryParse(parts[0], out int cls) &&
                                        float.TryParse(parts[1], out float cx) &&
                                        float.TryParse(parts[2], out float cy) &&
                                        float.TryParse(parts[3], out float nw) &&
                                        float.TryParse(parts[4], out float nh))
                                    {
                                        // Convert YOLO (cx, cy, w, h) to Box (x, y, w, h)
                                        float boxW = nw * w;
                                        float boxH = nh * h;
                                        float boxX = (cx * w) - (boxW / 2);
                                        float boxY = (cy * h) - (boxH / 2);

                                        existingAnns.Add(new
                                        {
                                            labelIndex = cls,
                                            bbox = new { x = boxX, y = boxY, w = boxW, h = boxH }
                                        });
                                    }
                                }
                            }
                        }

                        _ui.Invoke(() =>
                        {
                            SendToFrontend(new { action = "image_encoded", success = true, existing = existingAnns });
                        });
                    }
                    catch (Exception ex)
                    {
                        _ui.Invoke(() => SendError($"编码图像失败: {ex.Message}"));
                    }
                });
            }
            catch (Exception ex)
            {
                SendError($"请求编码失败: {ex.Message}");
            }
        }

        public async Task HandleSAMInferenceAsync(JsonElement data)
        {
            lock (_sam2Lock)
            {
                if (_sam2Service == null || !_sam2Service.IsLoaded) return;
            }

            try
            {
                float[]? bbox = null;

                // 解析阈值参数，默认为 0.0
                float threshold = 0.0f;
                if (data.TryGetProperty("threshold", out var thresholdProp))
                {
                    threshold = (float)thresholdProp.GetDouble();
                }

                // 检查是否有 Box Prompt
                if (data.TryGetProperty("boxPrompt", out var boxProp))
                {
                    float x1 = (float)boxProp.GetProperty("x1").GetDouble();
                    float y1 = (float)boxProp.GetProperty("y1").GetDouble();
                    float x2 = (float)boxProp.GetProperty("x2").GetDouble();
                    float y2 = (float)boxProp.GetProperty("y2").GetDouble();

                    await Task.Run(() =>
                    {
                        lock (_sam2Lock)
                        {
                            bbox = _sam2Service?.PredictWithBox(x1, y1, x2, y2, threshold);
                        }
                    });
                }
                else if (data.TryGetProperty("prompts", out var promptsProp))
                {
                    // 点提示模式
                    var prompts = new List<(System.Drawing.Point, bool)>();
                    var promptArray = promptsProp.EnumerateArray();

                    foreach (var p in promptArray)
                    {
                        int x = (int)p.GetProperty("x").GetDouble();
                        int y = (int)p.GetProperty("y").GetDouble();
                        bool isPos = p.GetProperty("isPositive").GetBoolean();
                        prompts.Add((new System.Drawing.Point(x, y), isPos));
                    }

                    await Task.Run(() =>
                    {
                        lock (_sam2Lock)
                        {
                            bbox = _sam2Service?.Predict(prompts, threshold);
                        }
                    });
                }

                if (bbox != null && bbox.Length == 4)
                {
                    _ui.Invoke(() =>
                    {
                        SendToFrontend(new
                        {
                            action = "sam_result",
                            bbox = new { x = bbox[0], y = bbox[1], w = bbox[2], h = bbox[3] }
                        });
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public void HandleSaveAnnotations(JsonElement data)
        {
            try
            {
                var imagePath = data.GetProperty("imagePath").GetString();
                var width = data.GetProperty("width").GetDouble();
                var height = data.GetProperty("height").GetDouble();
                var anns = data.GetProperty("annotations").EnumerateArray();
                var labels = data.GetProperty("labels").EnumerateArray().Select(x => x.GetString()).ToList();

                if (string.IsNullOrEmpty(imagePath)) return;

                var txtPath = Path.ChangeExtension(imagePath, ".txt");
                var lines = new List<string>();

                foreach (var ann in anns)
                {
                    int clsIdx = ann.GetProperty("labelIndex").GetInt32();
                    var bbox = ann.GetProperty("bbox");
                    double x = bbox.GetProperty("x").GetDouble();
                    double y = bbox.GetProperty("y").GetDouble();
                    double w = bbox.GetProperty("w").GetDouble();
                    double h = bbox.GetProperty("h").GetDouble();

                    // Convert to YOLO
                    double cx = (x + w / 2.0) / width;
                    double cy = (y + h / 2.0) / height;
                    double nw = w / width;
                    double nh = h / height;

                    // Clamp
                    cx = Math.Max(0, Math.Min(1, cx));
                    cy = Math.Max(0, Math.Min(1, cy));
                    nw = Math.Max(0, Math.Min(1, nw));
                    nh = Math.Max(0, Math.Min(1, nh));

                    lines.Add($"{clsIdx} {cx:F6} {cy:F6} {nw:F6} {nh:F6}");
                }

                File.WriteAllLines(txtPath, lines);

                // Save classes.txt
                if (labels.Count > 0)
                {
                    var classesPath = Path.Combine(Path.GetDirectoryName(imagePath)!, "classes.txt");
                    // Only write if not exists or if we added new classes?
                    // Usually we overwrite to ensure consistency.
                    File.WriteAllLines(classesPath, labels!);
                }

                SendLog($"标注已保存: {Path.GetFileName(txtPath)}", "success");
            }
            catch (Exception ex)
            {
                SendError($"保存标注失败: {ex.Message}");
            }
        }

        public void HandleGetImagesInFolder(JsonElement data)
        {
            try
            {
                var path = data.GetProperty("path").GetString();
                if (Directory.Exists(path))
                {
                    var ext = new[] { ".jpg", ".jpeg", ".png", ".bmp" };
                    var files = Directory.GetFiles(path)
                        .Where(f => ext.Contains(Path.GetExtension(f).ToLower()))
                        .OrderBy(f => f)
                        .ToArray();

                    // Try load classes.txt
                    string[]? classes = null;
                    var clsPath = Path.Combine(path, "classes.txt");
                    if (File.Exists(clsPath))
                    {
                        classes = File.ReadAllLines(clsPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                    }

                    SendToFrontend(new { action = "labeling_folder_loaded", images = files, labels = classes });
                }
            }
            catch (Exception ex)
            {
                SendError($"获取图片列表失败: {ex.Message}");
            }
        }
        public void HandleGetImageData(JsonElement data)
        {
            try
            {
                var path = data.GetProperty("path").GetString();
                if (File.Exists(path))
                {
                    var bytes = File.ReadAllBytes(path);
                    var base64 = Convert.ToBase64String(bytes);
                    SendToFrontend(new { action = "image_data_loaded", base64 = base64, path = path });
                }
                else
                {
                    SendError("图片文件不存在");
                }
            }
            catch (Exception ex)
            {
                SendError($"加载图片数据失败: {ex.Message}");
            }
        }


        private void SendToFrontend(object data) => _messenger.Send(data);
        private void SendLog(string message, string type = "info") => _messenger.Log(message, type);
        private void SendError(string message) => _messenger.Error(message);
    }
}
