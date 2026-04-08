<#
.SYNOPSIS
    Installs a local build of PowerCode.PsMcp into the user PowerShell modules folder.
.DESCRIPTION
    Copies the compiled module output from artifacts/bin/PowerCode.PsMcp/<configuration>
    into the current user's module path so it can be imported by name for interactive
    testing (for example: Import-Module PowerCode.PsMcp -Force).

    The module is installed into a versioned folder layout:
    <ModuleRoot>/<ModuleName>/<Version>.
    For prerelease builds, the folder name includes the prerelease label
    (for example: 1.2.3-beta1).

    This script is intended for local development loops and does not publish to PSGallery.
.PARAMETER Configuration
    Build configuration folder to install from. Defaults to 'debug'.
.PARAMETER SourcePath
    Optional explicit source path that contains PowerCode.PsMcp.psd1.
    When omitted, the path is resolved from artifacts/bin/PowerCode.PsMcp/<configuration>.
.PARAMETER ModuleName
    Target module name folder in the user module root. Defaults to 'PowerCode.PsMcp'.
.PARAMETER ModuleRoot
    Optional explicit module root path. When omitted, the script selects a per-user
    path from PSModulePath.
.PARAMETER NoImport
    Skip importing the module after installation.
.EXAMPLE
    .\scripts\install.ps1
.EXAMPLE
    .\scripts\install.ps1 -Configuration release
.EXAMPLE
    .\scripts\install.ps1 -SourcePath .\artifacts\bin\PowerCode.PsMcp\debug -NoImport
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$Configuration = 'debug',

    [Parameter()]
    [string]$SourcePath,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$ModuleName = 'PowerCode.PsMcp',

    [Parameter()]
    [string]$ModuleRoot,

    [Parameter()]
    [switch]$NoImport
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptRoot = Split-Path -Path $PSCommandPath -Parent
$RepoRoot = Resolve-Path -Path (Join-Path -Path $ScriptRoot -ChildPath '..')

if (-not $SourcePath) {
    $SourcePath = Join-Path -Path $RepoRoot -ChildPath ("artifacts/bin/PowerCode.PsMcp/{0}" -f $Configuration.ToLowerInvariant())
}

$SourcePath = (Resolve-Path -Path $SourcePath).Path

$ManifestPath = Join-Path -Path $SourcePath -ChildPath "$ModuleName.psd1"
if (-not (Test-Path -Path $ManifestPath -PathType Leaf)) {
    throw "Module manifest '$ManifestPath' was not found. Build first with: dotnet build PowerCode.PsMcp.slnx -c $Configuration"
}

$ManifestData = Import-PowerShellDataFile -Path $ManifestPath
$ModuleVersion = [string]$ManifestData.ModuleVersion
if ([string]::IsNullOrWhiteSpace($ModuleVersion)) {
    throw "Unable to determine ModuleVersion from '$ManifestPath'."
}

$ManifestFileList = @()
if ($ManifestData.ContainsKey('FileList')) {
    $ManifestFileList = @($ManifestData.FileList)
}
elseif ($ManifestData.ContainsKey('Filelist')) {
    $ManifestFileList = @($ManifestData.Filelist)
}

if ($ManifestFileList.Count -eq 0) {
    throw "Manifest '$ManifestPath' does not define a non-empty FileList/Filelist."
}

$Prerelease = $null
if ($ManifestData.ContainsKey('PrivateData') -and $ManifestData.PrivateData -is [hashtable]) {
    $PsData = $ManifestData.PrivateData['PSData']
    if ($PsData -is [hashtable] -and $PsData.ContainsKey('Prerelease')) {
        $Prerelease = [string]$PsData['Prerelease']
    }
}

$VersionFolderName = if ([string]::IsNullOrWhiteSpace($Prerelease)) {
    $ModuleVersion
}
else {
    "$ModuleVersion-$Prerelease"
}

if (-not $ModuleRoot) {
    $ModulePathCandidates = ($env:PSModulePath -split [IO.Path]::PathSeparator) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    $ModuleRoot = $ModulePathCandidates |
        Where-Object {
            # Prefer a per-user module location under the current profile.
            $_ -like (Join-Path -Path $HOME -ChildPath '*')
        } |
        Select-Object -First 1

    if (-not $ModuleRoot) {
        throw 'Unable to determine a user module path from PSModulePath. Provide -ModuleRoot explicitly.'
    }
}

$ModuleRoot = [IO.Path]::GetFullPath($ModuleRoot)
if (-not (Test-Path -Path $ModuleRoot -PathType Container)) {
    New-Item -Path $ModuleRoot -ItemType Directory -Force | Out-Null
}

$ModuleBasePath = Join-Path -Path $ModuleRoot -ChildPath $ModuleName
if (-not (Test-Path -Path $ModuleBasePath -PathType Container)) {
    New-Item -Path $ModuleBasePath -ItemType Directory -Force | Out-Null
}

$DestinationPath = Join-Path -Path $ModuleBasePath -ChildPath $VersionFolderName
$DestinationManifestPath = Join-Path -Path $DestinationPath -ChildPath "$ModuleName.psd1"

if ($PSCmdlet.ShouldProcess($DestinationPath, "Install module from '$SourcePath'")) {
    if (Test-Path -Path $DestinationPath) {
        Remove-Item -Path $DestinationPath -Recurse -Force
    }

    New-Item -Path $DestinationPath -ItemType Directory -Force | Out-Null

    foreach ($RelativeFile in $ManifestFileList) {
        if ([string]::IsNullOrWhiteSpace($RelativeFile)) {
            continue
        }

        $RelativePath = [string]$RelativeFile
        $SourceFilePath = Join-Path -Path $SourcePath -ChildPath $RelativePath
        if (-not (Test-Path -Path $SourceFilePath -PathType Leaf)) {
            throw "Manifest file entry '$RelativePath' does not exist at '$SourceFilePath'."
        }

        $DestinationFilePath = Join-Path -Path $DestinationPath -ChildPath $RelativePath
        $DestinationDirectory = Split-Path -Path $DestinationFilePath -Parent
        if (-not (Test-Path -Path $DestinationDirectory -PathType Container)) {
            New-Item -Path $DestinationDirectory -ItemType Directory -Force | Out-Null
        }

        Copy-Item -Path $SourceFilePath -Destination $DestinationFilePath -Force
    }

    Write-Host "Installed $ModuleName $VersionFolderName to $DestinationPath"
    Write-Host "Manifest: $DestinationManifestPath"
}

if (-not $NoImport) {
    if ($PSCmdlet.ShouldProcess($ModuleName, 'Import module for current session')) {
        Import-Module -Name $DestinationManifestPath -Force -ErrorAction Stop
        Write-Host "Imported $ModuleName from $DestinationManifestPath into current session."
    }
}
