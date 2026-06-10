using Microsoft.Extensions.AI;

namespace Srndx;

/// <summary>
/// A Microsoft.Extensions.AI embedding middleware that fans a batch out across CPU cores. The
/// underlying Model2Vec.Net embedder encodes a batch with a single-threaded loop; indexing a large
/// corpus embeds millions of passages, so splitting each batch over the thread pool turns embedding
/// from a serial step into a parallel one. The inner generator is stateless and read-only, so
/// concurrent encoding is safe; output order matches input order.
/// </summary>
public sealed class ParallelEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> inner,
    int? maxDegreeOfParallelism = null)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    private readonly int _maxDegree = maxDegreeOfParallelism is > 0 ? maxDegreeOfParallelism.Value : Environment.ProcessorCount;

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> inputs = values as IReadOnlyList<string> ?? values.ToList();

        int partitions = Math.Min(_maxDegree, inputs.Count);
        if (partitions <= 1)
        {
            return await base.GenerateAsync(inputs, options, cancellationToken).ConfigureAwait(false);
        }

        int chunk = (inputs.Count + partitions - 1) / partitions;
        var tasks = new Task<GeneratedEmbeddings<Embedding<float>>>[partitions];
        for (int p = 0; p < partitions; p++)
        {
            int start = p * chunk;
            int end = Math.Min(start + chunk, inputs.Count);
            string[] slice = new string[end - start];
            for (int i = start; i < end; i++)
            {
                slice[i - start] = inputs[i];
            }

            tasks[p] = Task.Run(() => base.GenerateAsync(slice, options, cancellationToken), cancellationToken);
        }

        GeneratedEmbeddings<Embedding<float>>[] parts = await Task.WhenAll(tasks).ConfigureAwait(false);

        var results = new Embedding<float>[inputs.Count];
        int offset = 0;
        foreach (GeneratedEmbeddings<Embedding<float>> part in parts)
        {
            for (int i = 0; i < part.Count; i++)
            {
                results[offset++] = part[i];
            }
        }

        return new GeneratedEmbeddings<Embedding<float>>(results);
    }
}
