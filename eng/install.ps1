#!/usr/bin/env pwsh
# Install or update the srndx CLI from its private GitHub Packages feed.
#
# Uses the GitHub CLI's token for read:packages and a throwaway NuGet config, so the token is only ever
# held in an environment variable for the install command — it is never written to any config file.
$ErrorActionPreference = 'Stop'

$feed = 'https://nuget.pkg.github.com/ericstj/index.json'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) is required: https://cli.github.com' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK (dotnet) is required: https://dotnet.microsoft.com' }

gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw 'Sign in to GitHub first: gh auth login' }

if (-not ((gh auth status 2>&1) -match 'read:packages')) {
    Write-Host 'Granting the gh login the read:packages scope (a browser may open)...'
    gh auth refresh -h github.com -s read:packages
}

# A throwaway config names the feed (URL only — no secret) and keeps nuget.org for any dependencies.
$cfg = New-TemporaryFile
try {
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="srndx" value="$feed" />
  </packageSources>
</configuration>
"@ | Set-Content -Path $cfg -Encoding utf8

    # Token lives only in this environment variable, matched to the "srndx" source by name.
    $login = gh api user --jq .login
    $env:NuGetPackageSourceCredentials_srndx = "Username=$login;Password=$(gh auth token)"

    # `dotnet tool update` installs when the tool is absent and upgrades when it is present.
    dotnet tool update -g dotnet-srndx --prerelease --configfile $cfg
}
finally {
    $env:NuGetPackageSourceCredentials_srndx = $null
    Remove-Item $cfg -Force -ErrorAction SilentlyContinue
}

Write-Host "srndx is ready. Run 'srndx --help' to get started."
