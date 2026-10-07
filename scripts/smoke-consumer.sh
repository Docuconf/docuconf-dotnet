#!/usr/bin/env bash
# Packs Docuconf.Options, installs it from a local feed into clean ASP.NET Core apps on .NET 10 and .NET 8 (the
# README's install steps), and checks what a consumer gets: the analyzer, the build-time export, startup validation,
# and, on .NET 8, no Microsoft.Extensions assemblies copied over the shared framework.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
version="0.0.0-smoke.$(date +%s)"
# A private package folder, so the smoke version never lands in the shared NuGet cache.
export NUGET_PACKAGES="$work/packages"

dotnet pack "$root/src/Docuconf" -c Release -o "$work/feed" -p:Version="$version" >/dev/null

for tf in net10.0 net8.0; do
  echo "== $tf"
  app="$work/app-$tf"
  dotnet new web -n App -o "$app" --framework "$tf" >/dev/null
  cd "$app"
  # As the README installs it, with the feed in this app's nuget.config rather than the user's.
  dotnet new nugetconfig >/dev/null
  dotnet nuget add source "$work/feed" --name docuconf-local --configfile nuget.config >/dev/null
  dotnet add package Docuconf.Options --prerelease >/dev/null
  grep -q "Docuconf.Options\" Version=\"$version\"" App.csproj
  # The build-time export.
  sed -i 's#</PropertyGroup>#  <DocuconfContractPath>contract.cue</DocuconfContractPath>\n  </PropertyGroup>#' App.csproj
  cat > Program.cs <<'CS'
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Docuconf;

if (DocuconfExport.RunIfRequested(args)) return;

var builder = WebApplication.CreateBuilder(args);
builder.AddDocuconf<AppOptions>();
var app = builder.Build();
var options = app.Services.LoadOrExit<AppOptions>();
Console.WriteLine($"ok: port {options.Port}");

[ConfigContract("smoke-app", Section = "App")]
public sealed class AppOptions
{
    [Required, Secret, UrlSchemes("postgres"), Description("Database connection string")]
    public string DatabaseUrl { get; set; } = "";

    [Range(1, 65535), Description("HTTP listen port")]
    public int Port { get; set; } = 8080;
}
CS

  # The analyzer fails the build on a declaration error.
  cat > Bad.cs <<'CS'
[Docuconf.ConfigContract("smoke-app", Section = "Bad")]
public sealed class BadOptions
{
    [System.ComponentModel.DataAnnotations.Range(1, 10)]
    public int Level { get; set; } = 3;
}
CS
  if dotnet build -c Release >"$work/build.txt" 2>&1; then
    echo "a declaration error built cleanly" >&2; exit 1
  fi
  grep -q "error DOCUCONF001: Bad:Level: add \[Description" "$work/build.txt" || { cat "$work/build.txt" >&2; exit 1; }
  echo "ok: the analyzer reports DOCUCONF001"
  rm Bad.cs

  dotnet build -c Release -v q >/dev/null
  grep -q 'name: "smoke-app"' contract.cue && echo "ok: the build wrote contract.cue"
  dotnet build -c Release -v q -p:DocuconfContractCheck=true >/dev/null && echo "ok: the contract check passes"
  dll="bin/Release/$tf/App.dll"

  App__DatabaseUrl=postgres://u:p@db/x dotnet "$dll" | grep -q "ok: port 8080" && echo "ok: startup with valid config"
  set +e
  App__Port=abc App__DatabaseUrl=mysql://u:hunter2@db/x dotnet "$dll" >"$work/bad.txt" 2>&1
  code=$?
  set -e
  [ "$code" = 1 ] || { echo "expected exit status 1, got $code" >&2; cat "$work/bad.txt" >&2; exit 1; }
  grep -q "^docuconf: 2 configuration problems:" "$work/bad.txt" && grep -q "\[invalid_type\] APP__PORT" "$work/bad.txt" \
    && grep -q "\[invalid_scheme\] APP__DATABASEURL" "$work/bad.txt" && ! grep -q hunter2 "$work/bad.txt" \
    && ! grep -q "   at " "$work/bad.txt" \
    || { cat "$work/bad.txt" >&2; exit 1; }
  echo "ok: startup reports every problem, exits 1, no stack trace, secret redacted"

  # On .NET 8 the package must not replace the ASP.NET Core 8 shared framework's Microsoft.Extensions assemblies.
  copied=$(find "bin/Release/$tf" -name 'Microsoft.Extensions.*.dll' | wc -l)
  [ "$copied" = 0 ] || { echo "bin/Release/$tf has $copied Microsoft.Extensions assemblies:" >&2; find "bin/Release/$tf" -name 'Microsoft.Extensions.*.dll' >&2; exit 1; }
  echo "ok: no Microsoft.Extensions assemblies copied into the output"
  cd "$root"
done
echo "smoke-consumer: ok"
