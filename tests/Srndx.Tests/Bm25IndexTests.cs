using Srndx;
using Xunit;

namespace Srndx.Tests;

public class Bm25IndexTests
{
    [Fact]
    public void CountReflectsAddsAndRemoves()
    {
        using var index = new Bm25Index();
        Assert.Equal(0, index.Count);

        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        index.Add(a, "the quick brown fox");
        index.Add(b, "lazy dog sleeps");
        Assert.Equal(2, index.Count);

        index.Remove(a);
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void AddWithExistingIdReplacesRatherThanDuplicates()
    {
        using var index = new Bm25Index();
        Guid id = Guid.NewGuid();
        index.Add(id, "alpha beta");
        index.Add(id, "gamma delta");
        Assert.Equal(1, index.Count);

        Assert.Empty(index.Search("alpha", 5));
        Assert.Single(index.Search("gamma", 5));
    }

    [Fact]
    public void SearchRanksDocumentsContainingTheQueryTerm()
    {
        using var index = new Bm25Index();
        Guid relevant = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        index.Add(relevant, "vector database similarity search over embeddings");
        index.Add(other, "the cat sat on the mat");

        IReadOnlyList<(Guid Id, double Score)> results = index.Search("similarity search", 5);

        Assert.NotEmpty(results);
        Assert.Equal(relevant, results[0].Id);
        Assert.DoesNotContain(results, r => r.Id == other);
    }

    [Fact]
    public void SearchHonorsTopK()
    {
        using var index = new Bm25Index();
        for (int i = 0; i < 5; i++)
        {
            index.Add(Guid.NewGuid(), "shared token doc number " + i);
        }

        Assert.Equal(2, index.Search("shared token", 2).Count);
    }

    [Fact]
    public void EmptyQueryAndEmptyIndexReturnNoResults()
    {
        using var index = new Bm25Index();
        Assert.Empty(index.Search("anything", 5));

        index.Add(Guid.NewGuid(), "some content");
        Assert.Empty(index.Search("   ", 5));
    }

    [Fact]
    public void SaveAndLoadRoundTripsSearchResults()
    {
        Guid id = Guid.NewGuid();
        byte[] bytes;
        using (var index = new Bm25Index())
        {
            index.Add(id, "reciprocal rank fusion blends signals");
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms))
            {
                index.Save(writer);
            }

            bytes = ms.ToArray();
        }

        using var loaded = new Bm25Index();
        using (var reader = new BinaryReader(new MemoryStream(bytes)))
        {
            loaded.Load(reader, tracking: false);
        }

        Assert.Equal(1, loaded.Count);
        IReadOnlyList<(Guid Id, double Score)> results = loaded.Search("fusion", 5);
        Assert.Single(results);
        Assert.Equal(id, results[0].Id);
    }

    [Fact]
    public void RemoveAfterTrackingLoadCleansPostings()
    {
        Guid id = Guid.NewGuid();
        using var index = new Bm25Index();
        index.Add(id, "removable content token");

        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            index.Save(writer);
        }

        ms.Position = 0;
        using var loaded = new Bm25Index();
        using (var reader = new BinaryReader(ms))
        {
            loaded.Load(reader, tracking: true);
        }

        loaded.Remove(id);
        Assert.Equal(0, loaded.Count);
        Assert.Empty(loaded.Search("token", 5));
    }

    [Fact]
    public void MappedLoadMatchesInMemorySearch()
    {
        var ids = new Guid[3];
        using var index = new Bm25Index();
        ids[0] = Guid.NewGuid();
        ids[1] = Guid.NewGuid();
        ids[2] = Guid.NewGuid();
        index.Add(ids[0], "vector database similarity search over embeddings");
        index.Add(ids[1], "the cat sat on the mat");
        index.Add(ids[2], "reciprocal rank fusion blends lexical and vector signals");

        IReadOnlyList<(Guid Id, double Score)> expected = index.Search("vector similarity search", 5);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            {
                index.Save(writer);
            }

            using var mapped = new Bm25Index();
            mapped.LoadMapped(path, 0);

            Assert.Equal(index.Count, mapped.Count);
            IReadOnlyList<(Guid Id, double Score)> actual = mapped.Search("vector similarity search", 5);

            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Id, actual[i].Id);
                Assert.Equal(expected[i].Score, actual[i].Score, 6);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MappedIndexIsReadOnly()
    {
        using var index = new Bm25Index();
        index.Add(Guid.NewGuid(), "alpha beta gamma");

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            {
                index.Save(writer);
            }

            using var mapped = new Bm25Index();
            mapped.LoadMapped(path, 0);

            Assert.Throws<InvalidOperationException>(() => mapped.Add(Guid.NewGuid(), "x"));
            Assert.Throws<InvalidOperationException>(() => mapped.Remove(Guid.NewGuid()));
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            Assert.Throws<InvalidOperationException>(() => mapped.Save(w));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
