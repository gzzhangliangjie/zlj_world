<#
unity-cli.ps1 - VoxelCraft Unity CLI (offline, single entry point)

One command line for every Unity workflow in this repo. Results are judged by
parsing Unity's own log, never by exit code alone (see AGENTS.md pitfalls).

Usage:
  powershell -File Tools\unity-cli.ps1 <command> [args] [-TimeoutSec N] [-UnityExe path]

Commands:
  status                Environment + project snapshot (no Unity launch)
  compile-fast          ~20s MSBuild syntax check of both assemblies (no Unity launch)
  compile               Full Unity batchmode compile check (authoritative)
  test                  Full Unity batchmode SelfTest.RunAll regression
  build webgl|win       Player build via WebGLBuild.Build / WindowsBuild.Build
  publish [message]     build webgl + commit + push (delegates publish-webgl.ps1)
  snapshot              Batchmode model snapshots into _logs (ModelSnapshot.Run)
  log [file]            Summarize a Unity log file (newest in _logs if omitted)
  clean [-Yes]          Delete Library/Temp/obj/Logs (asks unless -Yes)
  ci [-WithWebGL]       Gate: compile-fast (full compile if csproj drifted) + test
  help                  Show this help

Options:
  -UnityExe <path>      Unity editor exe. Default: D:\Unity\2022.3.57f1c2\Editor\Unity.exe
  -TimeoutSec <n>       Per Unity-invocation timeout. Default: 1200
  -Yes                  Assume "yes" for destructive steps (clean)

Exit codes: 0 = ok, 1 = failure, 2 = usage error.
All logs land in _logs\ with timestamps - never in the project root.
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = "help",
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$Rest,
    [string]$UnityExe = "D:\Unity\2022.3.57f1c2\Editor\Unity.exe",
    [int]$TimeoutSec = 1200,
    [switch]$Yes,
    [switch]$WithWebGL
)

$ErrorActionPreference = "Stop"

$Root    = Split-Path $PSScriptRoot -Parent
$Project = Join-Path $Root "VoxelCraft"
$LogsDir = Join-Path $Root "_logs"

$MsBuildCandidates = @(
    "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
)

# --- output helpers ----------------------------------------------------------

function Info([string]$m) { Write-Host $m }
function Ok([string]$m)   { Write-Host $m -ForegroundColor Green }
function Warn([string]$m) { Write-Host $m -ForegroundColor Yellow }
function Fail([string]$m) { Write-Host $m -ForegroundColor Red }

function StampLog([string]$name) {
    New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null
    return (Join-Path $LogsDir ("{0}_{1}.log" -f $name, (Get-Date -Format "yyyyMMdd-HHmmss")))
}

function UsageExit {
    Get-Help $PSCommandPath -Detailed 2>$null | Out-String | Write-Host
    exit 2
}

# --- shared checks -----------------------------------------------------------

function Test-ProjectLocked {
    return (Test-Path (Join-Path $Project "Temp\UnityLockfile"))
}

function Get-CsprojDrift {
    # Compares .cs files on disk with <Compile Include> entries in the Unity
    # generated csproj files. If a file was added without opening Unity, the
    # csproj is stale and compile-fast would silently skip the new file.
    $main = Join-Path $Project "Assembly-CSharp.csproj"
    $edit = Join-Path $Project "Assembly-CSharp-Editor.csproj"
    if (-not (Test-Path $main) -or -not (Test-Path $edit)) { return $null }
    $inCsproj = (Select-String -Path $main, $edit -Pattern "<Compile Include").Count
    $onDisk   = @(Get-ChildItem (Join-Path $Project "Assets") -Recurse -Filter *.cs -ErrorAction SilentlyContinue).Count
    return [pscustomobject]@{ OnDisk = $onDisk; InCsproj = $inCsproj; Drift = ($onDisk - $inCsproj) }
}

