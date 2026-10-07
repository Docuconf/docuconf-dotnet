# Releasing Docuconf.Options

`.github/workflows/release.yml` publishes to nuget.org when a version tag is pushed. It uses
[NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing): the workflow's
GitHub OIDC token is exchanged for a short-lived, single-use API key, so no long-lived key is stored. (nuget.org now
limits new API keys to 30 days, so trusted publishing is also the low-maintenance option.)

## One-time setup

1. **nuget.org account.** Create an organization account (for example `docuconf`) and add the maintainers.
2. **Reserve the ID prefix.** Request a reservation of the `Docuconf.` prefix for that account (nuget.org
   "ID prefix reservation"), so nobody else can publish `Docuconf.*` packages.
3. **Trusted publishing policy.** On nuget.org, under the account's trusted publishing settings, add a policy for
   repository owner `docuconf`, repository `docuconf-dotnet`, workflow `release.yml`, environment `nuget`.
4. **Secret.** In the GitHub repository, add the secret `NUGET_USER`: the nuget.org profile name that owns the
   policy (not an API key).
5. **Environment.** Create the GitHub environment `nuget`, limited to tags matching `v*`, with required reviewers if
   you want each release approved.

## Each release

1. Make sure `main` is green.
2. Tag and push: `git tag v0.1.0 && git push origin v0.1.0`. Use a suffix for pre-releases: `v0.2.0-beta.1`.
3. The workflow takes the version from the tag, runs the full test suite (including `cue vet` of exported contracts,
   the shared conformance suite from docuconf-go `main`, and the clean-install smoke test), packs `Docuconf.Options`
   with symbols and SourceLink, and pushes both packages.

The version is only set from the tag; `<Version>` in the project file is the default for local builds. Exported
contracts record the package version in `metadata.generator.version`.

## GitHub Packages and Releases

The `github` job in `.github/workflows/release.yml` runs on the same `v*` tags. It repeats the checks, then:

- pushes `Docuconf.Options` to GitHub Packages (`https://nuget.pkg.github.com/Docuconf/index.json`), the `.nupkg`
  first and then the `.snupkg` symbol package (a rejected `.snupkg` does not fail the job);
- creates the GitHub Release for the tag if it does not exist, and attaches the `.nupkg` and `.snupkg`.

It does not depend on the nuget.org `publish` job, so it works before the nuget.org account, trusted publishing policy
and `nuget` environment exist. It authenticates with the workflow's own `GITHUB_TOKEN` (`packages: write`,
`contents: write`); there are no secrets or accounts to set up. The only requirement is that the `Docuconf`
organization lets `GITHUB_TOKEN` write packages, which it does unless package creation has been restricted under
Organization settings > Packages. The package is linked to this repository through the repository URL that SourceLink
writes into the `.nuspec`.

### Installing from GitHub Packages

GitHub's NuGet registry requires a token even for public packages. Create a personal access token (classic) with the
`read:packages` scope and add the source in a `nuget.config` next to your solution:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="docuconf" value="https://nuget.pkg.github.com/Docuconf/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
    <packageSource key="docuconf">
      <package pattern="Docuconf.*" />
    </packageSource>
  </packageSourceMapping>
  <packageSourceCredentials>
    <docuconf>
      <add key="Username" value="YOUR_GITHUB_USERNAME" />
      <add key="ClearTextPassword" value="%GITHUB_TOKEN%" />
    </docuconf>
  </packageSourceCredentials>
</configuration>
```

then `dotnet add package Docuconf.Options` with `GITHUB_TOKEN` set in the environment. Or add the source from the
command line: `dotnet nuget add source https://nuget.pkg.github.com/Docuconf/index.json --name docuconf --username
YOUR_GITHUB_USERNAME --password "$GITHUB_TOKEN" --store-password-in-clear-text`.

Without a token, download the `.nupkg` from the GitHub Release into a folder and use that folder as a package source:
`dotnet nuget add source ./packages --name local`.
