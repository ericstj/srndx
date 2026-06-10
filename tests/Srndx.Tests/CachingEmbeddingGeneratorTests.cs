using Microsoft.Extensions.AI;
using Srndx;
using Xunit;

namespace Srndx.Tests;

public class CachingEmbeddingGeneratorTests
{
    private sealed class CountingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Dictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = new List<Embedding<float>>();
            foreach (string v in values)
            {
                Calls[v] = Calls.TryGetValue(v, out int c) ? c + 1 : 1;
                list.Add(new Embedding<float>(new float[] { v.Length }));
            }

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public async Task CachedInputsAreNotRecomputedAndResultsMapToInputs()
    {
        var inner = new CountingGenerator();
        using var caching = new CachingEmbeddingGenerator(inner);

        GeneratedEmbeddings<Embedding<float>> first = await caching.GenerateAsync(["aa", "bbb"]);
        Assert.Equal(2f, first[0].Vector.Span[0]);
        Assert.Equal(3f, first[1].Vector.Span[0]);

        GeneratedEmbeddings<Embedding<float>> second = await caching.GenerateAsync(["aa", "cccc"]);
        Assert.Equal(2f, second[0].Vector.Span[0]);
        Assert.Equal(4f, second[1].Vector.Span[0]);

        Assert.Equal(1, inner.Calls["aa"]);
        Assert.Equal(1, inner.Calls["bbb"]);
        Assert.Equal(1, inner.Calls["cccc"]);
    }

    [Fact]
    public async Task EvictedEntriesAreRecomputedOnNextUse()
    {
        var inner = new CountingGenerator();
        using var caching = new CachingEmbeddingGenerator(inner, capacity: 1);

        await caching.GenerateAsync(["a"]);
        await caching.GenerateAsync(["b"]);
        await caching.GenerateAsync(["a"]);

        Assert.Equal(2, inner.Calls["a"]);
    }
}
