using Srndx;
using Xunit;

namespace Srndx.Tests;

public class TokenizeTests
{
    [Theory]
    [InlineData("hello", new[] { "hello" })]
    [InlineData("foo-bar baz", new[] { "foo", "bar", "baz" })]
    [InlineData("  spaced   out ", new[] { "spaced", "out" })]
    [InlineData("ValidateOnStart", new[] { "validate", "on", "start", "validateonstart" })]
    [InlineData("abc123", new[] { "abc", "123", "abc123" })]
    public void TokenizeSplitsAndEmitsExpectedTokens(string input, string[] expected)
    {
        Assert.Equal(expected, Bm25Index.Tokenize(input).ToArray());
    }

    [Fact]
    public void TokenizeOfEmptyOrSymbolOnlyTextYieldsNothing()
    {
        Assert.Empty(Bm25Index.Tokenize(""));
        Assert.Empty(Bm25Index.Tokenize("--- *** ---"));
    }
}
