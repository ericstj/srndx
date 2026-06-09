using System.Text.Json.Serialization;

namespace SemanticSearch;

/// <summary>
/// Source-generated JSON metadata for the persisted record and key types. Supplying this to
/// <c>HnswCollection.Save/Load</c> keeps index persistence reflection-free, so ssearch publishes
/// cleanly with <c>PublishAot</c>.
/// </summary>
[JsonSerializable(typeof(SearchRecord))]
[JsonSerializable(typeof(Guid))]
internal sealed partial class SearchSerializerContext : JsonSerializerContext;
