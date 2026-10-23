[CmdletBinding()]
param([switch]$RemoveSettings)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name Stransfer -ErrorAction SilentlyContinue) { throw 'Exit Stransfer from its tray menu before uninstalling.' }
Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Stransfer -ErrorAction SilentlyContinue
foreach ($path in @('HKCU:\Software\Classes\Directory\Background\shell\Stransfer.Paste', 'HKCU:\Software\Classes\Directory\shell\Stransfer.Paste')) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse }
}
foreach ($directory in @([Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('SendTo'))) {
    $shortcut = Join-Path $directory 'Stransfer.lnk'
    if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut }
}
Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Stransfer' -Recurse -ErrorAction SilentlyContinue
$local = [IO.Path]::GetFullPath([Environment]::GetFolderPath('LocalApplicationData'))
$install = [IO.Path]::GetFullPath((Join-Path $local 'Programs\Stransfer'))
if ($install -ne (Join-Path $local 'Programs\Stransfer')) { throw 'Unexpected install path.' }
if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse }
if ($RemoveSettings) {
    $state = [IO.Path]::GetFullPath((Join-Path $local 'Stransfer'))
    if ($state -ne (Join-Path $local 'Stransfer')) { throw 'Unexpected settings path.' }
    if (Test-Path -LiteralPath $state) { Remove-Item -LiteralPath $state -Recurse }
}
Write-Host 'Stransfer removed. Completed transfers, source files and external tools were kept.'
