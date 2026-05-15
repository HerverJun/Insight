using Insight.Bridge;
using Insight.Services;

namespace Insight.Handlers
{
    public sealed class ProjectCommandHandler : WebViewCommandHandlerBase
    {
        public ProjectCommandHandler(InsightApplication app)
        {
            Map(WebViewActions.GetProjects, _ => app.HandleGetProjects());
            Map(WebViewActions.GetSubfolders, request => app.HandleGetSubfolders(request.Payload));
            Map(WebViewActions.CreateProject, request => app.HandleCreateProject(request.Payload));
            Map(WebViewActions.DeleteProject, request => app.HandleDeleteProject(request.Payload));
        }
    }
}
