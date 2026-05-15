using System.Text.RegularExpressions;
using Insight.Bridge;
using Insight.Handlers;
using Insight.Services;

namespace Insight.Tests;

public class WebViewActionContractTests
{
    [Fact]
    public void FrontendActionsMatchKnownProtocolActions()
    {
        var root = FindRepositoryRoot();
        var frontendSource = File.ReadAllText(Path.Combine(root, "index.html"));

        var backendActions = ToSortedSet(WebViewActions.Incoming);
        var frontendActions = ExtractFrontendActions(frontendSource);

        Assert.NotEmpty(backendActions);
        Assert.NotEmpty(frontendActions);
        Assert.Contains("start_training", backendActions);
        Assert.Contains("start_training", frontendActions);
        Assert.Equal(backendActions, frontendActions);
    }

    [Fact]
    public void DispatcherRegistryCoversEveryKnownProtocolAction()
    {
        var messenger = new RecordingFrontendMessenger();
        var ui = new ImmediateUiDispatcher();
        using var app = new InsightApplication(messenger, ui, new NullDialogService());
        var dispatcher = WebViewCompositionRoot.CreateDispatcher(app, messenger);

        var expected = ToSortedSet(WebViewActions.Incoming);
        var registered = ToSortedSet(dispatcher.RegisteredActions);

        Assert.Equal(expected, registered);
        Assert.Equal(registered.Count, dispatcher.RegisteredActions.Count);
    }

    [Fact]
    public async Task DispatcherReportsUnknownActionsWithoutThrowing()
    {
        var messenger = new RecordingFrontendMessenger();
        var ui = new ImmediateUiDispatcher();
        using var app = new InsightApplication(messenger, ui, new NullDialogService());
        var dispatcher = WebViewCompositionRoot.CreateDispatcher(app, messenger);

        await dispatcher.DispatchAsync("""{"action":"unknown_action"}""", CancellationToken.None);

        Assert.Contains(messenger.Errors, error => error.Contains("未知前端指令"));
    }

    private static SortedSet<string> ExtractFrontendActions(string source)
    {
        return ExtractMatches(source, "action\\s*:\\s*['\"]([^'\"]+)['\"]");
    }

    private static SortedSet<string> ExtractMatches(string source, string pattern)
    {
        var values = Regex.Matches(source, pattern)
            .Select(match => match.Groups[1].Value);

        return ToSortedSet(values);
    }

    private static SortedSet<string> ToSortedSet(IEnumerable<string> values)
    {
        return new SortedSet<string>(values, StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            var hasSolution = File.Exists(Path.Combine(directory.FullName, "Insight.sln"));
            var hasBackend = File.Exists(Path.Combine(directory.FullName, "Insight.cs"));
            var hasFrontend = File.Exists(Path.Combine(directory.FullName, "index.html"));

            if (hasSolution && hasBackend && hasFrontend)
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the InsightV4 repository root.");
    }

    private sealed class RecordingFrontendMessenger : IFrontendMessenger
    {
        public List<object> Messages { get; } = new();
        public List<string> Errors { get; } = new();

        public void Send(object data)
        {
            Messages.Add(data);
        }

        public void Log(string message, string type = "info")
        {
            Messages.Add(new { action = "log", message, type });
        }

        public void Error(string message)
        {
            Errors.Add(message);
            Messages.Add(new { action = "error", message });
        }

        public void Complete(string message)
        {
            Messages.Add(new { action = "complete", message });
        }
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
}
