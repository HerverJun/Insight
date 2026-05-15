using Insight.Bridge;
using Insight.Services;

namespace Insight.Handlers
{
    public static class WebViewCompositionRoot
    {
        public static WebViewMessageDispatcher CreateDispatcher(InsightApplication application, IFrontendMessenger messenger)
        {
            return new WebViewMessageDispatcher(CreateHandlers(application), messenger);
        }

        public static IReadOnlyCollection<IWebViewCommandHandler> CreateHandlers(InsightApplication application)
        {
            return new IWebViewCommandHandler[]
            {
                new ProjectCommandHandler(application),
                new DatasetCommandHandler(application),
                new TrainingCommandHandler(application),
                new CloudTrainingCommandHandler(application),
                new ModelCommandHandler(application),
                new OnnxProjectCommandHandler(application),
                new SamLabelingCommandHandler(application),
                new SettingsAndDialogsCommandHandler(application)
            };
        }
    }
}
