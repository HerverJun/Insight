# InsightV4 Architecture Governance

Date: 2026-06-09
Branch: `codex/refactor-prep`

## Governance Goal

InsightV4 should evolve from a feature-heavy desktop tool into a training platform shell with stable extension points. Local YOLO training, Kaggle cloud YOLO training, SAM labeling, ONNX validation, and deployment package export must plug into the same application boundaries instead of growing inside WebView handlers or monolithic services.

## Target Layers

1. Presentation
   - WinForms shell, WebView2 hosting, `index.html`, and frontend action dispatch.
   - This layer keeps action names stable and owns no training, ONNX, Kaggle, or file-system orchestration.

2. Application
   - Use-case orchestration, request validation, user-facing progress messages, and run/job state transitions.
   - This layer coordinates providers and stores; it does not directly start `Process` or call cloud APIs.

3. Domain
   - Dataset versions, QA reports, training runs, model registry, evaluation reports, package descriptors, provider-neutral job models.
   - This layer must stay independent of WebView2, WinForms, external processes, and Kaggle SDK details.

4. Infrastructure
   - File-system stores, process runner, ONNX Runtime adapter, WebView frontend messenger, Kaggle API client, secrets store.
   - This layer is replaceable in tests.

5. Training Providers
   - `local-yolo`: local Python/Ultralytics provider.
   - `kaggle-yolo`: Kaggle CLI-backed cloud provider for prepared dataset versions, notebooks, status polling, output downloads, and artifact return.

## First-Round Boundaries

- WebView protocol remains source-compatible: existing action names must not change without contract tests.
- New request payloads should move toward typed DTO binding through `WebViewPayloadBinder`.
- User-specific configuration must move under the Insight user data root instead of the publish output directory.
- External process execution must go through `IProcessRunner`.
- Cloud API integration must go through dedicated client interfaces such as `IKaggleClient`.
- Training implementations must be discoverable through `ITrainingProvider` descriptors and a provider catalog.

## Provider Contract

A training provider owns how a job is launched and monitored. It does not own model registry, dataset QA, evaluation, or deployment packaging.

Required provider responsibilities:

- Report descriptor metadata: id, display name, kind, capabilities.
- Start a job from a provider-neutral request.
- Stream logs and metric updates to an observer.
- Return final job state, exit code, failure reason, and primary artifact path if available.

Current providers:

- `local-yolo`: uses `ITrainingEngine` plus `IProcessRunner`.
- `kaggle-yolo`: uses `IKaggleClient` to test credentials, stage the selected `.insight` dataset version, create/version a Kaggle Dataset, push a Kaggle Notebook kernel, poll status, download output, and return `best.pt`, ONNX, metrics/report artifacts to the industrial training run.

Provider outputs flow back through `IndustrialTrainingService`. The service remains responsible for run/job state, evaluation report generation, model registry entries, ONNX smoke testing, and delivery package export.

## Storage Policy

- User configuration: `%APPDATA%/Insight/config`
- Local caches and WebView user data: `%LOCALAPPDATA%/Insight`
- Cloud job staging and downloaded cloud artifacts: `%LOCALAPPDATA%/Insight/cloud-jobs`
- Project-scoped durable artifacts: `<projectRoot>/.insight`
- Secrets: never store tokens in project files. Kaggle credentials are stored through `ProtectedFileSecretsStore` under the user config root using the current Windows user protection scope.

## Regression Gates

Required before merging architecture changes:

```powershell
dotnet restore Insight.sln
dotnet build Insight.sln -c Debug --no-restore
dotnet build Insight.sln -c Release --no-restore
dotnet test Insight.sln -c Debug --no-build --verbosity minimal
dotnet test Insight.sln -c Release --no-build --verbosity minimal
```

`dotnet format Insight.sln --verify-no-changes` remains a desired gate, but the current workstation cannot run it because the format tool cannot locate MSBuild even though `dotnet msbuild` works.

## First-Round Outcome

