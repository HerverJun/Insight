namespace Insight.Bridge
{
    public interface IWebViewCommandHandler
    {
        IReadOnlyCollection<string> Actions { get; }
        Task HandleAsync(WebViewRequest request, CancellationToken cancellationToken);
    }
}
