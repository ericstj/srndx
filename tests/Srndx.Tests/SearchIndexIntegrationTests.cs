using Srndx;
using Xunit;

namespace Srndx.Tests;

[Collection(SerialTests.Name)]
public class SearchIndexIntegrationTests
{
    private static readonly Passage[] Corpus =
    [
        new("file", "vectors.md:1-3", "Vector search",
            "An approximate nearest neighbor index finds similar embeddings quickly."),
        new("file", "bm25.md:1-3", "Lexical search",
            "BM25 ranks documents by exact term frequency and inverse document frequency."),
        new("file", "cuisine.md:1-2", "Cooking",
            "Slowly caramelize the onions before adding the garlic and tomatoes."),
    ];

    [ModelFact]
    public void DetectsEnglishAndFrench()
    {
        using var index = new SearchIndex();

        Assert.Equal("en", index.DetectLanguage("The quick brown fox jumps over the lazy dog.").Language);
        Assert.Equal("fr", index.DetectLanguage("Le renard brun rapide saute par-dessus le chien paresseux.").Language);
    }

    [ModelFact]
    public async Task IndexAndSearchRanksTheRelevantPassageFirst()
    {
        using var index = new SearchIndex();
        Assert.Equal(Corpus.Length, await index.IndexAsync(Corpus));

        IReadOnlyList<(SearchRecord Record, float Score)> results =
            await index.SearchAsync("nearest neighbor similarity search", top: 3);

        Assert.NotEmpty(results);
        Assert.Equal("Vector search", results[0].Record.Title);
    }

    [ModelFact]
    public async Task SaveAndLoadRoundTripsTheIndex()
    {
        using var ms = new MemoryStream();
        using (var index = new SearchIndex())
        {
            await index.IndexAsync(Corpus);
            index.Save(ms);
        }

        ms.Position = 0;
        using var reloaded = new SearchIndex();
        reloaded.Load(ms);

        IReadOnlyList<(SearchRecord Record, float Score)> results =
            await reloaded.SearchAsync("nearest neighbor similarity search", top: 3);

        Assert.NotEmpty(results);
        Assert.Equal("Vector search", results[0].Record.Title);
    }

    [ModelFact]
    public async Task RestoresShardCountFromFileOnLoad()
    {
        using var ms = new MemoryStream();
        using (var index = new SearchIndex(shards: 4))
        {
            await index.IndexAsync(Corpus);
            Assert.Equal(4, index.ShardCount);
            index.Save(ms);
        }

        ms.Position = 0;
        using var reloaded = new SearchIndex(shards: 8);
        reloaded.Load(ms);

        // The shard layout is a property of the file, not the loader.
        Assert.Equal(4, reloaded.ShardCount);

        IReadOnlyList<(SearchRecord Record, float Score)> results =
            await reloaded.SearchAsync("nearest neighbor similarity search", top: 3);

        Assert.NotEmpty(results);
        Assert.Equal("Vector search", results[0].Record.Title);
    }
}
