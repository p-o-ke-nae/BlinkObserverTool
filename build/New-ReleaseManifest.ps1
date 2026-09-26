[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath,

    [Parameter(Mandatory = $true)]
    [string]$ChecksumPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseUrl,

    [string]$Product = "BlinkObserverTool",
    [string]$Architecture = "x64",
    [string]$MinimumWindowsVersion = "10.0",
    [DateTimeOffset]$PublishedAt = [DateTimeOffset]::UtcNow
)

$ErrorActionPreference = "Stop"

if ($Tag -cne "v$Version" -or $Tag -cnotmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Tag '$Tag' must exactly match version '$Version' as vMAJOR.MINOR.PATCH."
}

$resolvedMsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$resolvedChecksumPath = (Resolve-Path -LiteralPath $ChecksumPath).Path
$installerFilename = [System.IO.Path]::GetFileName($resolvedMsiPath)
$hash = (Get-FileHash -LiteralPath $resolvedMsiPath -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedChecksum = "$hash  $installerFilename"
$checksum = (Get-Content -LiteralPath $resolvedChecksumPath -Raw).Trim()

if ($checksum -cne $expectedChecksum) {
    throw "Checksum file does not exactly match the MSI SHA-256 and filename."
}

$manifest = [ordered]@{
    version = $Version
    tag = $Tag
    product = $Product
    architecture = $Architecture
    minimumWindowsVersion = $MinimumWindowsVersion
    installerFilename = $installerFilename
    sha256 = $hash
    publishedAt = $PublishedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    releaseUrl = $ReleaseUrl
}

$resolvedOutputPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    [System.IO.Path]::GetFullPath($OutputPath)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputPath))
}
$outputDirectory = Split-Path -Parent $resolvedOutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}

[System.IO.File]::WriteAllText(
    $resolvedOutputPath,
    (($manifest | ConvertTo-Json -Depth 3) + "`n"),
    [System.Text.UTF8Encoding]::new($false)
)

Write-Output $resolvedOutputPath
