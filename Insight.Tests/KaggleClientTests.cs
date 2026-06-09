using Insight.Infrastructure.Cloud.Kaggle;
using Insight.Infrastructure.Processes;
using Insight.Services.Cloud.Kaggle;
using Insight.Services.Configuration;

namespace Insight.Tests;

public class KaggleClientTests
{
    [Fact]
    public async Task DownloadOutputUsesRunnerArgumentListAndDefaultCloudPath()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var paths = new InsightAppPaths(
                Path.Combine(tempRoot, "user"),
                Path.Combine(tempRoot, "local"));
            var runner = new RecordingProcessRunner();
            var client = new KaggleCliClient(runner, paths);

            var output = await client.DownloadOutputAsync(
                new KaggleOutputDownloadRequest
                {
                    KernelId = "user/kernel"
                },
                CancellationToken.None);

            var spec = Assert.Single(runner.Specs);
            Assert.Equal("kaggle", spec.FileName);
            Assert.Equal(new[] { "kernels", "output", "user/kernel", "-p", output }, spec.ArgumentList);
            Assert.StartsWith(Path.Combine(paths.CloudJobsRoot, "kaggle-outputs"), output);
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task GetTrainingStatusUsesKaggleStatusCommand()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var paths = new InsightAppPaths(
                Path.Combine(tempRoot, "user"),
                Path.Combine(tempRoot, "local"));
            var runner = new RecordingProcessRunner("status ok");
            var client = new KaggleCliClient(runner, paths);

            var status = await client.GetTrainingStatusAsync("user/kernel", CancellationToken.None);

            var spec = Assert.Single(runner.Specs);
            Assert.Equal("kaggle", spec.FileName);
            Assert.Equal(new[] { "kernels", "status", "user/kernel" }, spec.ArgumentList);
            Assert.Equal("user/kernel", status.KernelId);
            Assert.Equal("Submitted", status.State);
            Assert.Contains("status ok", status.Message);
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "InsightKaggleTests_" + Guid.NewGuid().ToString("N"));
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

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        private readonly string[] _lines;

        public RecordingProcessRunner(params string[] lines)
        {
            _lines = lines.Length == 0 ? new[] { "ok" } : lines;
        }

        public List<ProcessStartSpec> Specs { get; } = new();

        public Task<ProcessRunResult> RunAsync(
            ProcessStartSpec spec,
            Action<string>? onOutput,
            CancellationToken cancellationToken)
        {
            Specs.Add(spec);
            foreach (var line in _lines)
            {
                onOutput?.Invoke(line);
            }

            return Task.FromResult(new ProcessRunResult(0, cancellationToken.IsCancellationRequested, string.Join(Environment.NewLine, _lines)));
        }
    }
}
