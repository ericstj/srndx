# Benchmarks: indexing and search performance

These are `srndx`'s own scaling and latency numbers — how fast it builds an index, how big that index
is, and how quickly it answers — not a comparison against other tools. (For where `srndx` fits relative
to keyword, symbol, and embedding search, and where it is weak, see
[DESIGN.md → Limitations and scope](DESIGN.md#limitations-and-scope).)

## Setup

| | |
| --- | --- |
| Corpus | [`dotnet/runtime`](https://github.com/dotnet/runtime) — 57,923 files → 624,656 passages |
| Machine | 13th Gen Intel Core i7-13800H (14C/20T), 64 GB RAM, Windows 11 |
| Build | Native-AOT executable, default 8 vector shards, `potion-base-2M` embeddings |

This is a large, deliberately stressful corpus; it exercises the index build, the memory-mapped
cold-start load, and the sharded scatter-gather query path at scale.

## Sharded index build

The vector index is split into independent HNSW shards (`--shards`, default 8) built in parallel. On the
full corpus, single-graph (`--shards 1`) vs the default 8 shards on the same machine:

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

## Query latency

| Path | Latency | Notes |
| --- | ---: | --- |
| **Warm** (resident `serve`/`mcp`, proxied) | ~40–80 ms | query embed + sharded search, no reload |
| **Cold** one-shot `srndx search` | ~1.1–1.4 s | fresh process: model load + mmap + query |

A one-shot `srndx search` is a fresh process, so it pays model load and the memory-map of each shard.
Memory-mapping (both the vector shards and the BM25 lexical index) keeps that cold cost roughly
*independent of index size* — load is `max(slowest shard, lexical)` across cores, not the sum of a full
deserialization. Keeping a `serve`/`mcp` process resident (one-shot `search` is auto-proxied to it)
removes the reload entirely, so repeat queries are warm.

## Indexing throughput

Indexing language-detects and embeds passages in parallel across cores; the per-shard HNSW graph build
is the dominant remaining cost. `--ef-construction <N>` (default 200) trades a little vector recall for
a faster build. Building the 624,656-passage corpus takes ~2.5 min (8 shards, Native-AOT) and is a
one-time cost amortized over every later query.
