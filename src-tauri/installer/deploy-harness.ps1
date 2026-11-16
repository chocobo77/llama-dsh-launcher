# Deploy a DSH harness from the official repository, then let the installer mount
# the desktop shell into it ( <target>\apps\desktop-tauri ).
#
# Node is part of the deployment: a terminal user may have no Node.js at all, and
# the harness cannot be installed or run without one. When no suitable Node is
# found, the official Windows zip is downloaded, its SHA-256 is checked against
# nodejs.org's SHASUMS256.txt, and it is unpacked into <target>\node so the
# deployment is self-contained (the shell prefers that copy over PATH).
#
# Used by installer-hooks.nsh when no installed harness can be found, and usable
# standalone:
#   powershell -NoProfile -ExecutionPolicy Bypass -File deploy-harness.ps1 -Check
#   powershell -NoProfile -ExecutionPolicy Bypass -File deploy-harness.ps1 -Target "D:\Program files\deepseek-harness"
#
# ASCII only: the installer may run this under a code page that is not UTF-8.
# Exit codes: 0 deployed (or -Check passed), 2 prerequisites missing, 3 clone
# failed, 4 install/build failed, 5 the result does not look like a harness,
# 6 Node could not be provided.

param(
    [string]$Target = 'D:\Program files\deepseek-harness',
    [string]$Repo = 'https://github.com/deepseek-ai/deepseek-harness.git',
    [string]$Ref = '',
    [string]$NodeVersion = '',
    [switch]$Check,
    [switch]$SkipBuild,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$log = Join-Path $env:TEMP 'dsh-deploy.log'
$minNodeMajor = 24
$minNodeMinor = 9

function Log([string]$msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg
    Write-Output $line
    Add-Content -LiteralPath $log -Value $line -Encoding UTF8
}
function Fail([int]$code, [string]$msg) {
    Log "ERROR: $msg"
    exit $code
}
function Test-NodeUsable([string]$exe) {
    if (-not $exe -or -not (Test-Path -LiteralPath $exe)) { return $null }
    try { $v = (& $exe -v).TrimStart('v') } catch { return $null }
    $parts = $v -split '\.'
    if ($parts.Count -lt 2) { return $null }
    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    if ($major -gt $minNodeMajor) { return $v }
    if ($major -eq $minNodeMajor -and $minor -ge $minNodeMinor) { return $v }
    return $null
}
function Resolve-NodeDownload([string]$version) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    if (-not $version) {
        $index = Invoke-WebRequest -UseBasicParsing -Uri 'https://nodejs.org/dist/index.json'
        $entries = $index.Content | ConvertFrom-Json
        $candidate = $entries | Where-Object { $_.version -match '^v24\.' } | Select-Object -First 1
        if (-not $candidate) { Fail 6 'nodejs.org/dist/index.json lists no v24 release' }
        $version = $candidate.version
    }
    if ($version -notmatch '^v') { $version = "v$version" }
    $zipName = "node-$version-win-x64.zip"
    $shasums = (Invoke-WebRequest -UseBasicParsing -Uri "https://nodejs.org/dist/$version/SHASUMS256.txt").Content
    $line = ($shasums -split "`n") | Where-Object { $_ -match [regex]::Escape($zipName) } | Select-Object -First 1
    if (-not $line) { Fail 6 "SHASUMS256.txt for $version has no $zipName" }
    $expected = ($line -split '\s+')[0].Trim().ToLower()
    return [pscustomobject]@{
        version  = $version
        zipName  = $zipName
        url      = "https://nodejs.org/dist/$version/$zipName"
        expected = $expected
    }
}

Log "target: $Target"
Log "repo:   $Repo"

# ---------------------------------------------------------------------- git
$git = Get-Command git -ErrorAction SilentlyContinue
if (-not $git) { Fail 2 'git not found on PATH: install Git for Windows first' }
Log "git $(& git --version)"

# --------------------------------------------------------------- Node (own copy)
# Look for a usable Node before cloning: a terminal user may have none, and the
# deployment must not depend on PATH afterwards anyway.
$nodeDir = Join-Path $Target 'node'
$nodeExe = Join-Path $nodeDir 'node.exe'
$nodeVersion = Test-NodeUsable $nodeExe
if (-not $nodeVersion) {
    $onPath = Get-Command node -ErrorAction SilentlyContinue
    if ($onPath) { $nodeVersion = Test-NodeUsable $onPath.Source }
    if ($nodeVersion) {
        Log "using Node v$nodeVersion from PATH for the build (the deployment keeps its own copy only if needed)"
    }
}

if ($Check) {
    if (-not $nodeVersion) { Log 'no usable Node yet: a deployment would download one' }
    Log 'prerequisites OK (-Check)'
    exit 0
}

