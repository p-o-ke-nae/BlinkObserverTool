[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryPath,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateSet("Json", "Props")]
    [string]$OutputFormat = "Json",

    [string]$OutputPath
)

$ErrorActionPreference = "Stop"

function Invoke-Git {
    param(
        [string[]]$Arguments,
        [switch]$Optional
    )

    try {
        $output = @(& git -C $script:ResolvedRepositoryPath @Arguments 2>&1)
        if ($LASTEXITCODE -ne 0) {
            if ($Optional) {
                return $null
            }

            $details = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
            throw "Git command failed: git -C `"$script:ResolvedRepositoryPath`" $($Arguments -join ' ')`n$details"
        }

        return @($output | ForEach-Object { [string]$_ })
    }
    catch {
        if ($Optional) {
            return $null
        }

        throw
    }
}

function New-VersionResult {
    param(
        [int]$Major,
        [int]$Minor,
        [int]$Patch,
        [string]$InformationalVersion,
        [string]$Source
    )

    $numericVersion = "$Major.$Minor.$Patch"
    [pscustomobject][ordered]@{
        PackageVersion       = if ($Source -eq "git-tag") { $numericVersion } else { "0.0.0-local" }
        AssemblyVersion      = "$Major.$Minor.$Patch.0"
        FileVersion          = "$Major.$Minor.$Patch.0"
        InformationalVersion = $InformationalVersion
        MsiVersion           = $numericVersion
        Source               = $Source
    }
}

function Write-VersionResult {
    param([object]$Result)

    if ($OutputFormat -eq "Json") {
        $content = $Result | ConvertTo-Json -Compress
    }
    else {
        $content = @"
<Project>
  <PropertyGroup>
    <ResolvedPackageVersion>$([System.Security.SecurityElement]::Escape($Result.PackageVersion))</ResolvedPackageVersion>
    <ResolvedAssemblyVersion>$([System.Security.SecurityElement]::Escape($Result.AssemblyVersion))</ResolvedAssemblyVersion>
    <ResolvedFileVersion>$([System.Security.SecurityElement]::Escape($Result.FileVersion))</ResolvedFileVersion>
    <ResolvedInformationalVersion>$([System.Security.SecurityElement]::Escape($Result.InformationalVersion))</ResolvedInformationalVersion>
    <ResolvedMsiVersion>$([System.Security.SecurityElement]::Escape($Result.MsiVersion))</ResolvedMsiVersion>
    <ResolvedVersionSource>$([System.Security.SecurityElement]::Escape($Result.Source))</ResolvedVersionSource>
  </PropertyGroup>
</Project>
"@
    }

    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        Write-Output $content
        return
    }

    $resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = Split-Path -Parent $resolvedOutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    Set-Content -LiteralPath $resolvedOutputPath -Value $content -Encoding UTF8
}

$script:ResolvedRepositoryPath = [System.IO.Path]::GetFullPath($RepositoryPath)
if (-not (Test-Path -LiteralPath $script:ResolvedRepositoryPath -PathType Container)) {
    throw "RepositoryPath was not found: $script:ResolvedRepositoryPath"
}

if ($Configuration -ne "Release") {
    $shortCommit = @(Invoke-Git -Arguments @("rev-parse", "--short=12", "HEAD") -Optional)
    $informationalVersion = "0.0.0-local"
    if ($null -ne $shortCommit -and $shortCommit.Count -eq 1 -and $shortCommit[0] -match '^[0-9a-fA-F]+$') {
        $informationalVersion += "+g$(([string]$shortCommit[0]).ToLowerInvariant())"
    }

    Write-VersionResult (New-VersionResult -Major 0 -Minor 0 -Patch 0 -InformationalVersion $informationalVersion -Source "local")
    return
}

$insideWorkTree = @(Invoke-Git -Arguments @("rev-parse", "--is-inside-work-tree") -Optional)
if ($insideWorkTree.Count -ne 1 -or $insideWorkTree[0] -ne "true") {
    throw "Git metadata is required for Release versioning, but '$script:ResolvedRepositoryPath' is not a Git work tree."
}

$headCommit = @(Invoke-Git -Arguments @("rev-parse", "--verify", "HEAD") -Optional)
if ($headCommit.Count -ne 1 -or $headCommit[0] -notmatch '^[0-9a-fA-F]{40,64}$') {
    throw "Git metadata is required for Release versioning, but the repository has no valid HEAD commit."
}

$tags = @(Invoke-Git -Arguments @("tag", "--points-at", "HEAD"))
if ($tags.Count -eq 0) {
    throw "Release versioning requires HEAD to have exactly one Git tag named vMAJOR.MINOR.PATCH."
}

$tagPattern = '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$'
$malformedTags = @($tags | Where-Object { $_ -notmatch $tagPattern })
if ($malformedTags.Count -gt 0) {
    throw "Malformed Git tag(s) at HEAD: $($malformedTags -join ', '). Release tags must match vMAJOR.MINOR.PATCH exactly."
}

if ($tags.Count -gt 1) {
    throw "Conflicting Git release tags at HEAD: $($tags -join ', '). Release builds require exactly one version tag."
}

$tag = $tags[0]
$null = $tag -match $tagPattern
$major = [uint64]$Matches[1]
$minor = [uint64]$Matches[2]
$patch = [uint64]$Matches[3]

if ($major -gt 255 -or $minor -gt 255 -or $patch -gt 65535) {
    throw "Git tag '$tag' cannot be represented as an MSI version. MAJOR and MINOR must be <= 255; PATCH must be <= 65535."
}

$version = "$major.$minor.$patch"
Write-VersionResult (New-VersionResult -Major $major -Minor $minor -Patch $patch -InformationalVersion $version -Source "git-tag")
