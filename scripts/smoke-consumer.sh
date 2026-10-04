#!/usr/bin/env bash
# Packs Docuconf.Options, installs it from a local feed into a clean console app,
# and checks export and startup validation the way a consumer would use them.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
version="0.0.0-smoke.$(date +%s)"

dotnet pack "$root/src/Docuconf" -c Release -o "$work/feed" -p:Version="$version" >/dev/null
cd "$work"
dotnet new console -n App -o app --framework net10.0 >/dev/null
cd app
cat > nuget.config <<XML
<configuration>
  <packageSources>
    <add key="local" value="$work/feed" />
  </packageSources>
</configuration>
XML
dotnet add package Docuconf.Options --version "$version" >/dev/null
dotnet add package Microsoft.Extensions.Hosting --version 10.0.12 >/dev/null
cat > Program.cs <<'CS'
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Docuconf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

if (DocuconfExport.RunIfRequested(args)) return;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDocuconf<AppOptions>();
using var host = builder.Build();
try
{
    Console.WriteLine($"ok: port {host.Services.GetRequiredService<IOptions<AppOptions>>().Value.Port}");
}
catch (OptionsValidationException ex)
{
    Console.WriteLine(string.Join("\n", ex.Failures));
    return;
}

[ConfigContract("smoke-app", Section = "App")]
public sealed class AppOptions
{
    [Required, Secret, UrlSchemes("postgres"), Description("Database connection string")]
    public string DatabaseUrl { get; set; } = "";

    [Range(1, 65535), Description("HTTP listen port")]
    public int Port { get; set; } = 8080;
}
CS
dotnet build -c Release -v q >/dev/null
dll=bin/Release/net10.0/App.dll

App__DatabaseUrl=postgres://u:p@db/x dotnet "$dll" | grep -q "ok: port 8080" && echo "ok: startup with valid config"
out=$(App__Port=abc App__DatabaseUrl=mysql://u:hunter2@db/x dotnet "$dll")
grep -q "\[invalid_type\] APP__PORT" <<<"$out" && grep -q "\[invalid_scheme\] APP__DATABASEURL" <<<"$out" \
  && ! grep -q hunter2 <<<"$out" && echo "ok: startup reports every problem, secret redacted"
dotnet "$dll" docuconf export contract.cue >/dev/null
grep -q 'name: "smoke-app"' contract.cue && echo "ok: docuconf export"
