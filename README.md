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

Search is **hybrid**: a built-in [BM25 lexical index](Bm25Index.cs) (exact-token relevance) is fused
with the semantic vector index via reciprocal-rank fusion, so both keyword and intent matches surface.

## How it works

1. **`index`** walks a folder and/or a git repository.
   - **Files** are split into passages (blank-line blocks, merged to a readable size) so a hit
     points at a specific line range, not a whole file.
   - When the folder is inside a git repository, file discovery honors `.gitignore` (build output,
     restored packages, and tooling directories are skipped); otherwise common build and tooling
     directories (`bin`, `obj`, `node_modules`, ...) are skipped by name.
   - **Commits** become one passage each (subject + body), located by short SHA.
   - Every passage is language-detected (FastText.Net) and embedded automatically by the vector
     store (Model2Vec.Net, wired in as an `IEmbeddingGenerator`); its tokens are also added to a
     built-in BM25 lexical index.
   - The vector index and the BM25 lexical index are persisted together to a single file.
2. **`search`** loads that index and returns the most relevant passages by fusing semantic similarity
   (the vector index) with lexical BM25 relevance (reciprocal-rank fusion), with optional
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

## Benchmarks: hybrid search vs `grep`

How does hybrid (semantic + BM25) search compare to literal search? Measured on
[`dotnet/extensions`](https://github.com/dotnet/extensions) (3,661 indexed files) with the
Native-AOT `ssearch` executable, against `git grep` run from the repo root.

Building the index is a one-time cost; `git grep` pays its full cost on every query:

| | value |
| --- | --- |
| `ssearch index` (one-time) | **16.4 s** — 3,661 files → 20,487 passages |
| index size | 49 MB (vector + BM25) |

Per-query latency: a cold `ssearch search` (start + model load + index load + query) runs **~0.4 s**,
~2× faster than `git grep` over this repo (**~0.85 s**). `grep` re-walks the repo on every query;
`ssearch` loads a prebuilt index — and the BM25 half is loaded on a second core in parallel with the
vector index, so it adds essentially nothing to cold start. `serve` / `mcp` keep the index warm so
repeat queries skip the reload entirely.

**Search by intent** — phrases with no shared keywords, where `grep` has nothing literal to match:

| Query (typed as intent) | `ssearch` top hit | `git grep` same phrase |
| --- | --- | --- |
| validate options at startup | `Diagnostics.Probes.Tests…OptionsValidatorTests` ✓ | 0 hits |
| circuit breaker half-open state | `Http.Resilience…CustomValidator` ✓ | 0 hits |
| pool and reuse objects | `Shared/Pools/PoolFactory.cs` ✓ | 0 hits |
| retry with exponential backoff | `Http.Resilience.Tests…HttpRetryStrategyOptionsTests` ✓ | 0 hits |
| redact sensitive data from logs | `core-templates/steps/publish-logs.yml` ~ | 0 hits |

**Exact identifiers** — where the BM25 half earns its keep: typing the bare identifier now lands on a
genuinely relevant file (it used to drift off-topic under pure-semantic search), while `grep` returns
every literal occurrence for you to scan:

| Identifier | `ssearch` top hit | `git grep` |
| --- | --- | --- |
| `ValidateOnStart` | `HeaderParsing…ServiceCollectionExtensions` ✓ | 53 lines / 23 files |
| `Backoff` | `Http.Resilience.Tests…HttpClientBuilderExtensions` ✓ | 6 lines / 6 files |
| `CircuitBreaker` | `Http.Resilience…CustomValidator` ✓ | 56 lines / 24 files |
| `Redact` | `Compliance.Testing.Tests…FakeRedactorTests` ✓ | 1,853 lines / 159 files |
| `ObjectPool` | `Telemetry…ResetOnGetObjectPool` ✓ | 173 lines / 74 files |

What this shows:

- **Hybrid covers both modes.** BM25 supplies exact-identifier precision; the embeddings supply
  intent; reciprocal-rank fusion merges them, so the same query box answers "where is `ObjectPool`?"
  *and* "where do we pool and reuse objects?".
- **`grep` is exhaustive and unranked; `ssearch` is focused and ranked.** `grep` on `Redact` returns
  1,853 lines across 159 files; `ssearch` returns the single most relevant passage. Reach for `grep`
  when you want every occurrence of a known token, `ssearch` when you want the most relevant few.
- **Intent quality is bounded by the embedding model, honestly.** One of the five intent queries
  returned a loosely related top hit (`~`) — expected from the tiny `potion-base-2M` model; swapping a
  larger Model2Vec model trades startup/footprint for better semantic ranking with no code change.

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
