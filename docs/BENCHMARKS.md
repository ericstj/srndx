# Benchmarks: srndx vs the grep family

How does `srndx` — semantic + keyword (BM25) search over a prebuilt index — compare to literal search
tools on a large codebase? This measures `srndx` against `grep`, `ripgrep`, `git grep`, and
[`tgrep`](https://github.com/microsoft/tgrep) (a code-aware indexed grep) on the same corpus and the
same queries.

## Setup

| | |
| --- | --- |
| Corpus | [`dotnet/runtime`](https://github.com/dotnet/runtime) — 57,923 files → 624,656 passages |
| Machine | 13th Gen Intel Core i7-13800H (14C/20T), 64 GB RAM, Windows 11 |
| `srndx` | Native-AOT build, 8 vector shards (the default), `potion-base-2M` embeddings |
| Tools | ripgrep 15.1.0, git 2.53.0, tgrep 0.1.20, GNU grep (Git for Windows) |
| Method | median of 3 timed reps after one warmup; all tools run from a warm OS file cache |

`srndx` and `tgrep` build an index once and reuse it; the plain greps re-walk the tree on every query.
`srndx search` is reported both **cold** (a fresh process: model load + index map + query) and **warm**
(a resident `srndx serve` answers over the loopback proxy). `tgrep` is likewise reported cold and warm
(resident server). `srndx` returns the top 5 ranked passages by design; the grep tools return every
match. Different contracts — see the takeaways.

## Index build (one-time)

| Tool | Build time | Index size |
| --- | ---: | ---: |
| tgrep | 16.7 s | 379 MB |
| **srndx (8 shards)** | **154.6 s** | 1449 MB |

`srndx` builds a richer index — dense embeddings for every passage plus a BM25 lexical index — so it is
larger and slower to build than a keyword-only index. Sharding the vector index cut that build from
~491 s to ~155 s (see [Sharded indexing](#sharded-indexing) below); it is still a one-time cost
amortized over every later query. The plain greps have no build step.

## Literal identifiers

Exact tokens any literal tool can match. Median query latency across `CancellationToken`,
`ConfigureAwait`, `IAsyncEnumerable`, `StructLayout`:

| Tool | Median latency | Returns |
| --- | ---: | --- |
| grep -r | 39.0 s | all matches |
| ripgrep | 23.2 s | all matches |
| git grep | 8.0 s | all matches |
| tgrep (cold) | 0.32 s | all matches |
| **srndx (cold)** | **1.11 s** | top 5 ranked |
| tgrep (warm) | 0.04 s | all matches |
| **srndx (warm)** | **0.04 s** | top 5 ranked |

Warm, `srndx` (~38 ms) is on par with `tgrep` (~43 ms) and **200–1000× faster** than the tree-walking
greps. Cold, `srndx` (~1.1 s) still beats `git grep`/`ripgrep`/`grep` by 7–35×, but trails `tgrep` cold
(~0.32 s): `srndx`'s cold start additionally loads the language and embedding models and maps the vector
shards, which a keyword-only index does not pay.

## Natural-language queries (intent)

Phrases with no shared keyword — the literal tools have nothing to match. Median latency and number of
results returned, across four intent queries (e.g. *"cancel an async operation with a token"*,
*"parse json into a strongly typed object"*):

| Tool | Median latency | Results |
| --- | ---: | ---: |
| grep -r | 43.1 s | **0** |
| ripgrep | 22.7 s | **0** |
| git grep | 8.2 s | **0** |
| tgrep (cold) | 0.04 s | **0** |
| tgrep (warm) | 0.03 s | **0** |
| **srndx (cold)** | **1.18 s** | **5** |
| **srndx (warm)** | **0.08 s** | **5** |

Every literal tool — including the fast indexed one — returns **nothing**, because none of the query
words appear in the relevant code. `srndx` is the only tool that answers these queries at all, returning
5 ranked passages in ~80 ms warm. For examples of the passages `srndx` surfaces for intent queries, see
[Hybrid search quality](#hybrid-search-quality) below.

## Takeaways

- **Warm `srndx` is best-in-class on both axes at once.** ~38 ms on exact identifiers — matching the
  fastest indexed grep — *and* ~80 ms on natural-language intent, where every other tool scores zero.
  A running `srndx serve`/`mcp` makes warm the normal case for repeated queries.
- **Only `srndx` does intent.** `grep`, `ripgrep`, `git grep`, and `tgrep` are exact-match tools; on the
  four intent phrases they all returned 0 hits. This is the capability `srndx` adds, not a tuning
  difference.
- **Cold start is the honest trade.** A one-shot cold `srndx search` is ~1.1 s — far faster than the
  tree-walking greps, slower than `tgrep` cold (which loads no ML models). Keep an index warm
  (`srndx serve`) to erase it.
- **Different contracts.** The greps are exhaustive and unranked (e.g. `CancellationToken` returns ~1,396
  files); `srndx` returns the few most relevant passages, ranked. Reach for `grep` when you want *every*
  occurrence of a known token; reach for `srndx` when you want the most relevant few — by keyword **or**
  by meaning.

## Sharded indexing

The vector index is split into independent HNSW shards (`--shards`, default 8) built, loaded, and
searched in parallel. On the same `dotnet/runtime` corpus, single-graph (`--shards 1`) vs the default
8 shards on the same machine:

| | `--shards 1` | `--shards 8` | change |
| --- | ---: | ---: | ---: |
| `srndx index` build | 490.9 s | 148.0 s | **3.3× faster** |
| cold one-shot `search` (load + query) | 2.25 s | 1.41 s | **1.6× faster** |
| index size | 1449 MB | 1449 MB | same |

Sharding speeds the build (smaller graphs parallelize *and* have cheaper per-insert cost), the
cold-start load (each shard is a memory-mapped segment, mapped on its own core), and the query (shards
are searched concurrently, then merged). **Recall is preserved**: top-10 results overlap the
single-graph index ~92%, within the ~94% overlap two independent single-graph rebuilds already have —
the drift is ordinary approximate-nearest-neighbor nondeterminism, not a sharding penalty, and a
synthetic ground-truth check confirms sharded recall is at least as high as the single graph.

## Hybrid search quality

Hybrid (semantic + BM25) ranking on [`dotnet/extensions`](https://github.com/dotnet/extensions)
(3,661 files → 20,487 passages). The point of these tables is *what* surfaces, not latency.

**By intent** — phrases with no shared keyword, where `grep` has nothing literal to match:

| Query (typed as intent) | `srndx` top hit | `git grep` |
| --- | --- | --- |
| validate options at startup | `Diagnostics.Probes.Tests…OptionsValidatorTests` ✓ | 0 hits |
| circuit breaker half-open state | `Http.Resilience…CustomValidator` ✓ | 0 hits |
| pool and reuse objects | `Shared/Pools/PoolFactory.cs` ✓ | 0 hits |
| retry with exponential backoff | `Http.Resilience.Tests…HttpRetryStrategyOptionsTests` ✓ | 0 hits |
| redact sensitive data from logs | `core-templates/steps/publish-logs.yml` ~ | 0 hits |

**By exact identifier** — where the BM25 half earns its keep: the bare token lands on a genuinely
relevant file, while `grep` returns every literal occurrence to scan:

| Identifier | `srndx` top hit | `git grep` |
| --- | --- | --- |
| `ValidateOnStart` | `HeaderParsing…ServiceCollectionExtensions` ✓ | 53 lines / 23 files |
| `Backoff` | `Http.Resilience.Tests…HttpClientBuilderExtensions` ✓ | 6 lines / 6 files |
| `CircuitBreaker` | `Http.Resilience…CustomValidator` ✓ | 56 lines / 24 files |
| `Redact` | `Compliance.Testing.Tests…FakeRedactorTests` ✓ | 1,853 lines / 159 files |
| `ObjectPool` | `Telemetry…ResetOnGetObjectPool` ✓ | 173 lines / 74 files |

Reciprocal-rank fusion merges the two signals, so the same query box answers "where is `ObjectPool`?"
*and* "where do we pool and reuse objects?". Intent quality is bounded by the embedding model: one
intent query above returned a loosely related hit (`~`), expected from the tiny `potion-base-2M` model
— a larger Model2Vec model trades startup/footprint for better ranking with no code change.

_Raw data: `report-build.csv`, `report-latency.csv` (median + min per tool/query, with hit counts),
produced by `bench-report.ps1`._
