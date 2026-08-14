# proc-memlog.ps1 - process-level memory / handle logger
#
# Logs what ModMemoryProfiler cannot see: native memory, OS handles and GDI objects,
# for Beat Saber AND the VR runtime processes it depends on.
#
# A Mono-side profiler only covers the managed heap and Unity assets. Degradation that
# lives in the GPU driver, the OpenXR runtime or the streaming layer never shows up
# there. Monotonic growth in Handles / GDI is the classic signature of a native leak.
#
# NOTE: keep this file ASCII-only. PowerShell 5.1 reads scripts without a BOM using the
# system ANSI codepage, so non-ASCII comments can be mangled.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File proc-memlog.ps1
#   powershell -ExecutionPolicy Bypass -File proc-memlog.ps1 -IntervalSec 10

param(
    [int]$IntervalSec = 15,
    [string]$OutPath = ""
)

# Process names to watch. Missing ones are simply skipped, so the same list works
# for Quest Link, Virtual Desktop and SteamVR setups.
$targets = @(
    'Beat Saber',              # the game itself
    'OVRServer_x64',           # Oculus / Meta runtime - main suspect on Quest Link
    'OVRRedir',
    'OculusClient',
    'vrserver',                # SteamVR
    'vrcompositor',
    'VirtualDesktop.Streamer'  # Virtual Desktop
)

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
if ([string]::IsNullOrEmpty($OutPath)) {
    $OutPath = Join-Path $PSScriptRoot ("proc-memlog_{0}.csv" -f $stamp)
}
# GPU goes to its own file: different columns, one row per sample instead of per process.
$GpuPath = Join-Path (Split-Path $OutPath -Parent) ("gpu-memlog_{0}.csv" -f $stamp)

# System-wide memory. The single most important number when the machine is under
# pressure: if free memory runs out, Windows starts paging and everything gets slower
# regardless of which process is at fault. Watching only Beat Saber misses this.
$SysPath = Join-Path (Split-Path $OutPath -Parent) ("sys-memlog_{0}.csv" -f $stamp)

# Other applications competing for RAM. An editor or browser left open can easily
# hold several GB, which shortens how long a session runs before pressure sets in.
$TopPath = Join-Path (Split-Path $OutPath -Parent) ("apps-memlog_{0}.csv" -f $stamp)

$haveNvidiaSmi = [bool](Get-Command nvidia-smi -ErrorAction SilentlyContinue)

# Reads dedicated VRAM ("Local") and spilled-to-system-RAM ("Non Local") for one process.
# Non Local rising is the signature of VRAM exhaustion: the driver starts paging textures
# over PCIe, which is far slower and shows up as a gradual, time-based slowdown.
function Get-ProcessGpuMemoryMB([int]$ProcessId) {
    $local = 0.0
    $nonLocal = 0.0
    foreach ($pair in @(@('Local Usage', [ref]$local), @('Non Local Usage', [ref]$nonLocal))) {
        try {
            $samples = (Get-Counter ("\GPU Process Memory(*)\{0}" -f $pair[0]) -ErrorAction Stop).CounterSamples
            $sum = 0.0
            foreach ($s in $samples) {
                if ($s.InstanceName -like ("pid_{0}_*" -f $ProcessId)) { $sum += $s.CookedValue }
            }
            $pair[1].Value = [math]::Round($sum / 1MB, 1)
        }
        catch { }
    }
    return @($local, $nonLocal)
}

Add-Type -Name Gui -Namespace W32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern int GetGuiResources(IntPtr hProcess, int uiFlags);
'@

"elapsedMin,process,pid,workingSetMB,privateMB,virtualMB,handles,threads,gdiObjects,userObjects,gpuLocalMB,gpuNonLocalMB" |
    Out-File $OutPath -Encoding utf8

"elapsedMin,vramUsedMB,vramTotalMB,gpuTempC,smClockMHz,smClockMaxMHz,gpuUtilPct,throttleReasons" |
    Out-File $GpuPath -Encoding utf8

"elapsedMin,totalMB,freeMB,usedPct,committedMB,commitLimitMB,cacheMB" |
    Out-File $SysPath -Encoding utf8

"elapsedMin,process,instances,workingSetMB,privateMB" |
    Out-File $TopPath -Encoding utf8

