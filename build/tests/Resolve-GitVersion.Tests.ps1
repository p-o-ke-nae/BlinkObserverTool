$ErrorActionPreference = "Stop"

$resolverPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\Resolve-GitVersion.ps1"))
$testRoot = Join-Path $PSScriptRoot ".test-runs"
$script:passed = 0
$script:failed = 0

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

function Assert-Matches {
    param([string]$Pattern, [string]$Actual, [string]$Message)
    if ($Actual -notmatch $Pattern) {
        throw "$Message Expected '$Actual' to match '$Pattern'."
    }
}

function Invoke-TestGit {
    param([string]$Repository, [string[]]$Arguments)
    $output = @(& git -C $Repository @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }
    return $output
}

function New-TestRepository {
    $path = Join-Path $testRoot ([Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    Invoke-TestGit -Repository $path -Arguments @("init", "-q")
    Invoke-TestGit -Repository $path -Arguments @("config", "user.name", "Version Test")
    Invoke-TestGit -Repository $path -Arguments @("config", "user.email", "version-test@example.invalid")
    Set-Content -LiteralPath (Join-Path $path "content.txt") -Value "test" -Encoding UTF8
    Invoke-TestGit -Repository $path -Arguments @("add", "content.txt")
    Invoke-TestGit -Repository $path -Arguments @("commit", "-q", "-m", "test commit")
    return $path
}

function Resolve-Version {
    param(
        [string]$Repository,
        [string]$Configuration = "Release",
        [switch]$IsolateFromParentRepository
    )

    $previousCeilingDirectories = $env:GIT_CEILING_DIRECTORIES
    try {
        if ($IsolateFromParentRepository) {
            $env:GIT_CEILING_DIRECTORIES = [System.IO.Path]::GetFullPath($Repository)
        }
        $json = (& $resolverPath -RepositoryPath $Repository -Configuration $Configuration -OutputFormat Json) | Out-String
        return $json | ConvertFrom-Json
    }
    finally {
        $env:GIT_CEILING_DIRECTORIES = $previousCeilingDirectories
    }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern)
    try {
        & $Action
    }
    catch {
        Assert-Matches $Pattern $_.Exception.Message "Unexpected error."
        return
    }
    throw "Expected an error matching '$Pattern', but no error was thrown."
}

function Test-Case {
    param([string]$Name, [scriptblock]$Action)
    try {
        & $Action
        $script:passed++
        Write-Host "PASS $Name"
    }
    catch {
        $script:failed++
        Write-Host "FAIL $Name"
        Write-Host "  $($_.Exception.Message)"
    }
}

if (Test-Path -LiteralPath $testRoot) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

try {
    Test-Case "Debug without Git metadata uses explicit local version" {
        $directory = Join-Path $testRoot "not-a-repository"
        New-Item -ItemType Directory -Path $directory | Out-Null
        $result = Resolve-Version $directory "Debug" -IsolateFromParentRepository
        Assert-Equal "0.0.0-local" $result.PackageVersion "PackageVersion mismatch."
        Assert-Equal "0.0.0.0" $result.AssemblyVersion "AssemblyVersion mismatch."
        Assert-Equal "0.0.0.0" $result.FileVersion "FileVersion mismatch."
        Assert-Equal "0.0.0-local" $result.InformationalVersion "InformationalVersion mismatch."
        Assert-Equal "0.0.0" $result.MsiVersion "MsiVersion mismatch."
        Assert-Equal "local" $result.Source "Source mismatch."
    }

    Test-Case "Debug in Git remains a local build" {
        $repository = New-TestRepository
        Invoke-TestGit -Repository $repository -Arguments @("tag", "v9.8.7")
        $result = Resolve-Version $repository "Debug"
        Assert-Equal "0.0.0-local" $result.PackageVersion "PackageVersion mismatch."
        Assert-Matches '^0\.0\.0-local\+g[0-9a-f]{12}$' $result.InformationalVersion "InformationalVersion mismatch."
        Assert-Equal "0.0.0" $result.MsiVersion "MsiVersion mismatch."
    }

    Test-Case "Release without Git metadata fails clearly" {
        $directory = Join-Path $testRoot "release-without-git"
        New-Item -ItemType Directory -Path $directory | Out-Null
        Assert-Throws { Resolve-Version $directory "Release" -IsolateFromParentRepository } 'Git metadata is required for Release versioning'
    }

    Test-Case "Release requires a tag at HEAD" {
        $repository = New-TestRepository
        Assert-Throws { Resolve-Version $repository } 'requires HEAD to have exactly one Git tag'
    }

    Test-Case "Release resolves a strict version tag" {
        $repository = New-TestRepository
        Invoke-TestGit -Repository $repository -Arguments @("tag", "-a", "v12.34.567", "-m", "release")
        $result = Resolve-Version $repository
        Assert-Equal "12.34.567" $result.PackageVersion "PackageVersion mismatch."
        Assert-Equal "12.34.567.0" $result.AssemblyVersion "AssemblyVersion mismatch."
        Assert-Equal "12.34.567.0" $result.FileVersion "FileVersion mismatch."
        Assert-Equal "12.34.567" $result.InformationalVersion "InformationalVersion mismatch."
        Assert-Equal "12.34.567" $result.MsiVersion "MsiVersion mismatch."
        Assert-Equal "git-tag" $result.Source "Source mismatch."
    }

    Test-Case "Release rejects malformed tags" {
        $repository = New-TestRepository
        Invoke-TestGit -Repository $repository -Arguments @("tag", "1.2.3")
        Assert-Throws { Resolve-Version $repository } 'Malformed Git tag'
    }

    Test-Case "Release rejects multiple conflicting tags" {
        $repository = New-TestRepository
        Invoke-TestGit -Repository $repository -Arguments @("tag", "v1.2.3")
        Invoke-TestGit -Repository $repository -Arguments @("tag", "v2.0.0")
        Assert-Throws { Resolve-Version $repository } 'Conflicting Git release tags'
    }

    Test-Case "Release rejects versions outside MSI limits" {
        $repository = New-TestRepository
        Invoke-TestGit -Repository $repository -Arguments @("tag", "v256.1.1")
        Assert-Throws { Resolve-Version $repository } 'cannot be represented as an MSI version'
    }

    Test-Case "Props output contains all consumer versions" {
        $repository = New-TestRepository
        Invoke-TestGit -Repository $repository -Arguments @("tag", "v3.4.5")
        $propsPath = Join-Path $repository "generated\version.props"
        & $resolverPath -RepositoryPath $repository -Configuration Release -OutputFormat Props -OutputPath $propsPath
        [xml]$props = Get-Content -LiteralPath $propsPath -Raw
        Assert-Equal "3.4.5" $props.Project.PropertyGroup.ResolvedPackageVersion "Props package version mismatch."
        Assert-Equal "3.4.5.0" $props.Project.PropertyGroup.ResolvedAssemblyVersion "Props assembly version mismatch."
        Assert-Equal "3.4.5.0" $props.Project.PropertyGroup.ResolvedFileVersion "Props file version mismatch."
        Assert-Equal "3.4.5" $props.Project.PropertyGroup.ResolvedInformationalVersion "Props informational version mismatch."
        Assert-Equal "3.4.5" $props.Project.PropertyGroup.ResolvedMsiVersion "Props MSI version mismatch."
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Host ""
Write-Host "$script:passed passed, $script:failed failed"
if ($script:failed -gt 0) {
    exit 1
}
