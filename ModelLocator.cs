namespace Srndx;

/// <summary>
/// Resolves the on-disk model files. Each model can be swapped independently (bring-your-own-model):
/// <list type="bullet">
///   <item><c>SRNDX_LANGUAGE_MODEL</c> — path to a fastText language model file.</item>
///   <item><c>SRNDX_EMBEDDING_MODEL</c> — path to a Model2Vec model directory.</item>
///   <item><c>SRNDX_MODELS</c> — base directory holding both default models; used when the
///         per-model overrides above are unset.</item>
/// </list>
/// When nothing is overridden, the models bundled next to the executable are used.
/// </summary>
internal static class ModelLocator
{
    private const string LanguageModelFileName = "lid.176.ftz";
    private const string EmbeddingModelDirName = "potion-base-2M";

    public static string ModelsDirectory
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable("SRNDX_MODELS");
            if (!string.IsNullOrEmpty(overridden) && Directory.Exists(overridden))
            {
                return overridden;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "models");
            if (Directory.Exists(local))
            {
                return local;
            }

            throw new DirectoryNotFoundException(
                $"Could not find a models directory at '{local}'. Set SRNDX_MODELS to a folder " +
                $"containing '{LanguageModelFileName}' and the '{EmbeddingModelDirName}' model directory, or set " +
                "SRNDX_LANGUAGE_MODEL / SRNDX_EMBEDDING_MODEL to swap an individual model.");
        }
    }

    public static string LanguageModel =>
        ResolveOverride("SRNDX_LANGUAGE_MODEL", File.Exists, "language model file")
            ?? Path.Combine(ModelsDirectory, LanguageModelFileName);

    public static string EmbeddingModel =>
        ResolveOverride("SRNDX_EMBEDDING_MODEL", Directory.Exists, "embedding model directory")
            ?? Path.Combine(ModelsDirectory, EmbeddingModelDirName);

    private static string? ResolveOverride(string variable, Func<string, bool> exists, string what)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!exists(value))
        {
            throw new FileNotFoundException($"{variable} points to '{value}', but no {what} exists there.");
        }

        return value;
    }
}
