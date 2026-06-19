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

## Sign-off (DCO)

This project uses the [Developer Certificate of Origin](https://developercertificate.org/): by contributing, you certify that you wrote the change or otherwise have the right to submit it under the project's license. Sign off every commit:

```bash
git commit -s
```

This appends a `Signed-off-by: Your Name <you@example.com>` trailer (using your `git config user.name` / `user.email`). Commits made through GitHub's web editor are signed off automatically.

## Reporting security issues

See [SECURITY.md](./SECURITY.md).
