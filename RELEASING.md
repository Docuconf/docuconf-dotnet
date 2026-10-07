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

Releases are automated with [release-please](https://github.com/googleapis/release-please); see
[CONTRIBUTING.md](CONTRIBUTING.md#how-releases-happen) for the commit conventions it reads.

1. Merge the open release PR (`chore(main): release X.Y.Z`). It already updates `<Version>` in
   `src/Docuconf/Docuconf.csproj` and `CHANGELOG.md`. The example contract does not need regenerating: the
   contract check (`-p:DocuconfContractCheck=true`, `docuconf export --check`) ignores `metadata.generator.version`.
2. release-please tags the merge commit `vX.Y.Z` and creates the GitHub release with the changelog entries.
3. `.github/workflows/release.yml` runs on the tag. It takes the version from the tag, runs the full test suite
   (including `cue vet` of exported contracts, the shared conformance suite from docuconf-go `main`, and the
   clean-install smoke test), packs `Docuconf.Options` with symbols and SourceLink, and pushes both packages.

If the release PR was created with `GITHUB_TOKEN` (no release GitHub App configured), the tag does not trigger
`release.yml` by itself, so `.github/workflows/release-please.yml` starts it with `gh workflow run`. To redo a
release by hand: `gh workflow run release.yml --ref vX.Y.Z`.

The version is set from the tag; `<Version>` in the project file, which the release PR keeps in step, is the
default for local builds. Exported contracts record the package version in `metadata.generator.version`.

### Pre-releases

While the package is in alpha, `release-please-config.json` sets `"prerelease": true`, `"versioning": "prerelease"`
and `"prerelease-type": "alpha"`, so each release PR proposes the next alpha (`0.1.0-alpha.2`, ...) and the GitHub
release is marked as a pre-release. To leave the alpha, remove those three settings and land a commit whose message
has a `Release-As: 0.1.0` footer; the next release PR then proposes `0.1.0`, and later ones follow the commit types.

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

## docuconf-go version

docuconf-go owns the spec, the CUE meta-schema (`spec/cue`), the conformance suite (`conformance/cases.json`) and the
`docuconf` CLI. This SDK is tested against one docuconf-go commit, pinned in `.github/docuconf-go.ref` (a full SHA).

- **CI** checks out that commit on pushes and pull requests. The nightly scheduled run uses docuconf-go `main` instead,
  so a spec change that breaks this SDK shows up within a day. To try another docuconf-go commit or branch, run the CI
  workflow by hand (Actions, CI, Run workflow) with `docuconf_go_ref` set. Releases always build against the pinned commit.
- **Bump PRs.** `.github/workflows/docuconf-go-bump.yml` opens (or updates) a `build(deps): bump docuconf-go to <sha>`
  pull request from the `docuconf-go-bump` branch whenever docuconf-go `main` moves: immediately when docuconf-go sends
  a `docuconf-go-updated` dispatch (this needs the release GitHub App), otherwise on its daily schedule. CI on that PR
  is the compatibility check; merge it when it is green, or fix the SDK on the same branch. It can also be run by hand
  with a specific `sha`.
- **`scripts/conformance.sh`** runs only the docuconf-go-facing checks (the conformance suite and the `cue vet` of
  exported contracts) against any checkout: `DOCUCONF_GO_DIR=../docuconf-go scripts/conformance.sh`. CI runs it, and
  so does docuconf-go's downstream workflow, which runs it against every docuconf-go pull request that touches the spec,
  the conformance suite or the CLI. It needs the .NET 10 SDK and `cue` on `PATH`; with `DOCUCONF_SPEC_CUE` set (the script sets it) the export tests vet against that meta-schema instead of the copy in `tests/Docuconf.Tests/spec`.

Without the release App (secrets `RELEASE_APP_ID` and `RELEASE_APP_PRIVATE_KEY`) the bump workflow uses
`GITHUB_TOKEN`: the repository setting "Allow GitHub Actions to create and approve pull requests" must be on, and
because a PR opened that way triggers no workflows, the bump workflow starts CI on the branch itself
(`workflow_dispatch`, whose checks show on the PR).
