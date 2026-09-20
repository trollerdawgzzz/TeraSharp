#Requires -Version 5.1
<#
.SYNOPSIS
    Nightly backup of the TeraSharp SQLite database, and optionally the MySQL and
    SQL Server databases beside it. Registers itself as a scheduled task.

.DESCRIPTION
    !SECURITY_TODO_before_going_public.txt leaves this open:
        [ ] Schedule nightly SQL Server backups of the five *_2800 databases +
            MySQL dump, copied OFF the box. Test a restore once.
    This is that item, plus terasharp.db, which nothing else backs up at all.

    TeraSharp does not set a journal mode, so SQLite runs its default rollback
    journal - NOT WAL. Copying the .db file while the Arbiter is writing can
    therefore capture a torn page. The script avoids that:

      * with sqlite3.exe on PATH it runs  VACUUM INTO  - a consistent snapshot
        taken through SQLite itself, safe on a live database, and the result is a
        single defragmented file. It then runs PRAGMA integrity_check on the copy,
        so a backup that cannot be read is caught tonight and not during a restore.
      * without it, it falls back to a plain copy and says so. A plain copy is
        only trustworthy while the Arbiter is stopped.

    Get sqlite3.exe from sqlite.org (the tools bundle, one exe, no install) and
    drop it next to this script or anywhere on PATH. It is worth the two minutes.

.PARAMETER Destination
    Where backups go. Default D:\backups\terasharp.

.PARAMETER Database
    The SQLite file. Default follows TERASHARP_DB, else D:\packetlogs\terasharp.db.

.PARAMETER KeepDays
    Delete archives older than this. Default 14. 0 keeps everything.

.PARAMETER OffsiteDir
    Also copy the finished archive here - a mounted share, a rclone mount, a
    second disk. A backup that only exists on the box being backed up is not one.

.PARAMETER MySqlDump
    Also mysqldump teraapi, box2db and steer3db. Needs mysqldump on PATH and
    -MySqlUser / -MySqlPassword.

.PARAMETER SqlServer
    Also BACKUP DATABASE the five *_2800 databases. Needs sqlcmd on PATH.

.PARAMETER Register
    Create the scheduled task instead of running a backup. Combine with the
    parameters you want the task to use - they are baked into its command line.

.PARAMETER Unregister
    Remove the scheduled task.

.PARAMETER At
    Time of day for -Register. Default 04:30.

.EXAMPLE
    .\backup-db.ps1
    One backup, now, with the defaults.

.EXAMPLE
    .\backup-db.ps1 -Register -At 04:30 -OffsiteDir \\nas\tera -KeepDays 30
    Nightly at 04:30, copied to the NAS, a month of history.

.EXAMPLE
    .\backup-db.ps1 -MySqlDump -MySqlUser root -MySqlPassword secret -SqlServer
    Everything, once, by hand.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Destination = 'D:\backups\terasharp',
    [string] $Database,
    [int]    $KeepDays = 14,
    [string] $OffsiteDir,
    [switch] $MySqlDump,
    [string] $MySqlUser,
    [string] $MySqlPassword,
    [string[]] $MySqlDatabases = @('teraapi', 'box2db', 'steer3db'),
    [switch] $SqlServer,
    [string[]] $SqlServerDatabases = @('Account_2800', 'Chat_2800', 'Log_2800', 'Planet_2800', 'Shared_2800'),
    [string] $SqlServerInstance = '.',
    [switch] $Register,
    [switch] $Unregister,
    [string] $At = '04:30',
    [string] $TaskName = 'TeraSharp nightly backup'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Database) {
    $Database = $env:TERASHARP_DB
    if (-not $Database) { $Database = 'D:\packetlogs\terasharp.db' }
}

function Write-Step { param([string] $Text) Write-Host "  $Text" }

function Get-Tool {
    param([string] $Name)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $local = Join-Path $PSScriptRoot $Name
    if (Test-Path $local) { return $local }
    return $null
}

# ------------------------------------------------------------ scheduled task

