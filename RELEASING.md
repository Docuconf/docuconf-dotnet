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
3. The workflow takes the version from the tag, runs the full test suite (including `cue vet` of exported contracts
   and the clean-install smoke test), packs `Docuconf.Options` with symbols and SourceLink, and pushes both packages.

The version is only set from the tag; `<Version>` in the project file is the default for local builds. Exported
contracts record the package version in `metadata.generator.version`.