if (-not (Test-Path -LiteralPath (Join-Path $Target 'apps\cli\lib\bin.js'))) {
    if (Test-Path -LiteralPath $Target) {
        $entries = @(Get-ChildItem -LiteralPath $Target -Force)
        if ($entries.Count -gt 0 -and -not $Force) {
            Fail 4 "target exists and is not empty: $Target (pass -Force to deploy into it anyway)"
        }
    }
    $parent = Split-Path -Parent $Target
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        Log "created $parent"
    }
    if (-not (Test-Path -LiteralPath $Target)) {
        New-Item -ItemType Directory -Path $Target -Force | Out-Null
    }

    # Node into <target>\node, from the official zip when the machine has none
    # that is new enough.
    if (-not $nodeVersion) {
        $dl = Resolve-NodeDownload $NodeVersion
        Log "no usable Node found: downloading $($dl.zipName)"
        $zip = Join-Path $env:TEMP $dl.zipName
        Invoke-WebRequest -UseBasicParsing -Uri $dl.url -OutFile $zip
        $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $dl.expected) {
            Fail 6 "SHA-256 mismatch for $($dl.zipName): expected $($dl.expected), got $actual"
        }
        Log "checksum verified ($actual)"
        if (Test-Path -LiteralPath $nodeDir) { Remove-Item -LiteralPath $nodeDir -Recurse -Force }
        $staging = Join-Path $env:TEMP ("dsh-node-" + [Guid]::NewGuid().ToString('N'))
        Expand-Archive -LiteralPath $zip -DestinationPath $staging -Force
        $inner = Get-ChildItem -LiteralPath $staging -Directory | Select-Object -First 1
        Move-Item -LiteralPath $inner.FullName -Destination $nodeDir
        Remove-Item -LiteralPath $staging -Recurse -Force
        Remove-Item -LiteralPath $zip -Force
        $nodeExe = Join-Path $nodeDir 'node.exe'
        $nodeVersion = Test-NodeUsable $nodeExe
        if (-not $nodeVersion) { Fail 6 'unpacked Node is not usable' }
        Log "Node v$nodeVersion unpacked into $nodeDir"
    }

    # The rest of the deployment must use this Node.
    $env:PATH = "$nodeDir;$env:PATH"
    Log "node v$nodeVersion ($nodeExe)"

    # pnpm through corepack, which ships with Node and honours the repository's
    # packageManager field (pnpm@11.28.5 today).
    $pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
    if (-not $pnpm) {
        $corepack = Join-Path $nodeDir 'corepack.cmd'
        if (Test-Path -LiteralPath $corepack) {
            Log 'pnpm missing: enabling it through corepack'
            & $corepack enable 2>&1 | ForEach-Object { Log "corepack: $_" }
            $pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
        }
    }
    if (-not $pnpm) { Fail 6 'pnpm is unavailable and corepack could not provide it' }
    Log "pnpm $(& pnpm -v)"

    # -------------------------------------------------------------------- clone
    # Full clone on purpose (no --depth 1): the desktop shell's "check for
    # harness updates" flow reads ahead/behind counts across the history, and
    # a shallow boundary makes them wrong.
    $cloneArgs = @('clone')
    if ($Ref) { $cloneArgs += @('--branch', $Ref) }
    $cloneArgs += @($Repo, $Target)
    Log "git $($cloneArgs -join ' ')"
    & git @cloneArgs 2>&1 | ForEach-Object { Log "git: $_" }
    if ($LASTEXITCODE -ne 0) { Fail 3 "git clone failed (exit $LASTEXITCODE)" }

    # ----------------------------------------------------------- install + build
    Push-Location -LiteralPath $Target
    try {
        Log 'pnpm install (the slow step: minutes, and it needs the network)'
        & pnpm install --frozen-lockfile 2>&1 | ForEach-Object { Log "pnpm: $_" }
        if ($LASTEXITCODE -ne 0) {
            Log 'frozen lockfile install failed; retrying without --frozen-lockfile'
            & pnpm install 2>&1 | ForEach-Object { Log "pnpm: $_" }
            if ($LASTEXITCODE -ne 0) { Fail 4 "pnpm install failed (exit $LASTEXITCODE)" }
        }
        if (-not $SkipBuild) {
            Log 'pnpm run build'
            & pnpm run build 2>&1 | ForEach-Object { Log "pnpm: $_" }
            if ($LASTEXITCODE -ne 0) { Fail 4 "pnpm run build failed (exit $LASTEXITCODE)" }
        }
    }
    finally {
        Pop-Location
    }
}

# ------------------------------------------------------------------------ verify
if (-not (Test-Path -LiteralPath (Join-Path $Target 'apps\cli\lib\bin.js'))) {
    Fail 5 "deployed, but $Target\apps\cli\lib\bin.js is missing: the checkout or the build did not produce a CLI"
}

# PATH: the shell prefers <harness>\node\node.exe by itself, but `dsh` on a
# terminal should work too, so the Node directory is appended to the user PATH
# once, idempotently.
if (Test-Path -LiteralPath (Join-Path $Target 'node\node.exe')) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not $userPath) { $userPath = '' }
    $entries = $userPath -split ';' | Where-Object { $_ -and $_.Trim() -ne '' }
    $nodeDir = Join-Path $Target 'node'
    if (-not ($entries | Where-Object { $_.TrimEnd('\') -eq $nodeDir.TrimEnd('\') })) {
        $newPath = (@($entries) + $nodeDir) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        Log "added $nodeDir to the user PATH (new terminals only)"
    }
}

Log 'deployed and verified'
Write-Output "DEPLOYED $Target"
if ($nodeVersion) { Write-Output "NODE $nodeDir ($nodeVersion)" }
exit 0
