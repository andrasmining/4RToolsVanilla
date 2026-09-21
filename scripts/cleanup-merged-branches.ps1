[CmdletBinding()]
param([switch] $Apply)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'


$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    $branchName = (& git branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0 -or $branchName -cne 'main') { throw 'Branch cleanup requires the private repository main checkout.' }
    $sourceCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source commit.' }
    $origin = & git remote get-url origin
    if ($LASTEXITCODE -ne 0 -or $origin.TrimEnd('/') -notmatch '^https://github\.com/andrasmining/4RToolsVanilla(?:\.git)?$') {
        throw 'Unexpected origin; no branch deletion attempted.'
    }
    & gh auth setup-git --hostname github.com
    if ($LASTEXITCODE -ne 0) { throw 'GitHub authentication setup failed.' }
    & git fetch --prune origin
    if ($LASTEXITCODE -ne 0) { throw 'Remote branch refresh failed.' }
    $main = (& git rev-parse refs/remotes/origin/main).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Remote main could not be resolved.' }
    & git merge-base --is-ancestor $sourceCommit $main
    if ($LASTEXITCODE -ne 0) { throw 'The verified release commit is no longer on remote main.' }

    $removed = @()
    $eligible = @()
    $preserved = @()
    $refs = @(& git for-each-ref --format='%(refname:strip=3)|%(objectname)' refs/remotes/origin)
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate remote branches.' }
    foreach ($line in $refs) {
        $parts = $line.Split('|')
        if ($parts.Count -ne 2) { throw 'Unexpected branch record.' }
        $branch = $parts[0]
        $sha = $parts[1]
        if ($branch -eq 'HEAD' -or $branch -eq 'main') { continue }
        & git merge-base --is-ancestor $sha $main
        $ancestry = $LASTEXITCODE
        if ($ancestry -eq 1) { $preserved += $branch; continue }
        if ($ancestry -ne 0) { throw "Could not verify branch ancestry: $branch" }
        $eligible += $branch
        if ($Apply) {
            # This lease guards an authorized deletion; it never rewrites history.
            # A concurrent branch advance rejects the deletion instead of losing work.
            $lease = "--force-with-lease=refs/heads/${branch}:$sha"
            & git push $lease origin ":refs/heads/$branch"
            if ($LASTEXITCODE -ne 0) { throw "Branch changed or deletion was denied; preserved $branch." }
            $removed += $branch
            Write-Host "Deleted merged branch: $branch ($sha)"
        }
    }
    if ($Apply) {
        & git fetch --prune origin
        if ($LASTEXITCODE -ne 0) { throw 'Post-cleanup branch refresh failed.' }
    }
    $remaining = @(& git for-each-ref --format='%(refname:strip=3)' refs/remotes/origin | Where-Object { $_ -ne 'HEAD' })
    if ($LASTEXITCODE -ne 0) { throw 'Post-cleanup branch verification failed.' }
    New-Item -ItemType Directory -Path dist/published -Force | Out-Null
    $reportName = if ($Apply) { 'branch-cleanup.json' } else { 'branch-cleanup-plan.json' }
    [ordered]@{
        sourceCommit = $sourceCommit
        remoteMain = $main
        applied = [bool]$Apply
        mergedBranches = @($eligible)
        removedBranches = @($removed)
        preservedUnmergedBranches = @($preserved)
        remainingBranches = @($remaining)
    } | ConvertTo-Json | Out-File (Join-Path dist/published $reportName) -Encoding utf8
    Write-Host "Branch cleanup complete. Remaining: $($remaining -join ', ')"
    $global:LASTEXITCODE = 0
}
finally { Pop-Location }
