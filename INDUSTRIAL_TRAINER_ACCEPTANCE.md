# InsightV4 Industrial Trainer Acceptance

## Delivered Scope

- Local industrial detection trainer workbench.
- Dataset versions with manifest, YOLO materialization, train/val/test split, QA report, class counts, duplicate detection, missing/empty labels, unknown classes, invalid labels, out-of-bounds boxes, zero-area boxes, bad images, and class imbalance warnings.
- Typed training run config with persisted config, environment snapshot, command audit file, UTF-8 log, JSONL metrics, stop/resume protocol, failure reason, evaluation report, ONNX export, ONNX smoke test, and model registry.
- Model registry with Candidate/Production/Archived style status promotion, production pinning, model card, PT/ONNX paths, class list, evaluation report path, and ONNX load benchmark.
- Real ONNX inference preview with image selection, confidence/NMS thresholds, YOLO output parsing, class-aware NMS, annotated image output, preview report JSON, and model-card traceability.
- Full model acceptance evaluation center with split-aware batch inference, confidence threshold sweep, Precision/Recall/F1, per-class metrics, false-positive/false-negative sample gallery, readiness gates, and recommended production threshold.
- Platform deployment package export for ClearVision and generic ONNX Runtime platforms, including ONNX/PT artifacts, labels contract, SHA-256 hashes, ClearVision model manifest, model catalog entry, platform descriptor, and evidence reports.
- WebView protocol actions for the professional trainer workbench:
  - `create_dataset_version`
  - `validate_dataset`
  - `start_training_run`
  - `resume_training_run`
  - `stop_training_run`
  - `get_training_runs`
  - `get_training_run_detail`
  - `get_evaluation_report`
  - `promote_model`
  - `benchmark_model`
  - `run_inference_preview`
  - `run_model_evaluation`
  - `export_model_package`

## Manual Acceptance Flow

1. Open InsightV4 and select a project.
2. In the existing task config page, select one or more source folders and confirm classes are configured.
3. Open the `专业训练` page.
4. Click `创建版本`.
5. Confirm the dataset table shows a new version and the QA summary reports image count, label count, issue count, and recommendation.
6. Select the dataset version, choose a model and epochs, then click `启动训练`.
7. Watch the experiment table and logs. `停止` should terminate the active process and persist a stopped run.
8. Re-open the app and confirm dataset versions, runs, and model registry entries remain available.
9. After a successful run, confirm:
   - `.insight/runs/<runId>/config.json`
   - `.insight/runs/<runId>/environment.json`
   - `.insight/runs/<runId>/train_command.txt`
   - `.insight/runs/<runId>/train.log`
   - `.insight/runs/<runId>/evaluation_report.json`
   - `.insight/models/<runId>/model_card.json`
10. Use `Benchmark` on a registered model to verify ONNX loads and records input/output metadata.
11. Use `生产` to pin a model as Production and confirm `model_card.json` is updated.
12. Select a preview image, set confidence/NMS thresholds, click `预览`, and confirm:
   - `.insight/previews/<date>/preview_*.jpg`
   - `.insight/previews/<date>/preview_*.json`
   - the model card records the latest preview paths
13. Use `验收` on a registered model and confirm:
   - `.insight/evaluations/<evalId>/model_evaluation_report.json`
   - `.insight/evaluations/<evalId>/error_gallery/*.jpg` when false-positive/false-negative samples exist
   - the report includes threshold sweep, per-class metrics, readiness gates, and recommendation
   - the model card records `LastEvaluationReportPath` and `LastReadinessStatus`
14. Use `导出` on a registered model and confirm the package contains:
   - `model.onnx`
   - `labels.txt`
   - `model_catalog.json`
   - `clearvision.model.manifest.json`
   - `platform-package.json`
   - `reports/evaluation_report.json` when evaluation evidence exists

## Automated Gates

Run these before release:

```powershell
dotnet restore Insight.sln
dotnet build Insight.sln --no-restore
dotnet build Insight.sln -c Release --no-restore
dotnet test Insight.sln --no-build --verbosity minimal
```

## Notes

- A real training run still requires a valid Python environment with `ultralytics` installed and a working device selection such as `0` or `cpu`.
- If Python, Ultralytics, GPU, data path, or ONNX export fails, the run persists its failure reason and UTF-8 log under `.insight/runs/<runId>`.
