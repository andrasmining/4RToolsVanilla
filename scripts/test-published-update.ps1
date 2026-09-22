[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][Alias('Version')][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string] $ExpectedVersion,
    [ValidateSet('andrasmining/4RToolsVanilla')][string] $Repository = 'andrasmining/4RToolsVanilla',
    [Parameter(Mandatory = $true)][string] $ExecutablePath,
    [ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [string] $ReportPath,
    [switch] $AllowOlderClient,
    [switch] $UseApiToken
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Wait-VerifiedTask([object] $Task, [int] $TimeoutMilliseconds, [string] $Label) {
    try {
        if (-not $Task.Wait($TimeoutMilliseconds)) { throw "$Label timed out." }
    }
    catch {
        $root = $_.Exception
        while ($root.InnerException) { $root = $root.InnerException }
        throw "$Label failed: $($root.Message)"
    }
}

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
if (-not $ExecutablePath.StartsWith($repositoryRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw 'Probe executable must be inside this repository workspace.'
}
if (-not $ReportPath) { $ReportPath = Join-Path $repositoryRoot 'dist/published/updater-discovery.json' }
$ReportPath = [IO.Path]::GetFullPath($ReportPath)

if ([Environment]::Is64BitProcess) {
    # Reflection-load only; never call the game companion entry point or installer.
    $arguments = @('-NoProfile', '-NonInteractive', '-File', $PSCommandPath, '-ExpectedVersion', $ExpectedVersion,
        '-Repository', $Repository, '-ExecutablePath', $ExecutablePath, '-ReportPath', $ReportPath)
    if ($ExpectedCommit) { $arguments += @('-ExpectedCommit', $ExpectedCommit) }
    if ($AllowOlderClient) { $arguments += '-AllowOlderClient' }
    if ($UseApiToken) { $arguments += '-UseApiToken' }

    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $env:WINDIR 'SysWOW64/WindowsPowerShell/v1.0/powershell.exe'
    $start.Arguments = ($arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    $start.WorkingDirectory = $repositoryRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true

    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(660000)) {
            $process.Kill()
            throw 'Updater probe timed out.'
        }
        $stdout.Result | Write-Host
        if ($process.ExitCode -ne 0) {
            $stderr.Result | Write-Host
            throw 'Updater probe failed.'
        }
    }
    finally { $process.Dispose() }
    return
}

$oldEnvironment = @{}
foreach ($name in @('FOURRTOOLS_DATA_ROOT', 'GH_TOKEN', 'GITHUB_TOKEN', 'GH_CONFIG_DIR')) {
    $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$work = Join-Path $repositoryRoot ('dist/published/probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null

try {
    if (-not $UseApiToken) {
        [Environment]::SetEnvironmentVariable('GH_TOKEN', $null)
        [Environment]::SetEnvironmentVariable('GITHUB_TOKEN', $null)
    }
    elseif (-not $env:GH_TOKEN -and -not $env:GITHUB_TOKEN) {
        throw 'Explicit legacy-auth probe requires an API token in the environment.'
    }

    [Environment]::SetEnvironmentVariable('GH_CONFIG_DIR', (Join-Path $work 'empty-gh-config'))
    [Environment]::SetEnvironmentVariable('FOURRTOOLS_DATA_ROOT', (Join-Path $work 'data'))

    $assembly = [Reflection.Assembly]::LoadFrom($ExecutablePath)
    $updater = $assembly.GetType('_4RTools.Model.Vanilla.VanillaUpdater', $true)
    $current = $updater.GetProperty('CurrentVersion').GetValue($null, $null)
    if ($current -gt [version]$ExpectedVersion -or
        ($current -ne [version]$ExpectedVersion -and -not $AllowOlderClient)) {
        throw 'Probe client version is unexpected.'
    }

    $task = $updater.GetMethod('ReadLatestReleaseAsync').Invoke($null, $null)
    Wait-VerifiedTask $task 45000 'Latest release lookup'
    $info = $task.Result
    if ($null -eq $info -or $info.TagName -cne "v$ExpectedVersion" -or
        $info.Version -ne [version]$ExpectedVersion) {
        throw 'Latest does not identify the expected release.'
    }

    $zipName = "4RTools-Vanilla-v$ExpectedVersion-portable.zip"
    $assetPrefix = "https://api.github.com/repos/$Repository/releases/assets/"
    $publicPrefix = "https://github.com/$Repository/releases/download/v$ExpectedVersion/"
    $zipApi = $info.ZipUrl -match ('^' + [regex]::Escape($assetPrefix) + '[0-9]+$')
    $checksumApi = $info.ChecksumUrl -match ('^' + [regex]::Escape($assetPrefix) + '[0-9]+$')
    $zipPublic = $info.ZipUrl -ceq ($publicPrefix + $zipName)
    $checksumPublic = $info.ChecksumUrl -ceq ($publicPrefix + $zipName + '.sha256')

    if ($info.ZipName -cne $zipName -or
        -not (($zipApi -and $checksumApi) -or ($zipPublic -and $checksumPublic))) {
        throw 'Updater asset identity differs from the canonical repository.'
    }
    if (-not $UseApiToken -and -not ($zipPublic -and $checksumPublic)) {
        throw 'The current public updater did not use the anonymous github.com release-download path.'
    }

    $check = $updater.GetMethod('CheckAsync').Invoke($null, $null)
    Wait-VerifiedTask $check 45000 'Version comparison'
    if ($current -eq [version]$ExpectedVersion) {
        if ($null -ne $check.Result) { throw 'Equal version should report up to date.' }
    }
    elseif ($null -eq $check.Result -or $check.Result.Version -ne [version]$ExpectedVersion) {
        throw 'Older client did not discover the newer version.'
    }

    $stage = $updater.GetMethod('DownloadAndStageAsync').Invoke($null, [object[]]@($info))
    Wait-VerifiedTask $stage 620000 'Real updater staging'
    $payload = [IO.Path]::GetFullPath([string]$stage.Result)
    if (-not $payload.StartsWith($work + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Updater staging escaped its isolated directory.'
    }

    $versionInfo = [IO.File]::ReadAllText((Join-Path $payload 'VERSION.txt'))
    if ($versionInfo -notmatch '(?m)^Source commit: ([0-9a-f]{40})\r?$') {
        throw 'Packaged source identity is missing.'
    }
    $sourceCommit = $Matches[1]
    if ($ExpectedCommit -and $sourceCommit -cne $ExpectedCommit) {
        throw 'Published package does not match the tested commit.'
    }
    if ($versionInfo -notmatch '(?m)^Source working tree before packaging: clean\r?$') {
        throw 'Package source was not clean.'
    }

    $downloadedExe = Join-Path $payload '4RTools-Vanilla.exe'
    $downloadVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($downloadedExe)
    if (("{0}.{1}.{2}" -f $downloadVersion.FileMajorPart, $downloadVersion.FileMinorPart,
        $downloadVersion.FileBuildPart) -cne $ExpectedVersion) {
        throw 'Downloaded binary version is wrong.'
    }
    if ($current -eq [version]$ExpectedVersion -and
        (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $downloadedExe -Algorithm SHA256).Hash) {
        throw 'Published executable differs from the validated artifact.'
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $ReportPath) -Force | Out-Null
    [ordered]@{
        installedClientVersion = $current.ToString(3)
        publishedVersion = $info.Version.ToString(3)
        repository = $Repository
        sourceCommit = $sourceCommit
        anonymousPublicAccess = (-not [bool]$UseApiToken)
        versionComparisonPassed = $true
        realUpdaterDownloadAndStagePassed = $true
        portableChecksumManifestAndVersionPassed = $true
        equalVersionExecutableIdentityChecked = ($current -eq [version]$ExpectedVersion)
        installedOnUserMachine = $false
        applicationLaunched = $false
    } | ConvertTo-Json | Out-File -LiteralPath $ReportPath -Encoding utf8

    Write-Host "Updater $current verified published v$($ExpectedVersion): discovery, version, real download, checksums and source identity. No installation or UI launch."
}
finally {
    foreach ($name in $oldEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name])
    }
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
