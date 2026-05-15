using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Insight.Bridge;
using Insight.Training;

namespace Insight.Services.Training
{
    public sealed class TrainingOrchestrator : IDisposable
    {
        private readonly IFrontendMessenger _messenger;
        private readonly ITrainingEngine _engine;
        private Process? _trainingProcess;
        private bool _isTraining;
        private bool _trainingStopRequested;
        private double _lastBoxLoss;
        private double _lastMap50;
        private double _lastMap5095;

        public TrainingOrchestrator(IFrontendMessenger messenger, ITrainingEngine engine)
        {
            _messenger = messenger;
            _engine = engine;
        }

        public async Task StartAsync(JsonElement data)
        {
            if (_isTraining) return;

            try
            {
                _messenger.Log("收到训练请求...", "info");
                _trainingStopRequested = false;
                _lastBoxLoss = 0;
                _lastMap50 = 0;
                _lastMap5095 = 0;

                var request = ParseRequest(data);
                var launchPlan = _engine.BuildLaunchPlan(request);

                _isTraining = true;
                _messenger.Log("正在启动训练进程...", "info");
                _messenger.Log($"解释器: {launchPlan.PythonPath}", "info");
                _messenger.Log($"参数: {launchPlan.Arguments}", "info");

                await Task.Run(() => RunTrainingProcess(launchPlan, request.Params));
            }
            catch (Exception ex)
            {
                _isTraining = false;
                _messenger.Error($"启动训练失败: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (_trainingProcess == null || _trainingProcess.HasExited) return;

            try
            {
                _trainingStopRequested = true;
                _trainingProcess.Kill();
                _messenger.Log("已发送停止信号。", "warning");
                _messenger.Send(new { action = "training_stopped" });
            }
            catch (Exception ex)
            {
                _messenger.Error($"停止失败: {ex.Message}");
            }
        }

        public void LoadHistory()
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

                _messenger.Send(new { action = "training_history_loaded", history });
            }
            catch (Exception ex)
            {
                _messenger.Error($"加载训练历史失败: {ex.Message}");
                _messenger.Send(new { action = "training_history_loaded", history = new List<object>() });
            }
        }

        public void Dispose()
        {
            _trainingProcess?.Dispose();
            _trainingProcess = null;
        }

        private void RunTrainingProcess(TrainingLaunchPlan launchPlan, TrainingParams parameters)
        {
            int? exitCode = null;
            bool started = false;
            var trainingStartTime = DateTime.Now;

            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = launchPlan.PythonPath,
                    Arguments = launchPlan.Arguments,
                    WorkingDirectory = launchPlan.WorkingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                process.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                process.StartInfo.EnvironmentVariables["KMP_DUPLICATE_LIB_OK"] = "TRUE";
                process.OutputDataReceived += (_, e) => ParseTrainingOutput(e.Data);
                process.ErrorDataReceived += (_, e) => ParseTrainingOutput(e.Data);

                _trainingProcess = process;
                process.Start();
                started = true;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                exitCode = process.ExitCode;
            }
            catch (Exception ex)
            {
                _messenger.Error($"训练进程异常: {ex.Message}");
            }
            finally
            {
                _isTraining = false;
                _trainingProcess = null;
                CompleteTrainingRun(started, exitCode, launchPlan, parameters, trainingStartTime);
            }
        }

        private void CompleteTrainingRun(
            bool started,
            int? exitCode,
            TrainingLaunchPlan launchPlan,
            TrainingParams parameters,
            DateTime trainingStartTime)
        {
            if (_trainingStopRequested)
            {
                _messenger.Send(new { action = "training_stopped" });
                return;
            }

            if (!started || exitCode != 0)
            {
                var codeText = exitCode.HasValue ? exitCode.Value.ToString() : "未启动";
                _messenger.Error($"训练未成功完成，退出码: {codeText}。已跳过 ONNX 导出。");
                _messenger.Send(new { action = "training_finished", success = false });
                return;
            }

            var bestArtifact = _engine.FindBestArtifact(launchPlan.WorkingDirectory, trainingStartTime);
            if (string.IsNullOrEmpty(bestArtifact))
            {
                _messenger.Error("训练完成，但未找到本次训练产生的 best.pt。已跳过 ONNX 导出。");
                _messenger.Send(new { action = "training_finished", success = false });
                return;
            }

            var exported = ExportOnnx(launchPlan.PythonPath, launchPlan.WorkingDirectory, bestArtifact, parameters);
            _messenger.Send(new { action = "training_finished", success = exported });
        }

        private void ParseTrainingOutput(string? line)
        {
            if (string.IsNullOrEmpty(line)) return;

            var cleanLine = Regex.Replace(line, @"\x1B\[[^@-~]*[@-~]", "").Trim();
            _messenger.Log(cleanLine, "info");

            var update = _engine.TryParseMetrics(cleanLine);
            if (update == null) return;

            if (update.BoxLoss.HasValue)
            {
                _lastBoxLoss = update.BoxLoss.Value;
                _messenger.Send(new
                {
                    action = "training_data",
                    epoch = update.Epoch,
                    box_loss = update.BoxLoss.Value,
                    progress = update.Progress ?? 0
                });
            }

            if (update.Map50.HasValue)
            {
                _lastMap50 = update.Map50.Value;
                _lastMap5095 = update.Map5095 ?? 0;
                _messenger.Send(new
                {
                    action = "training_data",
                    epoch = "val",
                    map50 = _lastMap50,
                    map5095 = _lastMap5095
                });
            }
        }

