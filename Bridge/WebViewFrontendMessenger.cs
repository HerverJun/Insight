using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Insight.Bridge
{
    public sealed class WebViewFrontendMessenger : IFrontendMessenger
    {
        private readonly IUiDispatcher _ui;
        private readonly Func<CoreWebView2?> _getCoreWebView;

        public WebViewFrontendMessenger(IUiDispatcher ui, Func<CoreWebView2?> getCoreWebView)
        {
            _ui = ui;
            _getCoreWebView = getCoreWebView;
        }

        public void Send(object data)
        {
            try
            {
                var json = JsonSerializer.Serialize(data);
                _ui.Post(() => _getCoreWebView()?.PostWebMessageAsString(json));
            }
            catch
            {
                // Frontend messaging should never crash command execution.
            }
        }

        public void Log(string message, string type = "info")
        {
            Send(new { action = "log", message, type });
        }

        public void Error(string message)
        {
            Send(new { action = "error", message });
        }

        public void Complete(string message)
        {
            Send(new { action = "complete", message });
        }
    }
}
