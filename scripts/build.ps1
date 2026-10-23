[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$NoTests,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bundledDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $bundledDotnet) { $bundledDotnet } else { 'dotnet' }
$appProject = Join-Path $projectRoot 'src\Stransfer.App\Stransfer.App.csproj'
$testProject = Join-Path $projectRoot 'tests\Stransfer.Core.Tests\Stransfer.Core.Tests.csproj'
$uiTestProject = Join-Path $projectRoot 'tests\Stransfer.App.SmokeTests\Stransfer.App.SmokeTests.csproj'
$publishDirectory = Join-Path $projectRoot $(if ($SelfContained) { 'dist\Stransfer' } else { 'dist\Stransfer.Slim' })

if (-not (Get-Command $dotnetCommand -ErrorAction SilentlyContinue)) {
    throw 'The .NET 10 SDK was not found. Install the Windows .NET 10 SDK, then run this script again.'
}

Push-Location $projectRoot
try {
    if (-not $NoTests -and (Test-Path -LiteralPath $testProject)) {
        & $dotnetCommand run --project $testProject --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit code $LASTEXITCODE)." }
    }
    if (-not $NoTests -and (Test-Path -LiteralPath $uiTestProject)) {
        & $dotnetCommand run --project $uiTestProject --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "UI smoke test failed (exit code $LASTEXITCODE)." }
    }

    $includeRuntime = $SelfContained.IsPresent.ToString().ToLowerInvariant()
    $resolvedPublish = [IO.Path]::GetFullPath($publishDirectory)
    $expectedPublish = [IO.Path]::GetFullPath((Join-Path $projectRoot $(if ($SelfContained) { 'dist\Stransfer' } else { 'dist\Stransfer.Slim' })))
    if ($resolvedPublish -ne $expectedPublish -or -not $resolvedPublish.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar)) { throw 'Unexpected publish directory.' }
    if (Test-Path -LiteralPath $resolvedPublish) { Remove-Item -LiteralPath $resolvedPublish -Recurse -Force }
    & $dotnetCommand publish $appProject --configuration $Configuration --runtime $Runtime --self-contained $includeRuntime --output $publishDirectory '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' '-p:DebugSymbols=false'
    if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit code $LASTEXITCODE)." }
    $executable = Join-Path $publishDirectory 'Stransfer.exe'
    if (-not (Test-Path -LiteralPath $executable)) { throw "Expected executable was not produced: $executable" }
    $runtimeNote = if ($SelfContained) { 'This full build includes its runtime. No external copy tool is required.' } else { 'This slim build requires the .NET 10 Windows Desktop Runtime before it can start. Download it from https://dotnet.microsoft.com/download/dotnet/10.0 (Desktop Runtime, matching your architecture).' }
    @"
Stransfer

Launch Stransfer.exe.
$runtimeNote

Shortcuts controls Ctrl+Shift+V for Stransfer. Ctrl+V and Shift+Insert use Windows paste. Settings controls Explorer menus, background behavior, startup, verification, and workers.
Explorer integration and sign-in startup are off until you enable them. Native Explorer Paste menus and drag/drop are not replaced.
Optional tools install only when you choose Install in Tools. Tools are not required for transfers.

Save and exit from the tray menu to stop the app. Interrupted jobs ask before resuming when the app runs again and the original drives are available.
Transfers check destination capacity before starting, but other programs can still consume space later. Speed depends on the drives and workload.
See the source README and docs/VALIDATION.md for supported features, limits and test status.
"@ | Set-Content -LiteralPath (Join-Path $publishDirectory 'START-HERE.txt') -Encoding utf8
    Write-Host "Built: $executable" -ForegroundColor Green
    if ($SelfContained) {
        $installerProject = Join-Path $projectRoot 'src\Stransfer.Installer\Stransfer.Installer.csproj'
        if (Test-Path -LiteralPath $installerProject) {
            $payloadZip = Join-Path $projectRoot 'dist\payload.zip'
            Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $payloadZip -Force
            $installerOut = Join-Path $projectRoot 'dist\InstallerTemp'
            $resolvedInstallerOut = [IO.Path]::GetFullPath($installerOut)
            if ($resolvedInstallerOut -ne [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist\InstallerTemp'))) { throw 'Unexpected installer directory.' }
            if (Test-Path -LiteralPath $resolvedInstallerOut) { Remove-Item -LiteralPath $resolvedInstallerOut -Recurse -Force }
            & $dotnetCommand publish $installerProject --configuration $Configuration --runtime $Runtime --self-contained true --output $installerOut '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' '-p:DebugSymbols=false'
            if ($LASTEXITCODE -ne 0) { throw "Installer publish failed (exit code $LASTEXITCODE)." }
            if (Test-Path -LiteralPath (Join-Path $installerOut 'StransferSetup.exe')) {
                Move-Item -Path (Join-Path $installerOut 'StransferSetup.exe') -Destination (Join-Path $projectRoot 'dist\StransferSetup.exe') -Force
                $resolvedInstallerOut = [System.IO.Path]::GetFullPath($installerOut)
                $expectedInstallerOut = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'dist\InstallerTemp'))
                if ($resolvedInstallerOut -ne $expectedInstallerOut) { throw 'Unexpected installer cleanup directory.' }
                Remove-Item -LiteralPath $resolvedInstallerOut -Recurse -Force
                Write-Host "Built installer: $(Join-Path $projectRoot 'dist\StransferSetup.exe')" -ForegroundColor Green
            }
            else { throw 'Expected installer executable was not produced.' }
        }
        Write-Host 'Full build includes the Desktop Runtime. No external tools are required.'
    }
    else { Write-Host 'Slim build requires the .NET 10 Windows Desktop Runtime. No runtime is downloaded automatically.' }
}
finally {
    Pop-Location
}
