using System.Text.Json;
using System.Text.Json.Nodes;

namespace Srndx;

/// <summary>
/// Emits per-repository integration files: an MCP server entry so agents can reach srndx's live
/// <c>search</c> tool, and an Agent Skill so agents know how to drive the srndx CLI directly.
/// </summary>
internal static class Installers
{
    public static int InstallMcp(string repoDir, string? configPath, string serverName, string? command, string? indexPath)
    {
        string repo = Path.GetFullPath(repoDir);
        if (!Directory.Exists(repo))
        {
            Console.Error.WriteLine($"Directory not found: {repo}");
            return 1;
        }

        string path = configPath is not null ? Path.GetFullPath(configPath) : Path.Combine(repo, ".github", "mcp.json");
        string exe = command ?? Environment.ProcessPath ?? "srndx";
        string index = indexPath ?? Path.Combine(repo, ".github", "srndx.idx");

        JsonObject root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
            : new JsonObject();

        if (root["servers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["servers"] = servers;
        }

        servers[serverName] = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = exe,
            ["args"] = new JsonArray("mcp", "--files", repo, "--index", index),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        Console.WriteLine($"Wrote MCP server '{serverName}' to {path}.");
        Console.WriteLine($"  {exe} mcp --files \"{repo}\" --index \"{index}\"");
        return 0;
    }

    public static int InstallSkill(string repoDir, string? skillPath, string skillName)
    {
        string repo = Path.GetFullPath(repoDir);
        if (!Directory.Exists(repo))
        {
            Console.Error.WriteLine($"Directory not found: {repo}");
            return 1;
        }

        string path = skillPath is not null
            ? Path.GetFullPath(skillPath)
            : Path.Combine(repo, ".github", "skills", skillName, "SKILL.md");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, SkillMarkdown(skillName));

        Console.WriteLine($"Wrote skill to {path}.");
        return 0;
    }

    private static string SkillMarkdown(string skillName) =>
        $$"""
        ---
        name: {{skillName}}
        description: >-
          Offline semantic search over this repository with the srndx CLI. Use it to find code or
          documentation by meaning (not exact tokens) - for example locating where a concept is
          implemented when you don't know the identifier. Pure managed, no cloud, no GPU.
        ---

        # srndx - semantic search for this repository

        `srndx` is an offline semantic search CLI. It embeds text with a static model and serves
        approximate-nearest-neighbour queries from a local index. Reach for it when grep/literal search
        is a poor fit because you are searching by *meaning* rather than an exact string.

        ## When to use

        - "Where is rate limiting handled?" / "find the retry/back-off logic" - conceptual lookups.
        - Exploring an unfamiliar area before reading files.
        - Complement, not replace, grep: use grep for exact identifiers, srndx for intent.

        ## Build an index

        ```sh
        srndx index --files . --out .github/srndx.idx
        ```

        Indexing honours `.gitignore`. Re-run after large changes, or use `serve`/`mcp` to keep an index
        live (see below).

        ## Search

        ```sh
        srndx search "how are auth tokens refreshed" --index .github/srndx.idx --top 5
        ```

        Options:

        - `--top, -n` - number of results (default 5).
        - `--lang, -l` - restrict to a language ISO code (e.g. `en`, `fr`).
        - `--source, -s` - restrict to `file` or `git`.

        Each result line is `score  [language]  source:location`, followed by a snippet. `location` is a
        `path:startLine-endLine` you can open directly.

        ## Keep the index live

        - `srndx serve --files . --index .github/srndx.idx` - interactive prompt that re-indexes
          changed files as you edit.
        - `srndx mcp --files . --index .github/srndx.idx` - same self-updating index exposed as an MCP
          server over stdio with a `search` tool (prefer this when an MCP client is available).
        """;
}
