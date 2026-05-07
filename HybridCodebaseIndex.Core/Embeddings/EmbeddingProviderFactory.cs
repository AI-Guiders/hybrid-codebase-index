namespace HybridCodebaseIndex.Core.Embeddings;

internal static class EmbeddingProviderFactory
{
    internal static IEmbeddingProvider Create(IndexSettings settings)
    {
        var provider = (settings.EmbeddingProvider ?? "dummy").Trim().ToLowerInvariant();
        return provider switch
        {
            "dummy" => new DummyEmbeddingProvider(settings.EmbeddingDim > 0 ? settings.EmbeddingDim : 64),
            _ => new DummyEmbeddingProvider(settings.EmbeddingDim > 0 ? settings.EmbeddingDim : 64),
        };
    }
}