        private bool ExportOnnx(string pythonPath, string workDir, string bestPt, TrainingParams parameters)
        {
            if (!File.Exists(bestPt))
            {
                _messenger.Log($"未找到模型文件: {bestPt}。训练可能失败或未产生权重。", "error");
                return false;
            }

            _messenger.Log("开始导出 ONNX 模型...", "info");
            _messenger.Log($"Best.pt Path: {bestPt}", "info");

            try
            {
                using var process = new Process();
                process.StartInfo.FileName = pythonPath;
                process.StartInfo.Arguments = _engine.BuildExportArguments(bestPt);
                process.StartInfo.WorkingDirectory = workDir;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                process.StartInfo.EnvironmentVariables["KMP_DUPLICATE_LIB_OK"] = "TRUE";
                process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _messenger.Log(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _messenger.Log(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    _messenger.Log("ONNX 导出进程非正常退出。", "error");
                    return false;
                }

                return ArchiveExportedOnnx(bestPt, workDir, parameters);
            }
            catch (Exception ex)
            {
                _messenger.Log($"导出异常: {ex.Message}", "error");
                return false;
            }
        }

        private bool ArchiveExportedOnnx(string bestPt, string workDir, TrainingParams parameters)
        {
            var exportedOnnx = Path.ChangeExtension(bestPt, ".onnx");
            if (!File.Exists(exportedOnnx))
            {
                _messenger.Log("导出命令执行完毕但未生成 ONNX 文件，请检查日志。", "warning");
                return false;
            }

            var fileSizeKB = new FileInfo(exportedOnnx).Length / 1024;
            _messenger.Log($"ONNX 文件已生成，大小: {fileSizeKB}KB", "info");

            var targetDir = string.IsNullOrWhiteSpace(parameters.ModelStoragePath) ? workDir : parameters.ModelStoragePath;
            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

            var finalName = parameters.OnnxName;
            if (string.IsNullOrWhiteSpace(finalName))
            {
                var dateStr = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                finalName = $"yolo-{parameters.ModelSize}-{dateStr}.onnx";
            }

            if (!finalName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)) finalName += ".onnx";

            var finalPath = Path.Combine(targetDir, finalName);
            File.Move(exportedOnnx, finalPath, true);
            SaveModelMetadata(finalPath, parameters);

            var projectName = Path.GetFileName(Path.GetDirectoryName(workDir)) ?? "Unknown";
            SaveTrainingHistoryEntry(finalPath, parameters, projectName);

            _messenger.Log($"★ 导出成功! 已归档至: {finalPath}", "success");
            _messenger.Send(new { action = "model_operation_complete" });
            return true;
        }

        private static TrainingRequest ParseRequest(JsonElement data)
        {
            var parameters = new TrainingParams
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

            return new TrainingRequest
            {
                PythonPath = data.TryGetProperty("pythonPath", out var pyProp) ? pyProp.GetString() ?? "" : "",
                WorkDir = data.TryGetProperty("workDir", out var wdProp) ? wdProp.GetString() ?? "" : "",
                Params = parameters
            };
        }

        private static void SaveModelMetadata(string modelPath, TrainingParams parameters)
        {
            var jsonPath = Path.ChangeExtension(modelPath, ".json");
            var metadata = new
            {
                name = Path.GetFileName(modelPath),
                date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                paramsData = parameters,
                classes = parameters.Classes
            };

            var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json);
        }

        private string GetTrainingHistoryPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "training_history.json");
        }

        private void SaveTrainingHistoryEntry(string modelPath, TrainingParams parameters, string projectName)
        {
            try
            {
                var historyPath = GetTrainingHistoryPath();
                var history = new List<object>();

                if (File.Exists(historyPath))
                {
                    var existingJson = File.ReadAllText(historyPath);
                    using var doc = JsonDocument.Parse(existingJson);
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        history.Add(elem.Clone());
                    }
                }

                history.Add(new
                {
                    id = Guid.NewGuid().ToString(),
                    projectName,
                    date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    modelPath,
                    modelSize = parameters.ModelSize,
                    epochs = parameters.Epochs,
                    imgSize = parameters.ImgSize,
                    batchSize = parameters.BatchSize,
                    classes = parameters.Classes,
                    finalLoss = Math.Round(_lastBoxLoss, 4),
                    mAP50 = Math.Round(_lastMap50, 4),
                    mAP5095 = Math.Round(_lastMap5095, 4)
                });

                var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(historyPath, json);
                _messenger.Log("训练记录已保存到历史", "success");
            }
            catch (Exception ex)
            {
                _messenger.Log($"保存训练历史失败: {ex.Message}", "warning");
            }
        }
    }
}
