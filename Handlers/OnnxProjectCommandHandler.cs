using Insight.Bridge;
using Insight.Services;

namespace Insight.Handlers
{
    public sealed class OnnxProjectCommandHandler : WebViewCommandHandlerBase
    {
        public OnnxProjectCommandHandler(InsightApplication app)
        {
            Map(WebViewActions.GetOnnxProjects, _ => app.HandleGetOnnxProjects());
            Map(WebViewActions.CreateOnnxProject, request => app.HandleCreateOnnxProject(request.Payload));
            Map(WebViewActions.DeleteOnnxProject, request => app.HandleDeleteOnnxProject(request.Payload));
            Map(WebViewActions.GetOnnxModels, request => app.HandleGetOnnxModels(request.Payload));
        }
    }
}
