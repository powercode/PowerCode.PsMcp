<#
.SYNOPSIS
    Patches the PowerCode.PsMcp module manifest with the specified version.
.DESCRIPTION
    Updates ModuleVersion (and optionally Prerelease) in the PowerCode.PsMcp.psd1
    inside the module directory produced by the CI build. Called by the publish
    workflow after downloading the module artifact.
.PARAMETER ModulePath
    Path to the flat module directory that contains PowerCode.PsMcp.psd1.
.PARAMETER ModuleVersion
    Three-part version string to set (e.g. '1.2.3').
.PARAMETER Prerelease
    Optional prerelease label sanitised for PSGallery (no dots or hyphens,
    e.g. 'beta1'). When omitted the manifest is published as a stable release.
.EXAMPLE
    .\scripts\Update-PsMcpManifest.ps1 -ModulePath ./module -ModuleVersion '1.2.3'
.EXAMPLE
    .\scripts\Update-PsMcpManifest.ps1 -ModulePath ./module -ModuleVersion '1.2.3' -Prerelease 'beta1'
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ModulePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ModuleVersion,

    [Parameter()]
    [string]$Prerelease
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ManifestPath = Join-Path -Path $ModulePath -ChildPath 'PowerCode.PsMcp.psd1'
if (-not (Test-Path -Path $ManifestPath)) {
    Write-Error "Module manifest not found at '$ManifestPath'."
    return
}

if ($PSCmdlet.ShouldProcess($ManifestPath, 'Update module manifest version')) {
    Write-Verbose "Updating manifest at: $ManifestPath"
    Write-Verbose "  ModuleVersion: $ModuleVersion"
    Write-Verbose "  Prerelease:    $Prerelease"

    # Patch ModuleVersion directly in the file content so we are not
    # dependent on the directory name matching the version.
    $Content = Get-Content -Path $ManifestPath -Raw
    $Content = $Content -replace "(?<=ModuleVersion\s*=\s*')[^']*", $ModuleVersion
    Set-Content -Path $ManifestPath -Value $Content -NoNewline

    Write-Host 'Manifest updated successfully.'

    # Set Prerelease via Update-ModuleManifest after the version is patched.
    if ($Prerelease) {
        Update-ModuleManifest -Path $ManifestPath -Prerelease $Prerelease
    }

    # Validate the final manifest.
    $null = Test-ModuleManifest -Path $ManifestPath -ErrorAction Stop
    Write-Host 'Manifest validation passed.'
}
