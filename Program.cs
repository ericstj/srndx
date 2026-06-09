using System.CommandLine;
using SemanticSearch;

var filesOption = new Option<string?>("--files", "-f")
{
    Description = "Directory of text/source files to index (recursively).",
};
var gitOption = new Option<string?>("--git", "-g")
{
    Description = "Path to a git repository whose commit history should be indexed.",
};
var maxCommitsOption = new Option<int>("--max-commits")
{
    Description = "Maximum number of commits to read from --git.",
    DefaultValueFactory = _ => 500,
};
var outOption = new Option<string>("--out", "-o")
{
    Description = "Path to write the index file.",
    Required = true,
};

var indexCommand = new Command("index", "Build a search index from local files and/or git history.")
{
    filesOption,
    gitOption,
    maxCommitsOption,
    outOption,
};
indexCommand.SetAction((parseResult, _) => RunIndexAsync(
    parseResult.GetValue(filesOption),
    parseResult.GetValue(gitOption),
    parseResult.GetValue(maxCommitsOption),
    parseResult.GetValue(outOption)!));

var queryArgument = new Argument<string>("query")
{
    Description = "The natural-language query to search for.",
};
var indexInOption = new Option<string>("--index", "-i")
{
    Description = "Path to an index file produced by 'index'.",
    Required = true,
};
var langOption = new Option<string?>("--lang", "-l")
{
    Description = "Restrict results to a language (ISO code, e.g. en, fr, de).",
};
var sourceOption = new Option<string?>("--source", "-s")
{
    Description = "Restrict results to a source: file or git.",
};
sourceOption.AcceptOnlyFromAmong("file", "git");
var topOption = new Option<int>("--top", "-n")
{
    Description = "Number of results to return.",
    DefaultValueFactory = _ => 5,
};

var searchCommand = new Command("search", "Search an index built by 'index'.")
{
    queryArgument,
    indexInOption,
    langOption,
    sourceOption,
    topOption,
};
searchCommand.SetAction((parseResult, _) => RunSearchAsync(
    parseResult.GetValue(queryArgument)!,
    parseResult.GetValue(indexInOption)!,
    parseResult.GetValue(langOption),
    parseResult.GetValue(sourceOption),
    parseResult.GetValue(topOption)));

var serveFilesOption = new Option<string>("--files", "-f")
{
    Description = "Directory of text/source files to watch and index (recursively).",
    Required = true,
};
var serveIndexOption = new Option<string>("--index", "-i")
{
    Description = "Index file to maintain. Loaded if it exists, otherwise built from --files.",
    Required = true,
};
var debounceOption = new Option<int>("--debounce")
{
    Description = "Milliseconds to coalesce a burst of file changes before re-indexing.",
    DefaultValueFactory = _ => 500,
};

var serveCommand = new Command("serve", "Run as a service: keep an index in sync with a watched directory and answer queries.")
{
    serveFilesOption,
    serveIndexOption,
    debounceOption,
};
serveCommand.SetAction((parseResult, _) => RunServeAsync(
    parseResult.GetValue(serveFilesOption)!,
    parseResult.GetValue(serveIndexOption)!,
    parseResult.GetValue(debounceOption)));

var mcpFilesOption = new Option<string>("--files", "-f")
{
    Description = "Directory of text/source files to watch and index (recursively).",
    Required = true,
};
var mcpIndexOption = new Option<string>("--index", "-i")
{
    Description = "Index file to maintain. Loaded if it exists, otherwise built from --files.",
    Required = true,
};
var mcpDebounceOption = new Option<int>("--debounce")
{
    Description = "Milliseconds to coalesce a burst of file changes before re-indexing.",
    DefaultValueFactory = _ => 500,
};

var mcpCommand = new Command("mcp",
    "Run as an MCP server over stdio, exposing a 'search' tool backed by a live, self-updating index.")
{
    mcpFilesOption,
    mcpIndexOption,
    mcpDebounceOption,
};
mcpCommand.SetAction((parseResult, _) => RunMcpAsync(
    parseResult.GetValue(mcpFilesOption)!,
    parseResult.GetValue(mcpIndexOption)!,
    parseResult.GetValue(mcpDebounceOption)));

var installRepoOption = new Option<string>("--repo", "-r")
{
    Description = "Target repository directory.",
    DefaultValueFactory = _ => ".",
};
var installMcpPathOption = new Option<string?>("--path")
{
    Description = "Config file to write (default: <repo>/.github/mcp.json).",
};
var installMcpNameOption = new Option<string>("--name")
{
    Description = "MCP server name to register.",
    DefaultValueFactory = _ => "ssearch",
};
var installMcpCommandOption = new Option<string?>("--command")
{
    Description = "Executable to invoke (default: the current ssearch executable).",
};
var installMcpIndexOption = new Option<string?>("--index", "-i")
{
    Description = "Index file the server should maintain (default: <repo>/.github/ssearch.idx).",
};

var installMcpCommand = new Command("install-mcp",
    "Register ssearch as an MCP server in a repository so agents can use its live 'search' tool.")
{
    installRepoOption,
    installMcpPathOption,
    installMcpNameOption,
    installMcpCommandOption,
    installMcpIndexOption,
};
installMcpCommand.SetAction((parseResult, _) => Task.FromResult(Installers.InstallMcp(
    parseResult.GetValue(installRepoOption)!,
    parseResult.GetValue(installMcpPathOption),
    parseResult.GetValue(installMcpNameOption)!,
    parseResult.GetValue(installMcpCommandOption),
    parseResult.GetValue(installMcpIndexOption))));

