# Locate an installed DSH harness: a checkout of the deepseek-harness monorepo,
# identified by apps\cli\lib\bin.js plus either pnpm-workspace.yaml or packages\.
#
# Called from the NSIS installer (installer-hooks.nsh) and usable standalone:
#   powershell -NoProfile -ExecutionPolicy Bypass -File detect-harness.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File detect-harness.ps1 -Json
#   powershell -NoProfile -ExecutionPolicy Bypass -File detect-harness.ps1 -Best
#
# Sources, cheapest and most authoritative first: the root remembered by a
# previous install, the Host processes running right now, the session buckets DSH
# has already created, PATH, then a depth-limited scan of the usual install
# roots. Read-only; nothing is written. ASCII only on purpose: the installer may
# run it under a code page that is not UTF-8.

param(
    [switch]$Json,
    [switch]$Best,
    [int]$MaxDepth = 3
)

$ErrorActionPreference = 'SilentlyContinue'
$candidates = New-Object System.Collections.ArrayList

function Test-HarnessRoot([string]$root) {
    if ([string]::IsNullOrWhiteSpace($root)) { return $false }
    $root = $root.TrimEnd('\')
    if (-not (Test-Path -LiteralPath (Join-Path $root 'apps\cli\lib\bin.js'))) { return $false }
    return (Test-Path -LiteralPath (Join-Path $root 'pnpm-workspace.yaml')) -or
           (Test-Path -LiteralPath (Join-Path $root 'packages'))
}

function Add-Candidate([string]$root, [string]$source, [int]$score) {
    if (-not (Test-HarnessRoot $root)) { return }
    $root = $root.TrimEnd('\')
    foreach ($c in $candidates) { if ($c.root -eq $root) { return } }
    [void]$candidates.Add([pscustomobject]@{ root = $root; source = $source; score = $score })
}

# ------------------------------------------------------------ 1. remembered root
$remembered = (Get-ItemProperty -Path 'HKCU:\Software\DeepSeek\DSH Desktop' -Name 'HarnessRoot' -ErrorAction SilentlyContinue).HarnessRoot
Add-Candidate $remembered 'remembered by a previous install' 100

# ---------------------------------------------------- 2. running Host processes
# The strongest live signal: a running Host names its own checkout on its command
# line ( ...\apps\cli\lib\bin.js web ).
foreach ($p in (Get-CimInstance Win32_Process -Filter "Name='node.exe' or Name='bun.exe' or Name='deno.exe'")) {
    $cmd = $p.CommandLine
    if (-not $cmd) { continue }
    $lower = $cmd.ToLower()
    $idx = $lower.IndexOf('\apps\cli\lib\bin.js')
    if ($idx -lt 0) { $idx = $lower.IndexOf('/apps/cli/lib/bin.js') }
    if ($idx -lt 0) { continue }
    $prefix = $cmd.Substring(0, $idx).Trim().Trim('"')
    if ($prefix -match '^[A-Za-z]:') { Add-Candidate $prefix 'a DSH Host running now' 95 }
}

# -------------------------------------------------------------- 3. session buckets
# DSH buckets sessions per workspace path under %USERPROFILE%\.dsh\sessions:
# the directory name is `--`-wrapped, `-` stands for `\`, and characters that need
# escaping are written ~XXXX (a space is ~0020). Decoding gives every directory
# DSH has run in.
function Expand-Bucket([string]$name) {
    # DSH buckets sessions per workspace path under %USERPROFILE%\.dsh\sessions.
    # The encoding is `--`-wrapped, `~XXXX` for characters that need escaping, and
    # `-` for BOTH `\` and a literal hyphen: `--D-Program~0020files-deepseek-
    # harness--` is D:\Program files\deepseek-harness, not ...\deepseek\harness.
    # The split is therefore ambiguous, and the only honest oracle is the file
    # system: descend one segment at a time, and when a segment is missing on
    # disk, glue it to its neighbours with the hyphen it came from.
    $s = $name
    if ($s.StartsWith('--')) { $s = $s.Substring(2) }
    if ($s.EndsWith('--')) { $s = $s.Substring(0, $s.Length - 2) }
    while ($true) {
        $m = [regex]::Match($s, '~([0-9A-Fa-f]{4})')
        if (-not $m.Success) { break }
        $ch = [string][char][Convert]::ToInt32($m.Groups[1].Value, 16)
        $s = $s.Remove($m.Index, $m.Length).Insert($m.Index, $ch)
    }
    if ($s -notmatch '^([A-Za-z])-(.+)$') { return @() }
    $drive = "{0}:\" -f $Matches[1]
    $tokens = $Matches[2] -split '-'
    if (-not (Test-Path -LiteralPath $drive)) { return @() }

    $found = New-Object System.Collections.ArrayList
    $work = New-Object System.Collections.ArrayList
    [void]$work.Add([pscustomobject]@{ path = $drive; index = 0 })
    while ($work.Count -gt 0) {
        $item = $work[0]
        $work.RemoveAt(0)
        if ($item.index -ge $tokens.Count) {
            if (Test-HarnessRoot $item.path) { [void]$found.Add($item.path) }
            continue
        }
        # Try the segment alone, then glued with one or two following segments:
        # a literal hyphen in a directory name is indistinguishable from the
        # separator, and the filesystem decides.
        $joined = $tokens[$item.index]
        for ($j = $item.index; $j -lt $tokens.Count -and ($j - $item.index) -lt 3; $j++) {
            if ($j -gt $item.index) { $joined = $joined + '-' + $tokens[$j] }
            $candidate = Join-Path $item.path $joined
            if (Test-Path -LiteralPath $candidate) {
                [void]$work.Add([pscustomobject]@{ path = $candidate; index = $j + 1 })
            }
        }
    }
    return $found
}

$sessions = Join-Path $env:USERPROFILE '.dsh\sessions'
if (Test-Path -LiteralPath $sessions) {
    foreach ($d in (Get-ChildItem -LiteralPath $sessions -Directory -ErrorAction SilentlyContinue)) {
        # Only buckets that can name a harness are expanded: a session bucket for
        # any other workspace is noise, and skipping it keeps this cheap.
        if ($d.Name -notmatch '(?i)dsh|deepseek|harness') { continue }
        foreach ($root in (Expand-Bucket $d.Name)) {
            Add-Candidate $root 'a DSH session bucket' 80
        }
    }
}

# ------------------------------------------------------------------------ 4. PATH
$pathValues = @(
    [Environment]::GetEnvironmentVariable('Path', 'User'),
    [Environment]::GetEnvironmentVariable('Path', 'Machine')
)
foreach ($pv in $pathValues) {
    if (-not $pv) { continue }
    foreach ($entry in ($pv -split ';')) {
        if ($entry.Trim() -eq '') { continue }
        $e = $entry.Trim().Trim('"')
        if ($e -notmatch 'dsh|deepseek') { continue }
        $probe = $e
        for ($i = 0; $i -lt 4 -and $probe; $i++) {
            Add-Candidate $probe 'a PATH entry' 60
            $probe = Split-Path -Parent $probe
        }
    }
}

# ------------------------------------------------------- 5. depth-limited disk scan
# Skipped when a high-confidence source already answered: the installer should not
# walk the disk behind the user's back when it already knows the answer.
$bestSoFar = 0
if ($candidates.Count -gt 0) {
    $bestSoFar = ($candidates | Measure-Object -Property score -Maximum).Maximum
}
if ($bestSoFar -lt 80) {
$roots = New-Object System.Collections.ArrayList
foreach ($r in @(
        'D:\Program files', 'D:\Program Files', 'C:\Program Files', 'C:\Program Files (x86)',
        'D:\Apps', 'D:\app', 'D:\code', 'D:\dev', 'D:\work', 'D:\projects',
        "$env:LOCALAPPDATA", "$env:APPDATA", $env:USERPROFILE,
        'D:\', 'C:\', 'E:\', 'F:\')) {
    if ($r -and (Test-Path -LiteralPath $r)) { [void]$roots.Add($r) }
}
$skip = @('Windows', 'WinSxS', 'System Volume Information', '$Recycle.Bin', 'node_modules',
    'WindowsApps', 'ProgramData', '.git', '.pnpm-store', 'Temp', 'tmp', 'NVIDIA', 'AMD',
    'Intel', 'Config.Msi', 'recovery', 'Recovery', 'packages-cache')

foreach ($root in $roots) {
    $queue = New-Object System.Collections.ArrayList
    [void]$queue.Add([pscustomobject]@{ dir = $root; depth = 0 })
    $visited = 0
    while ($queue.Count -gt 0 -and $visited -lt 4000) {
        $item = $queue[0]
        $queue.RemoveAt(0)
        $visited++
        if (Test-Path -LiteralPath (Join-Path $item.dir 'pnpm-workspace.yaml')) {
            Add-Candidate $item.dir 'a disk scan' 40
        }
        if ($item.depth -ge $MaxDepth) { continue }
        foreach ($sub in (Get-ChildItem -LiteralPath $item.dir -Directory -ErrorAction SilentlyContinue)) {
            $name = $sub.Name
            $skipIt = $false
            foreach ($s in $skip) {
                if ($name -eq $s) { $skipIt = $true; break }
            }
            if ($skipIt) { continue }
            if ($sub.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            [void]$queue.Add([pscustomobject]@{ dir = $sub.FullName; depth = $item.depth + 1 })
        }
    }
}
}

# ------------------------------------------------------------------------ output
$sorted = @($candidates | Sort-Object -Property score -Descending)
if ($Json) {
    $sorted | ConvertTo-Json -Depth 3
    exit 0
}
if ($Best) {
    if ($sorted.Count -gt 0) { Write-Output $sorted[0].root }
    exit 0
}
if ($sorted.Count -eq 0) {
    Write-Output 'NO-HARNESS-FOUND'
    exit 1
}
foreach ($c in $sorted) { Write-Output ("{0}`t{1}`t{2}" -f $c.score, $c.source, $c.root) }
exit 0
