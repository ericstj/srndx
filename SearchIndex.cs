using FastTextNet;
using HnswNet;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Model2VecNet;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Srndx;

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

    /// <summary>Default HNSW build parameters; match the Hnsw.Net defaults.</summary>
    private const int DefaultEfConstruction = 200;
    private const int DefaultM = 16;

    private readonly FastTextModel _languageModel;
    private readonly Model2VecModel _embedder;
    private readonly HnswVectorStore _store;
    private readonly Bm25Index _lexical = new();

    /// <summary>Container magic for the combined vector + lexical index file ("SSK" v3).</summary>
    private const uint IndexMagic = 0x53534B33;

    /// <param name="efConstruction">
    /// HNSW build-time beam width. Higher builds a better-connected graph (higher recall) but is slower;
    /// lower speeds up indexing. Only affects records added by this instance.
    /// </param>
    /// <param name="m">HNSW maximum connections per node. Higher improves recall at the cost of build time and index size.</param>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The HNSW connector maps SearchRecord by reflection; its members are preserved via ILLink.Descriptors.xml.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "The HNSW connector maps SearchRecord by reflection; its members are preserved via ILLink.Descriptors.xml.")]
    public SearchIndex(
        string? languageModelPath = null,
        string? embeddingModelPath = null,
        bool cacheEmbeddings = false,
        int efConstruction = DefaultEfConstruction,
        int m = DefaultM)
    {
        _languageModel = FastTextModel.Load(languageModelPath ?? ModelLocator.LanguageModel);
        _embedder = Model2VecModel.Load(embeddingModelPath ?? ModelLocator.EmbeddingModel);

        IEmbeddingGenerator<string, Embedding<float>> generator = new ParallelEmbeddingGenerator(_embedder);
        if (cacheEmbeddings)
        {
            generator = new CachingEmbeddingGenerator(generator);
        }

        var storeOptions = new HnswVectorStoreOptions
        {
            EmbeddingGenerator = generator,
            EfConstruction = efConstruction,
            M = m,
        };

        _store = new HnswVectorStore(storeOptions);
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

    /// <summary>Language-detects (in parallel) and indexes a batch of items; the store embeds each one.</summary>
    /// <returns>The keys of the records that were added.</returns>
    public async Task<IReadOnlyList<Guid>> AddAsync(IEnumerable<Passage> passages)
    {
        Passage[] items = passages as Passage[] ?? [.. passages];
        if (items.Length == 0)
        {
            return [];
        }

        // FastText prediction uses thread-static scratch state, so detection parallelizes safely across
        // the batch; the BM25 add and the vector upsert below stay on the calling thread (single-writer).
        var languages = new string[items.Length];
        if (items.Length == 1)
        {
            languages[0] = DetectLanguage(items[0].Text).Language;
        }
        else
        {
            Parallel.For(0, items.Length, i => languages[i] = DetectLanguage(items[i].Text).Language);
        }

        var batch = new List<SearchRecord>(items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            var record = new SearchRecord
            {
                Id = Guid.NewGuid(),
                Source = items[i].Source,
                Location = items[i].Location,
                Title = items[i].Title,
                Language = languages[i],
                Text = items[i].Text,
            };
            batch.Add(record);
            _lexical.Add(record.Id, $"{record.Title} {record.Text}");
        }

        await Collection.UpsertAsync(batch).ConfigureAwait(false);
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
            _lexical.Remove(id);
        }
    }

    /// <summary>Enumerates every stored record.</summary>
    public IAsyncEnumerable<SearchRecord> EnumerateAllAsync()
        => Collection.GetAsync(_ => true, int.MaxValue);

    /// <summary>
    /// Finds the items most relevant to <paramref name="query" />, with optional filters. Results
    /// combine semantic similarity (the vector index) with lexical BM25 relevance (the inverted index)
    /// using reciprocal-rank fusion, so exact-token matches and intent matches both surface.
    /// </summary>
    public async Task<IReadOnlyList<(SearchRecord Record, float Score)>> SearchAsync(
        string query, int top = 5, string? language = null, string? source = null)
    {
        int pool = Math.Clamp(top * 10, 50, 200);

        // Vector candidates: the store pre-applies the language/source filter.
        var records = new Dictionary<Guid, SearchRecord>();
        var vectorRanked = new List<Guid>();
        var vectorScore = new Dictionary<Guid, double>();
        await foreach (VectorSearchResult<SearchRecord> result in
            Collection.SearchAsync(query, pool, BuildFilter(language, source)).ConfigureAwait(false))
        {
            records[result.Record.Id] = result.Record;
            vectorRanked.Add(result.Record.Id);
            vectorScore[result.Record.Id] = result.Score ?? 0d;
        }

        // Lexical candidates from BM25.
        IReadOnlyList<(Guid Id, double Score)> lexical = _lexical.Search(query, pool);
        var lexicalScore = new Dictionary<Guid, double>();
        foreach ((Guid id, double score) in lexical)
        {
            lexicalScore[id] = score;
        }

        // Reciprocal-rank fusion: score by position in each list, not by the (incomparable) raw scores.
        const double k = 60d;
        var fused = new Dictionary<Guid, double>();
        for (int rank = 0; rank < vectorRanked.Count; rank++)
        {
            Accumulate(fused, vectorRanked[rank], 1d / (k + rank + 1));
        }

        for (int rank = 0; rank < lexical.Count; rank++)
        {
            Accumulate(fused, lexical[rank].Id, 1d / (k + rank + 1));
        }

        var scored = new List<(SearchRecord Record, double Fused, double Lexical, double Vector)>(fused.Count);
        foreach ((Guid id, double score) in fused)
        {
            if (!records.TryGetValue(id, out SearchRecord? record))
            {
                // Lexical-only hit: fetch the record and apply the filter the vector path got for free.
                record = await Collection.GetAsync(id).ConfigureAwait(false);
                if (record is null || !Matches(record, language, source))
                {
                    continue;
                }
            }

            scored.Add((record, score, lexicalScore.GetValueOrDefault(id), vectorScore.GetValueOrDefault(id)));
        }

        // Many RRF scores tie (a hit at rank 1 of a single list). Break ties toward the stronger raw
        // signal - lexical first - so an exact identifier match wins over an incidental semantic neighbor.
        scored.Sort(static (a, b) =>
        {
            int byFused = b.Fused.CompareTo(a.Fused);
            if (byFused != 0)
            {
                return byFused;
            }

            int byLexical = b.Lexical.CompareTo(a.Lexical);
            return byLexical != 0 ? byLexical : b.Vector.CompareTo(a.Vector);
        });

        int count = Math.Min(top, scored.Count);
        var topResults = new List<(SearchRecord Record, float Score)>(count);
        for (int i = 0; i < count; i++)
        {
            topResults.Add((scored[i].Record, (float)scored[i].Fused));
        }

        return topResults;
    }

    /// <summary>Persists the vector and lexical indexes to a single stream.</summary>
    public void Save(Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(IndexMagic);

        using var vector = new MemoryStream();
        Collection.Save(vector, SearchSerializerContext.Default);
        writer.Write(vector.Length);
        writer.Flush();
        vector.Position = 0;
        vector.CopyTo(stream);

        _lexical.Save(writer);
    }

    /// <summary>Loads a vector and lexical index previously written by <see cref="Save" />.</summary>
    public void Load(Stream stream, bool tracking = false)
    {
        byte[] vectorBytes;
        byte[] lexicalBytes;
        using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
        {
            if (reader.ReadUInt32() != IndexMagic)
            {
                throw new InvalidDataException("Unrecognized index format. Rebuild the index with 'srndx index'.");
            }

            long vectorLength = reader.ReadInt64();
            vectorBytes = reader.ReadBytes((int)vectorLength);
            using var rest = new MemoryStream();
            stream.CopyTo(rest);
            lexicalBytes = rest.ToArray();
        }

        // The two indexes are independent; load them on separate cores so cold start pays
        // max(vector, lexical) instead of their sum.
        Task vectorTask = Task.Run(() =>
        {
            using var vector = new MemoryStream(vectorBytes, writable: false);
            Collection.Load(vector, SearchSerializerContext.Default);
        });
        Task lexicalTask = Task.Run(() =>
        {
            using var lexical = new MemoryStream(lexicalBytes, writable: false);
            using var lexReader = new BinaryReader(lexical, Encoding.UTF8);
            _lexical.Load(lexReader, tracking);
        });
        Task.WaitAll(vectorTask, lexicalTask);
    }

    /// <summary>
    /// Loads an index from a file, memory-mapping the vector index instead of reading it into memory.
    /// This is the read-only cold-start path: record payloads and vectors are faulted in on demand, so
    /// startup cost is independent of index size. When <paramref name="tracking" /> is set the index will
    /// be mutated (watch/serve), which a memory-mapped index cannot support, so it falls back to a
    /// fully-materialized load.
    /// </summary>
    public void Load(string path, bool tracking = false)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (tracking)
        {
            using FileStream mutable = File.OpenRead(path);
            Load(mutable, tracking);
            return;
        }

        long vectorOffset;
        long vectorLength;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
        {
            if (reader.ReadUInt32() != IndexMagic)
            {
                throw new InvalidDataException("Unrecognized index format. Rebuild the index with 'srndx index'.");
            }

            vectorLength = reader.ReadInt64();
            vectorOffset = stream.Position;
        }

        long lexicalOffset = vectorOffset + vectorLength;

        Task vectorTask = Task.Run(() => Collection.Load(path, vectorOffset, SearchSerializerContext.Default));
        Task lexicalTask = Task.Run(() => _lexical.LoadMapped(path, lexicalOffset));
        Task.WaitAll(vectorTask, lexicalTask);
    }

    private static VectorSearchOptions<SearchRecord>? BuildFilter(string? language, string? source)
    {
        if (language is not null && source is not null)
        {
            return new() { Filter = r => r.Language == language && r.Source == source };
        }

        if (language is not null)
        {
            return new() { Filter = r => r.Language == language };
        }

        if (source is not null)
        {
            return new() { Filter = r => r.Source == source };
        }

        return null;
    }

    private static bool Matches(SearchRecord record, string? language, string? source) =>
        (language is null || record.Language == language) && (source is null || record.Source == source);

    private static void Accumulate(Dictionary<Guid, double> map, Guid id, double add) =>
        map[id] = map.TryGetValue(id, out double s) ? s + add : add;

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

    public void Dispose()
    {
        _lexical.Dispose();
        _store.Dispose();
    }
}
