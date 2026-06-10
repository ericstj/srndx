#!/usr/bin/env bash
# Install or update the srndx CLI from its private GitHub Packages feed.
#
# Uses the GitHub CLI's token for read:packages and a throwaway NuGet config, so the token is only ever
# held in an environment variable for the install command — it is never written to any config file.
set -euo pipefail

FEED="https://nuget.pkg.github.com/ericstj/index.json"

command -v gh >/dev/null 2>&1 || { echo "GitHub CLI (gh) is required: https://cli.github.com" >&2; exit 1; }
command -v dotnet >/dev/null 2>&1 || { echo ".NET SDK (dotnet) is required: https://dotnet.microsoft.com" >&2; exit 1; }

if ! gh auth status >/dev/null 2>&1; then
  echo "Sign in to GitHub first: gh auth login" >&2
  exit 1
fi

if ! gh auth status 2>&1 | grep -q "read:packages"; then
  echo "Granting the gh login the read:packages scope (a browser may open)..."
  gh auth refresh -h github.com -s read:packages
fi

# A throwaway config names the feed (URL only — no secret) and keeps nuget.org for any dependencies.
cfg="$(mktemp)"
trap 'rm -f "$cfg"' EXIT
cat > "$cfg" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="srndx" value="$FEED" />
  </packageSources>
</configuration>
XML

# Token lives only in this environment variable, matched to the "srndx" source by name.
export NuGetPackageSourceCredentials_srndx="Username=$(gh api user --jq .login);Password=$(gh auth token)"

# `dotnet tool update` installs when the tool is absent and upgrades when it is present.
dotnet tool update -g dotnet-srndx --prerelease --configfile "$cfg"

echo "srndx is ready. Run 'srndx --help' to get started."
