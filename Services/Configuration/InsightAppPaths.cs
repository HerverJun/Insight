namespace Insight.Services.Configuration
{
    public sealed class InsightAppPaths
    {
        public InsightAppPaths(string userConfigRoot, string localDataRoot)
        {
            UserConfigRoot = Path.GetFullPath(userConfigRoot);
            LocalDataRoot = Path.GetFullPath(localDataRoot);
        }

        public string UserConfigRoot { get; }
        public string LocalDataRoot { get; }
        public string ConfigRoot => Path.Combine(UserConfigRoot, "config");
        public string WebViewUserDataRoot => Path.Combine(LocalDataRoot, "WebView2");
        public string CloudJobsRoot => Path.Combine(LocalDataRoot, "cloud-jobs");
        public string SecretsRoot => Path.Combine(UserConfigRoot, "secrets");
        public string PythonConfigPath => Path.Combine(ConfigRoot, "python_config.json");
        public string TrainingHistoryPath => Path.Combine(ConfigRoot, "training_history.json");

        public static InsightAppPaths CreateDefault()
        {
            var roamingRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return new InsightAppPaths(
                Path.Combine(roamingRoot, "Insight"),
                Path.Combine(localRoot, "Insight"));
        }

        public string GetProjectInsightRoot(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                throw new ArgumentException("Project root is required.", nameof(projectRoot));
            }

            return Path.Combine(Path.GetFullPath(projectRoot), ".insight");
        }

        public void EnsureUserRoots()
        {
            Directory.CreateDirectory(ConfigRoot);
            Directory.CreateDirectory(LocalDataRoot);
            Directory.CreateDirectory(CloudJobsRoot);
        }
    }
}
