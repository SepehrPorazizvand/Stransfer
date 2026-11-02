[CmdletBinding()]
param([switch]$History, [switch]$SelfTest)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$rules = [ordered]@{
    'GitHub token' = 'gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{50,255}'
    'OpenAI key' = 'sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{32,}'
    'AWS access key' = '(?:AKIA|ASIA)[A-Z0-9]{16}'
    'Private key' = '-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----'
    'Literal credential' = '(?i)(?:api[_-]?key|access[_-]?token|client[_-]?secret|password)\s*[:=]\s*["''][A-Za-z0-9/+=_.-]{20,}["'']'
}
if ($SelfTest) {
    $examples = @{
        'GitHub token' = 'ghp_' + ('A' * 36)
        'OpenAI key' = 'sk-proj-' + ('a' * 40)
        'AWS access key' = 'AKIA' + ('A' * 16)
        'Private key' = '-----BEGIN ' + 'PRIVATE KEY-----'
        'Literal credential' = 'api_key = "' + ('a' * 32) + '"'
    }
    foreach ($name in $rules.Keys) {
        if ($examples[$name] -notmatch $rules[$name]) { throw "Credential detector self-test failed: $name" }
    }
}
$failures = [Collections.Generic.List[string]]::new()
Push-Location $root
try {
    $hasGit = (Test-Path -LiteralPath (Join-Path $root '.git')) -and (Get-Command git -ErrorAction SilentlyContinue)
    if ($hasGit) {
        $files = @(git -c core.quotepath=false ls-files --cached --others --exclude-standard)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot list repository files for credential checks.' }
    }
    else {
        $files = @(Get-ChildItem -LiteralPath $root -File -Force)
        foreach ($name in @('src','tests','docs','scripts','.github')) {
            if (Test-Path -LiteralPath (Join-Path $root $name)) { $files += Get-ChildItem -LiteralPath (Join-Path $root $name) -Recurse -File -Force }
        }
        $files = @($files | ForEach-Object { $_.FullName.Substring($root.Length + 1).Replace('\','/') } | Where-Object { $_ -notmatch '(^|/)(bin|obj)/' })
    }
    foreach ($path in $files) {
        if ($path -match '(^|/)(\.env(?!\.example$)(\..*)?|\.secrets|secrets\.local\.json|credentials\.local\.json)(/|$)' -or $path -match '\.(pfx|p12|key)$') {
            $failures.Add("Credential file: $path"); continue
        }
        if ($path -match '\.(gif|png|ico|zip|exe|dll)$') { continue }
        $fullPath = Join-Path $root $path
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
        $content = [IO.File]::ReadAllText($fullPath)
        foreach ($name in $rules.Keys) { if ($content -match $rules[$name]) { $failures.Add("${name}: $path") } }
    }
    if ($History) {
        if (-not $hasGit) { throw 'A Git checkout is required to scan history.' }
        $combined = '(?i)(?:' + (($rules.Values | ForEach-Object { $_ -replace '^\(\?i\)', '' }) -join ')|(?:') + ')'
        $commits = @(git rev-list --all)
        foreach ($commit in $commits) {
            $matches = @(git grep -I -l -P -e $combined $commit -- .)
            if ($LASTEXITCODE -gt 1) { throw 'Git history credential scan failed.' }
            foreach ($match in $matches) { $failures.Add("Historical credential match: $match") }
        }
    }
    if ($failures.Count -gt 0) {
        # Report rule/path only. Never print the credential value in CI logs.
        $failures | Sort-Object -Unique | ForEach-Object { Write-Output $_ }
        throw 'Possible credentials found. Remove them before publishing; rotate any real credential already exposed.'
    }
    Write-Host 'PASS  credential checks (no matching secrets or credential files found).'
    # git grep returns 1 for no matches; do not let Actions treat that as a failure.
    $global:LASTEXITCODE = 0
}
finally { Pop-Location }
