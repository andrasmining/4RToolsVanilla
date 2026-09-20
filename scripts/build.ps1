[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string[]] $Configuration = @('Release', 'Debug'),
    [string] $CacheRoot = (Join-Path $env:LOCALAPPDATA '4RTools-Engineering'),
    [string] $MSBuildPath,
    [switch] $VanillaRelease
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$CacheRoot = [IO.Path]::GetFullPath($CacheRoot)
$logDirectory = Join-Path $CacheRoot ('builds/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

if (-not $MSBuildPath) {
    $vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswherePath) {
        $found = @(& $vswherePath -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Visual Studio discovery failed.' }
        if ($found.Count -gt 0) { $MSBuildPath = $found[0] }
    }
    if (-not $MSBuildPath) {
        $command = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
        if ($command) { $MSBuildPath = $command.Source }
    }
}
if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath -PathType Leaf)) {
    throw 'MSBuild was not found. Install Visual Studio Build Tools with .NET desktop build tools, or pass -MSBuildPath.'
}

# Keep the existing .NET Framework 4.7.2 target. A per-user reference pack
# supplies build-time assemblies when the machine targeting pack is absent.
$installedReferenceRoot = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies/Microsoft/Framework'
$referenceRoot = $installedReferenceRoot
$referenceMarker = '.NETFramework/v4.7.2/mscorlib.dll'
if (-not (Test-Path -LiteralPath (Join-Path $referenceRoot $referenceMarker))) {
    $packageRoot = Join-Path $CacheRoot 'net472'
    $referenceRoot = Join-Path $packageRoot 'build'
    if (-not (Test-Path -LiteralPath (Join-Path $referenceRoot $referenceMarker))) {
        $packageUrl = 'https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net472/1.0.3/microsoft.netframework.referenceassemblies.net472.1.0.3.nupkg'
        $archivePath = Join-Path $CacheRoot 'microsoft.netframework.referenceassemblies.net472.1.0.3.zip'
        Write-Host 'Downloading Microsoft .NET Framework 4.7.2 reference assemblies 1.0.3 from NuGet.'
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $packageUrl -OutFile $archivePath -UseBasicParsing
        Expand-Archive -LiteralPath $archivePath -DestinationPath $packageRoot -Force
        if (-not (Test-Path -LiteralPath (Join-Path $referenceRoot $referenceMarker))) {
            throw "Reference assemblies were not found after extraction to $packageRoot."
        }
    }
}

Write-Host "MSBuild: $MSBuildPath"
Write-Host "Reference assemblies: $referenceRoot"
Write-Host "Logs: $logDirectory"

Push-Location $repositoryRoot
try {
    foreach ($buildConfiguration in $Configuration) {
        $buildLog = Join-Path $logDirectory ("build-$buildConfiguration.log")
        $buildArguments = @(
            (Join-Path $repositoryRoot '4RTools.sln'),
            '/nologo',
            '/restore',
            '/t:Rebuild',
            '/p:RestorePackagesConfig=true',
            "/p:Configuration=$buildConfiguration",
            '/p:Platform=Any CPU',
            "/p:TargetFrameworkRootPath=$referenceRoot",
            '/consoleloggerparameters:Summary;Verbosity=minimal',
            "/fileloggerparameters:LogFile=$buildLog;Verbosity=normal;Encoding=UTF-8"
        )
        if ($VanillaRelease) { $buildArguments += '/p:VanillaRelease=true' }
        & $MSBuildPath @buildArguments
        if ($LASTEXITCODE -ne 0) { throw "$buildConfiguration build failed. See $buildLog." }

        $applicationOutput = Join-Path $repositoryRoot "bin/$buildConfiguration"
        $testOutput = Join-Path $repositoryRoot "Tests/bin/$buildConfiguration"
        # Native OCR and language data are runtime assets, not Costura resources.
        foreach ($folder in @('x86', 'x64', 'tessdata', 'tessdata-best')) {
            Copy-Item -LiteralPath (Join-Path $applicationOutput $folder) -Destination $testOutput -Recurse -Force
        }
        Copy-Item -LiteralPath (Join-Path $applicationOutput 'Tesseract.dll') -Destination $testOutput -Force
        Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $applicationOutput -Force
        $testExecutable = Join-Path $testOutput 'Vanilla.Diagnostics.Tests.exe'
        if (-not (Test-Path -LiteralPath $testExecutable -PathType Leaf)) {
            throw "The solution did not produce $testExecutable."
        }
        Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $testOutput -Force
        $testLog = Join-Path $logDirectory ("tests-$buildConfiguration.log")
        $testErrorPreference = $ErrorActionPreference
        try {
            # Windows PowerShell wraps native stderr in nonterminating error records.
            # Negative tests may deliberately log an error; the runner's exit code is authoritative.
            $ErrorActionPreference = 'Continue'
            & $testExecutable 2>&1 | Tee-Object -FilePath $testLog
            $testExitCode = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $testErrorPreference }
        if ($testExitCode -ne 0) { throw "$buildConfiguration tests failed. See $testLog." }
    }
}
finally {
    Pop-Location
}

Write-Host "Builds and diagnostics tests passed. Logs: $logDirectory"
