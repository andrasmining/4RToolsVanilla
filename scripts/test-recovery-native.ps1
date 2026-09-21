[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$test = Join-Path $root 'Tests/bin/Release/Vanilla.Diagnostics.Tests.exe'
if (-not (Test-Path -LiteralPath $test)) { throw 'Build Release tests before native recovery validation.' }
New-Item -ItemType Directory -Path (Join-Path $root 'dist') -Force | Out-Null
& (Join-Path $PSScriptRoot 'test-isolated.ps1') -Executable $test -TestArguments @('--native-recovery-tests') -LogPath (Join-Path $root 'dist/native-recovery.log') -TimeoutMilliseconds 120000
