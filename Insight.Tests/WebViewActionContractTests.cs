using System.Text.RegularExpressions;

namespace Insight.Tests;

public class WebViewActionContractTests
{
    [Fact]
    public void FrontendActionsMatchBackendMessageHandlers()
    {
        var root = FindRepositoryRoot();
        var backendSource = File.ReadAllText(Path.Combine(root, "Insight.cs"));
        var frontendSource = File.ReadAllText(Path.Combine(root, "index.html"));

        var backendActions = ExtractBackendActions(backendSource);
        var frontendActions = ExtractFrontendActions(frontendSource);

        Assert.NotEmpty(backendActions);
        Assert.NotEmpty(frontendActions);
        Assert.Contains("start_training", backendActions);
        Assert.Contains("start_training", frontendActions);
        Assert.Equal(backendActions, frontendActions);
    }

    private static SortedSet<string> ExtractBackendActions(string source)
    {
        return ExtractMatches(source, "case\\s+\"([^\"]+)\"\\s*:");
    }

    private static SortedSet<string> ExtractFrontendActions(string source)
    {
        return ExtractMatches(source, "action\\s*:\\s*['\"]([^'\"]+)['\"]");
    }

    private static SortedSet<string> ExtractMatches(string source, string pattern)
    {
        var values = Regex.Matches(source, pattern)
            .Select(match => match.Groups[1].Value);

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
}
