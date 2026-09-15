param(
  [string]$InputRoot = (Join-Path $PSScriptRoot 'RCJM1   MEP图纸汇总---20260617'),
  [string]$OutputRoot = (Join-Path $PSScriptRoot 'PDF输出_按原目录'),
  [double]$MinFrameArea = 0.0,
  [double]$MaxFrameArea = 0.0,
  [ValidateSet('BlockOnly','BlockFirst','ContourFirst')]
  [string]$DetectionMode = 'BlockOnly',
  [ValidateSet(0,1)]
  [int]$DebugFrames = 0,
  [switch]$IncludeRecoveryFiles
)
$ErrorActionPreference = 'Continue'
$allDwgFiles = @(Get-ChildItem -LiteralPath $InputRoot -Recurse -File -Filter '*.dwg' | Sort-Object FullName)
$recoveryPattern = '(?i)(^|[_\-.])(recover(ed)?\d*|autosave|temp|tmp|bak)(?=$|[_\-.])'
$skippedFiles = @()
if ($IncludeRecoveryFiles) {
  $files = $allDwgFiles
} else {
  $skippedFiles = @($allDwgFiles | Where-Object { $_.BaseName -match $recoveryPattern -or $_.Name -like '~$*' })
  $files = @($allDwgFiles | Where-Object { $_.BaseName -notmatch $recoveryPattern -and $_.Name -notlike '~$*' })
}
$log = Join-Path $PSScriptRoot 'batch-all.log'
"Started: $(Get-Date -Format s); DWGs=$($files.Count); skipped recovery/temp=$($skippedFiles.Count)" | Set-Content -LiteralPath $log -Encoding UTF8
foreach ($skipped in $skippedFiles) {
  "SKIPPED RECOVERY/TEMP: $($skipped.FullName)" | Add-Content -LiteralPath $log
}
$ok = 0
$failed = 0
for ($i = 0; $i -lt $files.Count; $i++) {
  $f = $files[$i]
  $progressLine = "[$($i+1)/$($files.Count)] $($f.FullName)"
  Write-Host $progressLine
  $progressLine | Add-Content -LiteralPath $log
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'run.ps1') -InputRoot $InputRoot -OutputRoot $OutputRoot -ExactFilePath $f.FullName -MinFrameArea $MinFrameArea -MaxFrameArea $MaxFrameArea -DetectionMode $DetectionMode -DebugFrames $DebugFrames -SkipBuild
  if ($LASTEXITCODE -eq 0) { $ok++ } else { $failed++; "FAILED: $($f.FullName) exit=$LASTEXITCODE" | Add-Content -LiteralPath $log }
  Get-Process accoreconsole -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 2
}
"Finished: $(Get-Date -Format s); OK=$ok; Failed=$failed" | Add-Content -LiteralPath $log
if ($failed -gt 0) { exit 1 }