function Get-UnityChildAlive([datetime]$since) {
    # True if a Unity process started at/after $since is still running.
    # Used to detect the known launcher quirk: the bootstrap process exits 0
    # while the real editor child keeps importing (AGENTS.md, 2026-09-13).
    foreach ($pr in @(Get-Process Unity -ErrorAction SilentlyContinue)) {
        try { if ($pr.StartTime -ge $since.AddSeconds(-2)) { return $true } }
        catch { return $true }  # StartTime inaccessible - assume alive
    }
    return $false
}

# --- log parsing -------------------------------------------------------------

$ErrPatterns = @(
    "error CS\d+",
    "Shader error",
    "Aborting batchmode",
    "It looks like another Unity instance",
    "scripts have compiler errors"
)
$GoodMarkers = @("SELFTEST PASS", "WEBGL BUILD OK", "WINDOWS BUILD OK", "MODEL SNAPSHOT OK")
$BadMarkers  = @("SELFTEST FAIL", "WEBGL BUILD FAIL", "WINDOWS BUILD FAIL", "SNAPSHOT FAIL")

function Get-LogSummary([string]$LogPath) {
    $s = [pscustomobject]@{
        Errors       = @()   # lines matching $ErrPatterns
        Good         = @()   # lines matching $GoodMarkers
        Bad          = @()   # lines matching $BadMarkers
        InstanceBusy = $false
        Lines        = 0
    }
    if (-not (Test-Path $LogPath)) { return $s }
    foreach ($line in @(Get-Content $LogPath -ErrorAction SilentlyContinue)) {
        $s.Lines++
        foreach ($p in $ErrPatterns) {
            if ($line -match $p) {
                $s.Errors += $line.Trim()
                if ($p -like "*another Unity instance*") { $s.InstanceBusy = $true }
            }
        }
        foreach ($g in $GoodMarkers) { if ($line -match $g) { $s.Good += $line.Trim() } }
        foreach ($b in $BadMarkers)  { if ($line -match $b) { $s.Bad  += $line.Trim() } }
    }
    return $s
}

function Show-Summary($s) {
    if ($s.Errors.Count -gt 0) {
        Fail ("  error lines: {0}" -f $s.Errors.Count)
        $s.Errors | Select-Object -First 12 | ForEach-Object { Fail ("    " + $_) }
        if ($s.Errors.Count -gt 12) { Fail ("    ... and {0} more" -f ($s.Errors.Count - 12)) }
    }
    if ($s.Bad.Count -gt 0)  { $s.Bad  | Select-Object -First 10 | ForEach-Object { Fail ("    " + $_) } }
    if ($s.Good.Count -gt 0) { $s.Good | Select-Object -First 10 | ForEach-Object { Ok   ("    " + $_) } }
}

# --- unity batch runner ------------------------------------------------------

