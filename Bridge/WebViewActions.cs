namespace Insight.Bridge
{
    public static class WebViewActions
    {
        public static readonly string[] Incoming =
        {
            SelectFolder,
            SelectFile,
            Generate,
            StartTraining,
            StopTraining,
            ExportDatasetZip,
            StartKaggleTraining,
            DownloadKaggleOutput,
            OpenOutput,
            DefectAugmentation,
            CreateProject,
            DeleteProject,
            ConvertTool,
            GetProjects,
            GetSubfolders,
            GetOnnxProjects,
            CreateOnnxProject,
            DeleteOnnxProject,
            GetOnnxModels,
            GetSamModels,
            GetModels,
            DeleteModel,
            RenameModel,
            ConvertModel,
            GetTrainingHistory,
            OpenModelFolder,
            SaveDefaultPythonPath,
            GetDefaultPythonPath,
            LoadSamModel,
            EncodeImage,
            SamInference,
            SaveAnnotations,
            GetImagesInFolder,
            GetImageData
        };

        public const string SelectFolder = "select_folder";
        public const string SelectFile = "select_file";
        public const string Generate = "generate";
        public const string StartTraining = "start_training";
        public const string StopTraining = "stop_training";
        public const string ExportDatasetZip = "export_dataset_zip";
        public const string StartKaggleTraining = "start_kaggle_training";
        public const string DownloadKaggleOutput = "download_kaggle_output";
        public const string OpenOutput = "open_output";
        public const string DefectAugmentation = "defect_augmentation";
        public const string CreateProject = "create_project";
        public const string DeleteProject = "delete_project";
        public const string ConvertTool = "convert_tool";
        public const string GetProjects = "get_projects";
        public const string GetSubfolders = "get_subfolders";
        public const string GetOnnxProjects = "get_onnx_projects";
        public const string CreateOnnxProject = "create_onnx_project";
        public const string DeleteOnnxProject = "delete_onnx_project";
        public const string GetOnnxModels = "get_onnx_models";
        public const string GetSamModels = "get_sam_models";
        public const string GetModels = "get_models";
        public const string DeleteModel = "delete_model";
        public const string RenameModel = "rename_model";
        public const string ConvertModel = "convert_model";
        public const string GetTrainingHistory = "get_training_history";
        public const string OpenModelFolder = "open_model_folder";
        public const string SaveDefaultPythonPath = "save_default_python_path";
        public const string GetDefaultPythonPath = "get_default_python_path";
        public const string LoadSamModel = "load_sam_model";
        public const string EncodeImage = "encode_image";
        public const string SamInference = "sam_inference";
        public const string SaveAnnotations = "save_annotations";
        public const string GetImagesInFolder = "get_images_in_folder";
        public const string GetImageData = "get_image_data";

        public static class Outgoing
        {
            public const string ProjectsLoaded = "projects_loaded";
            public const string SubfoldersLoaded = "subfolders_loaded";
            public const string OnnxProjectsLoaded = "onnx_projects_loaded";
        }
    }
}
