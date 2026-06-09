# ssearch

**Offline semantic search over your local files and git history.** Ask in plain language,
get back the passages and commits that mean the same thing — even when they share no keywords.

`ssearch` is a small .NET CLI that composes three pure-managed, **no-native-dependency**
libraries through the standard .NET AI ecosystem abstractions:

| Library | Role | Ecosystem abstraction |
| --- | --- | --- |
| [FastText.Net](https://github.com/ericstj/FastText.Net) | Detects each item's language (`lid.176`) | — |
| [Model2Vec.Net](https://github.com/ericstj/Model2Vec.Net) | Turns text into embeddings | `Microsoft.Extensions.AI.IEmbeddingGenerator` |
| [Hnsw.Net](https://github.com/ericstj/Hnsw.Net) | Approximate-nearest-neighbor vector index | `Microsoft.Extensions.VectorData` |

No GPU, no cloud, no API key, no native binary. Everything runs in-process, anywhere .NET runs.

## How it works

1. **`index`** walks a folder and/or a git repository.
   - **Files** are split into passages (blank-line blocks, merged to a readable size) so a hit
     points at a specific line range, not a whole file.
   - When the folder is inside a git repository, file discovery honors `.gitignore` (build output,
     restored packages, and tooling directories are skipped); otherwise common build and tooling
     directories (`bin`, `obj`, `node_modules`, ...) are skipped by name.
   - **Commits** become one passage each (subject + body), located by short SHA.
   - Every passage is language-detected (FastText.Net) and embedded automatically by the vector
     store (Model2Vec.Net, wired in as an `IEmbeddingGenerator`).
   - The Hnsw.Net index is persisted to a single file.
2. **`search`** loads that index and returns the closest passages by meaning, with optional
   `--lang` and `--source` filters (MEVD LINQ filters under the hood).
3. **`serve`** keeps an index in sync with a watched directory and answers queries interactively.
   - It builds the index on first run (or loads an existing one), then watches the directory with a
     `FileSystemWatcher` and re-indexes on change. A burst of edits is coalesced over a short
     `--debounce` window before a single incremental update.
   - A re-indexed edit to a file already in the index skips the per-file `.gitignore` check (it
     passed once); a brand-new file is indexed immediately and its ignore status is verified out of
     band, pruning it if it turns out to be ignored - so indexing never blocks on git.
   - The index is persisted atomically after each batch and on shutdown (`:quit` or Ctrl+C).
   - Re-indexing repeated/unchanged passages is short-circuited by a small
     [`Microsoft.Extensions.AI` embedding cache](CachingEmbeddingGenerator.cs) layered in front of
     the embedder (a `DelegatingEmbeddingGenerator`, the standard MEAI middleware extension point).
4. **`mcp`** runs the same self-updating index as a [Model Context Protocol](https://modelcontextprotocol.io)
   server over stdio, exposing a single `search` tool so agents can query by meaning while the index
   stays current. It uses the dependency-light `ModelContextProtocol.Core` package (no hosting/DI
   container) and registers the tool through low-level handlers with a hand-authored schema, so it
   stays Native-AOT clean. Index progress is written to stderr to keep the stdout JSON-RPC stream clean.
5. **`install-mcp`** / **`install-skill`** wire ssearch into a repository for agents: the former merges
   an `ssearch` entry into `.github/mcp.json` (preserving any existing servers); the latter emits an
   Agent Skill at `.github/skills/<name>/SKILL.md` describing how to drive the CLI.

## Usage

```sh
# Index a docs folder and a repo's recent history into one index file
ssearch index --files ./docs --git ./my-repo --max-commits 500 --out project.index

# Semantic search
ssearch search "how do we authenticate requests" --index project.index

# Filter by source and/or language
ssearch search "corrige el error de concurrencia" --index project.index --lang es --source git --top 10

# Run as a service: watch a directory, keep the index live, and query interactively
ssearch serve --files ./src --index project.index
#   search> how do we retry failed requests
#   search> :count
#   search> :quit

# Run as an MCP server over stdio (live, self-updating index with a 'search' tool)
ssearch mcp --files ./src --index project.index

# Wire ssearch into a repository for agents
ssearch install-mcp --repo .      # merge an 'ssearch' server into .github/mcp.json
ssearch install-skill --repo .    # emit .github/skills/ssearch/SKILL.md
```

Run `ssearch --help` (or `ssearch <command> --help`) for all options.

## Benchmarks: semantic search vs `grep`

How does meaning-based search actually compare to literal search? Measured on
[`dotnet/extensions`](https://github.com/dotnet/extensions) (3,661 git-tracked files) with the
Native-AOT `ssearch` executable, against `git grep` run from the repo root for the same scenarios.

Building the index is a one-time cost; `git grep` pays its full cost on every query:

| | value |
| --- | --- |
| `ssearch index` (one-time) | **15.4 s** — 3,661 files → 20,487 passages |
| index size | 38 MB |

Per-query latency (each `ssearch search` is a cold process: start + model load + index load + query):

| Scenario (typed as intent) | `ssearch` | `git grep` — same phrase | `git grep` — expert token |
| --- | --- | --- | --- |
| validate options at startup | **375 ms** ✓ | 956 ms — 0 hits | 912 ms — 53 lines (`ValidateOnStart`) |
| http retry w/ exponential backoff | **377 ms** ✓ | 893 ms — 2 lines | 994 ms — 15 lines (`Backoff`) |
| circuit breaker opens on failures | **358 ms** ~ | 872 ms — 0 hits | 947 ms — 58 lines (`CircuitBreaker`) |
| redact PII in logs | **371 ms** ✓ | 823 ms — 0 hits | 998 ms — 2,336 lines (`Redact`) |
| pool/reuse objects | **349 ms** ✓ | 861 ms — 0 hits | 1,000 ms — 185 lines (`ObjectPool`) |

What this shows:

- **Latency.** A cold `ssearch` query (~360 ms) is ~2.5× faster than `git grep` over this repo
  (~0.9 s), because `grep` re-walks every file each time while `ssearch` queries a prebuilt index.
  `serve` / `mcp` keep that index warm, so subsequent queries are faster still.
- **Search by intent.** Typing the *concept* literally, `grep` found nothing in four of five cases;
  `ssearch` returned the on-target `file:line` every time (PII → `Telemetry` redaction docs +
  `ExtendedLogger.cs`; pooling → `Shared/Pools/PoolFactory.cs`) — ranked by relevance.
- **Where `grep` wins, honestly.** When you already know the exact identifier, `grep` is precise and
  exhaustive. For "circuit breaker," `grep` on `CircuitBreaker` hit 25 files; `ssearch`'s top result
  was a weak 0.554 and lower hits drifted off-topic — semantic scores degrade gracefully, but this is
  not a literal-precision tool. And `grep` on `Redact` returned 2,336 unranked lines across 181 files,
  where `ssearch` returned the 3 most relevant.

Different tools: reach for `grep` when you know the token and want every occurrence; reach for
`ssearch` when you're searching by meaning, want ranked results, and want query latency that doesn't
grow with the repo.

## Models

The tool needs two model files, resolved from the `models/` folder next to the binary (override
with the `SEMANTIC_SEARCH_MODELS` environment variable):

- `lid.176.ftz` — FastText language-identification model.
- `potion-base-2M/` — Model2Vec embedding model (`config.json`, `model.safetensors`, `tokenizer.json`).

## Break glass: persistence

The `Microsoft.Extensions.VectorData` abstraction has no save/load API. `ssearch` follows the
ecosystem convention of *breaking glass* to the concrete provider type: it holds the concrete
`HnswCollection<TKey, TRecord>` and calls its provider-specific `Save` / `Load`. Everything else —
embedding, upsert, filtered search — goes through the standard abstractions, so swapping Hnsw.Net
for another vector store (Qdrant, Azure AI Search, Postgres pgvector, …) or Model2Vec.Net for
another embedder is a one-line change.

## Native AOT

`ssearch` publishes as a self-contained native executable with **no managed JIT and no native ML
dependency**:

```sh
dotnet publish -r win-x64 -c Release
```

Two things make this work:

- **Reflection-free persistence.** Hnsw.Net's `Save(Stream, JsonSerializerContext)` /
  `Load(Stream, JsonSerializerContext)` overloads take a source-generated
  [`JsonSerializerContext`](SearchSerializerContext.cs), so records serialize without runtime
  reflection.
- **Preserved record shape.** The MEVD connector maps the record by reflection; its members are
  kept under trimming via [`ILLink.Descriptors.xml`](ILLink.Descriptors.xml).

## Why these libraries

This is a working showcase of a fully managed semantic-search stack with zero native
dependencies — useful when you want private, offline retrieval that ships as plain NuGet packages
and runs everywhere the .NET runtime does.
