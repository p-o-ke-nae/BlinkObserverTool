param(
    [Parameter(Mandatory = $true)]
    [string]$SettingsPath,
    [Parameter(Mandatory = $true)]
    [string]$OutputDir,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $SettingsPath)) {
    throw "Settings file was not found: $SettingsPath"
}

try {
    $settings = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    throw "installer.settings.json is not valid JSON: $($_.Exception.Message)"
}

$supportedProperties = @(
    '$schema',
    'ProductName',
    'Manufacturer',
    'Version',
    'UpgradeCode',
    'AppDirectoryName',
    'CommonCompanyDirName',
    'CommonAppsRootDirName',
    'MainExecutableName',
    'IconFilePath',
    'AppSourceDir',
    'ReferencedAppProjectPath',
    'PublishConfiguration',
    'PublishRuntimeIdentifier',
    'PublishSelfContained'
)

$unknownProperties = @($settings.PSObject.Properties.Name | Where-Object { $_ -notin $supportedProperties })
if ($unknownProperties.Count -gt 0) {
    throw "Unsupported installer.settings.json properties: $($unknownProperties -join ', ')"
}

$requiredProperties = @(
    'ProductName',
    'Manufacturer',
    'Version',
    'UpgradeCode',
    'AppDirectoryName',
    'CommonCompanyDirName',
    'CommonAppsRootDirName',
    'MainExecutableName',
    'IconFilePath',
    'AppSourceDir',
    'ReferencedAppProjectPath',
    'PublishRuntimeIdentifier',
    'PublishSelfContained'
)

$missingProperties = @($requiredProperties | Where-Object { $null -eq $settings.PSObject.Properties[$_] })
if ($missingProperties.Count -gt 0) {
    throw "Missing installer.settings.json properties: $($missingProperties -join ', ')"
}

function Require-Value([object]$value, [string]$name) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        throw "$name is required in installer.settings.json."
    }
}

function Escape-Xml([string]$value) {
    if ($null -eq $value) { return "" }
    return [System.Security.SecurityElement]::Escape($value)
}

Require-Value $settings.ProductName "ProductName"
Require-Value $settings.Manufacturer "Manufacturer"
Require-Value $settings.Version "Version"
Require-Value $settings.UpgradeCode "UpgradeCode"
Require-Value $settings.AppDirectoryName "AppDirectoryName"
Require-Value $settings.CommonCompanyDirName "CommonCompanyDirName"
Require-Value $settings.CommonAppsRootDirName "CommonAppsRootDirName"
Require-Value $settings.MainExecutableName "MainExecutableName"
Require-Value $settings.AppSourceDir "AppSourceDir"

$upgradeCode = [string]$settings.UpgradeCode
$version = [string]$settings.Version
$appSourceDir = [string]$settings.AppSourceDir
$mainExecutableName = [string]$settings.MainExecutableName
$iconFilePath = [string]$settings.IconFilePath
$referencedProjectPath = [string]$settings.ReferencedAppProjectPath
$publishConfigurationProperty = $settings.PSObject.Properties['PublishConfiguration']
$publishConfiguration = if ($null -eq $publishConfigurationProperty) { "" } else { [string]$publishConfigurationProperty.Value }
$publishRuntimeIdentifier = [string]$settings.PublishRuntimeIdentifier
$publishSelfContained = $settings.PublishSelfContained

foreach ($propertyName in @(
    'ProductName',
    'Manufacturer',
    'AppDirectoryName',
    'CommonCompanyDirName',
    'CommonAppsRootDirName',
    'MainExecutableName'
)) {
    $propertyValue = [string]$settings.$propertyName
    if ($propertyValue.IndexOfAny([char[]]"`r`n`t;") -ge 0) {
        throw "$propertyName contains a control character or semicolon, which is not supported by WiX constants."
    }
}

if ([System.IO.Path]::GetFileName($mainExecutableName) -ne $mainExecutableName -or
    [System.IO.Path]::GetExtension($mainExecutableName) -ine '.exe') {
    throw "MainExecutableName must be an .exe file name without a directory."
}

if (-not [string]::IsNullOrWhiteSpace($publishConfiguration) -and
    $publishConfiguration -notin @('Debug', 'Release')) {
    throw "PublishConfiguration must be Debug or Release when specified."
}

if ($publishSelfContained -isnot [bool]) {
    throw "PublishSelfContained must be a JSON boolean."
}
$publishSelfContained = $publishSelfContained.ToString().ToLowerInvariant()

$parsedGuid = [Guid]::Empty
if (-not [Guid]::TryParse($upgradeCode, [ref]$parsedGuid)) {
    throw "UpgradeCode must be a valid GUID."
}
$upgradeCode = $parsedGuid.ToString('D').ToUpperInvariant()

$settingsDir = Split-Path -Parent $SettingsPath
if (-not [System.IO.Path]::IsPathRooted($appSourceDir)) {
    $appSourceDir = Join-Path $settingsDir $appSourceDir
}
$appSourceDir = [System.IO.Path]::GetFullPath($appSourceDir)

if (-not [string]::IsNullOrWhiteSpace($referencedProjectPath)) {
    if (-not [System.IO.Path]::IsPathRooted($referencedProjectPath)) {
        $referencedProjectPath = Join-Path $settingsDir $referencedProjectPath
    }
    $referencedProjectPath = [System.IO.Path]::GetFullPath($referencedProjectPath)
    if (-not (Test-Path -LiteralPath $referencedProjectPath)) {
        throw "ReferencedAppProjectPath was not found: $referencedProjectPath"
    }
}
elseif (-not (Test-Path -LiteralPath $appSourceDir)) {
    throw "AppSourceDir was not found: $appSourceDir"
}

