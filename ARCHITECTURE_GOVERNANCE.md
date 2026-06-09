# InsightV4 Architecture Governance

Date: 2026-06-09
Branch: `codex/refactor-prep`

## Governance Goal

InsightV4 should evolve from a feature-heavy desktop tool into a training platform shell with stable extension points. Local YOLO training, SAM labeling, ONNX validation, deployment package export, and future Kaggle API training must plug into the same application boundaries instead of growing inside WebView handlers or monolithic services.

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
   - `kaggle-api`: future cloud provider for Kaggle kernels, datasets, artifacts, and job polling.

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

Planned providers:

- `local-yolo`: uses `ITrainingEngine` plus `IProcessRunner`.
- `kaggle-api`: will use `IKaggleClient`, `ISecretsStore`, and artifact download/polling adapters.

## Storage Policy

- User configuration: `%APPDATA%/Insight/config`
- Local caches and WebView user data: `%LOCALAPPDATA%/Insight`
- Cloud job staging and downloaded cloud artifacts: `%LOCALAPPDATA%/Insight/cloud-jobs`
- Project-scoped durable artifacts: `<projectRoot>/.insight`
- Secrets: never store tokens in project files; use a secrets abstraction before adding Kaggle API credentials.

## Regression Gates

Required before merging architecture changes:

```powershell
dotnet restore Insight.sln
dotnet build Insight.sln --no-restore
dotnet build Insight.sln -c Release --no-restore
dotnet test Insight.sln --no-build --verbosity minimal
```

`dotnet format Insight.sln --verify-no-changes` remains a desired gate, but the current workstation cannot run it because the format tool cannot locate MSBuild even though `dotnet msbuild` works.

## First-Round Outcome

- `InsightAppPaths` now centralizes user config, local data, WebView2 user data, project `.insight` roots, and cloud job paths.
- `IProcessRunner` is the external process boundary for local training, industrial training export, model conversion, and Kaggle CLI integration.
- `IKaggleClient` isolates Kaggle submission/status/output behavior from WebView command handling and application orchestration.
- `ITrainingProvider`, `TrainingProviderCatalog`, provider-neutral job records, and file-backed job storage define the expansion point for local and cloud training providers.
- `WebViewPayloadBinder` and typed payload DTOs now protect high-risk settings, model, and Kaggle command paths while preserving existing frontend action names.
- Regression coverage includes protocol action parity, typed payload validation, process runner behavior, Kaggle CLI boundary behavior, provider catalog rules, job store persistence, industrial training process/job synchronization, and model conversion runner invocation.

## Remaining Governance Backlog

- Continue migrating dataset, project, SAM, and industrial-training WebView payload parsing from raw `JsonElement` to typed DTOs.
- Split large application services by use case once the provider boundary is stable enough to avoid churn.
- Replace placeholder secrets/artifact abstractions with concrete implementations before adding Kaggle API-token based flows.
- Add a real `kaggle-api` provider after the API client, secrets store, artifact store, and polling lifecycle are concrete.
