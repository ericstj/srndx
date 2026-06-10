using Srndx;
using Xunit;

namespace Srndx.Tests;

[Collection(SerialTests.Name)]
public class ModelLocatorTests
{
    private static void WithEnv(string variable, string? value, Action body)
    {
        string? original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Fact]
    public void LanguageModelOverridePointsAtSuppliedFile()
    {
        string file = Path.GetTempFileName();
        try
        {
            WithEnv("SRNDX_LANGUAGE_MODEL", file, () =>
                Assert.Equal(file, ModelLocator.LanguageModel));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void LanguageModelOverrideToMissingFileThrows()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".ftz");
        WithEnv("SRNDX_LANGUAGE_MODEL", missing, () =>
        {
            FileNotFoundException ex = Assert.Throws<FileNotFoundException>(() => _ = ModelLocator.LanguageModel);
            Assert.Contains("SRNDX_LANGUAGE_MODEL", ex.Message);
        });
    }

    [Fact]
    public void EmbeddingModelOverridePointsAtSuppliedDirectory()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            WithEnv("SRNDX_EMBEDDING_MODEL", dir, () =>
                Assert.Equal(dir, ModelLocator.EmbeddingModel));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EmbeddingModelOverrideToMissingDirectoryThrows()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        WithEnv("SRNDX_EMBEDDING_MODEL", missing, () =>
            Assert.Throws<FileNotFoundException>(() => _ = ModelLocator.EmbeddingModel));
    }

    [Fact]
    public void ModelsBaseDirectoryOverrideComposesDefaultPaths()
    {
        string baseDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            WithEnv("SRNDX_MODELS", baseDir, () =>
            {
                // No per-model overrides, so paths derive from the base directory.
                WithEnv("SRNDX_LANGUAGE_MODEL", null, () =>
                    Assert.Equal(Path.Combine(baseDir, "lid.176.ftz"), ModelLocator.LanguageModel));
                WithEnv("SRNDX_EMBEDDING_MODEL", null, () =>
                    Assert.Equal(Path.Combine(baseDir, "potion-base-2M"), ModelLocator.EmbeddingModel));
            });
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }
}
