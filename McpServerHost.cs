using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SemanticSearch;

/// <summary>
/// Hosts ssearch as a Model Context Protocol server over stdio, exposing a single <c>search</c> tool
/// backed by a live, self-updating index (the watcher in <see cref="IndexService" /> keeps it current).
/// <para>
/// The tool is registered through the low-level <see cref="McpServerHandlers" /> request handlers with a
/// hand-authored JSON schema rather than the reflection-based <c>McpServerTool.Create</c> helpers, so the
/// whole path stays trimming- and AOT-safe (no runtime schema generation). The SDK is consumed via the
/// dependency-light <c>ModelContextProtocol.Core</c> package - no Microsoft.Extensions.Hosting or DI
/// container.
/// </para>
/// </summary>
internal static class McpServerHost
{
    private const string SearchToolName = "search";

    private static readonly string Version =
        typeof(McpServerHost).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    private static readonly JsonElement SearchInputSchema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Natural-language description of the code or text to find."
            },
            "top": {
              "type": "integer",
              "description": "Maximum number of results to return (1-50).",
              "default": 5
            }
          },
          "required": ["query"]
        }
        """).RootElement.Clone();

    public static async Task RunAsync(IndexService service, CancellationToken cancellationToken)
    {
        var searchTool = new Tool
        {
            Name = SearchToolName,
            Title = "Semantic search",
            Description =
                "Searches this repository's live semantic index (kept up to date as files change) and " +
                "returns the most relevant passages with their file:line locations. Prefer this over " +
                "literal/grep search when looking for code or docs by meaning rather than exact tokens.",
            InputSchema = SearchInputSchema,
        };

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "ssearch", Version = Version },
            ServerInstructions =
                "Offline semantic search over this repository. Call the 'search' tool with a natural-language " +
                "query to find the most relevant code or documentation passages.",
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) =>
                    ValueTask.FromResult(new ListToolsResult { Tools = [searchTool] }),
                CallToolHandler = (context, ct) => CallToolAsync(service, context, ct),
            },
        };

        await using var transport = new StdioServerTransport("ssearch", loggerFactory: null);
        await using McpServer server = McpServer.Create(transport, options, loggerFactory: null, serviceProvider: null);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<CallToolResult> CallToolAsync(
        IndexService service, RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        CallToolRequestParams? request = context.Params;
        if (request is null || !string.Equals(request.Name, SearchToolName, StringComparison.Ordinal))
        {
            return Error($"Unknown tool '{request?.Name}'.");
        }

        string? query = null;
        int top = 5;
        if (request.Arguments is { } arguments)
        {
            if (arguments.TryGetValue("query", out JsonElement q) && q.ValueKind == JsonValueKind.String)
            {
                query = q.GetString();
            }

            if (arguments.TryGetValue("top", out JsonElement t) && t.ValueKind == JsonValueKind.Number &&
                t.TryGetInt32(out int requested))
            {
                top = Math.Clamp(requested, 1, 50);
            }
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return Error("The 'search' tool requires a non-empty 'query' string argument.");
        }

        IReadOnlyList<(SearchRecord Record, float Score)> results =
            await service.SearchAsync(query, top).ConfigureAwait(false);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = Format(results) }],
        };
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    private static string Format(IReadOnlyList<(SearchRecord Record, float Score)> results)
    {
        if (results.Count == 0)
        {
            return "No matches.";
        }

        var builder = new StringBuilder();
        foreach ((SearchRecord record, float score) in results)
        {
            builder.Append(score.ToString("F3")).Append("  [").Append(record.Language).Append("]  ")
                .Append(record.Source).Append(':').Append(record.Location).Append('\n');
            if (!string.IsNullOrWhiteSpace(record.Title))
            {
                builder.Append("    ").Append(Snippet(record.Title, 90)).Append('\n');
            }

            builder.Append("    ").Append(Snippet(record.Text.ReplaceLineEndings(" "), 200)).Append("\n\n");
        }

        return builder.ToString().TrimEnd();
    }

    private static string Snippet(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "\u2026";
    }
}
