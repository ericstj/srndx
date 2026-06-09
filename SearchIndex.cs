using FastTextNet;
using HnswNet;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Model2VecNet;
using System.Diagnostics.CodeAnalysis;

namespace SemanticSearch;

/// <summary>
/// The search engine: composes FastText.Net (language ID), Model2Vec.Net (embeddings) and
/// Hnsw.Net (vector index) through the .NET AI ecosystem abstractions.
/// <list type="bullet">
///   <item>Model2Vec.Net is handed to the store as a <c>Microsoft.Extensions.AI</c>
///   <c>IEmbeddingGenerator</c>, so the store embeds <see cref="SearchRecord.Text" /> automatically.</item>
///   <item>Hnsw.Net is used through <c>Microsoft.Extensions.VectorData</c>
///   (<see cref="HnswVectorStore" /> / <see cref="HnswCollection{TKey, TRecord}" />).</item>
///   <item>FastText.Net detects each item's language (no MEAI abstraction exists for that).</item>
/// </list>
/// Swapping the vector store or embedding generator for any other ecosystem implementation is a
/// one-line change. Everything is pure managed: no native dependency, no GPU, no external service.
/// </summary>
public sealed class SearchIndex : IDisposable
{
    private const string CollectionName = "items";

    private readonly FastTextModel _languageModel;
    private readonly Model2VecModel _embedder;
    private readonly HnswVectorStore _store;

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The HNSW connector maps SearchRecord by reflection; its members are preserved via ILLink.Descriptors.xml.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "The HNSW connector maps SearchRecord by reflection; its members are preserved via ILLink.Descriptors.xml.")]
    public SearchIndex(string? languageModelPath = null, string? embeddingModelPath = null, bool cacheEmbeddings = false)
    {
        _languageModel = FastTextModel.Load(languageModelPath ?? ModelLocator.LanguageModel);
        _embedder = Model2VecModel.Load(embeddingModelPath ?? ModelLocator.EmbeddingModel);

        IEmbeddingGenerator<string, Embedding<float>> generator =
            cacheEmbeddings ? new CachingEmbeddingGenerator(_embedder) : _embedder;

        _store = new HnswVectorStore(new HnswVectorStoreOptions { EmbeddingGenerator = generator });
        Collection = _store.GetCollection<Guid, SearchRecord>(CollectionName, BuildDefinition(_embedder.Dimension));
        Collection.EnsureCollectionExistsAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// The backing collection, exposed as the concrete provider type. The MEVD abstraction has no
    /// persistence API, so callers "break glass" to this type to reach
    /// <see cref="HnswCollection{TKey, TRecord}.Save" /> / <see cref="HnswCollection{TKey, TRecord}.Load" />.
    /// </summary>
    public HnswCollection<Guid, SearchRecord> Collection { get; }

    /// <summary>Detects the dominant language of a piece of text (ISO code and confidence).</summary>
    public (string Language, float Confidence) DetectLanguage(string text)
    {
        IReadOnlyList<FastTextPrediction> predictions = _languageModel.Predict(Normalize(text), k: 1);
        if (predictions.Count == 0)
        {
            return ("und", 0f);
        }

        FastTextPrediction top = predictions[0];
        return (StripLabel(top.Label), top.Probability);
    }

    /// <summary>Language-detects and indexes a batch of items; the store embeds each one.</summary>
    /// <returns>The keys of the records that were added.</returns>
    public async Task<IReadOnlyList<Guid>> AddAsync(IEnumerable<Passage> passages)
    {
        var batch = new List<SearchRecord>();
        foreach (Passage passage in passages)
        {
            (string language, _) = DetectLanguage(passage.Text);
            batch.Add(new SearchRecord
            {
                Id = Guid.NewGuid(),
                Source = passage.Source,
                Location = passage.Location,
                Title = passage.Title,
                Language = language,
                Text = passage.Text,
            });
        }

        if (batch.Count > 0)
        {
            await Collection.UpsertAsync(batch).ConfigureAwait(false);
        }

        return batch.ConvertAll(r => r.Id);
    }

    /// <summary>Language-detects and indexes a batch of items; the store embeds each one.</summary>
    public async Task<int> IndexAsync(IEnumerable<Passage> passages)
        => (await AddAsync(passages).ConfigureAwait(false)).Count;

    /// <summary>Removes records by key.</summary>
    public async Task RemoveAsync(IEnumerable<Guid> ids)
    {
        foreach (Guid id in ids)
        {
            await Collection.DeleteAsync(id).ConfigureAwait(false);
        }
    }

    /// <summary>Enumerates every stored record.</summary>
    public IAsyncEnumerable<SearchRecord> EnumerateAllAsync()
        => Collection.GetAsync(_ => true, int.MaxValue);

    /// <summary>Finds the items most similar to <paramref name="query" />, with optional filters.</summary>
    public async Task<IReadOnlyList<(SearchRecord Record, float Score)>> SearchAsync(
        string query, int top = 5, string? language = null, string? source = null)
    {
        VectorSearchOptions<SearchRecord>? options = null;
        if (language is not null && source is not null)
        {
            options = new() { Filter = r => r.Language == language && r.Source == source };
        }
        else if (language is not null)
        {
            options = new() { Filter = r => r.Language == language };
        }
        else if (source is not null)
        {
            options = new() { Filter = r => r.Source == source };
        }

        var results = new List<(SearchRecord, float)>();
        await foreach (VectorSearchResult<SearchRecord> result in Collection.SearchAsync(query, top, options).ConfigureAwait(false))
        {
            results.Add((result.Record, (float)(result.Score ?? 0d)));
        }

        return results;
    }

    /// <summary>Counts the stored items by enumerating the collection.</summary>
    public async Task<int> CountAsync()
    {
        int count = 0;
        await foreach (SearchRecord _ in Collection.GetAsync(_ => true, int.MaxValue).ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    private static VectorStoreCollectionDefinition BuildDefinition(int dimensions) => new()
    {
        Properties =
        {
            new VectorStoreKeyProperty(nameof(SearchRecord.Id), typeof(Guid)),
            new VectorStoreDataProperty(nameof(SearchRecord.Source), typeof(string)) { IsIndexed = true },
            new VectorStoreDataProperty(nameof(SearchRecord.Location), typeof(string)),
            new VectorStoreDataProperty(nameof(SearchRecord.Title), typeof(string)),
            new VectorStoreDataProperty(nameof(SearchRecord.Language), typeof(string)) { IsIndexed = true },
            new VectorStoreVectorProperty(nameof(SearchRecord.Text), typeof(string), dimensions)
            {
                DistanceFunction = DistanceFunction.CosineSimilarity,
            },
        },
    };

    private static string StripLabel(string label) =>
        label.StartsWith("__label__", StringComparison.Ordinal) ? label["__label__".Length..] : label;

    private static string Normalize(string text) => text.ReplaceLineEndings(" ").Trim();

    public void Dispose() => _store.Dispose();
}
