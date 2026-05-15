using System.Text.Json;

namespace Insight.Bridge
{
    public sealed class WebViewMessageDispatcher
    {
        private readonly Dictionary<string, IWebViewCommandHandler> _handlers;
        private readonly IFrontendMessenger _messenger;

        public WebViewMessageDispatcher(IEnumerable<IWebViewCommandHandler> handlers, IFrontendMessenger messenger)
        {
            _messenger = messenger;
            _handlers = BuildRouteTable(handlers);
        }

        public IReadOnlyCollection<string> RegisteredActions => _handlers.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();

        public async Task DispatchAsync(string? message, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            try
            {
                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;

                if (!root.TryGetProperty("action", out var actionElement))
                {
                    _messenger.Error("消息缺少 action 字段");
                    return;
                }

                var action = actionElement.GetString();
                if (string.IsNullOrWhiteSpace(action))
                {
                    _messenger.Error("消息 action 不能为空");
                    return;
                }

                if (!_handlers.TryGetValue(action, out var handler))
                {
                    _messenger.Error($"未知前端指令: {action}");
                    return;
                }

                await handler.HandleAsync(new WebViewRequest(action, root.Clone()), cancellationToken);
            }
            catch (JsonException ex)
            {
                _messenger.Error($"消息 JSON 解析失败: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                // App shutdown or explicit cancellation.
            }
            catch (Exception ex)
            {
                _messenger.Error($"消息交互异常: {ex.Message}");
            }
        }

        private static Dictionary<string, IWebViewCommandHandler> BuildRouteTable(IEnumerable<IWebViewCommandHandler> handlers)
        {
            var routes = new Dictionary<string, IWebViewCommandHandler>(StringComparer.Ordinal);

            foreach (var handler in handlers)
            {
                foreach (var action in handler.Actions)
                {
                    if (!routes.TryAdd(action, handler))
                    {
                        throw new InvalidOperationException($"Duplicate WebView action handler registration: {action}");
                    }
                }
            }

            return routes;
        }
    }
}
