using System.Text.Json;

namespace Insight
{
    public class ProjectConfig
    {
        public string Name { get; set; } = "";
        public string RootPath { get; set; } = "";
        public List<string> Classes { get; set; } = new();
    }

    public class ProjectManager
    {
        private const string ConfigFile = "projects.json";

        public static List<ProjectConfig> LoadProjects()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFile);
                if (!File.Exists(path))
                {
                    // Create default dummy project if not exists
                    var defaults = new List<ProjectConfig>
                    {
                        new ProjectConfig
                        {
                            Name = "示例项目",
                            RootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExampleProject"),
                            Classes = new List<string> { "ok", "ng" }
                        }
                    };
                    SaveProjects(defaults);
                    return defaults;
                }

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<ProjectConfig>>(json) ?? new List<ProjectConfig>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading projects: {ex.Message}");
                return new List<ProjectConfig>();
            }
        }

        public static void SaveProjects(List<ProjectConfig> projects)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFile);
            var json = JsonSerializer.Serialize(projects, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public static List<string> GetSubFolders(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath)) return new List<string>();

            try
            {
                return Directory.GetDirectories(rootPath)
                                .Select(Path.GetFileName)
                                .Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith("."))
                                .ToList()!;
            }
            catch
            {
                return new List<string>();
            }
        }

        public static void AddProject(ProjectConfig project)
        {
            var current = LoadProjects();
            current.Add(project);
            SaveProjects(current);
        }

        public static void DeleteProject(int index)
        {
            var current = LoadProjects();
            if (index >= 0 && index < current.Count)
            {
                current.RemoveAt(index);
                SaveProjects(current);
            }
        }
    }
}
