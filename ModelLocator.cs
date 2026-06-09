namespace SemanticSearch;

/// <summary>Resolves the on-disk model files, honoring the <c>SEMANTIC_SEARCH_MODELS</c> override.</summary>
internal static class ModelLocator
{
    public static string ModelsDirectory
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable("SEMANTIC_SEARCH_MODELS");
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
                $"Could not find a models directory at '{local}'. Set SEMANTIC_SEARCH_MODELS to a folder " +
                "containing 'lid.176.ftz' and the 'potion-base-2M' model directory.");
        }
    }

    public static string LanguageModel => Path.Combine(ModelsDirectory, "lid.176.ftz");

    public static string EmbeddingModel => Path.Combine(ModelsDirectory, "potion-base-2M");
}
