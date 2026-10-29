# Makes a clean copy to share: release\Roblox AFK Hub.exe, a zip of it, and SHA256SUMS.txt.
# Never share the bin folder: bin\AfkHub-data next to the exe holds YOUR accounts, cookies, webhook and macros.
# Usage:  powershell -ExecutionPolicy Bypass -File release.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$rel  = Join-Path $root 'release'

# ---- privacy check: words that must never be in the code or the exe ----
# Collected from your own data on this PC (never uploaded) plus private-words.txt (git-ignored).
function Get-PrivateWords {
    $words = New-Object System.Collections.Generic.List[string]
    $add = { param($w) if ($w -and "$w".Trim().Length -ge 4) { $words.Add("$w".Trim()) } }
    & $add $env:USERNAME
    & $add (git -C $root config user.email 2>$null)
    & $add (git config --global user.email 2>$null)
    $data = @((Join-Path $root 'bin\AfkHub-data'), (Join-Path $env:APPDATA 'AfkHub'))
    foreach ($d in $data) {
        $acc = Join-Path $d 'accounts.json'
        if (Test-Path $acc) {
            foreach ($a in (Get-Content $acc -Raw | ConvertFrom-Json).Accounts) { & $add $a.Name; & $add $a.DisplayName; & $add "$($a.UserId)" }
        }
        $cfg = Join-Path $d 'config.json'
        if (Test-Path $cfg) {
            $c = Get-Content $cfg -Raw
            $m = [regex]::Match($c, 'api/webhooks/(\d+)/([\w-]+)')
            if ($m.Success) { & $add $m.Groups[1].Value; & $add $m.Groups[2].Value }
        }
    }
    $extra = Join-Path $root 'private-words.txt'
    if (Test-Path $extra) { Get-Content $extra | Where-Object { $_ -notmatch '^\s*#' } | ForEach-Object { & $add $_ } }
    # The public noreply address is fine to publish; only the real email is private.
    $words | Where-Object { $_ -notmatch 'users\.noreply\.github\.com$' } | Sort-Object -Unique
}

function Test-Privacy([string[]]$words, [string]$exe) {
    $problems = @()
    $mask = { param($w) $w.Substring(0, 1) + ('*' * ($w.Length - 2)) + $w.Substring($w.Length - 1) }
    foreach ($f in (git -C $root ls-files)) {
        $path = Join-Path $root $f
        if (-not (Test-Path $path) -or (Get-Item $path).Length -gt 5MB) { continue }
        $text = [IO.File]::ReadAllText($path)
        foreach ($w in $words) { if ($text.IndexOf($w, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $problems += "  $f contains $(& $mask $w)" } }
    }
    if ($exe) {
        $bytes = [IO.File]::ReadAllBytes($exe)
        $ascii = [Text.Encoding]::ASCII.GetString($bytes); $wide = [Text.Encoding]::Unicode.GetString($bytes)
        foreach ($w in $words) {
            if ($ascii.IndexOf($w, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $wide.IndexOf($w, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $problems += "  the exe contains $(& $mask $w)" }
        }
    }
    $problems
}

$words = @(Get-PrivateWords)
Write-Host "Privacy check: looking for $($words.Count) private words (your accounts, IDs, webhook, email, Windows name)."
$found = @(Test-Privacy $words $null)
if ($found.Count) { Write-Host "STOPPED - personal info is in the code:" -ForegroundColor Red; $found | Write-Host; exit 1 }

& (Join-Path $root 'build.ps1')

$exe = Join-Path $root 'bin\Roblox AFK Hub.exe'
$found = @(Test-Privacy $words $exe | Where-Object { $_ -like '*the exe*' })
if ($found.Count) { Write-Host "STOPPED - personal info is in the built app:" -ForegroundColor Red; $found | Write-Host; exit 1 }
Write-Host "Privacy check passed: nothing personal in the code or the app." -ForegroundColor Green

$version = [regex]::Match((Get-Content (Join-Path $root 'src\Program.cs') -Raw), 'Version = "([^"]+)"').Groups[1].Value
if (Test-Path $rel) { Remove-Item $rel -Recurse -Force }
New-Item -ItemType Directory -Force $rel | Out-Null
Copy-Item $exe $rel

# Only the exe goes in: no AfkHub-data, logs or anything else from bin.
$zipName = "Roblox-AFK-Hub-v$version.zip"
$zip = Join-Path $rel $zipName
Compress-Archive -Path (Join-Path $rel 'Roblox AFK Hub.exe') -DestinationPath $zip

# Fingerprints: anyone can check their download matches (Get-FileHash <file>), so a repackaged fake stands out.
$sums = foreach ($n in 'Roblox AFK Hub.exe', $zipName) { "$((Get-FileHash (Join-Path $rel $n) -Algorithm SHA256).Hash.ToLower())  $n" }
$sums | Set-Content (Join-Path $rel 'SHA256SUMS.txt') -Encoding ascii

$items = Get-ChildItem $rel | ForEach-Object { $_.Name }
if ($items.Count -ne 3) { throw "Unexpected files in release: $($items -join ', ')" }
Write-Host "Release v$version ready in $rel"
$items | ForEach-Object { Write-Host "  $_" }
Write-Host "Fingerprints (put these in the release notes):"
$sums | ForEach-Object { Write-Host "  $_" }
