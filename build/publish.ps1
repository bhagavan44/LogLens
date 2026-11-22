<#
.SYNOPSIS
  Builds LogLens as ONE self-contained exe: no .NET runtime, Windows App SDK, installer or zip to unpack.

.EXAMPLE
  pwsh build/publish.ps1                      # win-x64, Release, artifacts/LogLens-<version>-win-x64.exe
  pwsh build/publish.ps1 -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/LogLens.App/LogLens.App.csproj'
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts "publish-$Runtime"

# Version comes from Directory.Build.props so the file name always matches the assembly.
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
if (-not $version) { throw 'Version not found in Directory.Build.props' }

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force $artifacts | Out-Null

# Single-file, self-contained and compression are switched on by the project file while publishing (LogLens.App.csproj).
dotnet publish $project -c $Configuration -r $Runtime -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Anything besides the exe (apart from symbol files of the class libraries) means the single-file setup broke:
# a loose .pri / .xbf / .dll next to it would hide a missing resource that crashes on another machine.
$exe = Join-Path $publishDir 'LogLens.exe'
if (-not (Test-Path $exe)) { throw 'Publish output is missing LogLens.exe' }
$extra = Get-ChildItem $publishDir -Recurse -File | Where-Object { $_.FullName -ne $exe -and $_.Extension -ne '.pdb' }
if ($extra) { throw "Publish output is not a single file; unexpected: $($extra.Name -join ', ')" }

$target = Join-Path $artifacts "LogLens-$version-$Runtime.exe"
Copy-Item $exe $target -Force

$sizeMb = [math]::Round((Get-Item $target).Length / 1MB, 1)
Write-Host "Created $target ($sizeMb MB)"
