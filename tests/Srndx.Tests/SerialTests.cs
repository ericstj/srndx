using Xunit;

namespace Srndx.Tests;

/// <summary>
/// Tests in this collection do not run in parallel. They either mutate process environment variables
/// (ModelLocator overrides) or load the real models, so isolating them avoids cross-test interference.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialTests
{
    public const string Name = "Serial";
}
