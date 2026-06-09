using System.Security.Cryptography;
using System.Text;
using Insight.Services.Configuration;
using Insight.Services.Security;

namespace Insight.Infrastructure.Security
{
    public sealed class ProtectedFileSecretsStore : ISecretsStore
    {
        private readonly string _root;

        public ProtectedFileSecretsStore(InsightAppPaths paths)
        {
            _root = Path.Combine(paths.ConfigRoot, "secrets");
        }

        public async Task SaveSecretAsync(string name, string value, CancellationToken cancellationToken)
        {
            ValidateName(name);
            Directory.CreateDirectory(_root);

            var plaintext = Encoding.UTF8.GetBytes(value ?? "");
            var protectedBytes = ProtectedData.Protect(
                plaintext,
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);

            await File.WriteAllBytesAsync(GetPath(name), protectedBytes, cancellationToken);
        }

        public async Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken)
        {
            ValidateName(name);
            var path = GetPath(name);
            if (!File.Exists(path))
            {
                return null;
            }

            var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var plaintext = ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken)
        {
            ValidateName(name);
            var path = GetPath(name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return Task.CompletedTask;
        }

        private string GetPath(string name)
        {
            return Path.Combine(_root, name + ".secret");
        }

        private static void ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
            {
                throw new ArgumentException("Secret name must be a file-safe name.", nameof(name));
            }
        }
    }
}
