namespace Insight.Bridge
{
    public sealed class WinFormsDialogService : IAppDialogService
    {
        private readonly IWin32Window _owner;
        private readonly IUiDispatcher _ui;

        public WinFormsDialogService(IWin32Window owner, IUiDispatcher ui)
        {
            _owner = owner;
            _ui = ui;
        }

        public string? SelectFolder(string type)
        {
            return _ui.Invoke(() =>
            {
                using var dialog = new FolderBrowserDialog
                {
                    RootFolder = Environment.SpecialFolder.Desktop,
                    ShowNewFolderButton = true,
                    UseDescriptionForTitle = true,
                    Description = type switch
                    {
                        "source" => "选择源文件夹",
                        "newProjectRoot" => "选择项目根目录",
                        _ => "选择保存位置"
                    }
                };

                return dialog.ShowDialog(_owner) == DialogResult.OK ? dialog.SelectedPath : null;
            });
        }

        public string? SelectPythonFile(string type)
        {
            return _ui.Invoke(() =>
            {
                using var dialog = new OpenFileDialog
                {
                    Filter = "Python Executable|python.exe|All Files|*.*",
                    Title = "选择 Python 解释器"
                };

                return dialog.ShowDialog(_owner) == DialogResult.OK ? dialog.FileName : null;
            });
        }

        public string? SelectDatasetZipPath(string? projectName, string yoloVersion)
        {
            return _ui.Invoke(() =>
            {
                using var dialog = new SaveFileDialog
                {
                    Filter = "ZIP Compression (*.zip)|*.zip",
                    Title = "导出并保存 YOLO 训练数据集 ZIP",
                    FileName = $"{projectName}_{yoloVersion}_dataset.zip"
                };

                return dialog.ShowDialog(_owner) == DialogResult.OK ? dialog.FileName : null;
            });
        }

        public void ShowWarning(string message, string title)
        {
            _ui.Post(() => MessageBox.Show(_owner, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }

        public void ShowError(string message, string title)
        {
            _ui.Post(() => MessageBox.Show(_owner, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error));
        }

        public void OpenFolder(string path)
        {
            System.Diagnostics.Process.Start("explorer.exe", path);
        }

        public void OpenFileInExplorer(string path)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
    }
}
