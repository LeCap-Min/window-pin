param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '未找到 .NET Framework C# 编译器。请安装 .NET Framework 4.8。' }

$assetDir = Join-Path $projectRoot 'assets'
New-Item -ItemType Directory -Force -Path $assetDir | Out-Null
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 64,64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::FromArgb(37,99,235))
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White),4
$graphics.DrawRectangle($pen,12,15,40,35)
$graphics.DrawLine($pen,12,25,52,25)
$brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$points = [System.Drawing.Point[]]@([System.Drawing.Point]::new(32,28),[System.Drawing.Point]::new(22,39),[System.Drawing.Point]::new(28,39),[System.Drawing.Point]::new(28,46),[System.Drawing.Point]::new(36,46),[System.Drawing.Point]::new(36,39),[System.Drawing.Point]::new(42,39))
$graphics.FillPolygon($brush,$points)
$png = New-Object System.IO.MemoryStream
$bitmap.Save($png,[System.Drawing.Imaging.ImageFormat]::Png)
$iconPath = Join-Path $assetDir 'window-pin.ico'
$stream = [System.IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter $stream
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]1)
$writer.Write([byte]64)
$writer.Write([byte]64)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([uint16]1)
$writer.Write([uint16]32)
$writer.Write([uint32]$png.Length)
$writer.Write([uint32]22)
$writer.Write($png.ToArray())
$writer.Dispose()
$png.Dispose()
$brush.Dispose()
$pen.Dispose()
$graphics.Dispose()
$bitmap.Dispose()

$exePath = Join-Path $projectRoot '窗口置顶.exe'
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 /warn:4 "/out:$exePath" "/win32icon:$iconPath" "/win32manifest:$(Join-Path $projectRoot 'src\app.manifest')" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $sourceFiles
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Get-Item -LiteralPath $exePath | Select-Object FullName,Length,LastWriteTime

if ($Test) {
    & (Join-Path $projectRoot 'tests\run-tests.ps1')
    if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
}
