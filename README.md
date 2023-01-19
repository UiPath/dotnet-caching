## Caching
UiPath Distributed Caching using Redis. Contains L2 caching using CloudEvents for synchronization

More documentation https://uipath.atlassian.net/wiki/x/bonSZw

## Branches and pull requests
Branches should follow a few naming conventions:
- fix/some_short_description_of_fix
- feature/some_short_description_of_feature

## How to build packages
Command below publishes all 2 packages and puts them in `Releases` folder
```
> dotnet build Caching.sln -c Release /p:GeneratePackageOnBuild=true
```

## Releasing a new version
Release a new version is always done from the **master** branch.

Steps to release a new version:
- make sure the code is stable before starting a new release process
- create a new branch from master with the following format: release/caching/v{Major}.{Minor}.{Patch}, replacing Major, Minor and Patch with the actual version
- go to AzureDevOps (uipath.visualstudio.com), find "Service Common" project and search for UiPath.ServiceCommon.Caching pipeline and select it
- click "Run pipeline" and select the branch just created
- monitor the pipeline run an make sure you approve the release, in order for the package to be published to the nuget-packages feed
- at the end run git tag -a -m "Caching: release v{Major}.{Minor}.{Patch}" "caching/{Major}.{Minor}.{Patch}", replacing Major, Minor and Patch with the actual version
- create a branch to bump version in GitVersion.yml (eg: feature/ledger/bump_version_x_y) and merge back in master

## Testing
Before running the sample code ensure you have a redis instance on localhost:6379
```
docker run -d --name redis-stack -p 6379:6379 -p 8001:8001 redis/redis-stack:latest
```
