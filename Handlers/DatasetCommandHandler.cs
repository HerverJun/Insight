using Insight.Bridge;
using Insight.Services;

namespace Insight.Handlers
{
    public sealed class DatasetCommandHandler : WebViewCommandHandlerBase
    {
        public DatasetCommandHandler(InsightApplication app)
        {
            Map(WebViewActions.Generate, (request, ct) => app.HandleGenerateAsync(request.Payload));
            Map(WebViewActions.ExportDatasetZip, (request, ct) => app.HandleExportDatasetZipAsync(request.Payload));
            Map(WebViewActions.DefectAugmentation, (request, ct) => app.HandleDefectAugmentationAsync(request.Payload));
            Map(WebViewActions.ConvertTool, request => app.HandleToolConvert(request.Payload));
        }
    }
}
