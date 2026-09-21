[CmdletBinding()]
param(
    [string]$ApplicationPath = (Join-Path $PSScriptRoot '..\bin\Release\4RTools-Vanilla.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\ui-layout')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ApplicationPath = [IO.Path]::GetFullPath($ApplicationPath)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not (Test-Path -LiteralPath $ApplicationPath)) { throw "Application missing: $ApplicationPath" }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$harness = Join-Path (Split-Path $ApplicationPath -Parent) 'UiLayoutHarness.exe'
$source = Join-Path $PSScriptRoot '..\Tests\UiLayout\Program.cs'
& $csc /nologo /target:exe /platform:x86 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "/out:$harness" $source
if ($LASTEXITCODE -ne 0) { throw 'UI layout harness compilation failed.' }
$stdout = Join-Path $OutputDirectory 'harness-stdout.txt'
& (Join-Path $PSScriptRoot 'test-isolated.ps1') -Executable $harness -TestArguments @($ApplicationPath, $OutputDirectory) -LogPath $stdout -TimeoutMilliseconds 180000
$reportPath = Join-Path $OutputDirectory 'layout-report.txt'
if (-not (Test-Path -LiteralPath $reportPath)) { throw 'UI layout report was not produced.' }
if ((Get-Content -LiteralPath $reportPath -Raw) -notmatch '(?m)^Failures: 0\s*$') {
    throw 'UI harness did not report zero failures.'
}
Write-Host 'Native Windows UI layout checks passed with mock accounts; no game input or live services were enabled.'
