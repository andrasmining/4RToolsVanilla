[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?$')]
    [string] $Version = '0.2.0',
    [string] $CacheRoot = (Join-Path $env:LOCALAPPDATA '4RTools-Engineering'),
    [string] $MSBuildPath,
    [switch] $Replace
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$distRoot = Join-Path $repositoryRoot 'dist'
$releaseName = "4RTools-Vanilla-v$Version"
$releasePath = Join-Path $distRoot $releaseName
$zipName = "$releaseName-portable.zip"
$zipPath = Join-Path $distRoot $zipName
$zipChecksumPath = "$zipPath.sha256"
$utf8 = New-Object Text.UTF8Encoding($false)

function Assert-RepositoryPath([string] $Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $prefix = $repositoryRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release path is outside the repository: $fullPath"
    }
    # Lexical containment alone is insufficient when an ancestor is a junction.
    $ancestor = $fullPath
    while ($ancestor -and -not $ancestor.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Release paths must not contain links or junctions: $ancestor"
            }
        }
        $ancestor = Split-Path -Parent $ancestor
    }
    return $fullPath
}

function Write-Utf8([string] $Path, [string] $Content) {
    [IO.File]::WriteAllText($Path, $Content, $utf8)
}

function Get-RelativeReleasePath([string] $Root, [string] $Path) {
    return $Path.Substring($Root.Length + 1).Replace('\', '/')
}

function Assert-X86Executable([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Release executable has no DOS header.' }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 26)) { throw 'Invalid PE offset.' }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x4550) { throw 'Release executable has no PE header.' }
        if ($reader.ReadUInt16() -ne 0x14C) { throw 'The portable release must target x86.' }
        # PE32 alone also describes AnyCPU. Check the CLR 32BITREQUIRED flag.
        $stream.Position = $peOffset + 6
        $sectionCount = $reader.ReadUInt16()
        $stream.Position = $peOffset + 20
        $optionalHeaderSize = $reader.ReadUInt16()
        $optionalHeader = $peOffset + 24
        $stream.Position = $optionalHeader
        if ($reader.ReadUInt16() -ne 0x10B) { throw 'The portable release must use a PE32 header.' }
        $stream.Position = $optionalHeader + 96 + 14 * 8
        $clrRva = $reader.ReadUInt32()
        $sectionTable = $optionalHeader + $optionalHeaderSize
        $clrOffset = $null
        for ($index = 0; $index -lt $sectionCount; $index++) {
            $stream.Position = $sectionTable + $index * 40 + 8
            $virtualSize = $reader.ReadUInt32()
            $virtualAddress = $reader.ReadUInt32()
            $rawSize = $reader.ReadUInt32()
            $rawOffset = $reader.ReadUInt32()
            if ($clrRva -ge $virtualAddress -and $clrRva -lt ([long]$virtualAddress + [Math]::Max($virtualSize, $rawSize))) {
                $clrOffset = [long]$rawOffset + $clrRva - $virtualAddress
                break
            }
        }
        if ($null -eq $clrOffset) { throw 'Could not locate the CLR header.' }
        $stream.Position = $clrOffset + 16
        $clrFlags = $reader.ReadUInt32()
        if (($clrFlags -band 2) -eq 0 -or ($clrFlags -band 0x20000) -ne 0) {
            throw 'Release executable is AnyCPU or Prefer32Bit; an explicit x86 build is required.'
        }
    }
    finally { $reader.Dispose(); $stream.Dispose() }
}

function New-PortableArchive([string] $Root, [string] $ArchivePath, [DateTime] $TimestampUtc) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($ArchivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $baseName = Split-Path -Leaf $Root
        foreach ($directory in @(Get-Item -LiteralPath $Root) + @(Get-ChildItem -LiteralPath $Root -Directory -Recurse | Sort-Object FullName)) {
            $relativePath = if ($directory.FullName -eq $Root) { '' } else { (Get-RelativeReleasePath $Root $directory.FullName) + '/' }
            $entry = $archive.CreateEntry("$baseName/$relativePath")
            $entry.LastWriteTime = [DateTimeOffset]::new($TimestampUtc)
        }
        foreach ($file in @(Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName)) {
            $relativePath = Get-RelativeReleasePath $Root $file.FullName
            $entry = $archive.CreateEntry("$baseName/$relativePath", [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new($TimestampUtc)
            $entryStream = $entry.Open()
            try {
                $fileStream = [IO.File]::OpenRead($file.FullName)
                try { $fileStream.CopyTo($entryStream) }
                finally { $fileStream.Dispose() }
            }
            finally { $entryStream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}

function Assert-PortableSmokeResult([object] $Report) {
    if ($null -eq $Report) { throw 'Portable smoke report is empty.' }
    foreach ($requiredField in @('Success', 'MainUi', 'PackagedOcr', 'AutomationEnabled', 'GameplayAttached', 'InputSent', 'OriginalFeatureForms',
        'VanillaPollingEnabled', 'FleetPollingEnabled', 'FleetPollCount', 'RecoveryRunning', 'WeightAlertsRunning', 'UpdateCheckRunning')) {
        if ($null -eq $Report.PSObject.Properties[$requiredField]) {
            throw "Portable smoke report is missing $requiredField."
        }
    }
    if ($Report.Success -isnot [bool] -or -not $Report.Success) { throw 'Portable smoke test reported failure.' }
    if ($Report.PackagedOcr -isnot [bool] -or -not $Report.PackagedOcr) { throw 'Portable OCR runtime/model validation failed.' }
    if ($Report.MainUi -cne 'Container') { throw 'Portable smoke test did not validate the original 4RTools main window.' }
    foreach ($inactiveField in @('AutomationEnabled', 'GameplayAttached', 'InputSent', 'VanillaPollingEnabled',
        'FleetPollingEnabled', 'RecoveryRunning', 'WeightAlertsRunning', 'UpdateCheckRunning')) {
        if ($Report.$inactiveField -isnot [bool] -or $Report.$inactiveField) {
            throw "Portable smoke test did not prove $inactiveField was false."
        }
    }
    if (($Report.FleetPollCount -isnot [int] -and $Report.FleetPollCount -isnot [long]) -or $Report.FleetPollCount -ne 0) {
        throw 'Portable smoke test did not prove that live fleet discovery remained inactive.'
    }
    if (($Report.OriginalFeatureForms -isnot [int] -and $Report.OriginalFeatureForms -isnot [long]) -or $Report.OriginalFeatureForms -lt 10) {
        throw 'Portable smoke test did not validate the original 4RTools feature forms.'
    }
}

function Test-PortableArchive([string] $ArchivePath, [string] $ExpectedZipHash) {
    $smokeRoot = Assert-RepositoryPath (Join-Path $distRoot ('.smoke-' + [Guid]::NewGuid().ToString('N')))
    $smokeLogRoot = Join-Path ([IO.Path]::GetFullPath($CacheRoot)) ('portable-tests/' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $smokeLogRoot -Force | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $smokeRoot)
    $smokeReleasePath = Join-Path $smokeRoot $releaseName
    foreach ($legacyDataFolder in @('Profile', 'Profiles', 'VanillaReconnect', 'Logs')) {
        if (Test-Path -LiteralPath (Join-Path $smokeReleasePath $legacyDataFolder)) { throw "Portable release must not contain user-data folder: $legacyDataFolder" }
    }
    $smokeOutput = Join-Path $smokeLogRoot 'portable-smoke.json'
    $smokeExecutable = Join-Path $smokeReleasePath $applicationName
    # All local executable launches stay inside the repo and use the repo cwd.
    $smokeArguments = '--portable-smoke-test --output "' + $smokeOutput + '"'
    $smokeProcess = Start-Process -FilePath $smokeExecutable -ArgumentList $smokeArguments -WorkingDirectory $repositoryRoot -WindowStyle Hidden -PassThru
    if (-not $smokeProcess.WaitForExit(30000)) {
        $smokeProcess.Kill()
        throw "Portable smoke test timed out. Evidence: $smokeLogRoot; extracted package: $smokeRoot"
    }
    $smokeProcess.Refresh()
    if ($smokeProcess.ExitCode -ne 0) { throw "Portable smoke test failed (exit $($smokeProcess.ExitCode)). Evidence: $smokeLogRoot" }
    if (-not (Test-Path -LiteralPath $smokeOutput -PathType Leaf)) { throw "Portable smoke test did not produce its report: $smokeOutput" }
    $smokeResult = Get-Content -LiteralPath $smokeOutput -Raw -Encoding UTF8 | ConvertFrom-Json
    try { Assert-PortableSmokeResult $smokeResult }
    catch { throw "$($_.Exception.Message) See $smokeOutput" }
    if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $ExpectedZipHash) {
        throw 'Portable ZIP changed during validation.'
    }
    foreach ($checksumLine in [IO.File]::ReadAllLines((Join-Path $smokeReleasePath 'SHA256SUMS.txt'))) {
        if ($checksumLine -notmatch '^([0-9a-f]{64})  (.+)$') { throw 'Invalid packaged checksum manifest.' }
        $expectedHash = $Matches[1]
        $relativeFile = $Matches[2]
        $payloadPath = Assert-RepositoryPath (Join-Path $smokeReleasePath $relativeFile)
        if ((Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash -ne $expectedHash) {
            throw "Portable payload checksum mismatch: $relativeFile"
        }
    }
    Write-Host "Portable launch test passed. Report: $smokeOutput"
    # This distinct extraction was created exclusively by this test; retain it
    # on failure, and remove it only after a successful launch and hash check.
    Assert-RepositoryPath $smokeRoot | Out-Null
    foreach ($smokeEntry in @(Get-ChildItem -LiteralPath $smokeRoot -Force -Recurse)) {
        Assert-RepositoryPath $smokeEntry.FullName | Out-Null
    }
    Remove-Item -LiteralPath $smokeRoot -Recurse
}

foreach ($target in @($releasePath, $zipPath, $zipChecksumPath)) {
    Assert-RepositoryPath $target | Out-Null
    if ((Test-Path -LiteralPath $target) -and -not $Replace) {
        throw "Release output already exists: $target. Pass -Replace to preserve it in dist/.previous before publishing the new build."
    }
}

$requiredSources = @('LICENSE', 'RELEASE-NOTES.md', 'packaging/README.txt', 'packaging/THIRD-PARTY-NOTICES.txt')
foreach ($source in $requiredSources) {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $source) -PathType Leaf)) {
        throw "Required release source is missing: $source"
    }
}

# Restore + Rebuild cleans compiler outputs; both build errors and test failures throw.
# The app and offline tests use x86 together through the VanillaRelease property.
$buildParameters = @{ Configuration = @('Release'); VanillaRelease = $true; CacheRoot = $CacheRoot }
if ($MSBuildPath) { $buildParameters.MSBuildPath = $MSBuildPath }
& (Join-Path $PSScriptRoot 'build.ps1') @buildParameters

$applicationOutput = Join-Path $repositoryRoot 'bin/Release'
$applicationName = '4RTools-Vanilla.exe'
$applicationPath = Join-Path $applicationOutput $applicationName
foreach ($name in @($applicationName, "$applicationName.config")) {
    if (-not (Test-Path -LiteralPath (Join-Path $applicationOutput $name) -PathType Leaf)) {
        throw "The validated release build did not produce $name."
    }
}
Assert-X86Executable $applicationPath
$expectedVersion = [Version]($Version.Split('-')[0])
$executableVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($applicationPath)
if ($executableVersion.FileMajorPart -ne $expectedVersion.Major -or
    $executableVersion.FileMinorPart -ne $expectedVersion.Minor -or
    $executableVersion.FileBuildPart -ne $expectedVersion.Build) {
    throw "Executable file version $($executableVersion.FileVersion) does not match release version $Version."
}
$releaseAssembly = [Reflection.Assembly]::ReflectionOnlyLoad([IO.File]::ReadAllBytes($applicationPath))
$embeddedResources = @($releaseAssembly.GetManifestResourceNames())
if ($embeddedResources -notcontains 'costura.newtonsoft.json.dll.compressed') {
    throw 'Required Newtonsoft.Json dependency was not embedded by Costura.'
}
if ($embeddedResources -contains 'costura.aspose.zip.dll.compressed') {
    throw 'The portable fork must not retain the unused Aspose updater dependency.'
}

Push-Location $repositoryRoot
try {
    $sourceCommit = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the source commit.' }
    $sourceTimestamp = (& git show -s --format=%cI HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the source commit time.' }
    $sourceStatus = @(& git status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the source working-tree state.' }
    $trackedBuildProfiles = @(& git -c core.quotepath=false ls-files -- 'VanillaBuilds/*.json')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the packaged client build profiles.' }
}
finally { Pop-Location }
$treeState = if ($sourceStatus.Count -eq 0) { 'clean' } else { 'modified' }
$sourceDate = [DateTimeOffset]::Parse($sourceTimestamp).UtcDateTime
if ($sourceDate.Year -lt 1980) { $sourceDate = [DateTime]::new(1980, 1, 1, 0, 0, 0, [DateTimeKind]::Utc) }

New-Item -ItemType Directory -Path (Assert-RepositoryPath $distRoot) -Force | Out-Null
$stagingRoot = Assert-RepositoryPath (Join-Path $distRoot ('.staging-' + [Guid]::NewGuid().ToString('N')))
$stagingPath = Join-Path $stagingRoot $releaseName
New-Item -ItemType Directory -Path $stagingPath | Out-Null

try {
    foreach ($name in @($applicationName, "$applicationName.config", 'Tesseract.dll')) {
        Copy-Item -LiteralPath (Join-Path $applicationOutput $name) -Destination $stagingPath
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $stagingPath
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging/README.txt') -Destination $stagingPath
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging/THIRD-PARTY-NOTICES.txt') -Destination $stagingPath
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging/licenses') -Destination $stagingPath -Recurse
    foreach ($folder in @('x86', 'tessdata', 'tessdata-best')) {
        Copy-Item -LiteralPath (Join-Path $applicationOutput $folder) -Destination $stagingPath -Recurse
    }
    # Ship the official redistributable CRT app-local; users need no developer tools
    # or system-wide VC runtime install to load the x86 OCR libraries.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vsInstall = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
    if (-not $vsInstall) { throw 'Visual C++ REDIST directory was not found for portable OCR.' }
    $crt = Get-ChildItem -LiteralPath (Join-Path $vsInstall 'VC/Redist/MSVC') -Directory |
        Where-Object { $_.Name -match '^14\.' } | Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Get-ChildItem -Path (Join-Path $_.FullName 'x86/Microsoft.VC*.CRT') -Directory } | Select-Object -First 1
    if (-not $crt) { throw 'The x86 redistributable CRT payload is unavailable.' }
    foreach ($required in @('msvcp140.dll', 'vcruntime140.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $crt.FullName $required))) { throw "Missing redistributable $required." }
    }
    Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' -File | Copy-Item -Destination $stagingPath

    # Profiles are defaults owned by the repository, never the user's live data.
    if ($trackedBuildProfiles.Count -gt 0) {
        $buildProfilesTarget = Join-Path $stagingPath 'VanillaBuilds'
        New-Item -ItemType Directory -Path $buildProfilesTarget | Out-Null
        foreach ($relativeProfile in $trackedBuildProfiles) {
            $profilePath = Assert-RepositoryPath (Join-Path $repositoryRoot $relativeProfile)
            Get-Content -LiteralPath $profilePath -Raw -Encoding UTF8 | ConvertFrom-Json | Out-Null
            Copy-Item -LiteralPath $profilePath -Destination $buildProfilesTarget
        }
    }

    $notesPath = Join-Path $repositoryRoot 'RELEASE-NOTES.md'
    $notes = [IO.File]::ReadAllText($notesPath)
    $checksumBlockPattern = '(?s)\r?\n?<!-- BEGIN GENERATED RELEASE CHECKSUMS -->.*?<!-- END GENERATED RELEASE CHECKSUMS -->\r?\n?'
    $packagedNotes = [Regex]::Replace($notes, $checksumBlockPattern, '').TrimEnd() + "`n"
    Write-Utf8 (Join-Path $stagingPath 'RELEASE-NOTES.md') $packagedNotes
    $versionInfo = @(
        "Product: 4RTools Vanilla",
        "Fork version: $Version",
        "Executable file version: $($executableVersion.FileVersion)",
        'Architecture: x86 (32BITREQUIRED)',
        'Runtime: Microsoft .NET Framework 4.7.2 or later (4.x)',
        "Source commit: $sourceCommit",
        "Source commit UTC: $($sourceDate.ToString('yyyy-MM-ddTHH:mm:ssZ'))",
        "Source working tree before packaging: $treeState",
        'Build validation: Release rebuild and offline tests passed',
        'Live validation and operating system coverage: see RELEASE-NOTES.md',
        'Upstream: https://github.com/4RTools/4RTools',
        'Copyright (c) 2022 4RTools',
        'Independent fork; see LICENSE and THIRD-PARTY-NOTICES.txt'
    ) -join "`n"
    Write-Utf8 (Join-Path $stagingPath 'VERSION.txt') ($versionInfo + "`n")

    $payloadFiles = @(Get-ChildItem -LiteralPath $stagingPath -File -Recurse | Sort-Object FullName)
    $payloadChecksums = foreach ($payloadFile in $payloadFiles) {
        $hash = (Get-FileHash -LiteralPath $payloadFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $(Get-RelativeReleasePath $stagingPath $payloadFile.FullName)"
    }
    Write-Utf8 (Join-Path $stagingPath 'SHA256SUMS.txt') (($payloadChecksums -join "`n") + "`n")

    # Stable entry order and UTC timestamps keep ZIP metadata reproducible.
    $stagedZip = Join-Path $stagingRoot $zipName
    New-PortableArchive $stagingPath $stagedZip $sourceDate
    $zipHash = (Get-FileHash -LiteralPath $stagedZip -Algorithm SHA256).Hash.ToLowerInvariant()
    $exeHash = (Get-FileHash -LiteralPath (Join-Path $stagingPath $applicationName) -Algorithm SHA256).Hash.ToLowerInvariant()
    $stagedZipChecksum = "$stagedZip.sha256"
    Write-Utf8 $stagedZipChecksum "$zipHash  $zipName`n"
    Test-PortableArchive $stagedZip $zipHash

    # Preserve previous release artifacts when -Replace is explicitly requested.
    $existingTargets = @(@($releasePath, $zipPath, $zipChecksumPath) | Where-Object { Test-Path -LiteralPath $_ })
    if ($existingTargets.Count -gt 0) {
        if (-not $Replace) { throw 'Release outputs appeared during the build. Nothing was replaced.' }
        $backupPath = Assert-RepositoryPath (Join-Path $distRoot ('.previous/' + $releaseName + '-' + [Guid]::NewGuid().ToString('N')))
        New-Item -ItemType Directory -Path $backupPath | Out-Null
        foreach ($existingTarget in $existingTargets) {
            Assert-RepositoryPath $existingTarget | Out-Null
            Move-Item -LiteralPath $existingTarget -Destination $backupPath
        }
        Write-Host "Previous release preserved: $backupPath"
    }
    foreach ($move in @(@($stagingPath, $releasePath), @($stagedZip, $zipPath), @($stagedZipChecksum, $zipChecksumPath))) {
        Assert-RepositoryPath $move[0] | Out-Null
        Assert-RepositoryPath $move[1] | Out-Null
        Move-Item -LiteralPath $move[0] -Destination $move[1]
    }
    # The staging directory is now empty; no recursive deletion is necessary.
    Remove-Item -LiteralPath (Assert-RepositoryPath $stagingRoot)

    $checksumBlock = @(
        '<!-- BEGIN GENERATED RELEASE CHECKSUMS -->',
        '',
        "Release version: $Version. SHA256:",
        '',
        ('- `' + $zipName + '`: `' + $zipHash + '`'),
        ('- `' + $applicationName + '`: `' + $exeHash + '`'),
        '',
        'These generated hashes are excluded from the packaged notes to avoid a circular ZIP checksum.',
        '<!-- END GENERATED RELEASE CHECKSUMS -->'
    ) -join "`n"
    Write-Utf8 $notesPath ($packagedNotes.TrimEnd() + "`n`n" + $checksumBlock + "`n")
    Write-Host "Portable folder: $releasePath"
    Write-Host "Portable ZIP: $zipPath"
    Write-Host "ZIP SHA256: $zipHash"
}
catch {
    Write-Warning "Packaging failed. Any staging files are preserved at $stagingRoot for inspection."
    throw
}
