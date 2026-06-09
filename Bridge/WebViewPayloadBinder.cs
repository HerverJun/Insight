using System.Text.Json;

namespace Insight.Bridge
{
    public static class WebViewPayloadBinder
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static T Bind<T>(JsonElement payload)
        {
            try
            {
                var value = payload.Deserialize<T>(JsonOptions);
                if (value == null)
                {
                    throw new InvalidOperationException($"Payload for {typeof(T).Name} was empty.");
                }

                if (value is IWebViewPayload validatable)
                {
                    validatable.Validate();
                }

                return value;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                throw new InvalidOperationException($"Invalid WebView payload for {typeof(T).Name}: {ex.Message}", ex);
            }
        }

        public static bool TryBind<T>(JsonElement payload, out T? value, out string error)
        {
            try
            {
                value = Bind<T>(payload);
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                value = default;
                error = ex.Message;
                return false;
            }
        }
    }
}