Write-Host "Logging to $OutPath (interval ${IntervalSec}s). Ctrl+C to stop."
Write-Host "Waiting for Beat Saber..."
while (-not (Get-Process 'Beat Saber' -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 5 }

$start = Get-Date
Write-Host "Attached. Start browsing / playing."

while (Get-Process 'Beat Saber' -ErrorAction SilentlyContinue) {
    $mins = [math]::Round(((Get-Date) - $start).TotalMinutes, 2)

    foreach ($name in $targets) {
        $procs = Get-Process $name -ErrorAction SilentlyContinue
        if (-not $procs) { continue }

        foreach ($p in $procs) {
            try {
                $p.Refresh()
                # 0 = GDI objects, 1 = USER objects. Returns 0 if the handle is not accessible.
                $gdi = 0; $usr = 0
                try {
                    $gdi = [W32.Gui]::GetGuiResources($p.Handle, 0)
                    $usr = [W32.Gui]::GetGuiResources($p.Handle, 1)
                } catch { }

                # Per-process GPU memory is relatively expensive to sample, so only do it
                # for the processes that actually push work to the GPU.
                $gpuLocal = 0; $gpuNonLocal = 0
                if ($name -in @('Beat Saber', 'OVRServer_x64', 'VirtualDesktop.Streamer', 'vrserver', 'vrcompositor')) {
                    $g = Get-ProcessGpuMemoryMB $p.Id
                    $gpuLocal = $g[0]; $gpuNonLocal = $g[1]
                }

                $line = "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11}" -f $mins, $name, $p.Id,
                    [math]::Round($p.WorkingSet64 / 1MB, 1),
                    [math]::Round($p.PrivateMemorySize64 / 1MB, 1),
                    [math]::Round($p.VirtualMemorySize64 / 1MB, 1),
                    $p.HandleCount, $p.Threads.Count, $gdi, $usr,
                    $gpuLocal, $gpuNonLocal

                $line | Out-File $OutPath -Append -Encoding utf8
                # Echo the processes that carry the frame: the game and whichever
                # runtime/compositor is in the chain for this setup.
                if ($name -in @('Beat Saber', 'vrcompositor', 'VirtualDesktop.Streamer')) { Write-Host $line }
            }
            catch {
                # process exited between enumeration and read - ignore
            }
        }
    }

    # System-wide memory pressure.
    try {
        $os = Get-CimInstance Win32_OperatingSystem
        $totalMB  = [math]::Round($os.TotalVisibleMemorySize / 1KB, 0)
        $freeMB   = [math]::Round($os.FreePhysicalMemory / 1KB, 0)
        $commitMB = [math]::Round(($os.TotalVirtualMemorySize - $os.FreeVirtualMemory) / 1KB, 0)
        $limitMB  = [math]::Round($os.TotalVirtualMemorySize / 1KB, 0)
        $cacheMB  = [math]::Round($os.FreeSpaceInPagingFiles / 1KB, 0)
        $usedPct  = if ($totalMB -gt 0) { [math]::Round((1 - $freeMB / $totalMB) * 100, 1) } else { 0 }
        "{0},{1},{2},{3},{4},{5},{6}" -f $mins, $totalMB, $freeMB, $usedPct, $commitMB, $limitMB, $cacheMB |
            Out-File $SysPath -Append -Encoding utf8
    }
    catch { }

    # Top memory consumers other than the ones already tracked above. Grouped by name
    # because editors and browsers spawn many child processes.
    try {
        Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $targets -notcontains $_.ProcessName } |
            Group-Object ProcessName |
            ForEach-Object {
                [pscustomobject]@{
                    Name = $_.Name
                    N    = $_.Count
                    WS   = ($_.Group | Measure-Object WorkingSet64 -Sum).Sum
                    PV   = ($_.Group | Measure-Object PrivateMemorySize64 -Sum).Sum
                }
            } |
            Sort-Object WS -Descending | Select-Object -First 10 |
            ForEach-Object {
                "{0},{1},{2},{3},{4}" -f $mins, $_.Name, $_.N,
                    [math]::Round($_.WS / 1MB, 0), [math]::Round($_.PV / 1MB, 0) |
                    Out-File $TopPath -Append -Encoding utf8
            }
    }
    catch { }

    # Adapter-level GPU state. Covers both candidate causes in one sample:
    #   vramUsed climbing toward vramTotal  -> VRAM exhaustion / texture paging
    #   gpuTemp up + smClock down           -> thermal throttling
    # throttleReasons is a bitmask; 0x1 means "GPU idle", anything with 0x20/0x40/0x80
    # set indicates a real thermal or power cap.
    if ($haveNvidiaSmi) {
        try {
            $q = nvidia-smi --query-gpu=memory.used,memory.total,temperature.gpu,clocks.current.sm,clocks.max.sm,utilization.gpu --format=csv,noheader,nounits 2>$null
            $t = nvidia-smi --query-gpu=clocks_throttle_reasons.active --format=csv,noheader 2>$null
            if ($q) {
                $f = ($q -split ',') | ForEach-Object { $_.Trim() }
                "{0},{1},{2},{3},{4},{5},{6},{7}" -f $mins, $f[0], $f[1], $f[2], $f[3], $f[4], $f[5], ($t -join '').Trim() |
                    Out-File $GpuPath -Append -Encoding utf8
            }
        }
        catch { }
    }

    Start-Sleep -Seconds $IntervalSec
}

Write-Host "Beat Saber exited."
Write-Host "  process CSV: $OutPath"
Write-Host "  gpu CSV    : $GpuPath"
Write-Host "  system CSV : $SysPath"
Write-Host "  apps CSV   : $TopPath"
