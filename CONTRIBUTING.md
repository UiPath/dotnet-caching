# Contributing to UiPath.Caching

Thanks for your interest in contributing! This document outlines the process for contributing code, reporting issues, and submitting pull requests.

## Build and test locally

Prerequisites:
- .NET 8 SDK and .NET 10 SDK
- Docker (for Redis integration tests)

```bash
git clone https://github.com/UiPath/dotnet-caching.git
cd dotnet-caching
docker run -d --name caching-redis -p 6379:6379 redis:7
dotnet build
dotnet test
```

## Pull requests

- Branch off `main`. Branch name convention: `feat/<topic>`, `fix/<topic>`, `docs/<topic>`.
- One logical change per PR.
- Add or update tests for any behavior change.
- Update CHANGELOG.md under `## [Unreleased]`.
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/): `<type>(<scope>): <subject>`. Example: `feat(redis): add cluster sharding support`.

## Public API changes

The `src/` libraries track their public surface with [`Microsoft.CodeAnalysis.PublicApiAnalyzers`](https://github.com/dotnet/roslyn-analyzers/blob/main/src/PublicApiAnalyzers/PublicApiAnalyzers.Help.md). Any change to public or protected API must be reflected in that project's `PublicAPI.Unshipped.txt`, or the build fails (`RS0016` for additions, `RS0017` for removals).

Update the files automatically from the repo root:

```bash
dotnet format analyzers UiPath.Caching.slnx --diagnostics RS0016 RS0017
```

…or apply the **"Add to public API"** code fix in your IDE. Entries already released live in `PublicAPI.Shipped.txt`; newly added members go in `PublicAPI.Unshipped.txt` and are promoted to shipped at release time. A public API change is also one of the signals that a [CLA](#contributor-license-agreement-cla) may be required.

## Sign-off (DCO)

This project uses the [Developer Certificate of Origin](https://developercertificate.org/): by contributing, you certify that you wrote the change or otherwise have the right to submit it under the project's license. Sign off every commit:

```bash
git commit -s
```

This appends a `Signed-off-by: Your Name <you@example.com>` trailer (using your `git config user.name` / `user.email`). Commits made through GitHub's web editor are signed off automatically.

## Contributor License Agreement (CLA)

Most contributions need only the DCO sign-off above. For **material or strategic** contributions, UiPath additionally requires a signed [Contributor License Agreement](./CLA.md). A maintainer will tell you when one applies by adding the `cla-required` label to your pull request — you don't need to sign one preemptively.

A CLA is typically requested when a contribution is:

- material or product-critical;
- patent-sensitive;
- made by or on behalf of a corporate contributor (i.e. your employer holds IP rights in your work); or
- intended for broader commercial or product use.

An automated check may add a `needs-cla-review` label to flag a pull request for a maintainer to assess — this is only a prompt for human review, not a decision that a CLA is required.

### How the CLA process works

1. A maintainer adds the `cla-required` label to your pull request, and an automated comment links to [CLA.md](./CLA.md). A `legal/cla` status check on the PR turns red while a CLA is outstanding.
2. Read [CLA.md](./CLA.md), complete the section that applies to you (**Option A — Individual Contributor** or **Option B — Corporate Contributor**), sign it, and email the signed copy to **contractnotice@uipath.com**, referencing your pull request URL.
3. Once UiPath has recorded your signed CLA, a maintainer adds the `cla-signed` label. The `legal/cla` check turns green and the pull request becomes eligible to merge.

The CLA covers your present and future Contributions, so you won't be asked to sign again for later pull requests — unless you change employers, in which case a new agreement is required (see clause 5.3 of the CLA).

## Reporting security issues

See [SECURITY.md](./SECURITY.md).
