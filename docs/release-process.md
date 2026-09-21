# Release process

## Prerequisites

- Windows, .NET 10 SDK, and Visual Studio 2022 with HeatWave for WiX v4 when building in Visual Studio.
- The `GenericRecognition.Workbench.*` packages must exist in GitHub Packages. Grant this repository read access to the package. If the package belongs to another owner, set the repository variable `GENERIC_RECOGNITION_PACKAGE_OWNER`.
- Local development continues to use `LocalPackages` from `NuGet.Config`; CI replaces that source in its ephemeral checkout and authenticates with `GITHUB_TOKEN`. No package credential is committed.

## Local verification

From the repository root:

```powershell
dotnet restore .\BlinkObserverTool.slnx
.\build\tests\Resolve-GitVersion.Tests.ps1
dotnet test .\BlinkObserverTool.ProfileSync.Tests\BlinkObserverTool.ProfileSync.Tests.csproj -c Debug --no-restore
dotnet test .\BlinkObserverTool.BlinkRecognition.Tests\BlinkObserverTool.BlinkRecognition.Tests.csproj -c Debug --no-restore
dotnet build .\installer\BlinkObserverTool.Installer\BlinkObserverTool.Installer.wixproj -c Debug --no-restore
```

Visual Studio users open `BlinkObserverTool.slnx`, select `Debug`, and build `BlinkObserverTool.Installer`. Debug/local builds use version `0.0.0`.

To verify a Release locally, commit all intended changes, create the tag on that exact commit, select `Release`, then build the installer project. The CLI equivalent is:

```powershell
git tag -a v1.2.3 -m "BlinkObserverTool v1.2.3"
.\build\Resolve-GitVersion.ps1 -RepositoryPath . -Configuration Release
dotnet build .\installer\BlinkObserverTool.Installer\BlinkObserverTool.Installer.wixproj -c Release
.\build\Test-MsiPackage.ps1 `
  -MsiPath .\installer\BlinkObserverTool.Installer\bin\Release\BlinkObserverTool.Installer.msi `
  -ExpectedVersion 1.2.3 `
  -ExpectedUpgradeCode 813D1E2D-7D7A-4984-A062-81B06B5EBC26 `
  -MainExecutableName BlinkObserverTool.exe `
  -RequiredFileNames manifest.json,profile.json,template.png
```

Release requires exactly one `vMAJOR.MINOR.PATCH` tag at `HEAD`. Visual Studio builds use the
same resolver and validation rules; after building, run the validation command above against its MSI.

## Publish through GitHub

1. Merge a green pull request into `main`.
2. On an up-to-date local `main`, create an annotated tag: `git tag -a v1.2.3 -m "BlinkObserverTool v1.2.3"`.
3. Push only that tag: `git push origin v1.2.3`.
4. The release workflow validates the tag, restores packages, runs all tests, builds and inspects the MSI, writes its SHA-256 checksum, and creates the GitHub Release.
5. Publish only `BlinkObserverTool.Installer.msi` and its `.sha256` file. Do not attach an EXE installer.

## Failure and rollback

- A failed workflow creates no release; fix the cause, delete the remote tag, move/recreate it on the corrected commit, and push it again only if the MSI was never distributed.
- If a release or MSI was already distributed, never replace assets under the same version. Fix forward with a higher patch version and retain the same `UpgradeCode`.
- If an invalid GitHub Release was created, mark it unavailable or delete it, but treat the version as consumed once users may have downloaded it.

The MSI is currently unsigned. SHA-256 verifies download integrity but does not provide publisher identity or Windows trust; code signing must be added as a separate secured release-stage capability.
