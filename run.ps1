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
$bin = Join-Path $PSScriptRoot 'DwgBatchPdf\bin\Release'
$pluginDll = Join-Path $bin 'DwgBatchPdf.dll'
$sourceFiles = @(
  (Join-Path $PSScriptRoot 'DwgBatchPdf\Plugin.cs'),
  (Join-Path $PSScriptRoot 'DwgBatchPdf\DwgBatchPdf.csproj')
)
$mustBuild = -not $SkipBuild -or -not (Test-Path -LiteralPath $pluginDll)
if (-not $mustBuild) {
  $dllTime = (Get-Item -LiteralPath $pluginDll).LastWriteTimeUtc
  $mustBuild = [bool]($sourceFiles | Where-Object {
    (Test-Path -LiteralPath $_) -and (Get-Item -LiteralPath $_).LastWriteTimeUtc -gt $dllTime
  } | Select-Object -First 1)
  if ($mustBuild) {
    Write-Warning 'SkipBuild was ignored because the source code is newer than DwgBatchPdf.dll.'
  }
}
if ($mustBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -AcadDir $AcadDir }
if (-not (Test-Path -LiteralPath $pluginDll)) {
  throw "Plugin DLL was not built: $pluginDll"
}

# AutoCAD 2018 NETLOAD is unreliable with non-ASCII paths. Stage runtime in TEMP.
$runtimeName = 'DwgBatchPdfRuntime_' + [Guid]::NewGuid().ToString('N')
$runtime = Join-Path $env:TEMP $runtimeName
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
Copy-Item $pluginDll (Join-Path $runtime 'DwgBatchPdf.dll') -Force
Copy-Item (Join-Path $bin 'DwgBatchPdf.pdb') (Join-Path $runtime 'DwgBatchPdf.pdb') -Force
$runtimeLog = Join-Path $runtime 'batch.log'
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
$seed = if ($ExactFilePath) { [IO.Path]::GetFullPath($ExactFilePath) } else { Get-ChildItem -LiteralPath $InputRoot -Recurse -File -Filter $FileNameFilter | Select-Object -First 1 -ExpandProperty FullName }
if (-not $seed) { throw "No DWG found under: $InputRoot" }
if (-not (Test-Path -LiteralPath $seed -PathType Leaf)) { throw "DWG file does not exist: $seed" }
if ([IO.Path]::GetExtension($seed) -ine '.dwg') { throw "Input file is not a DWG: $seed" }
if (-not (Test-Path -LiteralPath $seed -PathType Leaf)) {
  throw "DWG file does not exist. Replace -ExactFilePath with a real .dwg path: $seed"
}
if ([IO.Path]::GetExtension($seed) -ne '.dwg') {
  throw "ExactFilePath must point to a .dwg file: $seed"
}
$quotedSeed = '"' + $seed + '"'
$quotedScript = '"' + (Join-Path $runtime 'run.scr') + '"'
$completed = $false
$coreExit = 1
$timedOut = $false
for ($attempt = 1; $attempt -le 2 -and -not $completed -and -not $timedOut; $attempt++) {
  Remove-Item $runtimeLog,$consoleOut,$consoleErr -Force -ErrorAction SilentlyContinue
  $core = Start-Process -FilePath (Join-Path $AcadDir 'accoreconsole.exe') `
    -ArgumentList @('/i', $quotedSeed, '/s', $quotedScript, '/l', 'en-US') `
    -RedirectStandardOutput $consoleOut -RedirectStandardError $consoleErr `
    -PassThru -WindowStyle Hidden
  $deadline = (Get-Date).AddMinutes($MaxMinutes)
  while (-not $core.HasExited -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $core.Refresh()
    if (Test-Path $runtimeLog) {
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
$workspaceLog = Join-Path $bin 'batch.log'
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
  "TIMEOUT after $MaxMinutes minutes: $seed" | Add-Content -LiteralPath $workspaceLog -Encoding UTF8
}
if ($coreExit -ne 0) { throw "accoreconsole failed with exit code $coreExit. See $workspaceLog" }
if (-not (Test-Path $runtimeLog)) { throw "Plugin did not run; batch.log was not created." }
Write-Host "Done. Output: $OutputRoot"
Write-Host "Log: $workspaceLog"