if ([string]::IsNullOrWhiteSpace($referencedProjectPath)) {
    $mainExecutableFullPath = Join-Path $appSourceDir $mainExecutableName
    if (-not (Test-Path -LiteralPath $mainExecutableFullPath)) {
        throw "MainExecutableName was not found under AppSourceDir: $mainExecutableName"
    }
}

if ([string]::IsNullOrWhiteSpace($version) -or $version -in @("auto", "git") -or
    ($Configuration -eq "Release" -and -not [string]::IsNullOrWhiteSpace($referencedProjectPath))) {
    if ([string]::IsNullOrWhiteSpace($referencedProjectPath)) {
        throw "Version is required in installer.settings.json when ReferencedAppProjectPath is not specified."
    }

    $projectDir = Split-Path -Parent $referencedProjectPath
    $buildVersionScriptPath = [System.IO.Path]::GetFullPath((Join-Path $settingsDir "..\..\build\Resolve-GitVersion.ps1"))
    if (-not (Test-Path -LiteralPath $buildVersionScriptPath)) {
        throw "Build version script was not found: $buildVersionScriptPath"
    }

    $versionJson = (& $buildVersionScriptPath -RepositoryPath $projectDir -Configuration $Configuration -OutputFormat Json) | Out-String
    if (-not $? -or [string]::IsNullOrWhiteSpace($versionJson)) {
        throw "Failed to resolve installer version from Git metadata."
    }
    $resolvedVersion = $versionJson | ConvertFrom-Json
    $gitVersion = [string]$resolvedVersion.MsiVersion
    if ($Configuration -eq "Release" -and $version -notin @("", "auto", "git") -and $version -ne $gitVersion) {
        throw "Configured installer Version '$version' conflicts with the HEAD Git tag version '$gitVersion'."
    }
    $version = $gitVersion
}

if (-not ($version -match '^\d+\.\d+\.\d+$')) {
    throw "Version must be in MSI format: Major.Minor.Build."
}

$versionParts = @($version.Split('.') | ForEach-Object { [uint32]$_ })
if ($versionParts[0] -gt 255 -or $versionParts[1] -gt 255 -or $versionParts[2] -gt 65535) {
    throw "Version exceeds MSI limits (Major <= 255, Minor <= 255, Build <= 65535)."
}

if (-not [string]::IsNullOrWhiteSpace($iconFilePath)) {
    if (-not [System.IO.Path]::IsPathRooted($iconFilePath)) {
        $iconFilePath = Join-Path $settingsDir $iconFilePath
    }
    $iconFileFullPath = [System.IO.Path]::GetFullPath($iconFilePath)
    if (-not (Test-Path -LiteralPath $iconFileFullPath)) {
        throw "IconFilePath was not found: $iconFileFullPath"
    }
    if ([System.IO.Path]::GetExtension($iconFileFullPath).ToLowerInvariant() -ne ".ico") {
        throw "IconFilePath must be an .ico file."
    }
    $iconFilePath = $iconFileFullPath
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$propsPath = Join-Path $OutputDir "InstallerConfig.props"

$propsContent = @"
<Project>
  <PropertyGroup>
    <InstallerProductName>$(Escape-Xml([string]$settings.ProductName))</InstallerProductName>
    <InstallerManufacturer>$(Escape-Xml([string]$settings.Manufacturer))</InstallerManufacturer>
    <InstallerVersion>$(Escape-Xml($version))</InstallerVersion>
    <InstallerUpgradeCode>$(Escape-Xml($upgradeCode))</InstallerUpgradeCode>
    <InstallerAppDirectoryName>$(Escape-Xml([string]$settings.AppDirectoryName))</InstallerAppDirectoryName>
    <InstallerCommonCompanyDirName>$(Escape-Xml([string]$settings.CommonCompanyDirName))</InstallerCommonCompanyDirName>
    <InstallerCommonAppsRootDirName>$(Escape-Xml([string]$settings.CommonAppsRootDirName))</InstallerCommonAppsRootDirName>
    <InstallerMainExecutableName>$(Escape-Xml($mainExecutableName))</InstallerMainExecutableName>
    <InstallerIconFilePath>$(Escape-Xml($iconFilePath))</InstallerIconFilePath>
    <InstallerAppSourceDir>$(Escape-Xml($appSourceDir))</InstallerAppSourceDir>
    <InstallerReferencedAppProjectPath>$(Escape-Xml($referencedProjectPath))</InstallerReferencedAppProjectPath>
    <InstallerPublishConfiguration>$(Escape-Xml($publishConfiguration))</InstallerPublishConfiguration>
    <InstallerPublishRuntimeIdentifier>$(Escape-Xml($publishRuntimeIdentifier))</InstallerPublishRuntimeIdentifier>
    <InstallerPublishSelfContained>$(Escape-Xml($publishSelfContained))</InstallerPublishSelfContained>
  </PropertyGroup>
</Project>
"@

$normalizedPropsContent = $propsContent.Replace("`r`n", "`n").TrimEnd() + "`n"
$existingPropsContent = if (Test-Path -LiteralPath $propsPath) {
    [System.IO.File]::ReadAllText($propsPath)
} else {
    $null
}

if ($existingPropsContent -cne $normalizedPropsContent) {
    [System.IO.File]::WriteAllText(
        $propsPath,
        $normalizedPropsContent,
        [System.Text.UTF8Encoding]::new($false)
    )
}

Write-Host "Generated installer config:"
Write-Host "  $propsPath"
