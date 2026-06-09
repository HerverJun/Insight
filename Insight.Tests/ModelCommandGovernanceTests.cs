using Insight.Bridge;
using Insight.Infrastructure.Processes;
using Insight.Services;
using Insight.Services.Cloud.Kaggle;
using Insight.Services.Configuration;

namespace Insight.Tests;

public class ModelCommandGovernanceTests
{
    [Fact]
    public async Task ConvertModelUsesProcessRunnerArgumentListAndMovesArtifact()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var sourceDir = Path.Combine(tempRoot, "models");
            var targetDir = Path.Combine(tempRoot, "exports");
            Directory.CreateDirectory(sourceDir);
            var sourceModel = Path.Combine(sourceDir, "best.pt");
            File.WriteAllText(sourceModel, "pt");

            var runner = new ExportRecordingProcessRunner();
            var messenger = new RecordingFrontendMessenger();
            using var app = new InsightApplication(
                messenger,
                new ImmediateUiDispatcher(),
                new NullDialogService(),
                new InsightAppPaths(Path.Combine(tempRoot, "user"), Path.Combine(tempRoot, "local")),
                runner,
                new NoopKaggleClient());

            await app.HandleConvertModelAsync(new ConvertModelPayload
            {
                SourcePath = sourceModel,
                TargetDir = targetDir,
                PythonPath = "python.exe"
            });

            var spec = Assert.Single(runner.Specs);
            Assert.Equal("python.exe", spec.FileName);
            Assert.Equal(sourceDir, spec.WorkingDirectory);
            Assert.Equal(
                new[]
                {
                    "-c",
                    "from ultralytics.cfg import entrypoint; entrypoint()",
                    "export",
                    $"model={sourceModel}",
                    "format=onnx",
                    "simplify=True"
                },
                spec.ArgumentList);
            Assert.Equal("utf-8", spec.EnvironmentVariables["PYTHONIOENCODING"]);
            Assert.Equal("TRUE", spec.EnvironmentVariables["KMP_DUPLICATE_LIB_OK"]);
            Assert.True(File.Exists(Path.Combine(targetDir, "best.onnx")));
            Assert.Contains(messenger.Messages, message => HasAction(message, "model_operation_complete"));
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public void ConvertModelPayloadRequiresSourceAndTargetPaths()
    {
        using var invalid = System.Text.Json.JsonDocument.Parse("""{"sourcePath":"C:/models/best.pt"}""");

        var ex = Assert.Throws<InvalidOperationException>(
            () => WebViewPayloadBinder.Bind<ConvertModelPayload>(invalid.RootElement));

        Assert.Contains(nameof(ConvertModelPayload), ex.Message);
        Assert.Contains(nameof(ConvertModelPayload.TargetDir), ex.Message);
    }

    private static bool HasAction(object message, string action)
    {
        var actionProperty = message.GetType().GetProperty("action");
        return string.Equals(actionProperty?.GetValue(message)?.ToString(), action, StringComparison.Ordinal);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "InsightModelGovernanceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class ExportRecordingProcessRunner : IProcessRunner
    {
        public List<ProcessStartSpec> Specs { get; } = new();

        public Task<ProcessRunResult> RunAsync(
            ProcessStartSpec spec,
            Action<string>? onOutput,
            CancellationToken cancellationToken)
        {
            Specs.Add(spec);
            var modelArgument = spec.ArgumentList.First(argument => argument.StartsWith("model=", StringComparison.Ordinal));
            File.WriteAllText(Path.ChangeExtension(modelArgument.Substring("model=".Length), ".onnx"), "onnx");
            onOutput?.Invoke("export ok");
            return Task.FromResult(new ProcessRunResult(0, cancellationToken.IsCancellationRequested, "export ok"));
        }
    }

    private sealed class RecordingFrontendMessenger : IFrontendMessenger
    {
        public List<object> Messages { get; } = new();

        public void Send(object data) => Messages.Add(data);
        public void Log(string message, string type = "info") => Messages.Add(new { action = "log", message, type });
        public void Error(string message) => Messages.Add(new { action = "error", message });
        public void Complete(string message) => Messages.Add(new { action = "complete", message });
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool InvokeRequired => false;

        public void Post(Action action) => action();
        public void Invoke(Action action) => action();
        public T Invoke<T>(Func<T> action) => action();
    }

    private sealed class NullDialogService : IAppDialogService
    {
        public string? SelectFolder(string type) => null;
        public string? SelectPythonFile(string type) => null;
        public string? SelectDatasetZipPath(string? projectName, string yoloVersion) => null;
        public void ShowWarning(string message, string title) { }
        public void ShowError(string message, string title) { }
        public void OpenFolder(string path) { }
        public void OpenFileInExplorer(string path) { }
    }

    private sealed class NoopKaggleClient : IKaggleClient
    {
        public Task<KaggleConnectionTestResult> TestConnectionAsync(
            KaggleConnectionTestRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new KaggleConnectionTestResult { Success = true });
        }

        public Task<KaggleTrainingSubmissionResult> SubmitTrainingAsync(
            KaggleTrainingSubmissionRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new KaggleTrainingSubmissionResult());
        }

        public Task<KaggleTrainingSubmissionResult> SubmitPreparedTrainingAsync(
            KagglePreparedTrainingSubmissionRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new KaggleTrainingSubmissionResult { JobId = request.JobId });
        }

        public Task<KaggleTrainingJobStatus> GetTrainingStatusAsync(
            string kernelId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new KaggleTrainingJobStatus { KernelId = kernelId });
        }

        public Task<string> DownloadOutputAsync(
            KaggleOutputDownloadRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(request.OutputDirectory);
        }
    }
}
