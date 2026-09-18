$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskSource = Join-Path $taskRoot 'design/icons/adbdesk-icon-concept-v1.png'
$taskAssets = Join-Path $taskRoot 'src/AdbDesk.App/Assets'
New-Item -ItemType Directory -Path $taskAssets -Force | Out-Null
Copy-Item -LiteralPath $taskSource -Destination (Join-Path $taskAssets 'AdbDesk.png')
$taskImage = [System.Drawing.Image]::FromFile($taskSource)
try {
    $taskSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $taskFrames = @()
    foreach ($taskSize in $taskSizes) {
        $taskBitmap = [System.Drawing.Bitmap]::new($taskSize, $taskSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $taskGraphics = [System.Drawing.Graphics]::FromImage($taskBitmap)
        $taskStream = [System.IO.MemoryStream]::new()
        try {
            $taskGraphics.Clear([System.Drawing.Color]::Transparent)
            $taskGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $taskGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $taskGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $taskGraphics.DrawImage($taskImage, [System.Drawing.Rectangle]::new(0, 0, $taskSize, $taskSize))
            $taskBitmap.Save($taskStream, [System.Drawing.Imaging.ImageFormat]::Png)
            $taskFrames += ,$taskStream.ToArray()
        } finally {
            $taskStream.Dispose()
            $taskGraphics.Dispose()
            $taskBitmap.Dispose()
        }
    }
    $taskFile = [System.IO.File]::Create((Join-Path $taskAssets 'AdbDesk.ico'))
    $taskWriter = [System.IO.BinaryWriter]::new($taskFile)
    try {
        $taskWriter.Write([uint16]0)
        $taskWriter.Write([uint16]1)
        $taskWriter.Write([uint16]$taskSizes.Count)
        $taskOffset = 6 + 16 * $taskSizes.Count
        for ($taskIndex = 0; $taskIndex -lt $taskSizes.Count; $taskIndex++) {
            $taskDimension = if ($taskSizes[$taskIndex] -eq 256) { 0 } else { $taskSizes[$taskIndex] }
            $taskWriter.Write([byte]$taskDimension)
            $taskWriter.Write([byte]$taskDimension)
            $taskWriter.Write([byte]0)
            $taskWriter.Write([byte]0)
            $taskWriter.Write([uint16]1)
            $taskWriter.Write([uint16]32)
            $taskWriter.Write([uint32]$taskFrames[$taskIndex].Length)
            $taskWriter.Write([uint32]$taskOffset)
            $taskOffset += $taskFrames[$taskIndex].Length
        }
        foreach ($taskFrame in $taskFrames) { $taskWriter.Write([byte[]]$taskFrame) }
    } finally { $taskWriter.Dispose() }
} finally { $taskImage.Dispose() }
Write-Output 'Generated Assets/AdbDesk.ico (16 through 256 pixels) and copied approved PNG.'
