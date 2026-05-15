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
                var type = request.GetString("type");
                if (!string.IsNullOrEmpty(type)) app.HandleSelectFolder(type);
            });
            Map(WebViewActions.SelectFile, request =>
            {
                var type = request.GetString("type");
                if (!string.IsNullOrEmpty(type)) app.HandleSelectFile(type);
            });
            Map(WebViewActions.SaveDefaultPythonPath, request => app.HandleSaveDefaultPythonPath(request.Payload));
            Map(WebViewActions.GetDefaultPythonPath, _ => app.HandleGetDefaultPythonPath());
        }
    }
}
