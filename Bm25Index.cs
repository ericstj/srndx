using System.Text;

namespace SemanticSearch;

/// <summary>
/// A compact in-memory BM25 lexical index keyed by record id. It pairs with the vector index for
/// hybrid retrieval: BM25 contributes exact-token precision, the embeddings contribute semantic
/// ranking, and the two are fused with reciprocal-rank fusion in <see cref="SearchIndex" />.
/// <para>
/// Documents can be added and removed incrementally (the live <c>serve</c>/<c>mcp</c> path re-indexes
/// changed files), and the whole index round-trips through <see cref="Save" />/<see cref="Load" />.
/// Everything is pure managed and reflection-free, so it publishes cleanly with <c>PublishAot</c>.
/// </para>
/// <para>
/// Documents are addressed internally by a dense <c>int</c> slot rather than their <see cref="Guid" />
/// id, so postings hash and compare 4-byte keys instead of 16-byte ones - this keeps both load and
/// search cheap on large corpora. A free list recycles slots vacated by <see cref="Remove" />.
/// </para>
/// </summary>
public sealed class Bm25Index : IDisposable
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    // Per-term postings: term -> (document slot -> term frequency).
    private readonly Dictionary<string, Dictionary<int, int>> _postings = new(StringComparer.Ordinal);

    // Per-slot document state, indexed by the dense slot. Vacated slots are held in _free.
    private readonly List<Guid> _docId = [];
    private readonly List<int> _docLength = [];
    private readonly List<Dictionary<string, int>?> _docTerms = [];
    private readonly Dictionary<Guid, int> _slotById = [];
    private readonly Stack<int> _free = new();

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private long _totalLength;
    private int _count;

    /// <summary>The number of documents currently indexed.</summary>
    public int Count => _count;

    /// <summary>Adds or replaces the document with the given id.</summary>
    public void Add(Guid id, string text)
    {
        Dictionary<string, int> terms = CountTerms(text);
        _lock.EnterWriteLock();
        try
        {
            RemoveCore(id);

            int length = 0;
            foreach (int tf in terms.Values)
            {
                length += tf;
            }

            int slot = Allocate(id, length, terms);
            foreach ((string term, int tf) in terms)
            {
                if (!_postings.TryGetValue(term, out Dictionary<int, int>? posting))
                {
                    posting = [];
                    _postings[term] = posting;
                }

                posting[slot] = tf;
            }

            _totalLength += length;
            _count++;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Removes the document with the given id, if present.</summary>
    public void Remove(Guid id)
    {
        _lock.EnterWriteLock();
        try
        {
            RemoveCore(id);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private int Allocate(Guid id, int length, Dictionary<string, int>? terms)
    {
        int slot;
        if (_free.Count > 0)
        {
            slot = _free.Pop();
            _docId[slot] = id;
            _docLength[slot] = length;
            _docTerms[slot] = terms;
        }
        else
        {
            slot = _docId.Count;
            _docId.Add(id);
            _docLength.Add(length);
            _docTerms.Add(terms);
        }

        _slotById[id] = slot;
        return slot;
    }

    private void RemoveCore(Guid id)
    {
        if (!_slotById.Remove(id, out int slot))
        {
            return;
        }

        Dictionary<string, int>? terms = _docTerms[slot];
        if (terms is not null)
        {
            foreach (string term in terms.Keys)
            {
                if (_postings.TryGetValue(term, out Dictionary<int, int>? posting))
                {
                    posting.Remove(slot);
                    if (posting.Count == 0)
                    {
                        _postings.Remove(term);
                    }
                }
            }
        }

        _totalLength -= _docLength[slot];
        _count--;
        _docId[slot] = default;
        _docLength[slot] = 0;
        _docTerms[slot] = null;
        _free.Push(slot);
    }

    /// <summary>Returns the top-scoring documents for the query, highest BM25 score first.</summary>
    public IReadOnlyList<(Guid Id, double Score)> Search(string query, int top)
    {
        Dictionary<string, int> queryTerms = CountTerms(query);
        if (queryTerms.Count == 0)
        {
            return [];
        }

        _lock.EnterReadLock();
        try
        {
            int n = _count;
            if (n == 0)
            {
                return [];
            }

            double avgdl = _totalLength / (double)n;
            var scores = new Dictionary<int, double>();
            foreach (string term in queryTerms.Keys)
            {
                if (!_postings.TryGetValue(term, out Dictionary<int, int>? posting))
                {
                    continue;
                }

                double idf = Math.Log(1d + (n - posting.Count + 0.5) / (posting.Count + 0.5));
                foreach ((int slot, int tf) in posting)
                {
                    double norm = tf + K1 * (1d - B + B * _docLength[slot] / avgdl);
                    double contribution = idf * (tf * (K1 + 1d)) / norm;
                    scores[slot] = scores.TryGetValue(slot, out double s) ? s + contribution : contribution;
                }
            }

            return TopK(scores, top);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Serializes the index to <paramref name="writer" />.</summary>
    public void Save(BinaryWriter writer)
    {
        _lock.EnterReadLock();
        try
        {
            // Vocabulary: each distinct term is written once and referenced by index, so a common
            // term costs one string on disk and one allocation on load instead of one per occurrence.
            var termIds = new Dictionary<string, int>(_postings.Count, StringComparer.Ordinal);
            writer.Write(_postings.Count);
            foreach (string term in _postings.Keys)
            {
                termIds[term] = termIds.Count;
                writer.Write(term);
            }

            writer.Write(_count);
            Span<byte> id = stackalloc byte[16];
            foreach ((Guid key, int slot) in _slotById)
            {
                Dictionary<string, int> terms = _docTerms[slot]
                    ?? throw new InvalidOperationException("Cannot save an index loaded without term tracking.");
                key.TryWriteBytes(id);
                writer.Write(id);
                writer.Write(_docLength[slot]);
                writer.Write(terms.Count);
                foreach ((string term, int tf) in terms)
                {
                    writer.Write(termIds[term]);
                    writer.Write(tf);
                }
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Replaces the index contents with data read from <paramref name="reader" />. When
    /// <paramref name="tracking" /> is false the per-document term lists are not rebuilt - this halves
    /// load work and is used by the read-only CLI search path, which never mutates the index. The live
    /// <c>serve</c>/<c>mcp</c> path passes true so incremental <see cref="Remove" /> can work.
    /// </summary>
    public void Load(BinaryReader reader, bool tracking)
    {
        _lock.EnterWriteLock();
        try
        {
            _postings.Clear();
            _docId.Clear();
            _docLength.Clear();
            _docTerms.Clear();
            _slotById.Clear();
            _free.Clear();
            _totalLength = 0;
            _count = 0;

            int vocabCount = reader.ReadInt32();
            var vocab = new string[vocabCount];
            var posting = new Dictionary<int, int>[vocabCount];
            _postings.EnsureCapacity(vocabCount);
            for (int v = 0; v < vocabCount; v++)
            {
                string term = reader.ReadString();
                vocab[v] = term;
                var p = new Dictionary<int, int>();
                posting[v] = p;
                _postings[term] = p;
            }

            int docCount = reader.ReadInt32();
            _docId.Capacity = docCount;
            _docLength.Capacity = docCount;
            _docTerms.Capacity = docCount;
            _slotById.EnsureCapacity(docCount);
            for (int slot = 0; slot < docCount; slot++)
            {
                var id = new Guid(reader.ReadBytes(16));
                int length = reader.ReadInt32();
                int termCount = reader.ReadInt32();
                Dictionary<string, int>? terms = tracking
                    ? new Dictionary<string, int>(termCount, StringComparer.Ordinal)
                    : null;
                for (int j = 0; j < termCount; j++)
                {
                    int vid = reader.ReadInt32();
                    int tf = reader.ReadInt32();
                    posting[vid][slot] = tf;
                    terms?.Add(vocab[vid], tf);
                }

                _docId.Add(id);
                _docLength.Add(length);
                _docTerms.Add(terms);
                _slotById[id] = slot;
                _totalLength += length;
                _count++;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private IReadOnlyList<(Guid Id, double Score)> TopK(Dictionary<int, double> scores, int top)
    {
        var ranked = new List<(Guid Id, double Score)>(scores.Count);
        foreach ((int slot, double score) in scores)
        {
            ranked.Add((_docId[slot], score));
        }

        ranked.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        if (ranked.Count > top)
        {
            ranked.RemoveRange(top, ranked.Count - top);
        }

        return ranked;
    }

    private static Dictionary<string, int> CountTerms(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string token in Tokenize(text))
        {
            counts[token] = counts.TryGetValue(token, out int c) ? c + 1 : 1;
        }

        return counts;
    }

    /// <summary>
    /// Splits text into lowercased tokens for indexing and querying. It breaks runs on
    /// non-alphanumeric characters, then within each run on camelCase and letter/digit boundaries, so
    /// <c>ValidateOnStart</c> yields <c>validate</c>, <c>on</c> and <c>start</c> (recall). When a run is
    /// split, the whole run is emitted too (e.g. <c>validateonstart</c>) - a rare, high-IDF token that
    /// keeps exact-identifier queries precise.
    /// </summary>
    internal static IEnumerable<string> Tokenize(string text)
    {
        int i = 0;
        int length = text.Length;
        while (i < length)
        {
            while (i < length && !char.IsLetterOrDigit(text[i]))
            {
                i++;
            }

            int runStart = i;
            while (i < length && char.IsLetterOrDigit(text[i]))
            {
                i++;
            }

            int runEnd = i;
            int subwords = 0;
            int start = runStart;
            for (int k = runStart + 1; k <= runEnd; k++)
            {
                bool boundary = k == runEnd;
                if (!boundary)
                {
                    char prev = text[k - 1];
                    char cur = text[k];
                    boundary = (char.IsUpper(cur) && !char.IsUpper(prev)) || (char.IsDigit(cur) != char.IsDigit(prev));
                }

                if (boundary)
                {
                    yield return text.Substring(start, k - start).ToLowerInvariant();
                    subwords++;
                    start = k;
                }
            }

            if (subwords > 1)
            {
                yield return text.Substring(runStart, runEnd - runStart).ToLowerInvariant();
            }
        }
    }

    public void Dispose() => _lock.Dispose();
}
