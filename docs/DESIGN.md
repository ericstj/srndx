# Design & engineering notes

How `srndx` is put together, and the decisions behind the parts that make it fast. For raw
performance numbers and methodology, see [BENCHMARKS.md](BENCHMARKS.md).

## Architecture

`srndx` composes three pure-managed, no-native-dependency libraries through the standard .NET AI
ecosystem abstractions, and adds a BM25 lexical index of its own:

| Library | Role | Ecosystem abstraction |
| --- | --- | --- |
| [FastText.Net](https://github.com/ericstj/FastText.Net) | Per-item language ID (`lid.176`) | — |
| [Model2Vec.Net](https://github.com/ericstj/Model2Vec.Net) | Text → embedding | `Microsoft.Extensions.AI.IEmbeddingGenerator` |
| [Hnsw.Net](https://github.com/ericstj/Hnsw.Net) | Approximate-nearest-neighbor vector index | `Microsoft.Extensions.VectorData` |

Because each piece is wired in through its ecosystem interface, swapping the vector store (Qdrant,
Azure AI Search, Postgres pgvector, …) or the embedder is a one-line change; everything else —
embedding, upsert, filtered search — goes through the standard abstractions.

### Commands

- **`index`** walks a folder and/or a git repository. Files are split into passages (blank-line
  blocks merged to a readable size) so a hit points at a specific line range; commits become one
  passage each (subject + body), located by short SHA. Inside a git repo, discovery honors
  `.gitignore`; otherwise common build/tooling directories (`bin`, `obj`, `node_modules`, …) are
  skipped by name. Every passage is language-detected and embedded, and its tokens are added to the
  BM25 index. The vector index is written as independent HNSW shards plus the BM25 index, all in a
  single file.
- **`search`** loads the index and returns the most relevant passages by fusing semantic similarity
  (vectors) with lexical BM25 relevance via reciprocal-rank fusion, with optional `--lang`/`--source`
  filters (MEVD LINQ filters under the hood).
- **`serve`** keeps an index in sync with a watched directory and answers queries interactively. It
  watches with a `FileSystemWatcher`, coalesces a burst of edits over a `--debounce` window, and
  applies incremental updates. A re-indexed edit to a known file skips the per-file `.gitignore`
  check (it passed once); a brand-new file is indexed immediately and its ignore status is verified
  out of band, so indexing never blocks on git. The index is persisted atomically after each batch
  and on shutdown.
- **`mcp`** runs the same self-updating index as a [Model Context Protocol](https://modelcontextprotocol.io)
  server over stdio, exposing a single `search` tool. It uses the dependency-light
  `ModelContextProtocol.Core` package with low-level handlers and a hand-authored schema, so it stays
  Native-AOT clean; index progress goes to stderr to keep the stdout JSON-RPC stream clean.
- **`stop`** flushes and stops a backgrounded `serve`/`mcp` process.
- **`install-mcp`** / **`install-skill`** wire `srndx` into a repository for agents — merging an
  `srndx` server into `.github/mcp.json`, or emitting an Agent Skill at `.github/skills/<name>/SKILL.md`.

## The performance journey

The published numbers are the result of three rounds of work, each targeting whatever had become the
dominant cost.

### 1. Cold start: memory-map the index

A one-shot `srndx search` is a fresh process, so the index load is on the critical path. Fully
deserializing the index made cold start scale with index size — on a 600k-passage corpus the BM25
half alone took several seconds.

Both halves are now memory-mapped. The BM25 lexical index uses a term-major on-disk layout that the
reader binary-searches directly, faulting in only the postings for the queried terms. The vector
shards are mapped the same way (vectors and record payloads are faulted in on demand). Cold start
became roughly independent of index size: load is `max(slowest shard, lexical)` across cores, not the
sum of full deserialization.

### 2. Indexing throughput: parallelize the embarrassingly-parallel parts

Profiling the build showed the cost split roughly as: HNSW graph insertion ~60%, embedding ~15%,
language detection ~10%, BM25 ~10%. Language detection (FastText uses thread-static scratch state) and
embedding (a stateless encoder) are both safe to run concurrently, so both now fan out across cores,
and batches are sized so the fan-out amortizes.

`--ef-construction <N>` (default 200) exposes the HNSW build beam width as a direct lever: lower
values build each graph faster at a small recall cost. It is left opt-in because, unlike sharding, it
trades real recall against the same index.

### 3. The graph build: shard it

HNSW insertion is serial under a single writer lock per graph, so it was the build's long pole and it
grew with corpus size. Rather than rewrite the index for fine-grained locking, `srndx` splits the
vector index into *K* independent HNSW shards (`--shards`, default 8), routed by record key:

- **Build** fans out across shards (each has its own lock); smaller graphs also have cheaper inserts.
- **Cold start** maps each shard as its own segment, on its own core.
- **Search** embeds the query once, searches every shard concurrently by vector, and merges the
  ranked results before fusing with BM25.

The key result is that **sharding preserves recall**. Scatter-gather across *K* shards searched at the
same beam width covers at least as much of the space as one large graph at that width; a synthetic
ground-truth check (vs. an exact brute-force index) confirms sharded recall ≥ the single graph. On the
real corpus, top-10 results overlap the single-graph index ~92% — which is within the ~94% overlap two
independent single-graph rebuilds already have, i.e. ordinary approximate-nearest-neighbor
nondeterminism, not a sharding penalty.

See [BENCHMARKS.md](BENCHMARKS.md) for the build-time and latency numbers.

## Native AOT

`srndx` publishes as a self-contained native executable with no managed JIT and no native ML
dependency:

```sh
dotnet publish src/Srndx/Srndx.csproj -r win-x64 -c Release
```

Two things make this work:

- **Reflection-free persistence.** `Microsoft.Extensions.VectorData` has no save/load API, so `srndx`
  breaks glass to the concrete `HnswCollection<TKey, TRecord>` shards and calls their provider Save /
  Load, passing a source-generated `JsonSerializerContext`
  ([SearchSerializerContext.cs](../src/Srndx/SearchSerializerContext.cs)) so records serialize without
  runtime reflection. Each shard is one mappable segment in the index file.
- **Preserved record shape.** The MEVD connector maps the record by reflection; its members are kept
  under trimming via [ILLink.Descriptors.xml](../src/Srndx/ILLink.Descriptors.xml).

## Packaging

`srndx` ships as a [RID-specific .NET tool](https://learn.microsoft.com/dotnet/core/tools/rid-specific-tools):
native-AOT packages for common platforms (`dotnet pack -r <rid>`) plus a portable CoreCLR fallback
(`dotnet pack -r any -p:PublishAot=false`) selected when no RID matches, and a RID-agnostic pointer
package (`dotnet pack`) that ties `dotnet tool install dotnet-srndx` to the right platform package.
The ML models are bundled into each package, so the installed tool is self-contained. CI builds every
package on its matching OS on each push, and on a version tag (`v*`) publishes the set to the
repository's private GitHub Packages feed.

## Limitations and scope

`srndx` is a **retrieval** tool: it returns the most relevant *passages* by meaning and keyword. It does
not parse code into an AST, resolve symbols, or build a call/inheritance graph. That shapes what it is
and isn't good at — worth knowing before reaching for it.

Where it does well:

- **Intent / "how do I…" queries** — the semantic half finds passages that mean the same thing even with
  no shared keywords.
- **Distinctive or namespace-qualified names** — `HttpClient`, `System.Text.Json.JsonSerializer`,
  `TimeOnly` reliably surface the defining file (the path index favors a short, exact path/name match).
- **Concepts with a canonical home** — e.g. a query about garbage collection lands on the GC design doc;
  "thread pool work stealing" lands on `ThreadPoolWorkQueue.cs`.

Known weak spots:

- **Common single-word type names** (e.g. `List`, `Dictionary`). The token is so frequent — and the same
  name is reused across the BCL, tests, native code, and third-party sources (e.g. a compression
  "dictionary") — that the canonical definition is often not even retrieved. Picking *which* same-named
  file is meant requires knowing it's a type, which needs symbol/structure information `srndx` doesn't
  have. (Lexical re-ranking and a file-name boost were tried and don't reliably help: weighting the name
  match high enough to win promotes the *wrong* same-named file, and weighting it low leaves it neutral.)
- **Purely conceptual queries with no keyword anchor** (e.g. "how is async/await implemented", "string
  interning"). Ranking quality here is bounded by the embedding model; the bundled `potion-base-2M` is
  tiny. A larger Model2Vec model (see [Bring your own model](../README.md#bring-your-own-model)) improves
  these directly, at the cost of startup and footprint.
- **Keyword collisions across layers** — e.g. a P/Invoke query can land on a same-named native
  (`corehost`) symbol rather than the managed declaration, because `srndx` ranks text, not call sites.

In short: `srndx` answers "find passages about X" or "where is the type named X", not "where is symbol X
*defined*, what *references* it, or what *derives* from it". Precise symbol navigation — definitions,
references, inheritance, call chains, disambiguating same-named symbols — is the job of a language-aware
code-intelligence tool (an AST / symbol-graph indexer, or your editor's go-to-definition). The two are
complementary: use `srndx` to find the relevant region by meaning, and a symbol tool to resolve exact
structure. `srndx` deliberately stays in the lightweight, language-agnostic, no-native-dependency lane
rather than reimplementing that heavier machinery.
