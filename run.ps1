param(
  [string]$InputRoot = $PSScriptRoot,
  [string]$OutputRoot = (Join-Path $PSScriptRoot 'PDF_Output'),
  [string]$AcadDir = 'D:\CAD 2018\AutoCAD 2018',
  [string]$FileNameFilter = '*.dwg',
  [string]$ExactFilePath = '',
  [double]$MinFrameWidth = 100.0,
  [double]$MinFrameHeight = 100.0,
  [double]$MinFrameArea = 0.0,
  [double]$MaxFrameArea = 0.0,
  [ValidateSet('BlockOnly','BlockFirst','ContourFirst')]
  [string]$DetectionMode = 'BlockOnly',
  [ValidateSet(0,1)]
  [int]$DebugFrames = 0,
  [int]$MaxMinutes = 15,
  [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'

foreach ($pathArgument in @(
  @{ Name = 'InputRoot'; Value = $InputRoot },
  @{ Name = 'OutputRoot'; Value = $OutputRoot }
)) {
  $value = [string]$pathArgument.Value
  if ([string]::IsNullOrWhiteSpace($value) -or
      $value.IndexOfAny([IO.Path]::GetInvalidPathChars()) -ge 0 -or
      $value.Contains("`r") -or $value.Contains("`n")) {
    throw "$($pathArgument.Name) contains an illegal or embedded newline character. Use run-22.ps1 or assign the path from Get-Item."
  }
}

# Fail before creating a runtime directory when a path was damaged while being
# pasted into the console.  In particular, a wrapped Chinese path can contain a
# real CR/LF even though it looks like harmless terminal line wrapping.  Never
# silently turn that into an empty ExactFilePath and process the first DWG.
if (-not [string]::IsNullOrWhiteSpace($ExactFilePath)) {
  if ($ExactFilePath.IndexOfAny([IO.Path]::GetInvalidPathChars()) -ge 0 -or
      $ExactFilePath.Contains("`r") -or $ExactFilePath.Contains("`n")) {
    throw 'ExactFilePath contains an illegal or embedded newline character. Use -FileNameFilter with a narrower InputRoot, or assign the path from Get-ChildItem.'
  }
  $ExactFilePath = [IO.Path]::GetFullPath($ExactFilePath.Trim())
  if (-not (Test-Path -LiteralPath $ExactFilePath -PathType Leaf)) {
    throw "ExactFilePath does not exist: $ExactFilePath"
  }
  if ([IO.Path]::GetExtension($ExactFilePath) -ine '.dwg') {
    throw "ExactFilePath must point to a .dwg file: $ExactFilePath"
  }
}

# A force-closed host can leave an inaccessible accoreconsole.exe behind. Some
# installations keep such a process as a harmless shell, and new core-console
# jobs can still run beside it. Report it once but never block every DWG merely
# because Windows refuses to terminate that unrelated process.
Get-CimInstance Win32_Process -Filter "Name='accoreconsole.exe'" -ErrorAction SilentlyContinue |
  ForEach-Object {
    $parent = Get-Process -Id $_.ParentProcessId -ErrorAction SilentlyContinue
    $created = if ($_.CreationDate -is [datetime]) {
      [datetime]$_.CreationDate
    } else {
      [Management.ManagementDateTimeConverter]::ToDateTime([string]$_.CreationDate)
    }
    $isOurStaleRuntime = -not $parent -and
      $_.CommandLine -like '*DwgBatchPdfRuntime_*' -and
      $created -lt (Get-Date).AddMinutes(-30)
    if ($isOurStaleRuntime) {
      Write-Warning "Stopping stale orphaned DWG-PDF core console PID $($_.ProcessId), started $created."
      Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    } elseif (-not $parent) {
      Write-Warning "Ignoring unrelated/recent orphaned accoreconsole.exe PID $($_.ProcessId); starting a new task."
    }
  }

$bin = Join-Path $PSScriptRoot 'DwgBatchPdf\bin\Release'
$frozenPluginDir = Join-Path $PSScriptRoot 'DwgBatchPdf\frozen-20260922-1408'
$pluginDll = Join-Path $frozenPluginDir 'DwgBatchPdf.dll'
# This workspace is intentionally pinned to the exact converter binary that was
# used successfully at 14:08 on 2026-09-22.  Do not rebuild implicitly: later
# source experiments must never replace the requested production version.
if (-not (Test-Path -LiteralPath $pluginDll)) {
  throw "Frozen 14:08 plugin DLL is missing: $pluginDll"
}

# AutoCAD 2018 NETLOAD is unreliable with non-ASCII paths. Stage runtime in TEMP.
$runtimeName = 'DwgBatchPdfRuntime_' + [Guid]::NewGuid().ToString('N')
$runtime = Join-Path $env:TEMP $runtimeName
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
Copy-Item $pluginDll (Join-Path $runtime 'DwgBatchPdf.dll') -Force
Copy-Item (Join-Path $frozenPluginDir 'DwgBatchPdf.pdb') (Join-Path $runtime 'DwgBatchPdf.pdb') -Force
$runtimeFonts = Join-Path $runtime 'Fonts'
New-Item -ItemType Directory -Force -Path $runtimeFonts | Out-Null
# Legacy SHX/BigFont text is decoded while AutoCAD opens the DWG, before the
# managed plugin can inspect or replace a text style. Stage only genuine project
# fonts in an isolated support directory. Never rename a different BigFont as a
# substitute: custom SHX character maps are not interchangeable and false aliases
# can silently remove every annotation that uses them.
$projectFonts = Join-Path $PSScriptRoot 'Fonts'
if (Test-Path -LiteralPath $projectFonts) {
  Get-ChildItem -LiteralPath $projectFonts -File -ErrorAction SilentlyContinue |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $runtimeFonts -Force }
}
# Start-Process inherits this only inside the current powershell.exe invocation;
# it does not alter the user's permanent AutoCAD profile.
$env:ACAD = if ([string]::IsNullOrWhiteSpace($env:ACAD)) {
  $runtimeFonts
} else {
  $runtimeFonts + ';' + $env:ACAD
}
$runtimeLog = Join-Path $runtime 'batch.log'
$workspaceLog = Join-Path $bin 'batch.log'
$consoleOut = Join-Path $runtime 'accoreconsole.out.log'
$consoleErr = Join-Path $runtime 'accoreconsole.err.log'
Remove-Item $runtimeLog -Force -ErrorAction SilentlyContinue

$job = [ordered]@{
  InputRoot = [IO.Path]::GetFullPath($InputRoot)
  OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
  IncludeModel = $true
  Recurse = $true
  Overwrite = $true
  MinFrameWidth = $MinFrameWidth
  MinFrameHeight = $MinFrameHeight
  MinFrameArea = $MinFrameArea
  MaxFrameArea = $MaxFrameArea
  DetectionMode = $DetectionMode
  DebugFrames = [bool]$DebugFrames
  FileNameFilter = $FileNameFilter
  ExactFilePath = $ExactFilePath
}
$job | ConvertTo-Json | Set-Content (Join-Path $runtime 'batchjob.json') -Encoding UTF8
$script = @"
FILEDIA
0
CMDDIA
0
SECURELOAD
0
NETLOAD
"$runtime\DwgBatchPdf.dll"
BATCHDWGTOPDF
QUIT
"@
$script | Set-Content (Join-Path $runtime 'run.scr') -Encoding ASCII
$seed = if ($ExactFilePath) { $ExactFilePath } else { Get-ChildItem -LiteralPath $InputRoot -Recurse -File -Filter $FileNameFilter | Select-Object -First 1 -ExpandProperty FullName }
if (-not $seed) { throw "No DWG found under: $InputRoot" }
if (-not (Test-Path -LiteralPath $seed -PathType Leaf)) { throw "DWG file does not exist: $seed" }
if ([IO.Path]::GetExtension($seed) -ine '.dwg') { throw "Input file is not a DWG: $seed" }
if (-not (Test-Path -LiteralPath $seed -PathType Leaf)) {
  throw "DWG file does not exist. Replace -ExactFilePath with a real .dwg path: $seed"
}
if ([IO.Path]::GetExtension($seed) -ne '.dwg') {
  throw "ExactFilePath must point to a .dwg file: $seed"
}
# AutoCAD 2018 core console terminates natively when its current document is the
# bundled IRD.dwt and the plugin creates the first side Database. Use the target
# DWG as the host document; this is the stable path for this installation. The
# plugin still uses side databases when processing a different document.
$quotedSeed = '"' + $seed + '"'
$quotedScript = '"' + (Join-Path $runtime 'run.scr') + '"'
$runStartedUtc = [DateTime]::UtcNow
$completed = $false
$coreExit = 1
$timedOut = $false
$lastReportedProgress = ''
for ($attempt = 1; $attempt -le 2 -and -not $completed -and -not $timedOut; $attempt++) {
  Remove-Item $runtimeLog,$consoleOut,$consoleErr -Force -ErrorAction SilentlyContinue
  $core = Start-Process -FilePath (Join-Path $AcadDir 'accoreconsole.exe') `
    -ArgumentList @('/i', $quotedSeed, '/s', $quotedScript, '/l', 'en-US') `
    -RedirectStandardOutput $consoleOut -RedirectStandardError $consoleErr `
    -PassThru -WindowStyle Hidden
  # MaxMinutes is an inactivity timeout, not a total-file timeout. Large DWGs
  # can legitimately need more than five minutes when every detected sheet is
  # plotted. Refresh the deadline whenever the plugin trace/log or console
  # output advances; only a process making no observable progress is killed.
  $progressFiles = @(
    (Join-Path $runtime 'trace.log'),
    $runtimeLog,
    $consoleOut,
    $consoleErr
  )
  $lastProgressStamp = [DateTime]::MinValue
  $deadline = (Get-Date).AddMinutes($MaxMinutes)
  while (-not $core.HasExited -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $core.Refresh()
    $progressStamp = $progressFiles | Where-Object { Test-Path -LiteralPath $_ } |
      ForEach-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } |
      Sort-Object -Descending | Select-Object -First 1
    if ($progressStamp -and $progressStamp -gt $lastProgressStamp) {
      $lastProgressStamp = $progressStamp
      $deadline = (Get-Date).AddMinutes($MaxMinutes)
    }
    if (Test-Path $runtimeLog) {
      # Mirror the live plugin log to the stable workspace path. Atomic PDF
      # publishing intentionally leaves the final output directory unchanged
      # until every sheet succeeds, so this log is the visible progress source.
      try { Copy-Item -LiteralPath $runtimeLog -Destination $workspaceLog -Force } catch { }
      $progressLine = Get-Content -LiteralPath $runtimeLog -Tail 40 -ErrorAction SilentlyContinue |
        Where-Object { $_ -match '^PLOT (START|COMPLETE) \[' } |
        Select-Object -Last 1
      if ($progressLine -and $progressLine -ne $lastReportedProgress) {
        Write-Host $progressLine
        $lastReportedProgress = $progressLine
      }
      $completed = [bool](Select-String -LiteralPath $runtimeLog -Pattern '^Finished:' -Quiet)
      if ($completed) {
        Stop-Process -Id $core.Id -Force -ErrorAction SilentlyContinue
        [void]$core.WaitForExit(10000)
        break
      }
    }
  }
  if (-not $completed -and (Test-Path $runtimeLog)) {
    $completed = [bool](Select-String -LiteralPath $runtimeLog -Pattern '^Finished:' -Quiet)
  }
  if (-not $core.HasExited) {
    Stop-Process -Id $core.Id -Force -ErrorAction SilentlyContinue
    [void]$core.WaitForExit(10000)
    $timedOut = $true
  }
  $coreExit = if ($completed) { 0 } elseif ($timedOut) { 124 } elseif ($core.HasExited) { $core.ExitCode } else { 1 }
  if (-not $completed -and -not $timedOut -and $attempt -lt 2) {
    Write-Warning "accoreconsole attempt $attempt failed with exit code $coreExit; retrying once."
    Start-Sleep -Seconds 3
  }
}
if (Test-Path $runtimeLog) {
  Copy-Item $runtimeLog $workspaceLog -Force
} else {
  @(
    "accoreconsole did not start the plugin. Exit code: $coreExit"
    "DWG: $seed"
    "Runtime: $runtime"
    "--- stdout ---"
    (Get-Content -LiteralPath $consoleOut -Raw -ErrorAction SilentlyContinue)
    "--- stderr ---"
    (Get-Content -LiteralPath $consoleErr -Raw -ErrorAction SilentlyContinue)
  ) | Set-Content -LiteralPath $workspaceLog -Encoding UTF8
}
if ($timedOut) {
  "TIMEOUT after $MaxMinutes minutes without progress: $seed" | Add-Content -LiteralPath $workspaceLog -Encoding UTF8
}
# A completed AutoCAD command can still report failed DWGs in its own log.
# Treat that as a non-zero run so run-all.ps1 records and can retry the file;
# previously files with zero generated PDFs were incorrectly counted as OK.
if ($coreExit -eq 0 -and (Test-Path -LiteralPath $runtimeLog)) {
  $pluginFailed = Select-String -LiteralPath $runtimeLog -Pattern 'failed DWGs=[1-9][0-9]*' -Quiet
  if ($pluginFailed) { $coreExit = 2 }
}
if ($coreExit -ne 0) { throw "accoreconsole failed with exit code $coreExit. See $workspaceLog" }
if (-not (Test-Path $runtimeLog)) { throw "Plugin did not run; batch.log was not created." }
# Final conservative blank-output guard. Only inspect PDFs written by this
# invocation, so old files elsewhere under the shared output root are never
# removed. 23 KB means exactly 23 * 1024 bytes.
$minimumPdfBytes = 23KB
$newPdfCutoffUtc = $runStartedUtc.AddSeconds(-5)
$removedSmallPdfCount = 0
Get-ChildItem -LiteralPath $OutputRoot -Recurse -File -Filter '*.pdf' -ErrorAction SilentlyContinue |
  Where-Object { $_.LastWriteTimeUtc -ge $newPdfCutoffUtc -and $_.Length -lt $minimumPdfBytes } |
  ForEach-Object {
    $smallPdfPath = $_.FullName
    $smallPdfBytes = $_.Length
    try {
      Remove-Item -LiteralPath $smallPdfPath -Force -ErrorAction Stop
      $removedSmallPdfCount++
      "REMOVE SMALL PDF bytes=$smallPdfBytes threshold=$minimumPdfBytes path=$smallPdfPath" |
        Add-Content -LiteralPath $workspaceLog -Encoding UTF8
      Write-Warning "Removed PDF smaller than 23 KB: $smallPdfPath ($smallPdfBytes bytes)"
    } catch {
      "FAILED TO REMOVE SMALL PDF bytes=$smallPdfBytes path=$smallPdfPath error=$($_.Exception.Message)" |
        Add-Content -LiteralPath $workspaceLog -Encoding UTF8
      Write-Warning "Could not remove small PDF: $smallPdfPath"
    }
  }
"SMALL PDF FILTER threshold=$minimumPdfBytes removed=$removedSmallPdfCount" |
  Add-Content -LiteralPath $workspaceLog -Encoding UTF8
Write-Host "Done. Output: $OutputRoot"
Write-Host "Log: $workspaceLog"
