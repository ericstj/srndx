# Third-Party Notices

**SemanticSearch** (`ssearch`) is a demo application that composes several open-source
components. It does not bundle them: libraries are restored as NuGet packages and the
pretrained models are downloaded at build time. This file acknowledges those components.

## Libraries

### FastText.Net

- Project: https://github.com/ericstj/FastText.Net
- License: MIT

A managed C# port of the inference portion of **fastText**, an open-source library created
by Facebook AI Research (Meta) — https://github.com/facebookresearch/fastText,
Copyright (c) 2016-present, Facebook, Inc., MIT. Used here for language identification.

### Model2Vec.Net

- Project: https://github.com/ericstj/Model2Vec.Net
- License: MIT

A managed C# port of **Model2Vec** static embeddings
(https://github.com/MinishLab/model2vec, MIT). Used here to embed text.

### Hnsw.Net

- Project: https://github.com/ericstj/Hnsw.Net
- License: MIT

A managed implementation of Hierarchical Navigable Small World (HNSW) approximate
nearest-neighbor search. Used here as the vector index.

### Microsoft.Extensions.AI.Abstractions

- Project: https://github.com/dotnet/extensions
- License: MIT

Provides the `IEmbeddingGenerator` abstraction and the `DelegatingEmbeddingGenerator`
middleware used by the embedding pipeline.

### ModelContextProtocol C# SDK

- Project: https://github.com/modelcontextprotocol/csharp-sdk
- License: MIT

Used by the `mcp` command to expose the `search` tool over the Model Context Protocol.

### System.CommandLine

- Project: https://github.com/dotnet/command-line-api
- License: MIT

Provides the command-line parser.

## Pretrained models (downloaded at build time, not included)

### fastText `lid.176`

- https://fasttext.cc/docs/en/language-identification.html
- Copyright (c) Facebook, Inc.
- License: [Creative Commons Attribution-ShareAlike 3.0 (CC BY-SA 3.0)](https://creativecommons.org/licenses/by-sa/3.0/)

### Model2Vec `potion-base-2M`

- https://huggingface.co/minishlab/potion-base-2M
- License: MIT
