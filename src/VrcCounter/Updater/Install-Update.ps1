param([Parameter(Mandatory = $true)][string]$PlanPath, [switch]$Quiet)
$ErrorActionPreference = 'Stop'
$changedFiles = [System.Collections.Generic.List[object]]::new()
$installStarted = $false
$logPath = Join-Path (Split-Path -Parent $PlanPath) 'update.log'

function Resolve-Child([string]$Root, [string]$Relative) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $targetPath = [IO.Path]::GetFullPath((Join-Path $rootPath $Relative))
    if (-not $targetPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Update path escaped its folder.' }
    # Do not follow junctions or symbolic links in the installed application.
    $cursor = $targetPath
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked update path: $cursor" }
        }
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
    return $targetPath
}

function Start-Counter {
    $exe = Join-Path $plan.InstallDirectory 'VrcCounter.exe'
    # Only the PowerShell helper is hidden. This is a WinExe and needs its UI visible.
    Start-Process -FilePath $exe -WorkingDirectory $plan.DataDirectory -ArgumentList @('--data-dir', ('"' + $plan.DataDirectory.TrimEnd('\') + '"')) -WindowStyle Normal | Out-Null
}

try {
    $plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json
    $oldProcess = Get-Process -Id $plan.ProcessId -ErrorAction SilentlyContinue
    if ($oldProcess -and $oldProcess.StartTime.ToUniversalTime().Ticks.ToString() -eq $plan.ProcessStartTicks) {
        if (-not $oldProcess.WaitForExit(90000)) { throw 'The app did not finish closing. No files were changed.' }
    }
    # Validate every destination and make the entire backup before changing any file.
    $operations = foreach ($relative in $plan.Files) {
        if ($relative -match '(^|[/\\])\.\.?([/\\]|$)|[:*?"<>|]|\.(sqlite3?|db)(-wal|-shm)?$|vrc_multi_param_counter\.config\.json') { throw 'Unsafe file in update plan.' }
        $source = Resolve-Child $plan.PayloadDirectory $relative
        $target = Resolve-Child $plan.InstallDirectory $relative
        $backup = Resolve-Child $plan.BackupDirectory $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing update file: $relative" }
        $existed = Test-Path -LiteralPath $target -PathType Leaf
        if ($existed) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
            Copy-Item -LiteralPath $target -Destination $backup
        }
        [pscustomobject]@{ Source = $source; Target = $target; Backup = $backup; Existed = $existed }
    }
    $installStarted = $true
    foreach ($op in $operations) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $op.Target) -Force | Out-Null
        $temporary = $op.Target + '.update-' + [guid]::NewGuid().ToString('N') + '.tmp'
        try {
            Copy-Item -LiteralPath $op.Source -Destination $temporary
            # Atomic replacement never leaves a half-written DLL if a file is locked.
            # WebView2 may need a moment to release DLLs after its parent exits.
            for ($attempt = 0; ; $attempt++) {
                try {
                    if ($op.Existed) { [IO.File]::Replace($temporary, $op.Target, [NullString]::Value) }
                    else { [IO.File]::Move($temporary, $op.Target) }
                    $changedFiles.Add($op)
                    break
                } catch { if ($attempt -ge 9) { throw }; Start-Sleep -Milliseconds 500 }
            }
        } finally {
            if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
        }
    }
    Start-Counter
    "Update completed. Previous application files are in $($plan.BackupDirectory)." | Set-Content -LiteralPath $logPath
} catch {
    $failure = $_.Exception.Message
    $rollbackErrors = [System.Collections.Generic.List[string]]::new()
    for ($i = $changedFiles.Count - 1; $i -ge 0; $i--) {
        $op = $changedFiles[$i]
        try {
            if ($op.Existed) { Copy-Item -LiteralPath $op.Backup -Destination $op.Target -Force }
            elseif (Test-Path -LiteralPath $op.Target -PathType Leaf) { Remove-Item -LiteralPath $op.Target -Force }
        } catch { $rollbackErrors.Add($_.Exception.Message) }
    }
    $message = "Update could not finish: $failure`n`nYour settings and counter history were not changed."
    if ($rollbackErrors.Count) { $message += "`nRestore the application files from $($plan.BackupDirectory).`n$($rollbackErrors -join '`n')" }
    elseif ($installStarted) { $message += "`nThe previous application files were restored." }
    $message | Set-Content -LiteralPath $logPath
    if ($installStarted -and -not $rollbackErrors.Count) { try { Start-Counter } catch { } }
    if (-not $Quiet) {
        Add-Type -AssemblyName System.Windows.Forms
        [Windows.Forms.MessageBox]::Show($message, 'VRChat Counter update', 'OK', 'Error') | Out-Null
    }
    exit 1
}
