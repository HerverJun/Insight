using Insight.Bridge;
using Insight.Services;

namespace Insight.Handlers
{
    public sealed class SettingsAndDialogsCommandHandler : WebViewCommandHandlerBase
    {
        public SettingsAndDialogsCommandHandler(InsightApplication app)
        {
            Map(WebViewActions.SelectFolder, request =>
            {
                var payload = WebViewPayloadBinder.Bind<SelectPathPayload>(request.Payload);
                app.HandleSelectFolder(payload.Type);
            });
            Map(WebViewActions.SelectFile, request =>
            {
                var payload = WebViewPayloadBinder.Bind<SelectPathPayload>(request.Payload);
                app.HandleSelectFile(payload.Type);
            });
            Map(WebViewActions.SaveDefaultPythonPath, request =>
                app.HandleSaveDefaultPythonPath(WebViewPayloadBinder.Bind<SaveDefaultPythonPathPayload>(request.Payload)));
            Map(WebViewActions.GetDefaultPythonPath, _ => app.HandleGetDefaultPythonPath());
        }
    }
}
