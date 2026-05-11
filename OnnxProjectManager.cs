using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using System;
using System.Linq;

namespace Insight
{
    public class OnnxProjectConfig
    {
        public string Name { get; set; } = "";
        public string RootPath { get; set; } = "";
    }

    public class OnnxProjectManager
    {
        private const string ConfigFile = "onnx_projects.json";

        public static List<OnnxProjectConfig> LoadProjects()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFile);
                if (!File.Exists(path))
                {
                    // Create default dummy project if not exists
                    var defaults = new List<OnnxProjectConfig>
                    {
                        new OnnxProjectConfig
                        {
                            Name = "示例ONNX模型池",
                            RootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExampleOnnxProject")
                        }
                    };
                    SaveProjects(defaults);
                    return defaults;
                }

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<OnnxProjectConfig>>(json) ?? new List<OnnxProjectConfig>();
            }
            catch (Exception ex)
            {
                // 记录加载失败错误，实际生产环境建议记录到文件日志
                return new List<OnnxProjectConfig>();
            }
        }

        public static void SaveProjects(List<OnnxProjectConfig> projects)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFile);
            var json = JsonSerializer.Serialize(projects, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public static void AddProject(OnnxProjectConfig project)
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
