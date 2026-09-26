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
  -ExpectedUpgradeCode DE3231A1-62C5-4F03-B676-3F8ACCCCE08A `
  -MainExecutableName BlinkObserverTool.exe `
  -RequiredFileNames manifest.json,profile.json,template.png
```

Release requires exactly one `vMAJOR.MINOR.PATCH` tag at `HEAD`. Visual Studio builds use the
same resolver and validation rules; after building, run the validation command above against its MSI.
The release workflow additionally generates `manifest.json`, validates it against
`build\release-manifest.schema.json`, and verifies that its tag, MSI filename, SHA-256, and Release URL
all identify the same release.

## Publish through GitHub

1. Merge a green pull request into protected `main`.
2. Complete the manual installer checklist below on the exact candidate commit. Do not create or push a release tag before this gate passes.
3. On an up-to-date local `main`, create a new annotated tag: `git tag -a v1.2.3 -m "BlinkObserverTool v1.2.3"`.
4. Push only that tag: `git push origin v1.2.3`.
5. The release workflow validates the tag, restores packages, runs all tests, builds and inspects the MSI, creates the checksum and manifest, and uploads exactly these assets to a draft Release:
   - `BlinkObserverTool.Installer.msi`
   - `BlinkObserverTool.Installer.msi.sha256`
   - `manifest.json`
6. The workflow verifies the exact asset set and same-Release relationships before publishing the draft. Do not attach an EXE installer or other build output.

## Mandatory manual installer gate

- Clean installation
- Major Upgrade from the previously distributed MSI
- Rejection of an older MSI when the new version is installed
- Uninstallation
- Application launch and Start menu/Desktop shortcuts
- Application version equals MSI `ProductVersion`
- Default profile addition/update
- Preservation of user-created and user-edited profiles

Record the tested Windows version, old/new application versions, tester, date, and result in the
release preparation record or pull request. A successful automated workflow does not replace this gate.

## Failure and rollback

- Never move or reuse a pushed/shared release tag, even when its workflow failed or the Release remained a draft. Fix forward with a new patch version.
- If a release or MSI was already distributed, never replace assets under the same version. Fix forward with a higher patch version and retain the current SemVer-line `UpgradeCode`.
- If an invalid or incomplete GitHub Release was created, leave it as a draft or delete it after recording the cause. The tag/version remains consumed.

The MSI is currently unsigned. SHA-256 verifies download integrity but does not provide publisher identity or Windows trust; code signing must be added as a separate secured release-stage capability.
When signing is introduced, keep certificates and private keys only in GitHub Secrets or an
OIDC-compatible signing service. Never place signing secrets in the repository, release assets, or logs.

The default profile files referenced by `BlinkObserverTool\DefaultProfiles\manifest.json` must be
tracked in Git. Source videos and other local verification material may remain untracked, but a
CI checkout must contain every profile and template file that the MSI validation requires.
