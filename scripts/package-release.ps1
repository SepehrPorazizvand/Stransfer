[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.StransferVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'A stable three-part version is required.' }
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -SelfContained -Runtime $Runtime }
$publish = Join-Path $projectRoot 'dist\Stransfer'
$exe = Join-Path $publish 'Stransfer.exe'
$setup = Join-Path $projectRoot 'dist\StransferSetup.exe'
foreach ($file in @($exe, $setup)) {
    if (-not (Test-Path -LiteralPath $file)) { throw "Missing release file: $file" }
    $built = [Diagnostics.FileVersionInfo]::GetVersionInfo($file)
    if ("$($built.FileMajorPart).$($built.FileMinorPart).$($built.FileBuildPart)" -ne $version) { throw "Release version mismatch: $file" }
}
$out = [IO.Path]::GetFullPath((Join-Path $projectRoot "dist\releases\v$version"))
$expected = [IO.Path]::GetFullPath((Join-Path $projectRoot "dist\releases\v$version"))
if ($out -ne $expected -or -not $out.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar)) { throw 'Unexpected release directory.' }
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null
$packageName = "Stransfer-portable-$Runtime.zip"
$package = Join-Path $out $packageName
Add-Type -AssemblyName System.IO.Compression
$zipStream = [IO.File]::Open($package, [IO.FileMode]::CreateNew)
$zip = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in @('Stransfer.exe', 'START-HERE.txt')) {
        $inputStream = [IO.File]::OpenRead((Join-Path $publish $name))
        $entry = $zip.CreateEntry("Stransfer/$name", [IO.Compression.CompressionLevel]::Optimal)
        $output = $entry.Open()
        try { $inputStream.CopyTo($output) } finally { $output.Dispose(); $inputStream.Dispose() }
    }
} finally { $zip.Dispose(); $zipStream.Dispose() }
Copy-Item -LiteralPath $setup -Destination (Join-Path $out "Stransfer-Setup-$Runtime.exe")
$manifest = [ordered]@{
    schemaVersion = 1
    version = $version
    assetName = $packageName
    packageSize = (Get-Item -LiteralPath $package).Length
    sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    executableSize = (Get-Item -LiteralPath $exe).Length
    executableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out "update-$Runtime.json") -Encoding utf8NoBOM
$hashes = Get-ChildItem -LiteralPath $out -File | Sort-Object Name | ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" }
$hashes | Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Encoding utf8NoBOM
Write-Host "Release package: $out"
Get-ChildItem -LiteralPath $out -File | Select-Object Name, Length
