[CmdletBinding()]
param([string]$PreviousExecutable, [string]$CandidateExecutable)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $CandidateExecutable) { $CandidateExecutable = Join-Path $projectRoot 'dist\Stransfer\Stransfer.exe' }
if (-not $PreviousExecutable) { $PreviousExecutable = $CandidateExecutable }
$testId = [Guid]::NewGuid().ToString('N')
$testBase = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'Stransfer-update-tests'))
$testRoot = [IO.Path]::GetFullPath((Join-Path $testBase $testId))
$stateDir = Join-Path $testRoot 'state'
$appDir = Join-Path $testRoot 'app'
$target = Join-Path $appDir 'Stransfer.exe'
$updateId = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $stateDir "updates\$updateId"
$previousState = $env:STRANSFER_STATE_DIR
$old = $null; $helper = $null
function Wait-Until([scriptblock]$Condition, [string]$Message, [int]$Seconds = 45) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $Condition)) {
        if ($clock.Elapsed.TotalSeconds -gt $Seconds) { throw $Message }
        Start-Sleep -Milliseconds 100
    }
}
try {
    New-Item -ItemType Directory -Path $appDir,$stateDir,$stage -Force | Out-Null
    $env:STRANSFER_STATE_DIR = $stateDir
    @{ KeepInBackground=$true; StartWithWindows=$false; LastDestination='update-test-sentinel'; CheckForUpdates=$false } | ConvertTo-Json | Set-Content (Join-Path $stateDir 'settings.json')
    Copy-Item -LiteralPath $PreviousExecutable -Destination $target
    [IO.File]::WriteAllText((Join-Path $appDir 'keep-me.txt'), 'Keep unrelated files')
    $old = Start-Process -FilePath $target -ArgumentList '--background' -PassThru -WindowStyle Hidden
    Wait-Until { (Get-ChildItem -LiteralPath $stateDir -Directory | Where-Object Name -eq 'jobs') -and -not $old.HasExited } 'The original app did not start.'
    Copy-Item -LiteralPath $CandidateExecutable -Destination (Join-Path $stage 'Stransfer.exe')
    $hash = (Get-FileHash -LiteralPath $CandidateExecutable -Algorithm SHA256).Hash
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($CandidateExecutable)
    $version = "$($versionInfo.FileMajorPart).$($versionInfo.FileMinorPart).$($versionInfo.FileBuildPart)"
    @{
        Id=([Guid]::ParseExact($updateId,'N').ToString('D')); Target=$target; ParentProcessId=$old.Id; ParentStartTicks=$old.StartTime.ToUniversalTime().Ticks
        Sha256=$hash; Version=$version; Background=$true; CreatedAt=[DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'plan.json') -Encoding utf8NoBOM
    $helper = Start-Process -FilePath (Join-Path $stage 'Stransfer.exe') -ArgumentList @('--apply-update',$updateId) -PassThru -WindowStyle Hidden
    Wait-Until {
        if (Test-Path -LiteralPath (Join-Path $stage 'error.txt')) { throw (Get-Content -LiteralPath (Join-Path $stage 'error.txt') -Raw) }
        Test-Path -LiteralPath (Join-Path $stage 'ready')
    } 'The update helper did not acknowledge readiness.'
    $exitRequest = Start-Process -FilePath $target -ArgumentList '--exit' -PassThru -WindowStyle Hidden
    if (-not $exitRequest.WaitForExit(30000)) { throw 'The original app did not accept graceful shutdown.' }
    Wait-Until {
        if (Test-Path -LiteralPath (Join-Path $stage 'error.txt')) { throw (Get-Content -LiteralPath (Join-Path $stage 'error.txt') -Raw) }
        $helper.HasExited
    } 'Update helper did not finish.' 90
    if ($helper.ExitCode -ne 0) { throw 'The updater returned a failure code.' }
    Wait-Until { @(Get-CimInstance Win32_Process -Filter "name = 'Stransfer.exe'" | Where-Object { $_.ExecutablePath -eq $target -and $_.ProcessId -ne $old.Id }).Count -eq 1 } 'The updated app did not restart.'
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) { throw 'The updated executable does not match the candidate.' }
    $settings = Get-Content -LiteralPath (Join-Path $stateDir 'settings.json') -Raw | ConvertFrom-Json
    if ($settings.LastDestination -ne 'update-test-sentinel' -or (Get-Content -LiteralPath (Join-Path $appDir 'keep-me.txt') -Raw) -ne 'Keep unrelated files') { throw 'The updater changed settings or unrelated files.' }
    Wait-Until { -not (Test-Path -LiteralPath $stage) } 'Update staging was not cleaned.' 15
    if (@(Get-ChildItem -LiteralPath $appDir -Force -Filter '.stransfer-*').Count -ne 0) { throw 'The app folder contains updater leftovers.' }
    Write-Host 'PASS  published executable update: helper readiness, graceful exit, in-place replacement, restart acknowledgement, retained settings and clean folder'
}
finally {
    if (Test-Path -LiteralPath $target) {
        $running = @(Get-CimInstance Win32_Process -Filter "name = 'Stransfer.exe'" | Where-Object ExecutablePath -eq $target)
        if ($running.Count -gt 0) {
            $request = Start-Process -FilePath $target -ArgumentList '--exit' -PassThru -WindowStyle Hidden
            [void]$request.WaitForExit(30000)
            foreach ($process in $running) { try { [void](Get-Process -Id $process.ProcessId -ErrorAction Stop).WaitForExit(30000) } catch { } }
        }
    }
    if ($null -eq $previousState) { Remove-Item Env:\STRANSFER_STATE_DIR -ErrorAction SilentlyContinue } else { $env:STRANSFER_STATE_DIR = $previousState }
    $remaining = @(Get-CimInstance Win32_Process -Filter "name = 'Stransfer.exe'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($testRoot+[IO.Path]::DirectorySeparatorChar) })
    if ($remaining.Count -eq 0 -and $testRoot.StartsWith($testBase+[IO.Path]::DirectorySeparatorChar) -and [IO.Path]::GetFileName($testRoot) -eq $testId -and (Test-Path -LiteralPath $testRoot)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
