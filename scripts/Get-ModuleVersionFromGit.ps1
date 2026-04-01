<#
.SYNOPSIS
    Derives module version and prerelease label from git describe output.
.DESCRIPTION
    Runs 'git describe --tags' to determine the current version from the
    nearest reachable tag. Supports both exact tags (v1.2.3, v1.2.3-beta.1)
    and intermediate commits (v1.2.3-5-gabcdef0).

    For exact tag matches the ModuleVersion is the three-part version and the
    Prerelease label (if any) is extracted from the tag suffix. For intermediate
    commits (not on a tag) an error is raised unless -AllowNonTag is specified,
    in which case the commit distance and hash are appended to the prerelease
    label.

    The prerelease label is sanitised for PSGallery compatibility by removing
    dots and hyphens.

    When an OutputFile is specified (defaults to $env:GITHUB_OUTPUT) the parsed
    values are appended as key=value lines so downstream CI steps can consume them.
.PARAMETER AllowNonTag
    When set, allows deriving a version from intermediate commits that are not
    on an exact tag. The commit distance and short hash are appended to the
    prerelease label (e.g. '1.2.3' with prerelease 'dev5gabcdef0').
.PARAMETER OutputFile
    Optional path to a file where 'module-version' and 'prerelease' key=value
    lines are appended. Defaults to $env:GITHUB_OUTPUT. Pass $null or an empty
    string to skip file output.
.OUTPUTS
    PSCustomObject with ModuleVersion and Prerelease properties.
.EXAMPLE
    .\scripts\Get-ModuleVersionFromGit.ps1
    # On tag v1.2.3 → ModuleVersion='1.2.3', Prerelease=''
.EXAMPLE
    .\scripts\Get-ModuleVersionFromGit.ps1 -AllowNonTag
    # On commit 5 after v1.2.3 → ModuleVersion='1.2.3', Prerelease='dev5gabcdef0'
#>
[CmdletBinding()]
param(
    [Parameter()]
    [switch]$AllowNonTag,

    [Parameter()]
    [string]$OutputFile = $env:GITHUB_OUTPUT
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Run git describe to get the version from the nearest tag
$GitDescribe = git describe --tags --always 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "git describe failed (exit code $LASTEXITCODE): $GitDescribe"
    return
}

Write-Verbose "git describe: $GitDescribe"

# Strip leading 'v' prefix
$VersionString = $GitDescribe -replace '^v', ''

# Pattern: <version>[-<prerelease>][-<distance>-g<hash>]
# Examples:
#   1.2.3                       → exact tag v1.2.3
#   1.2.3-beta.1                → exact tag v1.2.3-beta.1
#   1.2.3-5-gabcdef0            → 5 commits after v1.2.3
#   1.2.3-beta.1-5-gabcdef0     → 5 commits after v1.2.3-beta.1
if ($VersionString -notmatch '^(?<version>\d+\.\d+\.\d+)(?:-(?<suffix>.+))?$') {
    Write-Error "git describe output '$GitDescribe' does not match expected version pattern."
    return
}

$ModuleVersion = $Matches['version']
$Suffix = $Matches['suffix']

# Determine if this is an exact tag or an intermediate commit
# Intermediate commits have a suffix ending in -<distance>-g<hash>
$PrereleaseLabel = ''
if ($Suffix) {
    if ($Suffix -match '^(?<pre>.*?)-(?<distance>\d+)-g(?<hash>[0-9a-f]+)$') {
        # Intermediate commit: not on an exact tag
        if (-not $AllowNonTag) {
            Write-Error "Current commit is not on an exact tag (git describe: '$GitDescribe'). Use -AllowNonTag to allow intermediate versions."
            return
        }
        $Distance = $Matches['distance']
        $Hash = $Matches['hash']
        $BasePre = $Matches['pre']
        if ($BasePre) {
            $PrereleaseLabel = "${BasePre}dev${Distance}g${Hash}"
        }
        else {
            $PrereleaseLabel = "dev${Distance}g${Hash}"
        }
    }
    else {
        # Exact tag with prerelease (e.g. v1.2.3-beta.1)
        $PrereleaseLabel = $Suffix
    }
}

# PSGallery prerelease strings cannot contain dots or hyphens
if ($PrereleaseLabel) {
    $PrereleaseLabel = $PrereleaseLabel -replace '[.\-]', ''
}

Write-Host "ModuleVersion: $ModuleVersion"
Write-Host "Prerelease:    $PrereleaseLabel"

# Write to output file when specified
if ($OutputFile) {
    "module-version=$ModuleVersion" | Out-File -FilePath $OutputFile -Append
    "prerelease=$PrereleaseLabel" | Out-File -FilePath $OutputFile -Append
}

[PSCustomObject]@{
    ModuleVersion = $ModuleVersion
    Prerelease    = $PrereleaseLabel
}
