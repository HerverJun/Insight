namespace Insight.Training.Providers
{
    public sealed class TrainingProviderCatalog
    {
        private readonly Dictionary<string, ITrainingProvider> _providers;

        public TrainingProviderCatalog(IEnumerable<ITrainingProvider> providers)
        {
            _providers = new Dictionary<string, ITrainingProvider>(StringComparer.OrdinalIgnoreCase);

            foreach (var provider in providers)
            {
                if (string.IsNullOrWhiteSpace(provider.Descriptor.Id))
                {
                    throw new InvalidOperationException("Training provider id is required.");
                }

                if (!_providers.TryAdd(provider.Descriptor.Id, provider))
                {
                    throw new InvalidOperationException($"Duplicate training provider id: {provider.Descriptor.Id}");
                }
            }
        }

        public IReadOnlyList<TrainingProviderDescriptor> ListDescriptors()
        {
            return _providers.Values
                .Select(x => x.Descriptor)
                .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public ITrainingProvider GetRequired(string providerId)
        {
            if (_providers.TryGetValue(providerId, out var provider))
            {
                return provider;
            }

            throw new InvalidOperationException($"Unknown training provider: {providerId}");
        }
    }
}
