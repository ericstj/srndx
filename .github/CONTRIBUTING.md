# Contributing to srndx

Thanks for your interest in improving srndx! This is a small, pure-managed (no native dependency),
AOT-friendly .NET tool, and contributions of all sizes are welcome.

## Prerequisites

- The [.NET SDK](https://dotnet.microsoft.com/download) version targeted by the projects (net10.0).
- Network access on the first build: the models (fastText `lid.176.ftz` and Model2Vec
  `potion-base-2M`) are downloaded automatically and cached under `models/`. They are not committed.

## Build and test

```sh
dotnet build Srndx.sln -c Release
dotnet test Srndx.sln -c Release --no-build
```

The integration tests load the real models. If the models could not be downloaded (for example, an
offline environment), those tests skip gracefully and the rest of the suite still runs.

## Building the AOT tool locally

`srndx` publishes as a Native AOT executable. To pack it as a RID-specific .NET tool, see
[RID-specific tools](https://learn.microsoft.com/dotnet/core/tools/rid-specific-tools). Native AOT
requires the matching native toolchain for your platform (for example, the C++ build tools on
Windows).

## Coding guidelines

- Match the existing style; `.editorconfig` captures the essentials (file-scoped namespaces,
  4-space indentation, explicit accessibility modifiers).
- Keep the code pure-managed and AOT-clean: no reflection on hot paths, no native dependencies.
- Keep comments brief and focused on the final state of the code, not the change.
- Add or update tests for behavior changes, and keep the suite green.

## Pull requests

- Keep changes focused and self-contained.
- Describe the motivation and the user-visible effect.
- Make sure `dotnet build` and `dotnet test` pass before submitting.

## Reporting issues

Please include the command you ran, what you expected, what happened, and your OS and .NET version.
For search-quality issues, a small reproducible corpus is especially helpful.
