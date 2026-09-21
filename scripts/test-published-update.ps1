[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][Alias('Version')][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string] $ExpectedVersion,
    [ValidateSet('andrasmining/4RToolsVanilla')][string] $Repository = 'andrasmining/4RToolsVanilla',
    [string] $ExecutablePath,
    [ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [string] $ReportPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -eq 'true') { throw 'GitHub Actions is prohibited for this repository.' }
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $ExecutablePath) { $ExecutablePath = Join-Path $repositoryRoot 'bin/Release/4RTools-Vanilla.exe' }
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
if (-not $ExecutablePath.StartsWith($repositoryRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw 'Updater probe requires a repository-owned compiled executable.' }
if (-not $ReportPath) { $ReportPath = Join-Path $repositoryRoot 'dist/published/updater-discovery.json' }
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
if ([Environment]::Is64BitProcess) {
    # Reflection-load the x86 release in a hidden, noninteractive x86 host. Never
    # invoke the application's entry point, update installer, or normal UI.
    $host32 = Join-Path $env:WINDIR 'SysWOW64/WindowsPowerShell/v1.0/powershell.exe'
    $arguments = @('-NoProfile', '-NonInteractive', '-File', $PSCommandPath,
        '-ExpectedVersion', $ExpectedVersion, '-Repository', $Repository,
        '-ExecutablePath', $ExecutablePath, '-ReportPath', $ReportPath)
    if ($ExpectedCommit) { $arguments += @('-ExpectedCommit', $ExpectedCommit) }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $host32
    $start.Arguments = ($arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    $start.WorkingDirectory = $repositoryRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(660000)) { $process.Kill(); throw 'Private updater probe timed out.' }
        $outputTask.Result | Write-Host
        if ($process.ExitCode -ne 0) { $errorTask.Result | Write-Host; throw 'Private updater probe failed.' }
    }
    finally { $process.Dispose() }
    return
}
$assembly = [Reflection.Assembly]::LoadFrom($ExecutablePath)
$updater = $assembly.GetType('_4RTools.Model.Vanilla.VanillaUpdater', $true)
$access = $assembly.GetType('_4RTools.Model.Vanilla.VanillaUpdateAccess', $true)
$flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
$current = $updater.GetProperty('CurrentVersion').GetValue($null, $null)
if ($current -ne [version]$ExpectedVersion) { throw 'Probe binary does not match the expected private release version.' }
$oldDataRoot = [Environment]::GetEnvironmentVariable('FOURRTOOLS_DATA_ROOT')
$oldToken = [Environment]::GetEnvironmentVariable('GH_TOKEN')
$work = Join-Path $repositoryRoot ('dist/published/probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    # Resolve using the actual portable credential provider before isolating user
    # data. The credential stays only in this short-lived process; never print it.
    $credentialTask = $access.GetMethod('GetTokenAsync', $flags).Invoke($null, $null)
    if (-not $credentialTask.Wait(15000)) { throw 'Private updater credential lookup timed out.' }
    [Environment]::SetEnvironmentVariable('GH_TOKEN', $credentialTask.Result)
    [Environment]::SetEnvironmentVariable('FOURRTOOLS_DATA_ROOT', (Join-Path $work 'data'))
    $task = $updater.GetMethod('ReadLatestReleaseAsync').Invoke($null, $null)
    if (-not $task.Wait(45000)) { throw 'Private latest release lookup timed out.' }
    $info = $task.Result
    if ($null -eq $info -or $info.TagName -cne "v$ExpectedVersion" -or $info.Version -ne [version]$ExpectedVersion) {
        throw 'Private Latest does not identify the expected published release.'
    }
    $assetPrefix = "https://api.github.com/repos/$Repository/releases/assets/"
    if ($info.ZipName -cne "4RTools-Vanilla-v$ExpectedVersion-portable.zip" -or
        $info.ZipUrl -notmatch ('^' + [regex]::Escape($assetPrefix) + '[0-9]+$') -or
        $info.ChecksumUrl -notmatch ('^' + [regex]::Escape($assetPrefix) + '[0-9]+$')) { throw 'Updater asset identity does not match the private repository.' }
    $checkTask = $updater.GetMethod('CheckAsync').Invoke($null, $null)
    if (-not $checkTask.Wait(45000) -or $null -ne $checkTask.Result) { throw 'Equal-version private release should report up to date.' }
    $stageTask = $updater.GetMethod('DownloadAndStageAsync').Invoke($null, [object[]]@($info))
    if (-not $stageTask.Wait(620000)) { throw 'Authenticated updater download/staging timed out.' }
    $payload = [string]$stageTask.Result
    $resolvedPayload = [IO.Path]::GetFullPath($payload)
    if (-not $resolvedPayload.StartsWith($work + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Updater staging escaped the isolated probe folder.' }
    $versionInfo = [IO.File]::ReadAllText((Join-Path $payload 'VERSION.txt'))
    if ($versionInfo -notmatch '(?m)^Source commit: ([0-9a-f]{40})\r?$') { throw 'Packaged source identity is missing.' }
    $sourceCommit = $Matches[1]
    if ($ExpectedCommit -and $sourceCommit -cne $ExpectedCommit) { throw 'Published package source commit differs from the tested release commit.' }
    if ($versionInfo -notmatch '(?m)^Source working tree before packaging: clean\r?$') { throw 'Published package was not built from a clean source tree.' }
    $downloadedExe = Join-Path $payload '4RTools-Vanilla.exe'
    $localHash = (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash
    $publishedHash = (Get-FileHash -LiteralPath $downloadedExe -Algorithm SHA256).Hash
    if ($localHash -cne $publishedHash) { throw 'Published executable differs from the tested local release binary.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $ReportPath) -Force | Out-Null
    [ordered]@{
        version = $info.Version.ToString(3)
        repository = $Repository
        sourceCommit = $sourceCommit
        zipAssetApi = $info.ZipUrl
        checksumAssetApi = $info.ChecksumUrl
        authenticatedPrivateLatestVerified = $true
        equalVersionCheckPassed = $true
        realUpdaterDownloadAndStagePassed = $true
        portableChecksumManifestAndVersionPassed = $true
        publishedExecutableMatchesLocal = $true
        installedOnUserMachine = $false
        applicationLaunched = $false
    } | ConvertTo-Json | Out-File -LiteralPath $ReportPath -Encoding utf8
    Write-Host "Private updater verified v${ExpectedVersion}: authenticated discovery, assets, checksums, source and executable identity. No installation or application launch."
}
finally {
    [Environment]::SetEnvironmentVariable('GH_TOKEN', $oldToken)
    [Environment]::SetEnvironmentVariable('FOURRTOOLS_DATA_ROOT', $oldDataRoot)
    $safeWork = [IO.Path]::GetFullPath($work)
    $safeParent = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'dist/published')) + '\'
    if ($safeWork.StartsWith($safeParent, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $safeWork)) {
        Remove-Item -LiteralPath $safeWork -Recurse -Force
    }
}
