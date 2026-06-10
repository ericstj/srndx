using Microsoft.Extensions.AI;
using Srndx;
using Xunit;

namespace Srndx.Tests;

public class ParallelEmbeddingGeneratorTests
{
    private sealed class IndexEchoGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int CallCount;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            var list = new List<Embedding<float>>();
            foreach (string v in values)
            {
                // Encode the input deterministically so the test can verify order is preserved.
                list.Add(new Embedding<float>(new float[] { float.Parse(v) }));
            }

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public async Task PreservesInputOrderAcrossPartitions()
    {
        var inner = new IndexEchoGenerator();
        using var parallel = new ParallelEmbeddingGenerator(inner, maxDegreeOfParallelism: 4);

        string[] inputs = [.. Enumerable.Range(0, 1000).Select(i => i.ToString())];
        GeneratedEmbeddings<Embedding<float>> result = await parallel.GenerateAsync(inputs);

        Assert.Equal(inputs.Length, result.Count);
        for (int i = 0; i < inputs.Length; i++)
        {
            Assert.Equal(i, result[i].Vector.Span[0]);
        }
    }

    [Fact]
    public async Task FansOutLargeBatchesAcrossPartitions()
    {
        var inner = new IndexEchoGenerator();
        using var parallel = new ParallelEmbeddingGenerator(inner, maxDegreeOfParallelism: 4);

        await parallel.GenerateAsync([.. Enumerable.Range(0, 400).Select(i => i.ToString())]);

        Assert.Equal(4, inner.CallCount);
    }

    [Fact]
    public async Task SingleInputDoesNotFanOut()
    {
        var inner = new IndexEchoGenerator();
        using var parallel = new ParallelEmbeddingGenerator(inner, maxDegreeOfParallelism: 4);

        GeneratedEmbeddings<Embedding<float>> result = await parallel.GenerateAsync(["7"]);

        Assert.Equal(1, inner.CallCount);
        Assert.Equal(7, result[0].Vector.Span[0]);
    }

    [Theory]
    [InlineData(41, 20)]
    [InlineData(7, 4)]
    [InlineData(5, 5)]
    [InlineData(101, 8)]
    public async Task HandlesCountsThatDoNotDivideEvenlyByDegree(int count, int degree)
    {
        var inner = new IndexEchoGenerator();
        using var parallel = new ParallelEmbeddingGenerator(inner, maxDegreeOfParallelism: degree);

        string[] inputs = [.. Enumerable.Range(0, count).Select(i => i.ToString())];
        GeneratedEmbeddings<Embedding<float>> result = await parallel.GenerateAsync(inputs);

        Assert.Equal(count, result.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i, result[i].Vector.Span[0]);
        }
    }
}
