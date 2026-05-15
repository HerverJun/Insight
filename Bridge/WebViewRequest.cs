using System.Text.Json;

namespace Insight.Bridge
{
    public sealed class WebViewRequest
    {
        public WebViewRequest(string action, JsonElement payload)
        {
            Action = action;
            Payload = payload;
        }

        public string Action { get; }
        public JsonElement Payload { get; }

        public string? GetString(string propertyName)
        {
            return Payload.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
        }
    }
}