function Register-BackupTask {
    # $PSCommandPath is the SCRIPT file; $MyInvocation inside a function is the function.
    $self = $PSCommandPath
    if (-not $self) { $self = Join-Path $PSScriptRoot 'backup-db.ps1' }

    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $self),
                 '-Destination', ('"{0}"' -f $Destination), '-KeepDays', $KeepDays)
    if ($Database)   { $argList += @('-Database', ('"{0}"' -f $Database)) }
    if ($OffsiteDir) { $argList += @('-OffsiteDir', ('"{0}"' -f $OffsiteDir)) }
    if ($MySqlDump)  {
        $argList += '-MySqlDump'
        if ($MySqlUser)     { $argList += @('-MySqlUser', $MySqlUser) }
        if ($MySqlPassword) { $argList += @('-MySqlPassword', $MySqlPassword) }
    }
    if ($SqlServer) { $argList += @('-SqlServer', '-SqlServerInstance', ('"{0}"' -f $SqlServerInstance)) }

    if ($MySqlPassword) {
        Write-Warning 'The MySQL password goes into the task definition in clear text. Prefer a ~\.my.cnf with [mysqldump] credentials and leave -MySqlPassword off.'
    }

    $action    = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ($argList -join ' ')
    $trigger   = New-ScheduledTaskTrigger -Daily -At $At
    $settings  = New-ScheduledTaskSettingsSet -StartWhenAvailable `
                    -DontStopOnIdleEnd -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
                    -MultipleInstances IgnoreNew
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest

    if ($PSCmdlet.ShouldProcess($TaskName, "Register a daily task at $At")) {
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
            -Settings $settings -Principal $principal -Force | Out-Null
    }
    Write-Host "Registered '$TaskName', daily at $At, running as SYSTEM."
    Write-Host 'Run it once now to prove it works:'
    Write-Host "  Start-ScheduledTask -TaskName '$TaskName'"
    Write-Host "  Get-ScheduledTaskInfo -TaskName '$TaskName'   # LastTaskResult 0 = ok"
}

function Unregister-BackupTask {
    if ($PSCmdlet.ShouldProcess($TaskName, 'Unregister')) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
    Write-Host "Removed '$TaskName'."
}

if ($Register)   { Register-BackupTask; return }
if ($Unregister) { Unregister-BackupTask; return }

# ------------------------------------------------------------ the backup

$stamp   = Get-Date -Format 'yyyy-MM-dd_HHmm'
$workDir = Join-Path $Destination "work-$stamp"
$archive = Join-Path $Destination "terasharp-$stamp.zip"
$problems = @()

Write-Host ''
Write-Host "=== TeraSharp backup $stamp ==="

foreach ($dir in @($Destination, $workDir)) {
    if (-not (Test-Path $dir)) {
        if ($PSCmdlet.ShouldProcess($dir, 'Create directory')) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
    }
}

# ---- 1. SQLite -------------------------------------------------------------
Write-Host 'SQLite:'
if (-not (Test-Path $Database)) {
    $problems += "the SQLite database is not at $Database"
    Write-Step "[!] not found: $Database"
} else {
    $sqlite = Get-Tool 'sqlite3.exe'
    $target = Join-Path $workDir 'terasharp.db'
    if ($sqlite) {
        if ($PSCmdlet.ShouldProcess($Database, 'VACUUM INTO')) {
            $escaped = $target.Replace("'", "''")
            & $sqlite $Database "VACUUM INTO '$escaped';"
            if ($LASTEXITCODE -ne 0) { $problems += "sqlite3 VACUUM INTO exited $LASTEXITCODE" }
        }
        Write-Step "[+] snapshot via VACUUM INTO (safe on a live database)"

        if ((Test-Path $target) -and $PSCmdlet.ShouldProcess($target, 'PRAGMA integrity_check')) {
            $check = (& $sqlite $target 'PRAGMA integrity_check;') -join ' '
            if ($check.Trim() -ne 'ok') {
                $problems += "integrity_check on the copy said: $check"
                Write-Step "[!] integrity_check: $check"
            } else {
                Write-Step '[+] integrity_check: ok'
            }
        }
    } else {
        Write-Warning 'sqlite3.exe not found - falling back to a file copy. That is only safe while the Arbiter is STOPPED, because TeraSharp uses the default rollback journal, not WAL.'
        if ($PSCmdlet.ShouldProcess($Database, 'Copy (unsafe while running)')) {
            Copy-Item $Database $target -Force
            foreach ($suffix in @('-wal', '-shm', '-journal')) {
                $extra = "$Database$suffix"
                if (Test-Path $extra) { Copy-Item $extra "$target$suffix" -Force }
            }
        }
        Write-Step '[~] plain copy - get sqlite3.exe and re-run for a snapshot that is safe hot'
    }
}

# ---- 2. MySQL --------------------------------------------------------------
if ($MySqlDump) {
    Write-Host 'MySQL:'
    $dump = Get-Tool 'mysqldump.exe'
    if (-not $dump) { $dump = Get-Tool 'mysqldump' }
    if (-not $dump) {
        $problems += 'mysqldump was not found on PATH'
        Write-Step '[!] mysqldump not found'
    } else {
        foreach ($db in $MySqlDatabases) {
            $out = Join-Path $workDir "mysql-$db.sql"
            $dumpArgs = @('--single-transaction', '--routines', '--events', '--databases', $db)
            if ($MySqlUser)     { $dumpArgs = @("--user=$MySqlUser") + $dumpArgs }
            if ($MySqlPassword) { $dumpArgs = @("--password=$MySqlPassword") + $dumpArgs }
            if ($PSCmdlet.ShouldProcess($db, 'mysqldump')) {
                & $dump @dumpArgs | Set-Content -Path $out -Encoding UTF8
                if ($LASTEXITCODE -ne 0) { $problems += "mysqldump $db exited $LASTEXITCODE" }
            }
            Write-Step "[+] $db"
        }
    }
}

# ---- 3. SQL Server ---------------------------------------------------------
if ($SqlServer) {
    Write-Host 'SQL Server:'
    $sqlcmd = Get-Tool 'sqlcmd.exe'
    if (-not $sqlcmd) { $sqlcmd = Get-Tool 'sqlcmd' }
    if (-not $sqlcmd) {
        $problems += 'sqlcmd was not found on PATH'
        Write-Step '[!] sqlcmd not found'
    } else {
        foreach ($db in $SqlServerDatabases) {
            $bak = Join-Path $workDir "mssql-$db.bak"
            $tsql = "BACKUP DATABASE [$db] TO DISK = N'$bak' WITH INIT, COMPRESSION, CHECKSUM, STATS = 25;"
            if ($PSCmdlet.ShouldProcess($db, 'BACKUP DATABASE')) {
                & $sqlcmd -S $SqlServerInstance -E -b -Q $tsql | Out-Null
                if ($LASTEXITCODE -ne 0) { $problems += "BACKUP DATABASE $db exited $LASTEXITCODE" }
            }
            Write-Step "[+] $db"
        }
        Write-Step 'note: SQL Server writes the .bak itself, so its service account needs write access to the work folder.'
    }
}

# ---- 4. archive, prune, copy off the box -----------------------------------
Write-Host 'Archive:'
if ($PSCmdlet.ShouldProcess($archive, 'Compress')) {
    Compress-Archive -Path (Join-Path $workDir '*') -DestinationPath $archive -Force
    Remove-Item $workDir -Recurse -Force
}
if (Test-Path $archive) {
    Write-Step ('[+] {0} ({1:N1} MB)' -f (Split-Path $archive -Leaf), ((Get-Item $archive).Length / 1MB))
}

if ($OffsiteDir) {
    if ($PSCmdlet.ShouldProcess($OffsiteDir, 'Copy the archive off the box')) {
        if (-not (Test-Path $OffsiteDir)) { New-Item -ItemType Directory -Path $OffsiteDir -Force | Out-Null }
        Copy-Item $archive $OffsiteDir -Force
    }
    Write-Step "[+] copied to $OffsiteDir"
} else {
    Write-Warning 'No -OffsiteDir. This archive is on the same disk as the database it protects, so it does not survive the failure it is for.'
}

if ($KeepDays -gt 0) {
    $cutoff = (Get-Date).AddDays(-$KeepDays)
    Get-ChildItem -Path $Destination -Filter 'terasharp-*.zip' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt $cutoff } |
        ForEach-Object {
            if ($PSCmdlet.ShouldProcess($_.Name, 'Delete (past the retention window)')) {
                Remove-Item $_.FullName -Force
            }
            Write-Step "[-] pruned $($_.Name)"
        }
}

Write-Host ''
if ($problems.Count) {
    Write-Host 'Finished WITH PROBLEMS:'
    $problems | ForEach-Object { Write-Host "  [!] $_" }
    exit 1
}
Write-Host 'Finished clean.'
Write-Host ''
Write-Host 'Restore drill - do it once, now, not during an outage:'
Write-Host '  1. Expand-Archive the newest zip somewhere scratch.'
Write-Host '  2. Stop the Arbiter.'
Write-Host '  3. Copy terasharp.db over the live one.'
Write-Host '  4. Start it and log a character in.'
Write-Host 'A backup you have never restored is a hypothesis.'
exit 0
