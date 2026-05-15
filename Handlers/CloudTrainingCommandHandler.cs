using Insight.Bridge;
using Insight.Services;

namespace Insight.Handlers
{
    public sealed class CloudTrainingCommandHandler : WebViewCommandHandlerBase
    {
        public CloudTrainingCommandHandler(InsightApplication app)
        {
            Map(WebViewActions.StartKaggleTraining, (request, ct) => app.HandleStartKaggleTrainingAsync(request.Payload));
            Map(WebViewActions.DownloadKaggleOutput, (request, ct) => app.HandleDownloadKaggleOutputAsync(request.Payload));
        }
    }
}
