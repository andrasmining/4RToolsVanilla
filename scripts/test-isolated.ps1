[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Executable,
    [string[]] $TestArguments = @(),
    [Parameter(Mandatory = $true)][string] $LogPath,
    [ValidateRange(1000, 3600000)][int] $TimeoutMilliseconds = 600000
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$Executable = [IO.Path]::GetFullPath($Executable)
$LogPath = [IO.Path]::GetFullPath($LogPath)
if (-not $Executable.StartsWith($repositoryRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Only repository-owned inert test executables may use this runner.'
}
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw "Missing inert test executable: $Executable" }
if ((Split-Path -Leaf $Executable) -notin @('Vanilla.Diagnostics.Tests.exe', 'UiLayoutHarness.exe', 'TemporaryUiHarness.exe', 'PortableSmokeHost.exe')) {
    throw 'The isolated runner accepts only the dedicated inert test hosts, never the normal application.'
}
New-Item -ItemType Directory -Path (Split-Path -Parent $LogPath) -Force | Out-Null
$dataRoot = Join-Path (Split-Path -Parent $LogPath) ('isolated-data-' + [Guid]::NewGuid().ToString('N'))
if (-not ('IsolatedDesktopProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'IsolatedDesktopProcess.cs') }
$exitCode = [IsolatedDesktopProcess]::Run($Executable, $TestArguments, $repositoryRoot, $LogPath,
    "$LogPath.stderr", $dataRoot, $TimeoutMilliseconds)
Get-Content -LiteralPath $LogPath -Encoding UTF8 | Write-Host
Get-Content -LiteralPath "$LogPath.stderr" -Encoding UTF8 | Write-Host
if ($exitCode -ne 0) { throw "Isolated tests failed (exit $exitCode). See $LogPath" }
