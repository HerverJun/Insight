namespace Insight.Services.Security
{
    public interface ISecretsStore
    {
        Task SaveSecretAsync(string name, string value, CancellationToken cancellationToken);
        Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken);
        Task DeleteSecretAsync(string name, CancellationToken cancellationToken);
    }
}
