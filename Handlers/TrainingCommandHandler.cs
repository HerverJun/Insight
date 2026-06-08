using Insight.Bridge;
using Insight.Services;

namespace Insight.Handlers
{
    public sealed class TrainingCommandHandler : WebViewCommandHandlerBase
    {
        public TrainingCommandHandler(InsightApplication app)
        {
            Map(WebViewActions.StartTraining, (request, ct) => app.HandleStartTrainingAsync(request.Payload));
            Map(WebViewActions.StopTraining, _ => app.HandleStopTraining());
            Map(WebViewActions.CreateDatasetVersion, (request, ct) => app.HandleCreateDatasetVersionAsync(request.Payload));
            Map(WebViewActions.ValidateDataset, request => app.HandleValidateDataset(request.Payload));
            Map(WebViewActions.StartTrainingRun, (request, ct) => app.HandleStartTrainingRunAsync(request.Payload));
            Map(WebViewActions.ResumeTrainingRun, (request, ct) => app.HandleResumeTrainingRunAsync(request.Payload));
            Map(WebViewActions.StopTrainingRun, _ => app.HandleStopTrainingRun());
            Map(WebViewActions.GetTrainingRuns, request => app.HandleGetTrainingRuns(request.Payload));
            Map(WebViewActions.GetTrainingRunDetail, request => app.HandleGetTrainingRunDetail(request.Payload));
            Map(WebViewActions.GetEvaluationReport, request => app.HandleGetEvaluationReport(request.Payload));
            Map(WebViewActions.PromoteModel, request => app.HandlePromoteModel(request.Payload));
            Map(WebViewActions.BenchmarkModel, request => app.HandleBenchmarkModel(request.Payload));
            Map(WebViewActions.RunInferencePreview, request => app.HandleRunInferencePreview(request.Payload));
            Map(WebViewActions.RunModelEvaluation, request => app.HandleRunModelEvaluation(request.Payload));
            Map(WebViewActions.ExportModelPackage, request => app.HandleExportModelPackage(request.Payload));
            Map(WebViewActions.GetTrainingHistory, _ => app.HandleGetTrainingHistory());
        }
    }
}
