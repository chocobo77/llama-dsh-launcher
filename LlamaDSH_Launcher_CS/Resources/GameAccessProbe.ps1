# GameAccessProbe.ps1 - verify that the elevated DSH child process can open a handle to the game process.
# Run by the elevated cmd.exe boot chain (see TryStartDshElevated in MainWindow.xaml.cs); its stdout/stderr
# are appended to LlamaDSH_dsh_boot.log so the launcher UI shows the result. A non-elevated (medium integrity)
# process cannot open a handle to a higher-integrity game process - that is exactly the wall this probe checks.
# Keep this file ASCII-only.

$gameName = 'EliteDangerous64'
$proc = Get-Process -Name $gameName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) {
    Write-Output "[probe] game access: $gameName not running"
    exit 0
}

try {
    $p = [System.Diagnostics.Process]::GetProcessById($proc.Id)
    $null = $p.Handle
    Write-Output "[probe] game access: OK pid=$($proc.Id) (handle opened)"
}
catch {
    Write-Output "[probe] game access: DENIED pid=$($proc.Id) - $($_.Exception.Message)"
}
