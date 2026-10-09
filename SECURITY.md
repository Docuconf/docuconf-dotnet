# Security policy

## Reporting a vulnerability

Please report vulnerabilities privately, through GitHub's private vulnerability reporting: open the repository's
**Security** tab and choose **Report a vulnerability**
([direct link](https://github.com/docuconf/docuconf-dotnet/security/advisories/new)). Do not open a public issue, pull
request or discussion for a suspected vulnerability.

Include what you can of:

- the affected `Docuconf.Options` version, and the .NET version it runs on;
- what an attacker can do, and what they need first;
- steps or a minimal options class, contract or environment that reproduces it.

We work on the fix in a private security advisory, credit you in it unless you prefer otherwise, and publish the
advisory when a fixed release is out.

## Response targets

| | |
|---|---|
| Acknowledge the report | within 3 business days |
| First assessment (confirmed or not, severity) | as soon as we can reproduce it, and we keep you updated in the advisory |
| Fix | released as a patch to the supported version, then the advisory is published |

## Supported versions

`Docuconf.Options` is released from tags `v*` (see [RELEASING.md](RELEASING.md)) to nuget.org and GitHub Packages.
Security fixes go to the latest release, as a new release.

**During the beta, only the latest release is supported.** Upgrade to it to get a fix.

## Scope

In scope:

- the `Docuconf.Options` package: the runtime library, its declaration analyzer, the build-time export target and
  `docuconf export`;
- the contract-first mode (`DocuconfContract`), for example a contract or environment that makes it accept a value
  the contract forbids, or an error message, log line or export that leaks a value marked secret.

Out of scope: the example application under [`examples`](examples) and the sample under [`samples`](samples),
vulnerabilities in dependencies that docuconf does not make reachable (report those upstream), and issues in a
platform or cluster that only arise from its own misconfiguration. The docuconf CLI, the CUE meta-schema and the other
SDKs live in their own repositories and follow their own policies.