function Invoke-UnityBatch {
    param([string]$Method, [string]$LogPath)

    if (-not (Test-Path $UnityExe)) { throw "Unity editor not found: $UnityExe (pass -UnityExe)" }
    $lock = Join-Path $Project "Temp\UnityLockfile"

    for ($attempt = 1; $attempt -le 8; $attempt++) {
        if ($attempt -gt 1) {
            $sleep = 15 + (Get-Random -Minimum 0 -Maximum 11)
            Info ("unity: attempt {0} died without a log (lost the instance race) - retrying in {1}s" -f ($attempt - 1), $sleep)
            Start-Sleep -Seconds $sleep
        }

        # Queue behind any Unity touching the project: an open editor, a live
        # batch run (lockfile held), or a bootstrap in its first seconds (process
        # alive but lock not yet taken). Launch only on a truly idle instant.
        $lockWait = [Math]::Min($TimeoutSec, 300)
        $waited = 0
        while ($waited -lt $lockWait) {
            $procs = @(Get-Process Unity -ErrorAction SilentlyContinue).Count
            if (-not (Test-Path $lock)) {
                if ($procs -eq 0) { break }
                if ($waited -eq 0) { Info "a Unity process is spinning up - waiting for it to take or clear the project lock..." }
            }
            elseif ($procs -eq 0) {
                Warn "lockfile present but no Unity process alive - treating as stale and proceeding"
                break
            }
            elseif ($waited -eq 0) { Warn "project is locked by another Unity instance - waiting for it to release..." }
            Start-Sleep -Seconds 5
            $waited += 5
        }
        if (Test-Path $lock) {
            Fail "lock still held after ${lockWait}s - another Unity/session keeps the project open. Rerun later."
            return $null
        }

        Remove-Item $LogPath -Force -ErrorAction SilentlyContinue
        $uargs = "-batchmode -quit -projectPath `"$Project`" -logFile `"$LogPath`""
        if ($Method) { $uargs += " -executeMethod $Method" }

        Info ("unity: batchmode launch{0} (log: {1})" -f ($(if ($Method) { " $Method" } else { "" })), $LogPath)
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $launchTime = Get-Date
        $p = Start-Process -FilePath $UnityExe -ArgumentList $uargs -PassThru
        $null = $p.Handle  # acquire handle now or ExitCode reads back empty later (PS bug)
        while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt $TimeoutSec) { Start-Sleep -Milliseconds 1500 }

        if (-not $p.HasExited) {
            throw ("Unity still running after {0}s - giving up. Check and kill manually; log: {1}" -f $TimeoutSec, $LogPath)
        }
        $exitCode = $p.ExitCode

        # Startup died without writing any log: the bootstrap lost the "another
        # Unity instance" race (exit code 140063) or waited on the lock until it
        # gave up. Sampling lock/processes right now is unreliable (the rival
        # runs in bursts with short gaps) - ALWAYS retry up to 8 attempts.
        if (-not (Test-Path $LogPath)) {
            Warn ("unity died after {0:mm\:ss} (exit code {1}) and wrote no log - instance race with another Unity session" -f $sw.Elapsed, $exitCode)
            continue
        }

        # Launcher-exited-early quirk: our log exists but the bootstrap already
        # exited - keep waiting while a child editor spawned at launch time is
        # alive and its log is still growing (max 30s of silence).
        if (Get-UnityChildAlive $launchTime) {
            Warn "unity: bootstrap exited early but a child editor is alive (known quirk); waiting for it to finish..."
            $lastSize = -1; $lastGrowth = Get-Date
            while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
                if (-not (Get-UnityChildAlive $launchTime)) { break }
                $size = 0
                if (Test-Path $LogPath) { $size = (Get-Item $LogPath).Length }
                if ($size -ne $lastSize) { $lastSize = $size; $lastGrowth = Get-Date }
                elseif (((Get-Date) - $lastGrowth).TotalSeconds -gt 30) { break }
                Start-Sleep -Seconds 3
            }
        }

        $summary = Get-LogSummary $LogPath
        return [pscustomobject]@{
            ExitCode = $exitCode
            LogPath  = $LogPath
            Summary  = $summary
            Elapsed  = $sw.Elapsed
        }
    }
    return $null
}

# --- commands ----------------------------------------------------------------

function Cmd-Status {
    Info "== unity-cli status =="
    Info ("project      : {0}" -f $Project)
    Info ("unity exe    : {0} (exists: {1})" -f $UnityExe, (Test-Path $UnityExe))
    $verFile = Join-Path $Project "ProjectSettings\ProjectVersion.txt"
    if (Test-Path $verFile) { Info ("unity version: {0}" -f ((Get-Content $verFile | Select-Object -First 1) -replace "m_EditorVersion:\s*", "")) }
    Info ("license      : {0}" -f (Test-Path "C:\ProgramData\Unity\Unity_lic.ulf"))

    $procs = @(Get-Process Unity -ErrorAction SilentlyContinue)
    Info ("unity procs  : {0}" -f $procs.Count)
    if (Test-ProjectLocked) { Warn "project lock : OPEN in an editor (Temp\UnityLockfile) - batch commands will queue and retry" }
    else { Info "project lock : free" }

    $ms = $MsBuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    Info ("msbuild      : {0}" -f $(if ($ms) { $ms } else { "NOT FOUND - compile-fast unavailable, use compile" }))

    $d = Get-CsprojDrift
    if ($null -eq $d) { Warn "csproj       : missing (open Unity once to generate project files)" }
    elseif ($d.Drift -eq 0) { Info ("csproj drift : none ({0} files on disk = {1} in csproj)" -f $d.OnDisk, $d.InCsproj) }
    else { Warn ("csproj drift : {0} .cs on disk vs {1} in csproj - compile-fast is NOT authoritative; run compile" -f $d.OnDisk, $d.InCsproj) }

    foreach ($b in @("Builds\WebGL\index.html", "Builds\Windows\VoxelCraft.exe", "Builds\voxelcraft-webgl.zip")) {
        $f = Join-Path $Project $b
        if (Test-Path $f) { Info ("last build   : {0}  ({1})" -f $b, (Get-Item $f).LastWriteTime) }
    }

    $latest = Get-ChildItem $LogsDir -Filter *.log -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 5
    if ($latest) {
        Info "recent logs  :"
        $latest | ForEach-Object { Info ("    {0}  {1,8:N0}B  {2}" -f $_.Name, $_.Length, $_.LastWriteTime) }
    }
}

