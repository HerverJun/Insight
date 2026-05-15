namespace Insight.Bridge
{
    public abstract class WebViewCommandHandlerBase : IWebViewCommandHandler
    {
        private readonly Dictionary<string, Func<WebViewRequest, CancellationToken, Task>> _routes = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Actions => _routes.Keys.ToArray();

        public Task HandleAsync(WebViewRequest request, CancellationToken cancellationToken)
        {
            if (!_routes.TryGetValue(request.Action, out var handler))
            {
                throw new InvalidOperationException($"Handler {GetType().Name} does not support action '{request.Action}'.");
            }

            return handler(request, cancellationToken);
        }

        protected void Map(string action, Action<WebViewRequest> handler)
        {
            Map(action, (request, _) =>
            {
                handler(request);
                return Task.CompletedTask;
            });
        }

        protected void Map(string action, Func<WebViewRequest, CancellationToken, Task> handler)
        {
            if (!_routes.TryAdd(action, handler))
            {
                throw new InvalidOperationException($"Duplicate action route '{action}' in {GetType().Name}.");
            }
        }
    }
}
