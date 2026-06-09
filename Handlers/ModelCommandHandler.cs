using Insight.Bridge;
using Insight.Services;

namespace Insight.Handlers
{
    public sealed class ModelCommandHandler : WebViewCommandHandlerBase
    {
        public ModelCommandHandler(InsightApplication app)
        {
            Map(WebViewActions.GetModels, request =>
                app.HandleGetModels(WebViewPayloadBinder.Bind<DirectoryPathPayload>(request.Payload)));
            Map(WebViewActions.DeleteModel, request =>
                app.HandleDeleteModel(WebViewPayloadBinder.Bind<ModelFilePayload>(request.Payload)));
            Map(WebViewActions.RenameModel, request =>
                app.HandleRenameModel(WebViewPayloadBinder.Bind<RenameModelPayload>(request.Payload)));
            Map(WebViewActions.ConvertModel, (request, ct) =>
                app.HandleConvertModelAsync(WebViewPayloadBinder.Bind<ConvertModelPayload>(request.Payload)));
            Map(WebViewActions.OpenModelFolder, request =>
                app.HandleOpenModelFolder(WebViewPayloadBinder.Bind<DirectoryPathPayload>(request.Payload)));
            Map(WebViewActions.OpenOutput, request =>
                app.HandleOpenOutput(WebViewPayloadBinder.Bind<DirectoryPathPayload>(request.Payload)));
        }
    }
}
