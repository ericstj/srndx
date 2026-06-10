using Xunit;

namespace Srndx.Tests;

/// <summary>
/// A <see cref="FactAttribute" /> that skips when the bundled models are not present next to the test
/// assembly (for example, an offline build that could not download them). Integration tests that load
/// the real FastText and Model2Vec models use this so the suite stays green without network access.
/// </summary>
public sealed class ModelFactAttribute : FactAttribute
{
    public ModelFactAttribute()
    {
        if (!ModelsAvailable.Value)
        {
            Skip = "Models are not present next to the test assembly; skipping integration test.";
        }
    }
}

internal static class ModelsAvailable
{
    private static readonly Lazy<bool> Present = new(() =>
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "models");
        return File.Exists(Path.Combine(dir, "lid.176.ftz"))
            && Directory.Exists(Path.Combine(dir, "potion-base-2M"));
    });

    public static bool Value => Present.Value;
}
