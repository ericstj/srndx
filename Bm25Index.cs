using System.Text;

namespace Srndx;

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
    internal const double K1 = 1.2;
    internal const double B = 0.75;

    // On-disk section format (memory-mappable; written by Save, read by Bm25MappedView).
    internal const uint SectionMagic = 0x4D353242; // "B25M"
    internal const int SectionVersion = 1;
    internal const int HeaderSize = 68; // magic+version+3 ints + 6 longs
    internal const int DocEntrySize = 20; // Guid(16) + length(int)
    internal const int TermEntrySize = 24; // strOffset(long) + postOffset(long) + strLen(int) + postCount(int)
    internal const int PostingSize = 8; // slot(int) + tf(int)

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

    // When non-null, the index is backed by a read-only memory-mapped section; mutators are disabled
    // and queries are served directly off mapped pages (the cold-start CLI search path).
    private Bm25MappedView? _view;

    /// <summary>The number of documents currently indexed.</summary>
    public int Count => _view is not null ? _view.Count : _count;

    /// <summary>Adds or replaces the document with the given id.</summary>
    public void Add(Guid id, string text)
    {
        ThrowIfMapped();
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
        ThrowIfMapped();
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
        if (_view is not null)
        {
            return _view.Search(query, top);
        }

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

    /// <summary>
    /// Serializes the index to <paramref name="writer" /> in a memory-mappable, term-major layout:
    /// a header of section-relative offsets, a slot-indexed document table, a sorted term table, the
    /// term strings, and the postings. <see cref="Bm25MappedView" /> reads it without materializing
    /// anything - it binary-searches the term table and faults in only the queried terms' postings.
    /// Terms are ordered by their UTF-8 bytes so the mapped reader can compare encoded query bytes
    /// directly.
    /// </summary>
    public void Save(BinaryWriter writer)
    {
        ThrowIfMapped();
        _lock.EnterReadLock();
        try
        {
            var terms = new List<(string Term, byte[] Bytes)>(_postings.Count);
            foreach (string term in _postings.Keys)
            {
                terms.Add((term, Encoding.UTF8.GetBytes(term)));
            }

            terms.Sort(static (a, b) => a.Bytes.AsSpan().SequenceCompareTo(b.Bytes));

            int vocabCount = terms.Count;
            int docSlotCount = _docId.Count;

            long offDocTable = HeaderSize;
            long offTermTable = offDocTable + (long)docSlotCount * DocEntrySize;
            long offStrings = offTermTable + (long)vocabCount * TermEntrySize;

            long stringsLength = 0;
            long postingsLength = 0;
            foreach ((string term, byte[] bytes) in terms)
            {
                stringsLength += bytes.Length;
                postingsLength += (long)_postings[term].Count * PostingSize;
            }

            long offPostings = offStrings + stringsLength;
            long sectionLength = offPostings + postingsLength;

            writer.Write(SectionMagic);
            writer.Write(SectionVersion);
            writer.Write(docSlotCount);
            writer.Write(_count);
            writer.Write(vocabCount);
            writer.Write(_totalLength);
            writer.Write(offDocTable);
            writer.Write(offTermTable);
            writer.Write(offStrings);
            writer.Write(offPostings);
            writer.Write(sectionLength);

            // Document table, indexed directly by slot. Free (recycled) slots are marked length -1 so
            // the in-memory loader can rebuild the free list; the mapped reader never visits them.
            var live = new bool[docSlotCount];
            foreach (int slot in _slotById.Values)
            {
                live[slot] = true;
            }

            Span<byte> id = stackalloc byte[16];
            for (int slot = 0; slot < docSlotCount; slot++)
            {
                _docId[slot].TryWriteBytes(id);
                writer.Write(id);
                writer.Write(live[slot] ? _docLength[slot] : -1);
            }

            // Term table: section-relative string and postings offsets per term, in sorted order.
            long stringCursor = offStrings;
            long postingCursor = offPostings;
            foreach ((string term, byte[] bytes) in terms)
            {
                int postCount = _postings[term].Count;
                writer.Write(stringCursor);
                writer.Write(postingCursor);
                writer.Write(bytes.Length);
                writer.Write(postCount);
                stringCursor += bytes.Length;
                postingCursor += (long)postCount * PostingSize;
            }

            foreach ((string _, byte[] bytes) in terms)
            {
                writer.Write(bytes);
            }

            foreach ((string term, byte[] _) in terms)
            {
                foreach ((int slot, int tf) in _postings[term])
                {
                    writer.Write(slot);
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
    /// Materializes the index from <paramref name="reader" />, fully reconstructing the in-memory
    /// structures (used by the live <c>serve</c>/<c>mcp</c> path and the stream-load path). When
    /// <paramref name="tracking" /> is false the per-document term lists are not rebuilt - this halves
    /// load work and suffices for read-only search; the live path passes true so incremental
    /// <see cref="Remove" /> can work. The cold-start CLI path uses <see cref="LoadMapped" /> instead.
    /// The section is laid out so it can be read sequentially in offset order.
    /// </summary>
    public void Load(BinaryReader reader, bool tracking)
    {
        _view?.Dispose();
        _view = null;

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

            if (reader.ReadUInt32() != SectionMagic)
            {
                throw new InvalidDataException("Unrecognized index format. Rebuild the index with 'srndx index'.");
            }

            int version = reader.ReadInt32();
            if (version != SectionVersion)
            {
                throw new InvalidDataException($"Unsupported index version {version}. Rebuild the index with 'srndx index'.");
            }

            int docSlotCount = reader.ReadInt32();
            int liveCount = reader.ReadInt32();
            int vocabCount = reader.ReadInt32();
            long totalLength = reader.ReadInt64();
            reader.ReadInt64(); // offDocTable - implied by sequential order
            reader.ReadInt64(); // offTermTable
            reader.ReadInt64(); // offStrings
            reader.ReadInt64(); // offPostings
            reader.ReadInt64(); // sectionLength

            _docId.Capacity = docSlotCount;
            _docLength.Capacity = docSlotCount;
            _docTerms.Capacity = docSlotCount;
            _slotById.EnsureCapacity(liveCount);
            for (int slot = 0; slot < docSlotCount; slot++)
            {
                var id = new Guid(reader.ReadBytes(16));
                int length = reader.ReadInt32();
                if (length < 0)
                {
                    _docId.Add(default);
                    _docLength.Add(0);
                    _docTerms.Add(null);
                    _free.Push(slot);
                }
                else
                {
                    _docId.Add(id);
                    _docLength.Add(length);
                    _docTerms.Add(tracking ? new Dictionary<string, int>(StringComparer.Ordinal) : null);
                    _slotById[id] = slot;
                }
            }

            var vocab = new string[vocabCount];
            var strLengths = new int[vocabCount];
            var postCounts = new int[vocabCount];
            for (int v = 0; v < vocabCount; v++)
            {
                reader.ReadInt64(); // strOffset
                reader.ReadInt64(); // postOffset
                strLengths[v] = reader.ReadInt32();
                postCounts[v] = reader.ReadInt32();
            }

            var posting = new Dictionary<int, int>[vocabCount];
            _postings.EnsureCapacity(vocabCount);
            for (int v = 0; v < vocabCount; v++)
            {
                string term = Encoding.UTF8.GetString(reader.ReadBytes(strLengths[v]));
                vocab[v] = term;
                var p = new Dictionary<int, int>(postCounts[v]);
                posting[v] = p;
                _postings[term] = p;
            }

            for (int v = 0; v < vocabCount; v++)
            {
                Dictionary<int, int> p = posting[v];
                string term = vocab[v];
                for (int j = 0; j < postCounts[v]; j++)
                {
                    int slot = reader.ReadInt32();
                    int tf = reader.ReadInt32();
                    p[slot] = tf;
                    _docTerms[slot]?.Add(term, tf);
                }
            }

            _totalLength = totalLength;
            _count = liveCount;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Loads the index in read-only mode by memory-mapping the section at <paramref name="offset" />
    /// in <paramref name="path" />, so cold-start cost is independent of index size: only the queried
    /// terms' postings are faulted in. The mapped index cannot be mutated or saved.
    /// </summary>
    public void LoadMapped(string path, long offset)
    {
        _lock.EnterWriteLock();
        try
        {
            _view?.Dispose();
            _view = new Bm25MappedView(path, offset);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void ThrowIfMapped()
    {
        if (_view is not null)
        {
            throw new InvalidOperationException("A memory-mapped index is read-only.");
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

    public void Dispose()
    {
        _view?.Dispose();
        _lock.Dispose();
    }
}
