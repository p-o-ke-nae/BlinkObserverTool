---
name: windows-app-release
description: Publish BlinkObserverTool as a validated tag-driven Windows MSI release with checksum and manifest.
---

# Windows application release

Use this skill when preparing or changing the BlinkObserverTool Windows release process.

## Required sequence

1. Start from a clean, pushed commit on protected `main`.
2. Complete and record the manual installer gate in `docs/release-process.md`.
3. Create one new annotated `vMAJOR.MINOR.PATCH` tag pointing directly to the release commit.
4. Push only that tag.
5. Let `.github/workflows/release.yml` restore packages and run version resolver tests and application tests.
6. Build the x64, self-contained MSI and run `build/Test-MsiPackage.ps1`.
7. Generate `BlinkObserverTool.Installer.msi.sha256` and `manifest.json`.
8. Validate `manifest.json` with `build/release-manifest.schema.json` and
   `build/Test-ReleaseManifest.ps1`.
9. Upload only the MSI, checksum, and manifest to a draft GitHub Release.
10. Validate the exact same-Release asset set, then publish the draft.

## Invariants

- Git tags are the only release-version source.
- The tag, application version, MSI `ProductVersion`, manifest version, and Release tag must agree.
- Keep `win-x64`, self-contained publishing, the current `UpgradeCode`, and downgrade rejection.
- Never publish Debug MSI or EXE installers.
- A missing or mismatched asset is a release failure; do not guess or substitute URLs.
- The MSI remains unsigned until a separately approved signing stage is implemented.

## Failure rules

- Never move or reuse a pushed/shared tag, including when a workflow fails.
- Fix the cause on a new commit and use a higher patch version.
- Leave incomplete releases as drafts or remove them after recording the failure.
- Do not bypass tag, MSI, checksum, manifest, package permission, or manual-install checks.

## Signing secrets

Do not commit certificates, private keys, passwords, or signing tokens. When signing is introduced,
use GitHub Secrets or an OIDC-backed signing service, prevent secret output in logs, and expose secrets
only to the signing step.