function Invoke-FastCompile {
    $ms = $MsBuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $ms) { throw "MSBuild not found. Run 'compile' (full Unity batchmode) instead." }

    $main = Join-Path $Project "Assembly-CSharp.csproj"
    $edit = Join-Path $Project "Assembly-CSharp-Editor.csproj"
    if (-not (Test-Path $edit)) { throw "missing Assembly-CSharp-Editor.csproj - open Unity once to generate project files" }

    $d = Get-CsprojDrift
    if ($null -ne $d -and $d.Drift -ne 0) {
        Warn ("csproj drift: {0} files on disk vs {1} in csproj - NEW files are NOT covered by this check." -f $d.OnDisk, $d.InCsproj)
        Warn "run 'compile' for the authoritative verdict after adding files."
    }

    $out = StampLog "msbuild"
    $err = "$out.err"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $ms -ArgumentList (
        "`"$edit`" /p:Configuration=Debug /p:Platform=AnyCPU /p:OutputPath=obj\vxfast\ /v:minimal /nologo"
    ) -WorkingDirectory $Project -RedirectStandardOutput $out -RedirectStandardError $err -PassThru -NoNewWindow
    $null = $p.Handle  # acquire handle now or ExitCode reads back empty later (PS bug)
    while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt 180) { Start-Sleep -Milliseconds 500 }
    if (-not $p.HasExited) { throw "MSBuild timed out after 180s" }

    # Judge by log content first; exit code is secondary (may be lost, see above).
    $outLines = @(Get-Content $out, $err -ErrorAction SilentlyContinue)
    $errLines = @($outLines | Where-Object { $_ -match ":\s*error\b" })
    $failed   = @($outLines | Where-Object { $_ -match "Build FAILED" })
    $built    = @($outLines | Where-Object { $_ -match "->\s*.+\.dll" })
    $exit     = $p.ExitCode
    $elapsed  = [int]$sw.Elapsed.TotalSeconds

    if ($errLines.Count -gt 0 -or $failed.Count -gt 0) {
        Fail ("compile-fast FAIL ({0}s) - {1} error line(s) - log: {2}" -f $elapsed, $errLines.Count, $out)
        $errLines | Select-Object -First 20 | ForEach-Object { Fail "  $_" }
        return $false
    }
    if ($exit -eq 0 -or ($null -eq $exit -and $built.Count -ge 1)) {
        Ok ("compile-fast PASS in {0}s ({1} assemblies) - log: {2}" -f $elapsed, [Math]::Max($built.Count, 1), $out)
        return $true
    }
    Fail ("compile-fast FAIL ({0}s) - msbuild exit code {1} with no error lines - log: {2}" -f $elapsed, $exit, $out)
    return $false
}

function Cmd-Compile {
    $log = StampLog "compile"
    $r = Invoke-UnityBatch -Method $null -LogPath $log
    if ($null -eq $r) { Fail "compile FAIL: could not start Unity batch (see above)"; return $false }
    Info ("unity exited with code {0} in {1:mm\:ss}" -f $r.ExitCode, $r.Elapsed)
    Show-Summary $r.Summary
    if ($r.Summary.InstanceBusy) { Fail "compile FAIL: another Unity instance blocks batchmode (close it, or kill stray Unity.exe)."; return $false }
    if ($r.Summary.Errors.Count -gt 0) { Fail "compile FAIL"; return $false }
    if ($r.ExitCode -ne 0) { Fail ("compile FAIL: unity exit code {0} but log shows no error lines - inspect the log." -f $r.ExitCode); return $false }
    Ok "compile PASS"
    return $true
}

function Cmd-Test {
    $log = StampLog "selftest"
    $r = Invoke-UnityBatch -Method "VoxelCraft.Editor.SelfTest.RunAll" -LogPath $log
    if ($null -eq $r) { Fail "test FAIL: could not start Unity batch (see above)"; return $false }
    Info ("unity exited with code {0} in {1:mm\:ss}" -f $r.ExitCode, $r.Elapsed)
    Show-Summary $r.Summary
    if ($r.Summary.InstanceBusy) { Fail "test FAIL: another Unity instance blocks batchmode."; return $false }
    if ($r.Summary.Bad.Count -gt 0)  { Fail "test FAIL (see SELFTEST FAIL lines above) - log: $log"; return $false }
    if ($r.Summary.Errors.Count -gt 0) { Fail "test FAIL: compile errors - log: $log"; return $false }
    if ($r.Summary.Good.Count -eq 0) { Fail "test FAIL: no SELFTEST PASS marker found - log: $log"; return $false }
    Ok "test PASS (SelfTest.RunAll) - log: $log"
    return $true
}

function Cmd-Build([string]$Target) {
    $target = $Target.ToLower()
    $method = $null; $marker = $null; $artifact = $null; $name = $null
    switch ($target) {
        "webgl"  { $method = "VoxelCraft.Editor.WebGLBuild.Build";   $marker = "WEBGL BUILD OK";    $artifact = "Builds\WebGL\index.html";       $name = "webgl" }
        "win"    { $method = "VoxelCraft.Editor.WindowsBuild.Build"; $marker = "WINDOWS BUILD OK";  $artifact = "Builds\Windows\VoxelCraft.exe"; $name = "win" }
        "windows" { return Cmd-Build "win" }
        default  { Fail "build target must be 'webgl' or 'win' (got '$Target')"; return $false }
    }
    $log = StampLog "build_$name"
    $r = Invoke-UnityBatch -Method $method -LogPath $log
    if ($null -eq $r) { Fail "build $name FAIL: could not start Unity batch (see above)"; return $false }
    Info ("unity exited with code {0} in {1:mm\:ss}" -f $r.ExitCode, $r.Elapsed)
    Show-Summary $r.Summary
    $artifactOk = (Test-Path (Join-Path $Project $artifact))
    if ($r.Summary.Good | Where-Object { $_ -match $marker }) {
        if ($artifactOk) { Ok "build $name PASS - artifact: $artifact (log: $log)"; return $true }
        Fail "build $name SUSPICIOUS: marker OK but artifact missing: $artifact"; return $false
    }
    Fail "build $name FAIL - log: $log"
    return $false
}

function Cmd-Snapshot {
    $log = StampLog "snapshot"
    $r = Invoke-UnityBatch -Method "VoxelCraft.Editor.ModelSnapshot.Run" -LogPath $log
    if ($null -eq $r) { Fail "snapshot FAIL: could not start Unity batch (see above)"; return $false }
    Info ("unity exited with code {0} in {1:mm\:ss}" -f $r.ExitCode, $r.Elapsed)
    Show-Summary $r.Summary
    if ($r.Summary.Good | Where-Object { $_ -match "MODEL SNAPSHOT OK" }) { Ok "snapshot PASS - PNGs in _logs (log: $log)"; return $true }
    Fail "snapshot FAIL - log: $log"
    return $false
}

function Cmd-Publish {
    $msg = ($Rest -join " ")
    $script = Join-Path $PSScriptRoot "publish-webgl.ps1"
    if (-not (Test-Path $script)) { Fail "missing $script"; return $false }
    Info "publish: delegating to Tools\publish-webgl.ps1 (build -> commit -> push)"
    $okp = $false
    try {
        if ($msg) { & $script -Message $msg } else { & $script }
        $okp = ($LASTEXITCODE -eq 0)
    } catch {
        Fail "publish failed: $_"
    }
    return $okp
}

function Cmd-Log([string]$Path) {
    if (-not $Path) {
        $newest = Get-ChildItem $LogsDir -Filter *.log -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $newest) { Fail "no logs found in _logs"; return $false }
        $Path = $newest.FullName
    }
    if (-not (Test-Path $Path)) { Fail "log not found: $Path"; return $false }
    Info "== log summary: $Path =="
    $s = Get-LogSummary $Path
    Info ("lines: {0}" -f $s.Lines)
    Show-Summary $s
    if ($s.InstanceBusy) { Fail "verdict: BLOCKED by another Unity instance" }
    elseif ($s.Errors.Count -gt 0) { Fail "verdict: FAIL (errors above)" }
    elseif ($s.Bad.Count -gt 0) { Fail "verdict: FAIL (bad markers above)" }
    elseif ($s.Good.Count -gt 0) { Ok ("verdict: PASS ({0} good marker(s))" -f $s.Good.Count) }
    else { Warn "verdict: no verdict markers found (plain log)" }
    return $true
}

function Cmd-Clean {
    if (Test-ProjectLocked) { Fail "refusing: project is open in Unity (Temp\UnityLockfile). Close the editor first."; return $false }
    $targets = @("Library", "Temp", "obj", "Logs") | ForEach-Object { Join-Path $Project $_ } | Where-Object { Test-Path $_ }
    if ($targets.Count -eq 0) { Ok "nothing to clean"; return $true }
    $bytes = 0
    foreach ($t in $targets) {
        $bytes += (Get-ChildItem $t -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    }
    Info ("will delete ({0:N1} GB, next Unity open re-imports 1-3 min):" -f ($bytes / 1GB))
    $targets | ForEach-Object { Info "    $_" }
    if (-not $Yes) {
        $answer = Read-Host "type yes to continue"
        if ($answer -ne "yes") { Warn "aborted"; return $true }
    }
    foreach ($t in $targets) { Remove-Item $t -Recurse -Force }
    Ok "clean done"
    return $true
}

function Cmd-Ci {
    $total = [System.Diagnostics.Stopwatch]::StartNew()
    $d = Get-CsprojDrift
    if ($null -eq $d) { Fail "ci FAIL: csproj files missing - open Unity once to generate them."; return $false }

    Info "== [1/2] compile gate =="
    if ($d.Drift -ne 0) {
        Warn "csproj drifted - using full Unity compile instead of compile-fast"
        if (-not (Cmd-Compile)) { return $false }
    }
    else {
        if (-not (Invoke-FastCompile)) { return $false }
    }

    Info "== [2/2] selftest gate =="
    if (-not (Cmd-Test)) { return $false }

    if ($WithWebGL) {
        Info "== [extra] webgl build =="
        if (-not (Cmd-Build "webgl")) { return $false }
    }

    Ok ("ci PASS - total {0:mm\:ss}" -f $total.Elapsed)
    return $true
}

# --- dispatch ----------------------------------------------------------------

switch ($Command.ToLower()) {
    "status"       { $ok = $true; Cmd-Status }
    "compile-fast" { $ok = Invoke-FastCompile }
    "compile"      { $ok = Cmd-Compile }
    "test"         { $ok = Cmd-Test }
    "build"        { if ($Rest.Count -lt 1) { Fail "usage: build webgl|win"; exit 2 } ; $ok = Cmd-Build $Rest[0] }
    "publish"      { $ok = Cmd-Publish }
    "snapshot"     { $ok = Cmd-Snapshot }
    "log"          { $ok = Cmd-Log $(if ($Rest.Count -ge 1) { $Rest[0] } else { $null }) }
    "clean"        { $ok = Cmd-Clean }
    "ci"           { $ok = Cmd-Ci }
    "help"         { $ok = $true; Get-Help $PSCommandPath | Out-String | Write-Host }
    default        { Fail "unknown command: $Command"; $ok = $false; exit 2 }
}

if ($ok) { exit 0 } else { exit 1 }
