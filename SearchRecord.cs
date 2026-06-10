namespace Srndx;

/// <summary>
/// A single searchable item. <see cref="Text" /> is the vector source: the store embeds it
/// automatically via the configured <c>IEmbeddingGenerator</c>. The remaining properties are
/// filterable/displayable data. The record shape is described by a runtime
/// <c>VectorStoreCollectionDefinition</c> in <see cref="SearchIndex" />, so no attributes are
/// needed and the embedding dimension is taken from the model.
/// </summary>
public sealed class SearchRecord
{
    public Guid Id { get; set; }

    /// <summary>Origin of the item: <c>file</c> or <c>git</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Where to find it: <c>path:startLine-endLine</c> for files, a short SHA for commits.</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>A short label: the file name, or a commit subject.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The detected ISO language code (FastText.Net), e.g. <c>en</c>.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>The passage or commit text. Doubles as the embedded vector source.</summary>
    public string Text { get; set; } = string.Empty;
}
