# InsightV4 Refactor Prep

Date: 2026-05-15
Branch: `codex/refactor-prep`
Base commit: `472d076 clean baseline`
Canonical refactor base: local baseline (`472d076 clean baseline`)

## Current Baseline

- Project type: .NET WinForms app targeting `net8.0-windows`.
- UI shell: `Microsoft.Web.WebView2` loading embedded `index.html`.
- Core native/ML dependencies:
  - `Microsoft.ML.OnnxRuntime.DirectML` 1.17.1
  - `Microsoft.Web.WebView2` 1.0.3650.58
  - `OpenCvSharp4` 4.9.0.20240103
  - `OpenCvSharp4.runtime.win` 4.9.0.20240103
- Build baseline:
  - `dotnet restore Insight.sln`: pass
  - `dotnet build Insight.sln --no-restore`: pass, 0 warnings, 0 errors
  - `dotnet build Insight.sln -c Release --no-restore`: pass, 0 warnings, 0 errors
- Test baseline:
  - `Insight.Tests` has been added as the first refactor safety net.
  - `dotnet test Insight.sln --no-build --verbosity minimal`: pass, 1 test.
  - Current test coverage starts with the WebView action contract between `Insight.cs` and `index.html`.
- Tooling note:
  - `dotnet msbuild -version` works.
  - `dotnet format Insight.sln --verify-no-changes` currently fails with "unable to find MSBuild" in this environment, so formatting verification needs a tooling fix before it can be used as a gate.

## Git State

- The refactor prep branch was created from local `main`.
- Local `main` has one unique commit: `472d076 clean baseline`.
- `origin/main` is ahead with multiple product commits and has diverged from local `main`.
- Canonical refactor base decision: use the local baseline, not `origin/main`.
- `origin/main` is useful as reference material, but it is not the source of truth for this refactor.

Remote delta from this local base includes changes to:

- `.gitignore`
- `DefectAugmentationService.cs`
- `Insight.cs`
- `Insight.csproj`
- `OnnxProjectManager.cs`
- `ProjectManager.cs`
- `YoloDatasetExporter.cs`
- `index.html`
- removal of `KaggleCloudTrainer.cs`
- addition of `DefectAugmentationGuide.md`
- addition of `index.broken_20260225_154317.html`

## Hotspots

- `index.html`: about 4402 lines, about 190 KB. It currently holds UI markup, styles, WebView message calls, chart logic, model list logic, cloud training UI, and labeling canvas logic in one file.
- `Insight.cs`: about 2650 lines. It currently owns the WinForms shell, WebView initialization, message dispatch, dataset generation, training process orchestration, Kaggle handling, model management, SAM integration, file dialogs, and frontend messaging.
- `SAM2Service.cs`: about 633 lines. It mixes ONNX session management, preprocessing, inference, coordinate transforms, and diagnostics.
- `YoloDatasetExporter.cs`: about 595 lines. It owns export orchestration and file processing.
- `DefectAugmentationService.cs`: about 500 lines. It owns defect synthesis, label parsing, JSON writing, and parallel image processing.
- `KaggleCloudTrainer.cs`: about 383 lines locally, but is deleted on `origin/main`; this needs an explicit product decision before refactoring cloud training.

## WebView Contract Snapshot

Backend action cases and frontend `postMessage` action names currently match.

Action count: 34

Key action groups:

- Project management: `get_projects`, `create_project`, `delete_project`, `get_subfolders`
- Dataset generation/export: `generate`, `export_dataset_zip`, `defect_augmentation`
- Training: `start_training`, `stop_training`, `get_training_history`
- Kaggle cloud: `start_kaggle_training`, `download_kaggle_output`
- Model management: `get_models`, `rename_model`, `delete_model`, `convert_model`, `open_model_folder`
- Tool conversion: `convert_tool`, `open_output`
- ONNX projects: `get_onnx_projects`, `create_onnx_project`, `delete_onnx_project`, `get_onnx_models`
- SAM labeling: `get_sam_models`, `load_sam_model`, `encode_image`, `sam_inference`, `save_annotations`, `get_images_in_folder`, `get_image_data`
- Config/file selection: `select_file`, `select_folder`, `get_default_python_path`, `save_default_python_path`

## Recommended Refactor Order

1. Establish safety rails.
   - Add a small test project. Done: `Insight.Tests`.
   - Add WebView action contract tests. Done: `WebViewActionContractTests`.
   - Add focused tests around config persistence and path handling.
   - Keep Debug and Release build as required gates.

2. Split backend message routing.
   - Extract WebView message parsing and dispatch from `Insight.cs`.
   - Replace the giant `switch` with a handler registry or typed command handlers.
   - Preserve all existing action names first; rename only after tests exist.

3. Extract application services from `Insight.cs`.
   - Training process orchestration.
   - Dataset generation.
   - Model storage/history management.
   - Python path configuration.
   - File/folder shell operations.
   - SAM labeling bridge.

4. Split frontend assets.
   - Move CSS, shared UI helpers, chart logic, training UI, model UI, project UI, cloud UI, and labeling canvas logic out of the monolithic `index.html`.
   - Decide whether to keep a simple embedded static frontend or introduce a build step.

5. Normalize configuration/storage.
   - Centralize config file paths.
   - Separate app config, project config, ONNX project config, model metadata, and training history.
   - Avoid writing user state into build output when possible.

6. Revisit external process boundaries.
   - Isolate Python/YOLO command construction.
   - Add command preview/logging with redaction.
   - Make cancellation and process cleanup explicit.

7. Clean up ML integration.
   - Isolate ONNX runtime session lifetime.
   - Separate preprocessing/postprocessing from session execution.
   - Add CPU/GPU fallback behavior tests where practical.

## Immediate Decisions Needed

- Should Kaggle cloud training remain in the product? Local branch has it; `origin/main` deletes `KaggleCloudTrainer.cs`.
- Should the frontend stay as embedded static HTML, or should it move to a small frontend build pipeline?
- Should the project be reorganized into folders first, or should tests be added before moving files?

## Regression Checklist

- Debug build passes.
- Release build passes.
- WebView initializes and loads the UI.
- All 34 frontend actions still dispatch.
- Project list load/create/delete works.
- Dataset generation works on a tiny sample.
- Training command generation works without starting a long real training run.
- Stop training cancels the active process.
- Model list/rename/delete/convert flows work.
- SAM model load, image encode, and inference report clear errors when models are absent.
- Annotation save/load round trip works on a sample image.
- Config files are read and written in the intended location.
