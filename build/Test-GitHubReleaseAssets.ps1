[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Repository,

    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [string[]]$ExpectedAssetNames = @(
        "BlinkObserverTool.Installer.msi",
        "BlinkObserverTool.Installer.msi.sha256",
        "manifest.json"
    )
)

$ErrorActionPreference = "Stop"

if ($Tag -cnotmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Tag must be vMAJOR.MINOR.PATCH."
}

$release = @(
    gh api --paginate "repos/$Repository/releases?per_page=100" |
        ConvertFrom-Json |
        Where-Object { $_.tag_name -cne $null -and $_.tag_name -cne "" -and $_.tag_name -ceq $Tag }
) | Select-Object -First 1
if ($null -eq $release) {
    throw "GitHub Release with tag '$Tag' was not found, including draft releases."
}
if ($release.tag_name -cne $Tag) {
    throw "GitHub Release tag '$($release.tag_name)' does not match '$Tag'."
}

$actualAssetNames = @($release.assets | ForEach-Object { [string]$_.name } | Sort-Object)
$expected = @($ExpectedAssetNames | Sort-Object)
if (($actualAssetNames -join "`n") -cne ($expected -join "`n")) {
    throw "Release assets differ. Expected [$($expected -join ', ')], found [$($actualAssetNames -join ', ')]."
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$expectedReleaseUrl = "https://github.com/$Repository/releases/tag/$Tag"
if ($manifest.tag -cne $Tag -or $manifest.releaseUrl -cne $expectedReleaseUrl) {
    throw "Manifest does not identify the same GitHub Release."
}
if ($manifest.installer -cne "BlinkObserverTool.Installer.msi") {
    throw "Manifest installer filename is not the attached MSI."
}
if ($actualAssetNames -cnotcontains $manifest.installer) {
    throw "Manifest installer is absent from the GitHub Release."
}

Write-Output "Validated draft release assets for $Repository $Tag."
