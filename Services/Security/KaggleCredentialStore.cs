using System.Text.Json;

namespace Insight.Services.Security
{
    public sealed class KaggleCredentialStore
    {
        public const string SecretName = "kaggle-cli";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly ISecretsStore _secretsStore;

        public KaggleCredentialStore(ISecretsStore secretsStore)
        {
            _secretsStore = secretsStore;
        }

        public async Task SaveAsync(KaggleCredential credential, CancellationToken cancellationToken)
        {
            if (credential == null) throw new ArgumentNullException(nameof(credential));
            if (string.IsNullOrWhiteSpace(credential.Username))
            {
                throw new ArgumentException("Kaggle username is required.", nameof(credential));
            }

            if (string.IsNullOrWhiteSpace(credential.Key))
            {
                throw new ArgumentException("Kaggle API key is required.", nameof(credential));
            }

            await _secretsStore.SaveSecretAsync(
                SecretName,
                JsonSerializer.Serialize(credential, JsonOptions),
                cancellationToken);
        }

        public async Task<KaggleCredential?> GetAsync(CancellationToken cancellationToken)
        {
            var json = await _secretsStore.GetSecretAsync(SecretName, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<KaggleCredential>(json, JsonOptions);
        }

        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            return _secretsStore.DeleteSecretAsync(SecretName, cancellationToken);
        }
    }
}
