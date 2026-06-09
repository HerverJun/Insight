using Insight.Infrastructure.Cloud.Kaggle;
using Insight.Infrastructure.Processes;
using Insight.Infrastructure.Security;
using Insight.Services.Cloud.Kaggle;
using Insight.Services.Configuration;
using Insight.Services.Security;

namespace Insight.Tests;

public class KaggleClientTests
{
    [Fact]
    public async Task ProtectedFileSecretsStoreRoundTripsKaggleCredentialForCurrentUser()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var paths = new InsightAppPaths(
                Path.Combine(tempRoot, "user"),
                Path.Combine(tempRoot, "local"));
            var store = new KaggleCredentialStore(new ProtectedFileSecretsStore(paths));

            await store.SaveAsync(
                new KaggleCredential { Username = "tester", Key = "super-secret-key" },
                CancellationToken.None);

            var loaded = await store.GetAsync(CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal("tester", loaded.Username);
            Assert.Equal("super-secret-key", loaded.Key);

            await store.DeleteAsync(CancellationToken.None);
            Assert.Null(await store.GetAsync(CancellationToken.None));
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task TestConnectionRunsVersionAndAuthenticatedDatasetList()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var paths = new InsightAppPaths(
                Path.Combine(tempRoot, "user"),
                Path.Combine(tempRoot, "local"));
            var runner = new RecordingProcessRunner("kaggle 1.7.4");
            var client = new KaggleCliClient(runner, paths);

            var result = await client.TestConnectionAsync(
                new KaggleConnectionTestRequest { Username = "tester" },
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(2, runner.Specs.Count);
            Assert.Equal(new[] { "--version" }, runner.Specs[0].ArgumentList);
            Assert.Equal(new[] { "datasets", "list", "-s", "tester", "-p", "1" }, runner.Specs[1].ArgumentList);
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task TestConnectionUsesStoredCredentialAndClassifiesUnauthorizedWithoutLeakingKey()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var paths = new InsightAppPaths(
                Path.Combine(tempRoot, "user"),
                Path.Combine(tempRoot, "local"));
            var secrets = new ProtectedFileSecretsStore(paths);
            await new KaggleCredentialStore(secrets).SaveAsync(
                new KaggleCredential { Username = "tester", Key = "secret-token-123" },
                CancellationToken.None);

            var runner = new ScriptedProcessRunner(
                new ScriptedRun(0, "kaggle 1.7.4"),
                new ScriptedRun(1, "401 Unauthorized KAGGLE_KEY=secret-token-123"));
            var client = new KaggleCliClient(runner, paths, secrets);

            var result = await client.TestConnectionAsync(
                new KaggleConnectionTestRequest { Username = "tester" },
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(KaggleConnectionErrorKind.Unauthorized, result.ErrorKind);
            Assert.DoesNotContain("secret-token-123", result.Message);
            Assert.Equal("tester", runner.Specs[1].EnvironmentVariables["KAGGLE_USERNAME"]);
            Assert.Equal("secret-token-123", runner.Specs[1].EnvironmentVariables["KAGGLE_KEY"]);
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task SubmitPreparedTrainingStagesDatasetAndPushesKernel()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var dataset = Path.Combine(tempRoot, "dataset-version", "yolo");
            Directory.CreateDirectory(Path.Combine(dataset, "images", "train"));
            Directory.CreateDirectory(Path.Combine(dataset, "labels", "train"));
            File.WriteAllText(Path.Combine(dataset, "data.yaml"), $"path: {dataset}\ntrain: images/train\n", System.Text.Encoding.UTF8);

            var paths = new InsightAppPaths(
                Path.Combine(tempRoot, "user"),
                Path.Combine(tempRoot, "local"));
            var runner = new RecordingProcessRunner("ok");
            var client = new KaggleCliClient(runner, paths);

            var result = await client.SubmitPreparedTrainingAsync(
                new KagglePreparedTrainingSubmissionRequest
                {
                    JobId = "run_1",
                    DatasetVersionId = "ds_1",
                    DatasetDirectory = dataset,
                    ProjectName = "Surface QA",
                    KaggleUsername = "Test User",
                    DatasetSlug = "Surface DS",
                    KernelSlug = "Surface Kernel",
                    ModelSize = "v11s",
                    Classes = new[] { "scratch" }
                },
                CancellationToken.None);

            Assert.True(result.Submitted);
            Assert.Equal("test-user/surface-ds", result.DatasetId);
            Assert.Equal("test-user/surface-kernel", result.KernelId);
            Assert.Equal(3, runner.Specs.Count);
            Assert.Equal(new[] { "datasets", "create", "-p", result.DatasetDirectory, "--dir-mode", "zip", "-q" }, runner.Specs[0].ArgumentList);
            Assert.Equal(new[] { "kernels", "push", "-p", result.KernelDirectory }, runner.Specs[1].ArgumentList);
            Assert.Equal(new[] { "kernels", "status", result.KernelId }, runner.Specs[2].ArgumentList);

            var stagedYaml = File.ReadAllText(Path.Combine(result.DatasetDirectory, "data.yaml"), System.Text.Encoding.UTF8);
            Assert.DoesNotContain(tempRoot.Replace("\\", "/"), stagedYaml);
            Assert.Contains("train: images/train", stagedYaml);
            Assert.True(File.Exists(Path.Combine(result.KernelDirectory, "kernel-metadata.json")));
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task SubmitPreparedTrainingRetriesTransientUploadFailureAndReusesSubmissionMetadata()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var dataset = Path.Combine(tempRoot, "dataset-version", "yolo");
            Directory.CreateDirectory(Path.Combine(dataset, "images", "train"));
            Directory.CreateDirectory(Path.Combine(dataset, "labels", "train"));
            File.WriteAllText(Path.Combine(dataset, "data.yaml"), "train: images/train\n", System.Text.Encoding.UTF8);

            var paths = new InsightAppPaths(
                Path.Combine(tempRoot, "user"),
                Path.Combine(tempRoot, "local"));
            var runner = new ScriptedProcessRunner(
                new ScriptedRun(1, "429 rate limit"),
                new ScriptedRun(0, "created"),
                new ScriptedRun(0, "pushed"),
                new ScriptedRun(0, "complete"));
            var client = new KaggleCliClient(runner, paths);

            var request = new KagglePreparedTrainingSubmissionRequest
            {
                JobId = "run_retry",
                DatasetVersionId = "ds_1",
                DatasetDirectory = dataset,
                ProjectName = "Surface QA",
                KaggleUsername = "tester",
                DatasetSlug = "surface-ds",
                KernelSlug = "surface-kernel",
                Classes = new[] { "scratch" },
                RetryPolicy = new KaggleRetryPolicy { MaxAttempts = 2, DelayMilliseconds = 0 }
            };

            var first = await client.SubmitPreparedTrainingAsync(request, CancellationToken.None);
            var second = await client.SubmitPreparedTrainingAsync(request, CancellationToken.None);

            Assert.Equal(first.KernelId, second.KernelId);
            Assert.Equal(4, runner.Specs.Count);
            Assert.Equal(new[] { "datasets", "create", "-p", first.DatasetDirectory, "--dir-mode", "zip", "-q" }, runner.Specs[0].ArgumentList);
            Assert.Equal(new[] { "datasets", "create", "-p", first.DatasetDirectory, "--dir-mode", "zip", "-q" }, runner.Specs[1].ArgumentList);
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

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

    private sealed record ScriptedRun(int ExitCode, string Output);

    private sealed class ScriptedProcessRunner : IProcessRunner
    {
        private readonly Queue<ScriptedRun> _runs;

        public ScriptedProcessRunner(params ScriptedRun[] runs)
        {
            _runs = new Queue<ScriptedRun>(runs);
        }

        public List<ProcessStartSpec> Specs { get; } = new();

        public Task<ProcessRunResult> RunAsync(
            ProcessStartSpec spec,
            Action<string>? onOutput,
            CancellationToken cancellationToken)
        {
            Specs.Add(spec);
            var run = _runs.Count > 0 ? _runs.Dequeue() : new ScriptedRun(0, "ok");
            onOutput?.Invoke(run.Output);
            return Task.FromResult(new ProcessRunResult(run.ExitCode, cancellationToken.IsCancellationRequested, run.Output));
        }
    }
}