- `InsightAppPaths` now centralizes user config, local data, WebView2 user data, project `.insight` roots, and cloud job paths.
- `IProcessRunner` is the external process boundary for local training, industrial training export, model conversion, and Kaggle CLI integration.
- `IKaggleClient` isolates Kaggle submission/status/output behavior from WebView command handling and application orchestration.
- `ITrainingProvider`, `TrainingProviderCatalog`, provider-neutral job records, and file-backed job storage define the expansion point for local and cloud training providers.
- `WebViewPayloadBinder` and typed payload DTOs now protect high-risk settings, model, and Kaggle command paths while preserving existing frontend action names.
- Regression coverage includes protocol action parity, typed payload validation, process runner behavior, Kaggle CLI boundary behavior, provider catalog rules, job store persistence, industrial training process/job synchronization, and model conversion runner invocation.

## Kaggle Provider v1

`kaggle-yolo` is selected from the professional training workflow with `providerId = "kaggle-yolo"` and provider options:

- `kaggleUsername`: Kaggle account name used for dataset and kernel ids.
- `datasetSlug`: target Kaggle Dataset slug. Existing datasets are versioned when create fails.
- `kernelSlug`: target Kaggle Notebook slug. `kaggle kernels push` creates or updates the notebook.
- `pollIntervalSeconds` and `pollTimeoutMinutes`: status polling controls.

The provider stages the existing dataset version directory rather than re-splitting raw sources. `KaggleCliClient.SubmitPreparedTrainingAsync` copies the selected `DatasetVersion.YoloRoot`, rewrites Kaggle-safe `data.yaml`, writes `training_config.json`, uploads the dataset with `--dir-mode zip`, writes kernel metadata with dataset attachment, pushes `YOLO_TRAIN_KaggleDataset.ipynb`, and returns dataset/kernel ids. After the kernel reaches a terminal state, provider output is downloaded into the run root, artifacts are discovered recursively, and the existing completion path registers the model and keeps evaluation/package flows unchanged.

## Kaggle Provider v1.1

Cloud state machine:

- Connection test runs `kaggle --version` and an authenticated `datasets list`, returning `KaggleConnectionErrorKind` values for CLI unavailable, missing credentials, unauthorized, network, rate limit, and unknown failures.
- Submission stages to a deterministic job root based on `jobId`, writes `submission-result.json`, and reuses that metadata for repeat submissions of the same job.
- Dataset create/version, kernel push, status, and output download go through `IKaggleClient` and `IProcessRunner` with sanitized output and bounded retry for network/rate-limit failures.
- Provider polling maps Kaggle CLI text into `KaggleKernelState`: Submitted, Queued, Running, Completed, Failed, Canceled, TimedOut, Unknown.
- Timeout and terminal failure return failed provider results with diagnostic metadata; local cancellation marks the Insight run stopped but still does not remotely terminate an already-running Kaggle kernel.

Failure recovery:

- `ITrainingProvider.RecoverAsync` is the provider-owned recovery boundary.
- `IndustrialTrainingService.HandleGetTrainingRuns` attempts recovery for persisted `kaggle-yolo` runs in Preparing, Running, Evaluating, or Exporting states.
- Recovery rebuilds provider options from the stored run config plus `ProviderMetadata`, polls the persisted `kernelId`, downloads artifacts on completion, and then uses the normal evaluation/model-registration path.
- Failed recovery marks the run and provider-neutral job failed with the recovery reason in the log and job record.

Artifact trust boundary:

- Kaggle output is untrusted until the provider writes and verifies `insight-kaggle-artifacts.json`.
- The manifest records relative path, absolute path, format, length, and SHA-256 for discovered artifacts.
- The provider requires `best.pt`, an ONNX artifact, and at least one metrics/report artifact before returning Completed.
- `IndustrialTrainingService` verifies provider-supplied `bestPtSha256` and `onnxSha256` before registering a model.
- Delivery package export still uses the project-scoped model registry copy, not arbitrary cloud output paths.

## Remaining Governance Backlog

- Continue migrating dataset, project, SAM, and industrial-training WebView payload parsing from raw `JsonElement` to typed DTOs.
- Split large application services by use case once the provider boundary is stable enough to avoid churn.
- Add optional remote cancel/delete support when a Kaggle API adapter is available; v1.1 maps local cancellation to stopped run state but does not terminate an already-running Kaggle kernel remotely.
- Add richer cloud progress parsing from Kaggle logs/results beyond coarse kernel state polling.
- Add artifact manifest signing if cloud artifacts become part of regulated release evidence.
