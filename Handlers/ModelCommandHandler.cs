using Insight.Bridge;
using Insight.Services;

namespace Insight.Handlers
{
    public sealed class ModelCommandHandler : WebViewCommandHandlerBase
    {
        public ModelCommandHandler(InsightApplication app)
        {
            Map(WebViewActions.GetModels, request => app.HandleGetModels(request.Payload));
            Map(WebViewActions.DeleteModel, request => app.HandleDeleteModel(request.Payload));
            Map(WebViewActions.RenameModel, request => app.HandleRenameModel(request.Payload));
            Map(WebViewActions.ConvertModel, (request, ct) => app.HandleConvertModelAsync(request.Payload));
            Map(WebViewActions.OpenModelFolder, request => app.HandleOpenModelFolder(request.Payload));
            Map(WebViewActions.OpenOutput, request => app.HandleOpenOutput(request.Payload));
        }
    }
}
