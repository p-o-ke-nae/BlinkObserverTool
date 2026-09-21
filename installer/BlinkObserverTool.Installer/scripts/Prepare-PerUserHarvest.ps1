[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$HarvestPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$resolvedHarvestPath = (Resolve-Path -LiteralPath $HarvestPath).Path
$wixNamespace = "http://wixtoolset.org/schemas/v4/wxs"

function New-DeterministicGuid([string]$name) {
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes("BlinkObserverTool.Installer|$name"))
    }
    finally {
        $sha256.Dispose()
    }

    $guidBytes = [byte[]]::new(16)
    [Array]::Copy($bytes, $guidBytes, 16)
    $guidBytes[7] = ($guidBytes[7] -band 0x0F) -bor 0x50
    $guidBytes[8] = ($guidBytes[8] -band 0x3F) -bor 0x80
    return ([Guid]::new($guidBytes)).ToString("D").ToUpperInvariant()
}

function New-WixElement([xml]$document, [string]$name) {
    return $document.CreateElement($name, $wixNamespace)
}

$document = [xml]::new()
$document.PreserveWhitespace = $true
$document.Load($resolvedHarvestPath)

$namespaceManager = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
$namespaceManager.AddNamespace("wix", $wixNamespace)

$components = @($document.SelectNodes("//wix:Component", $namespaceManager))
foreach ($component in $components) {
    $componentId = $component.GetAttribute("Id")
    if ($componentId.StartsWith("Cleanup_", [StringComparison]::Ordinal) -or
        $null -ne $component.SelectSingleNode(
            "wix:RegistryValue[@Key='Software\pokenae\BlinkObserverTool\InstallerComponents']",
            $namespaceManager
        )) {
        continue
    }

    $component.SetAttribute("Guid", (New-DeterministicGuid "Component|$componentId"))

    foreach ($file in @($component.SelectNodes("wix:File[@KeyPath]", $namespaceManager))) {
        $file.RemoveAttribute("KeyPath")
    }

    $registryValue = New-WixElement $document "RegistryValue"
    $registryValue.SetAttribute("Root", "HKCU")
    $registryValue.SetAttribute("Key", "Software\pokenae\BlinkObserverTool\InstallerComponents")
    $registryValue.SetAttribute("Name", $componentId)
    $registryValue.SetAttribute("Type", "integer")
    $registryValue.SetAttribute("Value", "1")
    $registryValue.SetAttribute("KeyPath", "yes")
    [void]$component.AppendChild($registryValue)
}

$componentGroup = $document.SelectSingleNode("//wix:ComponentGroup[@Id='AppFiles']", $namespaceManager)
if ($null -eq $componentGroup) {
    throw "The harvested AppFiles component group was not found."
}

$directories = @($document.SelectNodes("//wix:Directory | //wix:DirectoryRef", $namespaceManager))
foreach ($directory in $directories) {
    $directoryId = $directory.GetAttribute("Id")
    $cleanupComponentId = "Cleanup_$directoryId"
    if ($null -ne $directory.SelectSingleNode("wix:Component[@Id='$cleanupComponentId']", $namespaceManager)) {
        continue
    }

    $cleanupComponent = New-WixElement $document "Component"
    $cleanupComponent.SetAttribute("Id", $cleanupComponentId)
    $cleanupComponent.SetAttribute("Guid", (New-DeterministicGuid "Cleanup|$directoryId"))

    $removeFolder = New-WixElement $document "RemoveFolder"
    $removeFolder.SetAttribute("Id", "Remove_$directoryId")
    $removeFolder.SetAttribute("On", "uninstall")
    [void]$cleanupComponent.AppendChild($removeFolder)

    $registryValue = New-WixElement $document "RegistryValue"
    $registryValue.SetAttribute("Root", "HKCU")
    $registryValue.SetAttribute("Key", "Software\pokenae\BlinkObserverTool\InstallerDirectories")
    $registryValue.SetAttribute("Name", $directoryId)
    $registryValue.SetAttribute("Type", "integer")
    $registryValue.SetAttribute("Value", "1")
    $registryValue.SetAttribute("KeyPath", "yes")
    [void]$cleanupComponent.AppendChild($registryValue)
    [void]$directory.AppendChild($cleanupComponent)

    $componentRef = New-WixElement $document "ComponentRef"
    $componentRef.SetAttribute("Id", $cleanupComponentId)
    [void]$componentGroup.AppendChild($componentRef)
}

$settings = [System.Xml.XmlWriterSettings]::new()
$settings.Encoding = [System.Text.UTF8Encoding]::new($false)
$settings.Indent = $true
$settings.NewLineChars = "`n"
$settings.NewLineHandling = [System.Xml.NewLineHandling]::Replace

$writer = [System.Xml.XmlWriter]::Create($resolvedHarvestPath, $settings)
try {
    $document.Save($writer)
}
finally {
    $writer.Dispose()
}

Write-Host "Prepared per-user harvested authoring:"
Write-Host "  $resolvedHarvestPath"
