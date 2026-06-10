namespace Srndx;

/// <summary>A unit of content to index, produced by a source (files or git) before embedding.</summary>
/// <param name="Source">Origin tag: <c>file</c> or <c>git</c>.</param>
/// <param name="Location">Where to find it: <c>path:startLine-endLine</c> or a short SHA.</param>
/// <param name="Title">A short label (file name or commit subject).</param>
/// <param name="Text">The text to embed and display.</param>
public readonly record struct Passage(string Source, string Location, string Title, string Text);
