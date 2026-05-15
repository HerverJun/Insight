namespace Insight.Bridge
{
    public interface IAppDialogService
    {
        string? SelectFolder(string type);
        string? SelectPythonFile(string type);
        string? SelectDatasetZipPath(string? projectName, string yoloVersion);
        void ShowWarning(string message, string title);
        void ShowError(string message, string title);
        void OpenFolder(string path);
        void OpenFileInExplorer(string path);
    }
}
