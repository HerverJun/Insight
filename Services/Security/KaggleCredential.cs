namespace Insight.Services.Security
{
    public sealed class KaggleCredential
    {
        public string Username { get; set; } = "";
        public string Key { get; set; } = "";

        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(Username) &&
            !string.IsNullOrWhiteSpace(Key);
    }
}