var installSkillPathOption = new Option<string?>("--path")
{
    Description = "Skill file to write (default: <repo>/.github/skills/<name>/SKILL.md).",
};
var installSkillNameOption = new Option<string>("--name")
{
    Description = "Skill name (directory under .github/skills).",
    DefaultValueFactory = _ => "ssearch",
};

var installSkillCommand = new Command("install-skill",
    "Emit an Agent Skill (SKILL.md) that tells an agent how to use the ssearch CLI.")
{
    installRepoOption,
    installSkillPathOption,
    installSkillNameOption,
};
installSkillCommand.SetAction((parseResult, _) => Task.FromResult(Installers.InstallSkill(
    parseResult.GetValue(installRepoOption)!,
    parseResult.GetValue(installSkillPathOption),
    parseResult.GetValue(installSkillNameOption)!)));

var root = new RootCommand(
    "ssearch - offline semantic search over local files and git history. " +
    "Pure managed: FastText.Net (language ID) + Model2Vec.Net (embeddings) + Hnsw.Net (vector index). " +
    "No native dependency, no GPU, no cloud.")
{
    indexCommand,
    searchCommand,
    serveCommand,
    mcpCommand,
    installMcpCommand,
    installSkillCommand,
};

return await root.Parse(args).InvokeAsync();

static async Task<int> RunIndexAsync(string? filesDir, string? gitRepo, int maxCommits, string outPath)
{
    if (filesDir is null && gitRepo is null)
    {
        Console.Error.WriteLine("Specify at least one source: --files <dir> and/or --git <repo>.");
        return 1;
    }

    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var index = new SearchIndex();

    int total = 0;
    if (filesDir is not null)
    {
        if (!Directory.Exists(filesDir))
        {
            Console.Error.WriteLine($"Directory not found: {filesDir}");
            return 1;
        }

        Console.WriteLine($"Indexing files under {Path.GetFullPath(filesDir)} ...");
        total += await IndexBatchedAsync(index, FileSource.Enumerate(filesDir));
    }

    if (gitRepo is not null)
    {
        Console.WriteLine($"Indexing up to {maxCommits} commits from {Path.GetFullPath(gitRepo)} ...");
        total += await IndexBatchedAsync(index, GitSource.Enumerate(gitRepo, maxCommits));
    }

    using (FileStream stream = File.Create(outPath))
    {
        index.Collection.Save(stream, SearchSerializerContext.Default);
    }

    Console.WriteLine($"Indexed {total} passages. Wrote index to {Path.GetFullPath(outPath)}.");
    return 0;
}

static async Task<int> IndexBatchedAsync(SearchIndex index, IEnumerable<Passage> passages)
{
    const int batchSize = 256;
    int count = 0;
    var batch = new List<Passage>(batchSize);
    foreach (Passage passage in passages)
    {
        batch.Add(passage);
        if (batch.Count == batchSize)
        {
            count += await index.IndexAsync(batch);
            batch.Clear();
            Console.Write($"\r  {count} passages...");
        }
    }

    if (batch.Count > 0)
    {
        count += await index.IndexAsync(batch);
    }

    if (count > 0)
    {
        Console.Write($"\r  {count} passages.   \n");
    }

    return count;
}

static async Task<int> RunSearchAsync(string query, string indexPath, string? language, string? source, int top)
{
    if (!File.Exists(indexPath))
    {
        Console.Error.WriteLine($"Index file not found: {indexPath}. Build one with 'ssearch index'.");
        return 1;
    }

    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var index = new SearchIndex();
    using (FileStream stream = File.OpenRead(indexPath))
    {
        index.Collection.Load(stream, SearchSerializerContext.Default);
    }

    IReadOnlyList<(SearchRecord Record, float Score)> results =
        await index.SearchAsync(query, top, language, source);

    ConsoleResults.Print(results);
    return 0;
}

static async Task<int> RunServeAsync(string filesDir, string indexPath, int debounceMs)
{
    if (!Directory.Exists(filesDir))
    {
        Console.Error.WriteLine($"Directory not found: {filesDir}");
        return 1;
    }

    if (debounceMs < 0)
    {
        Console.Error.WriteLine("--debounce must be non-negative.");
        return 1;
    }

    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var index = new SearchIndex(cacheEmbeddings: true);
    var service = new IndexService(index, filesDir, indexPath, debounceMs);
    await service.InitializeAsync();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, _) =>
    {
        Console.WriteLine("\nShutting down ...");
        service.FlushAsync().GetAwaiter().GetResult();
        cts.Cancel();
    };

    Task maintain = service.RunAsync(cts.Token);
    await service.RunInteractiveAsync(cts.Token);
    cts.Cancel();
    await maintain;
    return 0;
}

static async Task<int> RunMcpAsync(string filesDir, string indexPath, int debounceMs)
{
    if (!Directory.Exists(filesDir))
    {
        Console.Error.WriteLine($"Directory not found: {filesDir}");
        return 1;
    }

    if (debounceMs < 0)
    {
        Console.Error.WriteLine("--debounce must be non-negative.");
        return 1;
    }

    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var index = new SearchIndex(cacheEmbeddings: true);

    // Standard output is the JSON-RPC channel; route all index progress/status to standard error.
    var service = new IndexService(index, filesDir, indexPath, debounceMs, Console.Error);
    await service.InitializeAsync();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    Task maintain = service.RunAsync(cts.Token);
    try
    {
        await McpServerHost.RunAsync(service, cts.Token);
    }
    finally
    {
        cts.Cancel();
        await maintain;
    }

    return 0;
}
