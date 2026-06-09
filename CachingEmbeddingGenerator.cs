using Microsoft.Extensions.AI;
using System.Collections.Concurrent;

namespace SemanticSearch;

/// <summary>
/// A Microsoft.Extensions.AI embedding middleware that memoizes embeddings by input text. It is the
/// standard MEAI extension point (<see cref="DelegatingEmbeddingGenerator{TInput, TEmbedding}" />)
/// composed in front of Model2Vec.Net.
/// <para>
/// In serve mode a single file edit re-reads and re-embeds every passage of the changed file;
/// passages that did not change (and identical text shared across files, such as license headers)
/// are served from this cache instead of recomputed. The cache is bounded so a long-running service
/// keeps a fixed memory ceiling; evicted entries are simply recomputed on next use.
/// </para>
/// </summary>
public sealed class CachingEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> inner,
    int capacity = 8192)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    private readonly ConcurrentDictionary<string, Embedding<float>> _cache = new(StringComparer.Ordinal);

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> inputs = values as IReadOnlyList<string> ?? values.ToList();
        var results = new Embedding<float>[inputs.Count];

        List<string>? misses = null;
        List<int>? missIndexes = null;
        for (int i = 0; i < inputs.Count; i++)
        {
            if (_cache.TryGetValue(inputs[i], out Embedding<float>? hit))
            {
                results[i] = hit;
            }
            else
            {
                (misses ??= []).Add(inputs[i]);
                (missIndexes ??= []).Add(i);
            }
        }

        if (misses is not null)
        {
            GeneratedEmbeddings<Embedding<float>> generated =
                await base.GenerateAsync(misses, options, cancellationToken).ConfigureAwait(false);
            for (int j = 0; j < missIndexes!.Count; j++)
            {
                Embedding<float> embedding = generated[j];
                results[missIndexes[j]] = embedding;
                Store(misses[j], embedding);
            }
        }

        return new GeneratedEmbeddings<Embedding<float>>(results);
    }

    private void Store(string key, Embedding<float> value)
    {
        if (_cache.Count >= capacity && !_cache.ContainsKey(key))
        {
            foreach (string evict in _cache.Keys)
            {
                _cache.TryRemove(evict, out _);
                break;
            }
        }

        _cache[key] = value;
    }
}
