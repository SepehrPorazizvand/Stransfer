[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dist = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$archive = Join-Path $dist 'Stransfer-source.zip'
$allowedRoots = @('src', 'tests', 'docs', 'scripts', '.github')
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Open($archive, [IO.FileMode]::Create)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    $rootNames = @('README.md', 'Stransfer.slnx', '.gitignore', 'LICENSE', 'LICENSE.md')
    $files = @(Get-ChildItem -LiteralPath $projectRoot -File -Force | Where-Object { $_.Name -in $rootNames })
    foreach ($name in $allowedRoots) {
        $folder = Join-Path $projectRoot $name
        if (Test-Path -LiteralPath $folder) { $files += Get-ChildItem -LiteralPath $folder -File -Force -Recurse }
    }
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')
        if ($relative -match '(^|/)(bin|obj|TestResults)(/|$)' -or $relative -match '\.(log|user|suo)$') { continue }
        if ($relative -match '^\.' -and $relative -notmatch '^\.gitignore$|^\.github/') { continue }
        $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
        $inputStream = $file.OpenRead(); $outputStream = $entry.Open()
        try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
    }
}
finally { $zip.Dispose(); $stream.Dispose() }
Write-Host "Source package: $archive ($((Get-Item -LiteralPath $archive).Length) bytes)"
