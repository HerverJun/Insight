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
            Map(WebViewActions.GetTrainingHistory, _ => app.HandleGetTrainingHistory());
        }
    }
}
