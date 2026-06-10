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
    /// <summary>Default HNSW build parameters; match the Hnsw.Net defaults.</summary>
    private const int DefaultEfConstruction = 200;
    private const int DefaultM = 16;

    /// <summary>
    /// Default number of vector shards. The vector index is split into this many independent HNSW
    /// graphs so the build, the cold-start load, and the query fan out across cores; each graph is
    /// smaller, which also keeps per-insert cost low. Search merges results across shards.
    /// </summary>
    private const int DefaultShards = 8;

    private readonly FastTextModel _languageModel;
    private readonly Model2VecModel _embedder;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly int _efConstruction;
    private readonly int _m;
    private readonly int _dimension;

    /// <summary>BM25 over passage body text (title + content): the lexical relevance signal.</summary>
    private readonly Bm25Index _lexical = new();

    /// <summary>
    /// BM25 over each item's file path and name (or a commit's subject), kept separate so its IDF and
    /// length normalization rank a short, exact path/name match (e.g. the file that *defines* a type)
    /// above files that merely mention the term in their body.
    /// </summary>
    private readonly Bm25Index _pathIndex = new();

    private HnswVectorStore _store = null!;
    private HnswCollection<Guid, SearchRecord>[] _shards = [];
    private int _shardCount;

    /// <summary>Container magic for the combined index file ("SSK" v5: sharded vectors + body and path BM25).</summary>
    private const uint IndexMagic = 0x53534B35;

    /// <param name="efConstruction">
    /// HNSW build-time beam width. Higher builds a better-connected graph (higher recall) but is slower;
    /// lower speeds up indexing. Only affects records added by this instance.
    /// </param>
    /// <param name="m">HNSW maximum connections per node. Higher improves recall at the cost of build time and index size.</param>
    /// <param name="shards">
    /// Number of independent vector shards. More shards build, load, and query with more parallelism;
    /// the value is persisted with the index and restored on load.
    /// </param>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The HNSW connector maps SearchRecord by reflection; its members are preserved via ILLink.Descriptors.xml.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "The HNSW connector maps SearchRecord by reflection; its members are preserved via ILLink.Descriptors.xml.")]
    public SearchIndex(
        string? languageModelPath = null,
        string? embeddingModelPath = null,
        bool cacheEmbeddings = false,
        int efConstruction = DefaultEfConstruction,
        int m = DefaultM,
        int shards = DefaultShards)
    {
        _languageModel = FastTextModel.Load(languageModelPath ?? ModelLocator.LanguageModel);
        _embedder = Model2VecModel.Load(embeddingModelPath ?? ModelLocator.EmbeddingModel);
        _dimension = _embedder.Dimension;
        _efConstruction = efConstruction;
        _m = m;

        IEmbeddingGenerator<string, Embedding<float>> generator = new ParallelEmbeddingGenerator(_embedder);
        if (cacheEmbeddings)
        {
            generator = new CachingEmbeddingGenerator(generator);
        }

        _generator = generator;
        ConfigureShards(Math.Max(1, shards));
    }

    /// <summary>The number of vector shards the index is currently split into.</summary>
    public int ShardCount => _shardCount;

    /// <summary>
    /// (Re)creates the vector store and its shard collections. Called by the constructor with the
    /// requested shard count and by the load paths with the count persisted in the file, so a loaded
    /// index always uses the shard layout it was written with.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The HNSW connector maps SearchRecord by reflection; its members are preserved via ILLink.Descriptors.xml.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "The HNSW connector maps SearchRecord by reflection; its members are preserved via ILLink.Descriptors.xml.")]
    private void ConfigureShards(int shardCount)
    {
        _store?.Dispose();
        _shardCount = shardCount;
        _store = new HnswVectorStore(new HnswVectorStoreOptions
        {
            EmbeddingGenerator = _generator,
            EfConstruction = _efConstruction,
            M = _m,
        });

        _shards = new HnswCollection<Guid, SearchRecord>[shardCount];
        for (int s = 0; s < shardCount; s++)
        {
            HnswCollection<Guid, SearchRecord> shard =
                _store.GetCollection<Guid, SearchRecord>($"items{s}", BuildDefinition(_dimension));
            shard.EnsureCollectionExistsAsync().GetAwaiter().GetResult();
            _shards[s] = shard;
        }
    }

    /// <summary>Routes a record to a shard by its key; random GUIDs spread evenly across shards.</summary>
    private int ShardOf(Guid id) => (int)((uint)id.GetHashCode() % (uint)_shardCount);

    /// <summary>
    /// Builds the path/name text fed to the dedicated path index. For files this is the path with its
    /// separators turned into spaces (so each directory and the file name become terms); for commits it
    /// is the subject. A short, exact path or name therefore scores highest under BM25 length
    /// normalization, surfacing the defining file ahead of incidental body mentions.
    /// </summary>
    private static string BuildPathText(SearchRecord record)
    {
        if (string.Equals(record.Source, "file", StringComparison.Ordinal))
        {
            int colon = record.Location.LastIndexOf(':');
            string path = colon > 0 ? record.Location[..colon] : record.Location;
            return path.Replace('/', ' ').Replace('.', ' ');
        }

        return record.Title;
    }

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

        // Partition records across shards by key, then build the shard graphs concurrently (each shard
        // has its own writer lock) while the BM25 add stays single-writer on this thread.
        var batch = new SearchRecord[items.Length];
        var buckets = new List<SearchRecord>[_shardCount];
        for (int s = 0; s < _shardCount; s++)
        {
            buckets[s] = [];
        }

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
            batch[i] = record;
            _lexical.Add(record.Id, $"{record.Title} {record.Text}");
            _pathIndex.Add(record.Id, BuildPathText(record));
            buckets[ShardOf(record.Id)].Add(record);
        }

        var upserts = new List<Task>(_shardCount);
        for (int s = 0; s < _shardCount; s++)
        {
            if (buckets[s].Count > 0)
            {
                upserts.Add(_shards[s].UpsertAsync(buckets[s]));
            }
        }

        await Task.WhenAll(upserts).ConfigureAwait(false);
        return Array.ConvertAll(batch, r => r.Id);
    }

    /// <summary>Language-detects and indexes a batch of items; the store embeds each one.</summary>
    public async Task<int> IndexAsync(IEnumerable<Passage> passages)
        => (await AddAsync(passages).ConfigureAwait(false)).Count;

    /// <summary>Removes records by key.</summary>
    public async Task RemoveAsync(IEnumerable<Guid> ids)
    {
        foreach (Guid id in ids)
        {
            await _shards[ShardOf(id)].DeleteAsync(id).ConfigureAwait(false);
            _lexical.Remove(id);
            _pathIndex.Remove(id);
        }
    }

    /// <summary>Enumerates every stored record across all shards.</summary>
    public async IAsyncEnumerable<SearchRecord> EnumerateAllAsync()
    {
        foreach (HnswCollection<Guid, SearchRecord> shard in _shards)
        {
            await foreach (SearchRecord record in shard.GetAsync(_ => true, int.MaxValue).ConfigureAwait(false))
            {
                yield return record;
            }
        }
    }

    /// <summary>
    /// Finds the items most relevant to <paramref name="query" />, with optional filters. Results
    /// combine semantic similarity (the vector index) with lexical BM25 relevance (the inverted index)
    /// using reciprocal-rank fusion, so exact-token matches and intent matches both surface.
    /// </summary>
    public async Task<IReadOnlyList<(SearchRecord Record, float Score)>> SearchAsync(
        string query, int top = 5, string? language = null, string? source = null)
    {
        int pool = Math.Clamp(top * 10, 50, 200);

        // Embed the query once, then search every shard in parallel by vector (each shard returns up to
        // `pool` hits). Merge by similarity score into one ranked list - scores are comparable because
        // every shard uses the same metric - and keep the top `pool` for fusion.
        GeneratedEmbeddings<Embedding<float>> embedded =
            await _generator.GenerateAsync([query]).ConfigureAwait(false);
        ReadOnlyMemory<float> queryVector = embedded[0].Vector;
        VectorSearchOptions<SearchRecord>? filter = BuildFilter(language, source);

        var shardSearches = new Task<List<VectorSearchResult<SearchRecord>>>[_shardCount];
        for (int s = 0; s < _shardCount; s++)
        {
            HnswCollection<Guid, SearchRecord> shard = _shards[s];
            shardSearches[s] = Task.Run(async () =>
            {
                var hits = new List<VectorSearchResult<SearchRecord>>();
                await foreach (VectorSearchResult<SearchRecord> result in
                    shard.SearchAsync(queryVector, pool, filter).ConfigureAwait(false))
                {
                    hits.Add(result);
                }

                return hits;
            });
        }

        List<VectorSearchResult<SearchRecord>>[] perShard = await Task.WhenAll(shardSearches).ConfigureAwait(false);

        var merged = new List<VectorSearchResult<SearchRecord>>(_shardCount * pool);
        foreach (List<VectorSearchResult<SearchRecord>> hits in perShard)
        {
            merged.AddRange(hits);
        }

        merged.Sort(static (a, b) => (b.Score ?? 0d).CompareTo(a.Score ?? 0d));

        var records = new Dictionary<Guid, SearchRecord>();
        var vectorRanked = new List<Guid>();
        var vectorScore = new Dictionary<Guid, double>();
        foreach (VectorSearchResult<SearchRecord> result in merged)
        {
            if (records.ContainsKey(result.Record.Id))
            {
                continue;
            }

            records[result.Record.Id] = result.Record;
            vectorRanked.Add(result.Record.Id);
            vectorScore[result.Record.Id] = result.Score ?? 0d;
            if (vectorRanked.Count >= pool)
            {
                break;
            }
        }

        // Lexical candidates from BM25 over body text.
        IReadOnlyList<(Guid Id, double Score)> lexical = _lexical.Search(query, pool);
        var lexicalScore = new Dictionary<Guid, double>();
        foreach ((Guid id, double score) in lexical)
        {
            lexicalScore[id] = score;
        }

        // Path/name candidates: a short, exact path or file-name match is the strongest signal for a
        // "find this type/file" query, so this list is fused with extra weight.
        IReadOnlyList<(Guid Id, double Score)> path = _pathIndex.Search(query, pool);

        // Reciprocal-rank fusion: score by position in each list, not by the (incomparable) raw scores.
        const double k = 60d;
        const double pathWeight = 2d;
        var fused = new Dictionary<Guid, double>();
        for (int rank = 0; rank < vectorRanked.Count; rank++)
        {
            Accumulate(fused, vectorRanked[rank], 1d / (k + rank + 1));
        }

        for (int rank = 0; rank < lexical.Count; rank++)
        {
            Accumulate(fused, lexical[rank].Id, 1d / (k + rank + 1));
        }

        for (int rank = 0; rank < path.Count; rank++)
        {
            Accumulate(fused, path[rank].Id, pathWeight / (k + rank + 1));
        }

        var scored = new List<(SearchRecord Record, double Fused, double Lexical, double Vector)>(fused.Count);
        foreach ((Guid id, double score) in fused)
        {
            if (!records.TryGetValue(id, out SearchRecord? record))
            {
                // Lexical-only hit: fetch the record and apply the filter the vector path got for free.
                record = await _shards[ShardOf(id)].GetAsync(id).ConfigureAwait(false);
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

    /// <summary>Persists the sharded vector index and the body and path lexical indexes to a single stream.</summary>
    public void Save(Stream stream)
    {
        // Serialize the shards in parallel (independent collections), then lay them out as
        // length-prefixed segments so the load path can map each at a known offset.
        var segments = new byte[_shardCount][];
        Parallel.For(0, _shardCount, s =>
        {
            using var buffer = new MemoryStream();
            _shards[s].Save(buffer, SearchSerializerContext.Default);
            segments[s] = buffer.ToArray();
        });

        byte[] lexicalBytes = SaveBm25(_lexical);

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(IndexMagic);
        writer.Write(_shardCount);
        foreach (byte[] segment in segments)
        {
            writer.Write((long)segment.Length);
        }

        // Record the body-lexical section length so the load path can find the path index that follows it.
        writer.Write((long)lexicalBytes.Length);
        writer.Flush();

        foreach (byte[] segment in segments)
        {
            stream.Write(segment, 0, segment.Length);
        }

        stream.Write(lexicalBytes, 0, lexicalBytes.Length);
        _pathIndex.Save(writer);
    }

    private static byte[] SaveBm25(Bm25Index index)
    {
        using var buffer = new MemoryStream();
        using (var w = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            index.Save(w);
        }

        return buffer.ToArray();
    }

    /// <summary>Loads a sharded index previously written by <see cref="Save" />.</summary>
    public void Load(Stream stream, bool tracking = false)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != IndexMagic)
        {
            throw new InvalidDataException("Unrecognized index format. Rebuild the index with 'srndx index'.");
        }

        int shardCount = reader.ReadInt32();
        if (shardCount <= 0)
        {
            throw new InvalidDataException("Invalid shard count in index header.");
        }

        var segLengths = new long[shardCount];
        for (int s = 0; s < shardCount; s++)
        {
            segLengths[s] = reader.ReadInt64();
        }

        long lexicalLength = reader.ReadInt64();

        var segments = new byte[shardCount][];
        for (int s = 0; s < shardCount; s++)
        {
            segments[s] = reader.ReadBytes((int)segLengths[s]);
        }

        byte[] lexicalBytes = reader.ReadBytes((int)lexicalLength);

        using var rest = new MemoryStream();
        stream.CopyTo(rest);
        byte[] pathBytes = rest.ToArray();

        ConfigureShards(shardCount);

        // The shards and the two lexical indexes are independent; load them on separate cores so cold
        // start pays max(slowest section) rather than their sum.
        var tasks = new List<Task>(shardCount + 2);
        for (int s = 0; s < shardCount; s++)
        {
            int shard = s;
            tasks.Add(Task.Run(() =>
            {
                using var seg = new MemoryStream(segments[shard], writable: false);
                _shards[shard].Load(seg, SearchSerializerContext.Default);
            }));
        }

        tasks.Add(Task.Run(() => LoadBm25(_lexical, lexicalBytes, tracking)));
        tasks.Add(Task.Run(() => LoadBm25(_pathIndex, pathBytes, tracking)));
        Task.WaitAll([.. tasks]);
    }

    private static void LoadBm25(Bm25Index index, byte[] bytes, bool tracking)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        index.Load(r, tracking);
    }

    /// <summary>
    /// Loads an index from a file, memory-mapping each vector shard and the two lexical indexes instead
    /// of reading them into memory. This is the read-only cold-start path: record payloads, vectors, and
    /// lexical postings are faulted in on demand, so startup cost is independent of index size. When
    /// <paramref name="tracking" /> is set the index will be mutated (watch/serve), which a memory-mapped
    /// index cannot support, so it falls back to a fully-materialized load.
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

        int shardCount;
        long[] segLengths;
        long lexicalLength;
        long headerEnd;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
        {
            if (reader.ReadUInt32() != IndexMagic)
            {
                throw new InvalidDataException("Unrecognized index format. Rebuild the index with 'srndx index'.");
            }

            shardCount = reader.ReadInt32();
            if (shardCount <= 0)
            {
                throw new InvalidDataException("Invalid shard count in index header.");
            }

            segLengths = new long[shardCount];
            for (int s = 0; s < shardCount; s++)
            {
                segLengths[s] = reader.ReadInt64();
            }

            lexicalLength = reader.ReadInt64();
            headerEnd = stream.Position;
        }

        ConfigureShards(shardCount);

        var offsets = new long[shardCount];
        long offset = headerEnd;
        for (int s = 0; s < shardCount; s++)
        {
            offsets[s] = offset;
            offset += segLengths[s];
        }

        long lexicalOffset = offset;
        long pathOffset = lexicalOffset + lexicalLength;

        var tasks = new List<Task>(shardCount + 2);
        for (int s = 0; s < shardCount; s++)
        {
            int shard = s;
            tasks.Add(Task.Run(() => _shards[shard].Load(path, offsets[shard], SearchSerializerContext.Default)));
        }

        tasks.Add(Task.Run(() => _lexical.LoadMapped(path, lexicalOffset)));
        tasks.Add(Task.Run(() => _pathIndex.LoadMapped(path, pathOffset)));
        Task.WaitAll([.. tasks]);
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

    /// <summary>Counts the stored items across all shards.</summary>
    public async Task<int> CountAsync()
    {
        int count = 0;
        foreach (HnswCollection<Guid, SearchRecord> shard in _shards)
        {
            await foreach (SearchRecord _ in shard.GetAsync(_ => true, int.MaxValue).ConfigureAwait(false))
            {
                count++;
            }
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
        _pathIndex.Dispose();
        _store.Dispose();
    }
}
