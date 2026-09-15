param([string]$AcadDir = 'D:\CAD 2018\AutoCAD 2018')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'DwgBatchPdf\DwgBatchPdf.csproj'
$msbuild = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
& $msbuild $project /t:Rebuild /p:Configuration=Release "/p:AcadDir=$AcadDir" /nologo
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }
Write-Host "Built: $PSScriptRoot\DwgBatchPdf\bin\Release\DwgBatchPdf.dll"
