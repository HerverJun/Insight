using Insight.Bridge;
using Insight.Services;

namespace Insight.Handlers
{
    public sealed class CloudTrainingCommandHandler : WebViewCommandHandlerBase
    {
        public CloudTrainingCommandHandler(InsightApplication app)
        {
            Map(WebViewActions.TestKaggleConnection, (request, ct) =>
                app.HandleTestKaggleConnectionAsync(WebViewPayloadBinder.Bind<TestKaggleConnectionPayload>(request.Payload), ct));
            Map(WebViewActions.StartKaggleTraining, (request, ct) =>
                app.HandleStartKaggleTrainingAsync(WebViewPayloadBinder.Bind<StartKaggleTrainingPayload>(request.Payload)));
            Map(WebViewActions.DownloadKaggleOutput, (request, ct) =>
                app.HandleDownloadKaggleOutputAsync(WebViewPayloadBinder.Bind<DownloadKaggleOutputPayload>(request.Payload)));
        }
    }
}
