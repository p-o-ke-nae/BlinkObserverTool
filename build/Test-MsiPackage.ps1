[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedUpgradeCode,

    [Parameter(Mandatory = $true)]
    [string]$MainExecutableName,

    [string[]]$RequiredFileNames = @()
)

$ErrorActionPreference = "Stop"

$resolvedMsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
if ([System.IO.Path]::GetExtension($resolvedMsiPath) -ne ".msi") {
    throw "MsiPath must point to an .msi file: $resolvedMsiPath"
}

function Release-ComObject([object]$value) {
    if ($null -ne $value -and [System.Runtime.InteropServices.Marshal]::IsComObject($value)) {
        [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($value)
    }
}

function Get-MsiScalar([object]$database, [string]$query) {
    $view = $null
    $record = $null
    try {
        $view = $database.OpenView($query)
        [void]$view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) {
            return $null
        }

        return ([string]$record.StringData(1))
    }
    finally {
        if ($null -ne $view) {
            [void]            [void]$view.Close()
        }
        Release-ComObject $record
        Release-ComObject $view
    }
}

function Get-MsiProperty([object]$database, [string]$name) {
    $escapedName = $name.Replace("'", "''")
    return (Get-MsiScalar $database "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$escapedName'")
}

function Test-MsiContainsFile([object]$database, [string]$fileName) {
    $view = $null
    $record = $null
    try {
        $view = $database.OpenView("SELECT ``FileName`` FROM ``File``")
        [void]$view.Execute()
        while ($null -ne ($record = $view.Fetch())) {
            $storedName = [string]$record.StringData(1)
            $longName = ($storedName -split '\|', 2)[-1]
            if ([string]::Equals($longName, $fileName, [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
            Release-ComObject $record
            $record = $null
        }
        return $false
    }
    finally {
        Release-ComObject $record
        if ($null -ne $view) {
            [void]$view.Close()
        }
        Release-ComObject $view
    }
}

$installer = $null
$database = $null
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase($resolvedMsiPath, 0)

    $actualVersionValue = Get-MsiProperty $database "ProductVersion"
    if ([string]::IsNullOrWhiteSpace($actualVersionValue)) {
        throw "ProductVersion was not found in the MSI Property table."
    }
    $actualVersion = $actualVersionValue.Trim()
    if ($actualVersion -ne $ExpectedVersion) {
        throw "ProductVersion mismatch. Expected '$ExpectedVersion', found '$actualVersion'."
    }

    $actualUpgradeCodeValue = Get-MsiProperty $database "UpgradeCode"
    if ([string]::IsNullOrWhiteSpace($actualUpgradeCodeValue)) {
        throw "UpgradeCode was not found in the MSI Property table."
    }
    $actualUpgradeCode = $actualUpgradeCodeValue.Trim()
    $actualUpgradeGuid = [Guid]::Empty
    $expectedUpgradeGuid = [Guid]::Empty
    if (-not [Guid]::TryParse($actualUpgradeCode, [ref]$actualUpgradeGuid) -or
        -not [Guid]::TryParse($ExpectedUpgradeCode, [ref]$expectedUpgradeGuid)) {
        throw "UpgradeCode values must be valid GUIDs. Expected '$ExpectedUpgradeCode', found '$actualUpgradeCode'."
    }
    if ($actualUpgradeGuid -ne $expectedUpgradeGuid) {
        throw "UpgradeCode mismatch. Expected '$ExpectedUpgradeCode', found '$actualUpgradeCode'."
    }

    $allRequiredFiles = @($MainExecutableName) + @($RequiredFileNames)
    foreach ($requiredFile in $allRequiredFiles | Select-Object -Unique) {
        if ([string]::IsNullOrWhiteSpace($requiredFile)) {
            continue
        }

        if (-not (Test-MsiContainsFile $database $requiredFile)) {
            throw "Required file '$requiredFile' was not found in the MSI File table."
        }
    }

    Write-Output "Validated MSI: $resolvedMsiPath"
    Write-Output "  ProductVersion: $actualVersion"
    Write-Output "  UpgradeCode: $actualUpgradeCode"
    Write-Output "  Required files: $($allRequiredFiles -join ', ')"
}
finally {
    Release-ComObject $database
    Release-ComObject $installer
}
