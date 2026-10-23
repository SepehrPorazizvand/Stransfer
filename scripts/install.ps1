[CmdletBinding()]
param(
    [switch]$Launch,
    [switch]$NoDesktopShortcut,
    [switch]$Slim
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishDirectory = Join-Path $projectRoot $(if ($Slim) { 'dist\Stransfer.Slim' } else { 'dist\Stransfer' })
$sourceExecutable = Join-Path $publishDirectory 'Stransfer.exe'
if (-not (Test-Path -LiteralPath $sourceExecutable)) {
    throw 'Build the full version first: .\scripts\build.ps1 -SelfContained. For a slim install, build without that switch and use install.ps1 -Slim.'
}

$installDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Stransfer'
$installedExecutable = Join-Path $installDirectory 'Stransfer.exe'
New-Item -ItemType Directory -Force -Path $installDirectory | Out-Null
try {
    Get-ChildItem -LiteralPath $publishDirectory -Force | Copy-Item -Destination $installDirectory -Recurse -Force
}
catch {
    throw "Could not update Stransfer. Close the running app, then retry. $($_.Exception.Message)"
}

$shell = New-Object -ComObject WScript.Shell
function New-StransferShortcut([string]$ShortcutPath) {
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $installedExecutable
    $shortcut.WorkingDirectory = $installDirectory
    $shortcut.IconLocation = "$installedExecutable,0"
    $shortcut.Description = 'Stransfer - resumable file transfers'
    $shortcut.Save()
    [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
}

try {
    $startMenuDirectory = [Environment]::GetFolderPath('Programs')
    New-StransferShortcut (Join-Path $startMenuDirectory 'Stransfer.lnk')
    if (-not $NoDesktopShortcut) {
        $desktopDirectory = [Environment]::GetFolderPath('DesktopDirectory')
        New-StransferShortcut (Join-Path $desktopDirectory 'Stransfer.lnk')
    }
    $sendToDirectory = [Environment]::GetFolderPath('SendTo')
    if (Test-Path -LiteralPath $sendToDirectory) {
        New-StransferShortcut (Join-Path $sendToDirectory 'Stransfer.lnk')
    }
}
finally {
    [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
}

# Register Windows Explorer context menu
$menuKey = "Stransfer.Paste"
foreach ($regPath in @("HKCU:\Software\Classes\Directory\Background\shell\$menuKey", "HKCU:\Software\Classes\Directory\shell\$menuKey")) {
    New-Item -Path $regPath -Force | Out-Null
    Set-ItemProperty -Path $regPath -Name "(Default)" -Value "Paste with Stransfer"
    Set-ItemProperty -Path $regPath -Name "Icon" -Value $installedExecutable
    Set-ItemProperty -Path $regPath -Name "Position" -Value "Top"
    $cmdPath = Join-Path $regPath "command"
    New-Item -Path $cmdPath -Force | Out-Null
    $dest = if ($regPath -like "*Background*") { "%V" } else { "%1" }
    Set-ItemProperty -Path $cmdPath -Name "(Default)" -Value "`"$installedExecutable`" --paste --destination `"$dest\.`""
}

# Configure default settings
$stateDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Stransfer'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
$settingsFile = Join-Path $stateDir 'settings.json'
if (-not (Test-Path -LiteralPath $settingsFile)) {
    @{
        StartWithWindows = $false
        KeepInBackground = $true
        UseForExplorerPaste = $true
        ExplorerContextMenu = $true
        VerifyCopies = $false
        SmallFileWorkers = 0
        LastDestination = ""
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsFile -Encoding utf8
}

# Register Windows Uninstaller
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Stransfer"
New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty -Path $uninstallKey -Name "DisplayName" -Value "Stransfer"
Set-ItemProperty -Path $uninstallKey -Name "DisplayVersion" -Value ([Diagnostics.FileVersionInfo]::GetVersionInfo($installedExecutable).ProductVersion.Split('+')[0])
Set-ItemProperty -Path $uninstallKey -Name "Publisher" -Value "Stransfer"
Set-ItemProperty -Path $uninstallKey -Name "DisplayIcon" -Value "$installedExecutable,0"
Set-ItemProperty -Path $uninstallKey -Name "InstallLocation" -Value $installDirectory
Set-ItemProperty -Path $uninstallKey -Name "NoModify" -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name "NoRepair" -Value 1 -Type DWord

Write-Host "Installed: $installedExecutable" -ForegroundColor Green
Write-Host "Explorer right-click context menu and shortcuts registered." -ForegroundColor Green
Write-Host 'No administrator access is required. Automatic startup is controlled inside the app.'
if ($Launch) { Start-Process -FilePath $installedExecutable -WorkingDirectory $installDirectory }
