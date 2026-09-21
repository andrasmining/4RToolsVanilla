[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string] $Version,
    [switch] $Publish,
    [switch] $Restore,
    [switch] $Replace,
    [string] $MSBuildPath,
    [string] $CacheRoot = (Join-Path $env:LOCALAPPDATA '4RTools-Engineering')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$repository = 'andrasmining/4RToolsVanilla'
if ($env:GITHUB_ACTIONS -eq 'true') { throw 'This private repository uses local builds; GitHub Actions is disabled.' }

function Read-Git([string[]] $GitArguments) {
    $result = @(& git @GitArguments)
    if ($LASTEXITCODE -ne 0) { throw "Git verification failed: $($GitArguments -join ' ')" }
    return ($result -join "`n").Trim()
}
function Assert-PublishSource([string] $ExpectedCommit) {
    if ((Read-Git @('branch', '--show-current')) -cne 'main') { throw 'Publishing requires main.' }
    $origin = Read-Git @('remote', 'get-url', 'origin')
    if ($origin.TrimEnd('/') -notmatch '^(https://github\.com/|git@github\.com:)andrasmining/4RToolsVanilla(?:\.git)?$') {
        throw 'Publishing is restricted to the private andrasmining/4RToolsVanilla origin.'
    }
    if (Read-Git @('status', '--porcelain', '--untracked-files=normal')) { throw 'Commit all source changes before publishing.' }
    if ((Read-Git @('rev-parse', 'HEAD')) -cne $ExpectedCommit) { throw 'Source changed during release validation.' }
    $remote = Read-Git @('ls-remote', 'origin', 'refs/heads/main')
    if (($remote -split '\s+')[0] -cne $ExpectedCommit) { throw 'Push main before publishing; remote source must match exactly.' }
    $infoText = & gh repo view $repository --json nameWithOwner,isPrivate
    if ($LASTEXITCODE -ne 0) { throw 'Authenticated private repository access failed.' }
    $info = $infoText | ConvertFrom-Json
    if (-not $info.isPrivate -or $info.nameWithOwner -cne $repository) { throw 'Unexpected repository identity or visibility.' }
}

Push-Location $repositoryRoot
try {
    $sourceCommit = Read-Git @('rev-parse', 'HEAD')
    if ($Publish) { Assert-PublishSource $sourceCommit }
    $build = @{ Configuration = @('Debug', 'Release'); VanillaRelease = $true; Offline = (-not $Restore); CacheRoot = $CacheRoot }
    if ($MSBuildPath) { $build.MSBuildPath = $MSBuildPath }
    & (Join-Path $PSScriptRoot 'build.ps1') @build
    & (Join-Path $PSScriptRoot 'validate-vanilla-build-profiles.ps1')
    & (Join-Path $PSScriptRoot 'test-recovery-native.ps1')
    & (Join-Path $PSScriptRoot 'test-ui-layout.ps1')
    $package = @{ Version = $Version; CacheRoot = $CacheRoot; Offline = (-not $Restore); SkipBuild = $true; Replace = $Replace }
    if ($MSBuildPath) { $package.MSBuildPath = $MSBuildPath }
    & (Join-Path $PSScriptRoot 'build-release.ps1') @package
    $artifactBase = "4RTools-Vanilla-v$Version"
    $zip = Join-Path $repositoryRoot "dist/$artifactBase-portable.zip"
    $checksum = "$zip.sha256"
    $receipt = Get-Content -LiteralPath (Join-Path $repositoryRoot "dist/$artifactBase-build.json") -Raw | ConvertFrom-Json
    if ($receipt.sourceCommit -cne $sourceCommit) { throw 'Packaged source changed during validation.' }
    if (-not $Publish) {
        Write-Host "Local validation and portable packaging passed: $zip"
        Write-Host 'Publication was not requested. Network was used only if -Restore was supplied.'
        return
    }

    Assert-PublishSource $sourceCommit
    if ($receipt.sourceTree -cne 'clean') { throw 'Refusing to publish a package from modified source.' }
    $tag = "v$Version"
    $tagRemote = Read-Git @('ls-remote', 'origin', "refs/tags/$tag", "refs/tags/$tag^{}")
    if ($tagRemote) {
        $resolved = (($tagRemote -split "`n")[-1] -split '\s+')[0]
        if ($resolved -cne $sourceCommit) { throw "Existing tag $tag points to different source." }
    }
    $releaseListText = & gh release list --repo $repository --limit 1000 --json tagName
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate authenticated release metadata.' }
    $exists = @($releaseListText | ConvertFrom-Json | Where-Object { $_.tagName -ceq $tag }).Count -gt 0
    if (-not $exists) {
        & gh release create $tag $zip $checksum --repo $repository --target $sourceCommit --title "4RTools Vanilla $tag" --notes-file (Join-Path $repositoryRoot 'RELEASE-NOTES.md') --latest
        if ($LASTEXITCODE -ne 0) { throw 'Private release publication failed.' }
    }

    # Verify exact remote source and assets before accepting/re-marking an existing release.
    $tagRemote = Read-Git @('ls-remote', 'origin', "refs/tags/$tag", "refs/tags/$tag^{}")
    if (-not $tagRemote -or ((($tagRemote -split "`n")[-1] -split '\s+')[0] -cne $sourceCommit)) { throw 'Published tag/source verification failed.' }
    $downloadRoot = Join-Path $repositoryRoot ('dist/published/' + $tag + '-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
    & gh release download $tag --repo $repository --pattern "$artifactBase-portable.zip*" --dir $downloadRoot
    if ($LASTEXITCODE -ne 0) { throw 'Authenticated release asset download failed.' }
    foreach ($source in @($zip, $checksum)) {
        $downloaded = Join-Path $downloadRoot (Split-Path -Leaf $source)
        if (-not (Test-Path -LiteralPath $downloaded) -or
            (Get-FileHash -LiteralPath $downloaded -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash) {
            throw 'Published release bytes differ from the validated local artifacts.'
        }
    }
    if ($exists) {
        & gh release edit $tag --repo $repository --latest
        if ($LASTEXITCODE -ne 0) { throw 'Could not mark the verified release Latest.' }
    }
    $latestText = & gh api "repos/$repository/releases/latest"
    if ($LASTEXITCODE -ne 0) { throw 'Authenticated Latest verification failed.' }
    $latest = $latestText | ConvertFrom-Json
    if ($latest.tag_name -cne $tag -or $latest.draft -or $latest.prerelease) { throw 'Expected stable non-draft release is not Latest.' }
    foreach ($asset in @((Split-Path -Leaf $zip), (Split-Path -Leaf $checksum))) {
        if (@($latest.assets | Where-Object { $_.name -ceq $asset }).Count -ne 1) { throw "Latest release is missing unique asset $asset." }
    }
    & (Join-Path $PSScriptRoot 'test-published-update.ps1') -ExpectedVersion $Version -Repository $repository -ExpectedCommit $sourceCommit -ExecutablePath (Join-Path $repositoryRoot "dist/$artifactBase/4RTools-Vanilla.exe")
    Assert-PublishSource $sourceCommit
    [ordered]@{ version = $Version; sourceCommit = $sourceCommit; releaseUrl = $latest.html_url;
        zipSha256 = $receipt.zipSha256; latest = $true; private = $true; verifiedUtc = [DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json | Out-File -LiteralPath (Join-Path $downloadRoot 'release-verification.json') -Encoding utf8
    Write-Host "Published and verified private Latest release: $($latest.html_url)"
}
finally { Pop-Location }
