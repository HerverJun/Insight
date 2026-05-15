using Insight.Bridge;
using Insight.Services;

namespace Insight.Handlers
{
    public sealed class SamLabelingCommandHandler : WebViewCommandHandlerBase
    {
        public SamLabelingCommandHandler(InsightApplication app)
        {
            Map(WebViewActions.GetSamModels, _ => app.HandleGetSAMModels());
            Map(WebViewActions.LoadSamModel, (request, ct) => app.HandleLoadSAMModelAsync(request.Payload));
            Map(WebViewActions.EncodeImage, (request, ct) => app.HandleEncodeImageAsync(request.Payload));
            Map(WebViewActions.SamInference, (request, ct) => app.HandleSAMInferenceAsync(request.Payload));
            Map(WebViewActions.SaveAnnotations, request => app.HandleSaveAnnotations(request.Payload));
            Map(WebViewActions.GetImagesInFolder, request => app.HandleGetImagesInFolder(request.Payload));
            Map(WebViewActions.GetImageData, request => app.HandleGetImageData(request.Payload));
        }
    }
}
