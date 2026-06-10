using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text;

namespace Srndx;

/// <summary>
/// A read-only, memory-mapped view over a BM25 section written by <see cref="Bm25Index.Save" />.
/// Cold-start cost is independent of index size: the section is mapped (not read), query terms are
/// located by binary search over the sorted term table, and only the matched terms' postings and the
/// scored documents' table entries are faulted in. Pure managed - <see cref="MemoryMappedFile" /> plus
/// pointer arithmetic, no native dependency. Little-endian only, matching the on-disk blocks.
/// </summary>
internal sealed unsafe class Bm25MappedView : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly byte* _base;
    private bool _acquired;

    private readonly int _count;
    private readonly int _vocabCount;
    private readonly long _totalLength;
    private readonly long _offDocTable;
    private readonly long _offTermTable;

    public Bm25MappedView(string path, long sectionOffset)
    {
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        try
        {
            _accessor = _file.CreateViewAccessor(sectionOffset, 0, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _acquired = true;
            _base = pointer + _accessor.PointerOffset;

            if (Read<uint>(0) != Bm25Index.SectionMagic)
            {
                throw new InvalidDataException("Unrecognized index format. Rebuild the index with 'srndx index'.");
            }

            int version = Read<int>(4);
            if (version != Bm25Index.SectionVersion)
            {
                throw new InvalidDataException($"Unsupported index version {version}. Rebuild the index with 'srndx index'.");
            }

            _count = Read<int>(12);
            _vocabCount = Read<int>(16);
            _totalLength = Read<long>(20);
            _offDocTable = Read<long>(28);
            _offTermTable = Read<long>(36);
            // offStrings (44) and offPostings (52) are absolute section offsets stored per-term in the
            // term table, so they aren't needed as a base here.
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The number of documents in the mapped index.</summary>
    public int Count => _count;

    /// <summary>Scores documents for the query against the mapped postings (same BM25 as the in-memory path).</summary>
    public IReadOnlyList<(Guid Id, double Score)> Search(string query, int top)
    {
        if (_count == 0)
        {
            return [];
        }

        double avgdl = _totalLength / (double)_count;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scores = new Dictionary<int, double>();

        Span<byte> stackBuffer = stackalloc byte[256];
        foreach (string term in Bm25Index.Tokenize(query))
        {
            if (!seen.Add(term))
            {
                continue;
            }

            int byteCount = Encoding.UTF8.GetByteCount(term);
            byte[]? rented = byteCount > stackBuffer.Length ? ArrayPool<byte>.Shared.Rent(byteCount) : null;
            Span<byte> termBytes = rented ?? stackBuffer;
            termBytes = termBytes[..Encoding.UTF8.GetBytes(term, termBytes)];

            if (TryFindTerm(termBytes, out long postingOffset, out int postCount))
            {
                double idf = Math.Log(1d + (_count - postCount + 0.5) / (postCount + 0.5));
                byte* posting = _base + postingOffset;
                for (int i = 0; i < postCount; i++)
                {
                    int slot = Unsafe.ReadUnaligned<int>(posting);
                    int tf = Unsafe.ReadUnaligned<int>(posting + 4);
                    posting += Bm25Index.PostingSize;

                    int docLength = Unsafe.ReadUnaligned<int>(_base + _offDocTable + (long)slot * Bm25Index.DocEntrySize + 16);
                    double norm = tf + Bm25Index.K1 * (1d - Bm25Index.B + Bm25Index.B * docLength / avgdl);
                    double contribution = idf * (tf * (Bm25Index.K1 + 1d)) / norm;
                    scores[slot] = scores.TryGetValue(slot, out double s) ? s + contribution : contribution;
                }
            }

            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        var ranked = new List<(Guid Id, double Score)>(scores.Count);
        foreach ((int slot, double score) in scores)
        {
            Guid id = Unsafe.ReadUnaligned<Guid>(_base + _offDocTable + (long)slot * Bm25Index.DocEntrySize);
            ranked.Add((id, score));
        }

        ranked.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        if (ranked.Count > top)
        {
            ranked.RemoveRange(top, ranked.Count - top);
        }

        return ranked;
    }

    // Binary search the sorted term table for an exact UTF-8 match; terms were ordered by their bytes
    // at save time, so an encoded-bytes comparison is consistent with that order.
    private bool TryFindTerm(ReadOnlySpan<byte> term, out long postingOffset, out int postCount)
    {
        int lo = 0;
        int hi = _vocabCount - 1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            long entry = _offTermTable + (long)mid * Bm25Index.TermEntrySize;
            long stringOffset = Read<long>(entry);
            long entryPostingOffset = Read<long>(entry + 8);
            int stringLength = Read<int>(entry + 16);
            int entryPostCount = Read<int>(entry + 20);

            var candidate = new ReadOnlySpan<byte>(_base + stringOffset, stringLength);
            int cmp = candidate.SequenceCompareTo(term);
            if (cmp == 0)
            {
                postingOffset = entryPostingOffset;
                postCount = entryPostCount;
                return true;
            }

            if (cmp < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        postingOffset = 0;
        postCount = 0;
        return false;
    }

    private T Read<T>(long offset) where T : unmanaged => Unsafe.ReadUnaligned<T>(_base + offset);

    public void Dispose()
    {
        if (_acquired)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _acquired = false;
        }

        _accessor?.Dispose();
        _file?.Dispose();
    }
}
