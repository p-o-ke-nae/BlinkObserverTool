[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$SchemaPath,

    [Parameter(Mandatory = $true)]
    [string]$MsiPath,

    [Parameter(Mandatory = $true)]
    [string]$ChecksumPath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedTag,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedReleaseUrl,

    [string]$SettingsPath = ".\installer\BlinkObserverTool.Installer\installer.settings.json"
)

$ErrorActionPreference = "Stop"

$resolvedManifestPath = (Resolve-Path -LiteralPath $ManifestPath).Path
$resolvedSchemaPath = (Resolve-Path -LiteralPath $SchemaPath).Path
$resolvedMsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$resolvedChecksumPath = (Resolve-Path -LiteralPath $ChecksumPath).Path
$resolvedSettingsPath = (Resolve-Path -LiteralPath $SettingsPath).Path
$manifestJson = Get-Content -LiteralPath $resolvedManifestPath -Raw
$schemaJson = Get-Content -LiteralPath $resolvedSchemaPath -Raw

if (-not ($manifestJson | Test-Json -Schema $schemaJson)) {
    throw "Release manifest does not satisfy the JSON schema."
}

$manifest = $manifestJson | ConvertFrom-Json
$settings = Get-Content -LiteralPath $resolvedSettingsPath -Raw | ConvertFrom-Json
$expectedVersion = $ExpectedTag.Substring(1)
$installerFilename = [System.IO.Path]::GetFileName($resolvedMsiPath)
$actualHash = (Get-FileHash -LiteralPath $resolvedMsiPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = (Get-Content -LiteralPath $resolvedChecksumPath -Raw).Trim()

if ($manifest.tag -cne $ExpectedTag -or $manifest.version -cne $expectedVersion) {
    throw "Manifest version/tag does not match '$ExpectedTag'."
}
if ($manifest.product -cne $settings.ProductName) {
    throw "Manifest product does not match installer.settings.json."
}
if ($manifest.architecture -cne "x64" -or $settings.PublishRuntimeIdentifier -cne "win-x64") {
    throw "Release architecture must remain x64/win-x64."
}
if ($settings.PublishSelfContained -ne $true) {
    throw "Release publish must remain self-contained."
}
if ($manifest.installerFilename -cne $installerFilename -or $installerFilename -cne "BlinkObserverTool.Installer.msi") {
    throw "Manifest installer filename does not match the release MSI."
}
if ($manifest.sha256 -cne $actualHash) {
    throw "Manifest SHA-256 does not match the release MSI."
}
if ($checksum -cne "$actualHash  $installerFilename") {
    throw "Checksum asset does not match the release MSI."
}
if ($manifest.releaseUrl -cne $ExpectedReleaseUrl) {
    throw "Manifest releaseUrl does not match the tagged GitHub Release."
}

$publishedAt = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse(
        [string]$manifest.publishedAt,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$publishedAt)) {
    throw "Manifest publishedAt is not a valid ISO-8601 timestamp."
}

Write-Output "Validated release manifest: $resolvedManifestPath"
Write-Output "  Tag: $($manifest.tag)"
Write-Output "  Installer: $($manifest.installerFilename)"
Write-Output "  SHA-256: $($manifest.sha256)"
