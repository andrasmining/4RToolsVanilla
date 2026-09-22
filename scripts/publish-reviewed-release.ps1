[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string] $Version,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = 'andrasmining/4RToolsVanilla'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Set-Location $root
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -cne $repo -or $env:GITHUB_REF -cne 'refs/heads/main' -or $env:GITHUB_EVENT_NAME -cne 'push') { throw 'CI publication requires the canonical main push.' }
if ((git rev-parse HEAD).Trim() -cne $ExpectedCommit -or (git status --porcelain)) { throw 'Publisher checkout is not the clean validated source.' }
$main = gh api "repos/$repo/git/ref/heads/main" --jq '.object.sha'
if ($LASTEXITCODE -ne 0 -or $main.Trim() -cne $ExpectedCommit) { throw 'Main advanced before publication; revalidate the current main.' }
$tag = "v$Version"
$tagRemote = @(git ls-remote origin "refs/tags/$tag" "refs/tags/$tag^{}")
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect existing version tag.' }
if ($tagRemote.Count -gt 0) {
    $resolvedTag = (($tagRemote[-1] -split '\\s+')[0]).Trim()
    if ($resolvedTag -cne $ExpectedCommit) {
        Write-Host "Version $tag already belongs to an earlier commit; no release assets or tags changed."
        return
    }
}
$artifactInput = Join-Path $root 'dist/published-input'
New-Item -ItemType Directory -Path $artifactInput -Force | Out-Null
gh run download $env:GITHUB_RUN_ID --repo $repo --name "portable-$ExpectedCommit" --dir $artifactInput
if ($LASTEXITCODE -ne 0) { throw 'Validated portable artifact could not be downloaded.' }
$zipName = "4RTools-Vanilla-$tag-portable.zip"
$zip = Join-Path $artifactInput $zipName
$checksum = "$zip.sha256"
$expectedHash = ((Get-Content -LiteralPath $checksum -Raw).Trim() -split '\s+')[0]
if ($expectedHash -notmatch '^[0-9a-fA-F]{64}$' -or (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ine $expectedHash) { throw 'Validated artifact checksum mismatch.' }
$reference = Join-Path $root 'dist/published-reference'
Expand-Archive -LiteralPath $zip -DestinationPath $reference -Force
$executable = Join-Path $reference "4RTools-Vanilla-$tag/4RTools-Vanilla.exe"
$notes = Join-Path $root 'RELEASE-NOTES.md'
if (-not (Test-Path -LiteralPath $notes)) { throw 'Reviewed release notes are missing.' }
$releaseJson = gh release list --repo $repo --limit 1000 --json tagName,isDraft
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect existing releases.' }
$existing = @($releaseJson | ConvertFrom-Json | Where-Object { $_.tagName -ceq $tag })
if ($existing.Count -eq 0) {
    gh release create $tag $zip $checksum --repo $repo --target $ExpectedCommit --title "4RTools Vanilla $tag" --notes-file $notes --draft
    if ($LASTEXITCODE -ne 0) { throw 'Draft release/asset upload failed.' }
}
# Re-download the actual GitHub assets before exposing the release as stable Latest.
$verify = Join-Path $root 'dist/published-verify'
New-Item -ItemType Directory -Path $verify -Force | Out-Null
gh release download $tag --repo $repo --pattern $zipName --pattern "$zipName.sha256" --dir $verify --clobber
if ($LASTEXITCODE -ne 0) { throw 'Release assets were not retrievable.' }
foreach ($name in @($zipName, "$zipName.sha256")) {
    if ((Get-FileHash -LiteralPath (Join-Path $verify $name) -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath (Join-Path $artifactInput $name) -Algorithm SHA256).Hash) { throw 'Uploaded release asset differs from its validated artifact.' }
}
gh release edit $tag --repo $repo --draft=false --latest
if ($LASTEXITCODE -ne 0) { throw 'Stable release publication failed.' }
& (Join-Path $PSScriptRoot 'test-published-update.ps1') -ExpectedVersion $Version -ExpectedCommit $ExpectedCommit -ExecutablePath $executable -ReportPath (Join-Path $root 'dist/published/updater-public.json')
# Exercise the previously published private-era updater against the now-public repo.
$legacy = Join-Path $root 'dist/legacy-reference'
New-Item -ItemType Directory -Path $legacy -Force | Out-Null
gh release download v0.6.69 --repo $repo --pattern '4RTools-Vanilla-v0.6.69-portable.zip' --dir $legacy --clobber
if ($LASTEXITCODE -ne 0) { throw 'Legacy update client could not be retrieved.' }
Expand-Archive -LiteralPath (Join-Path $legacy '4RTools-Vanilla-v0.6.69-portable.zip') -DestinationPath $legacy -Force
$legacyExe = Join-Path $legacy '4RTools-Vanilla-v0.6.69/4RTools-Vanilla.exe'
& (Join-Path $PSScriptRoot 'test-published-update.ps1') -ExpectedVersion $Version -ExpectedCommit $ExpectedCommit -ExecutablePath $legacyExe -AllowOlderClient -UseApiToken -ReportPath (Join-Path $root 'dist/published/updater-legacy-v069.json')
"Published and verified [$tag](https://github.com/$repo/releases/tag/$tag) from clean main $ExpectedCommit. Public anonymous updater and v0.6.69 authenticated upgrade both passed." | Out-File -LiteralPath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
