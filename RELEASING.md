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
   `src/Docuconf/Docuconf.csproj` and `CHANGELOG.md`. The example contract does not need regenerating: CI's
   comparison ignores `metadata.generator.version`.
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
