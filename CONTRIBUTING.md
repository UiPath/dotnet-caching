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

## Sign-off

<!-- TBD: DCO vs CLA decision. Default below assumes DCO; replace if a CLA is chosen. -->
<SIGN_OFF_POLICY — TBD: DCO or CLA>

We use the [Developer Certificate of Origin](https://developercertificate.org/). Sign off each commit with `git commit -s`.

## Code of Conduct

This project follows the [Contributor Covenant](./CODE_OF_CONDUCT.md). By participating, you agree to abide by its terms.

## Reporting security issues

See [SECURITY.md](./SECURITY.md).
