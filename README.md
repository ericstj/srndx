# srndx

**Offline semantic search over your local files and git history.** Ask in plain language,
get back the passages and commits that mean the same thing — even when they share no keywords.

`srndx` is a small .NET CLI that composes three pure-managed, **no-native-dependency** libraries
through the standard .NET AI ecosystem abstractions:

| Library | Role | Ecosystem abstraction |
| --- | --- | --- |
| [FastText.Net](https://github.com/ericstj/FastText.Net) | Detects each item's language (`lid.176`) | — |
| [Model2Vec.Net](https://github.com/ericstj/Model2Vec.Net) | Turns text into embeddings | `Microsoft.Extensions.AI.IEmbeddingGenerator` |
| [Hnsw.Net](https://github.com/ericstj/Hnsw.Net) | Approximate-nearest-neighbor vector index | `Microsoft.Extensions.VectorData` |

No GPU, no cloud, no API key, no native binary — everything runs in-process, anywhere .NET runs. Search
is **hybrid**: a built-in BM25 lexical index (exact-token relevance) is fused with the semantic vector
index via reciprocal-rank fusion, so both keyword and intent matches surface from the same query box.

## Install

`srndx` is published to this repository's **private [GitHub Packages](https://docs.github.com/packages)
NuGet feed** as a [RID-specific .NET tool](https://learn.microsoft.com/dotnet/core/tools/rid-specific-tools):
native-AOT packages for common platforms plus a portable fallback. The CLI picks the best match for your
machine, and the ML models are bundled in, so the tool is self-contained. CI publishes a rolling
prerelease on every push to `main`, and a stable version on each `v*` tag.

You need the [.NET SDK](https://dotnet.microsoft.com) and the [GitHub CLI](https://cli.github.com),
signed in (`gh auth login`). Then install (or upgrade) with one line, which fetches and runs the helper
[`eng/install.sh`](eng/install.sh) ([`eng/install.ps1`](eng/install.ps1) on Windows):

```sh
bash <(gh api repos/ericstj/srndx/contents/eng/install.sh -H "Accept: application/vnd.github.raw")
```

```powershell
gh api repos/ericstj/srndx/contents/eng/install.ps1 -H "Accept: application/vnd.github.raw" | Out-String | iex
```

The script grants `gh` the `read:packages` scope if needed and installs the tool, passing the feed token
through an environment variable so it is **never written to any NuGet config**. Equivalent manual steps:

```sh
gh auth refresh -h github.com -s read:packages                                        # let gh read packages
dotnet nuget add source https://nuget.pkg.github.com/ericstj/index.json --name srndx   # URL only — no secret on disk

# The token lives only in this environment variable, scoped to the one command
NuGetPackageSourceCredentials_srndx="Username=$(gh api user --jq .login);Password=$(gh auth token)" \
  dotnet tool update -g dotnet-srndx --prerelease
```

> `dotnet tool install` has no flag for feed credentials, so NuGet reads them from the
> `NuGetPackageSourceCredentials_<source-name>` environment variable, matched to the source by name —
> keeping the token out of `nuget.config` entirely. This reuses `gh`'s managed session token (revoke any
> time with `gh auth logout`); `gh` can't mint a throwaway PAT because GitHub no longer exposes a
> token-creation API. To use your own token instead, create a
> [personal access token](https://github.com/settings/tokens) with `read:packages` and put it in the
> `Password=` field.

## Usage

```sh
# Index a docs folder and a repo's recent history into one index file
srndx index --files ./docs --git ./my-repo --max-commits 500 --out project.index

# Semantic search (add --lang / --source / --top to filter)
srndx search "how do we authenticate requests" --index project.index

# Run as a live service: watch a directory, keep the index current, query interactively
srndx serve --files ./src --index project.index

# Run as an MCP server over stdio (a 'search' tool over a live, self-updating index)
srndx mcp --files ./src --index project.index

# Stop a backgrounded serve/mcp process (flushes the index first)
srndx stop --index project.index

# Wire srndx into a repository for agents
srndx install-mcp --repo .      # merge an 'srndx' server into .github/mcp.json
srndx install-skill --repo .    # emit .github/skills/srndx/SKILL.md
```

Run `srndx --help` (or `srndx <command> --help`) for all options. While a `serve`/`mcp` process is
running, a one-shot `srndx search` against the same index is answered by that resident process over a
loopback socket — skipping the cold-start load — and falls back to loading locally when none is running.

## Performance

Full methodology and a tool-by-tool comparison (`grep`, `ripgrep`, `git grep`, `tgrep`) are in
[docs/BENCHMARKS.md](docs/BENCHMARKS.md). The headlines, on `dotnet/runtime`
(57,923 files → 624,656 passages):

| | |
| --- | --- |
| Query, **warm** (resident `serve`/`mcp`) | ~38 ms exact identifier · ~80 ms natural-language intent |
| Query, **cold** (one-shot `search`) | ~1.1 s (model load + mmap + query) |
| Index build (one-time) | 624,656 passages in ~2.5 min; amortized over every later query |

Against literal search tools, warm `srndx` matches the fastest indexed grep on exact identifiers **and**
is the only one that answers natural-language queries at all — `grep`, `ripgrep`, `git grep`, and `tgrep`
return zero hits on phrases that share no keywords with the code. The greps stay the right tool when you
want *every* literal occurrence of a known token; `srndx` returns the most relevant few, by keyword or by
meaning.

Indexing and querying scale with cores: language detection and embedding run in parallel, and the vector
index is split into independent HNSW shards (`--shards`, default 8) that build, memory-map, and search in
parallel while preserving recall. See [docs/DESIGN.md](docs/DESIGN.md) for how that works.

## How it works

- **`index`** splits files into passages and reads commit messages, language-detects and embeds each,
  adds its tokens to a BM25 index, and writes the sharded vector index plus BM25 to one file.
- **`search`** fuses semantic similarity and BM25 relevance with reciprocal-rank fusion.
- **`serve`** / **`mcp`** keep an index in sync with a watched directory and answer queries — interactively
  or as a [Model Context Protocol](https://modelcontextprotocol.io) tool for agents.
- **`install-mcp`** / **`install-skill`** wire `srndx` into a repository for agents.

Architecture, the Native-AOT design, persistence, packaging, and the performance engineering behind the
numbers above are documented in [docs/DESIGN.md](docs/DESIGN.md).

## Models

The tool needs two model files. When installed as a packaged tool they are bundled alongside the binary;
otherwise they are resolved from the `models/` folder next to the binary:

- `lid.176.ftz` — FastText language-identification model.
- `potion-base-2M/` — Model2Vec embedding model (`config.json`, `model.safetensors`, `tokenizer.json`).

### Bring your own model

Each model can be swapped independently via environment variables (no rebuild required):

| Variable | Points to | Effect |
| --- | --- | --- |
| `SRNDX_LANGUAGE_MODEL` | a FastText model **file** | Replaces the language-ID model. |
| `SRNDX_EMBEDDING_MODEL` | a Model2Vec model **directory** | Replaces the embedding model. |
| `SRNDX_MODELS` | a **directory** holding both defaults | Replaces both at once. |

Swapping the embedding model changes the vector dimension, so re-run `srndx index` to rebuild any index
with the new model. A larger Model2Vec model trades startup/footprint for better semantic ranking with no
code change.

## Why this exists

A working showcase of a fully managed semantic-search stack with zero native dependencies — for private,
offline retrieval that ships as plain NuGet packages and runs everywhere the .NET runtime does.
