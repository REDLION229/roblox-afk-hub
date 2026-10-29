# Builds AFK Hub into .\bin\ using the Roslyn compiler from NuGet (no .NET SDK or Visual Studio needed).
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root  = $PSScriptRoot
$tools = Join-Path $root '.tools'
$out   = Join-Path $root 'bin'
$src   = Join-Path $root 'src'

function Get-NuGet($id, $dir) {
    if (Test-Path (Join-Path $tools $dir)) { return }
    New-Item -ItemType Directory -Force $tools | Out-Null
    $versions = (Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$id/index.json").versions | Where-Object { $_ -notmatch '-' }
    $v = $versions[-1]
    Write-Host "Downloading $id $v"
    $zip = Join-Path $tools "$dir.zip"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$id/$v/$id.$v.nupkg" -OutFile $zip -UseBasicParsing
    Expand-Archive $zip (Join-Path $tools $dir) -Force
    Remove-Item $zip
}
Get-NuGet 'microsoft.net.compilers.toolset' 'roslyn'
Get-NuGet 'microsoft.web.webview2' 'webview2'

# ---- app icon (PNG-in-ICO): a tilted silver Roblox-style square with a crescent moon and "Zz" ----
$ico = Join-Path $src 'app.ico'
if (-not (Test-Path $ico)) {
    Add-Type -AssemblyName System.Drawing
    $sizes = 256, 64, 48, 32, 24, 16
    $pngs = foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
        $small = $s -lt 32

        # Roblox-style tilted square with a square hole
        $side = $s * ($(if ($small) { 0.80 } else { 0.66 })); $hole = $side * 0.30
        $cx = $s * ($(if ($small) { 0.5 } else { 0.44 })); $cy = $s * ($(if ($small) { 0.5 } else { 0.56 }))
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.FillMode = 'Alternate'
        $path.AddRectangle((New-Object System.Drawing.RectangleF (-$side / 2), (-$side / 2), $side, $side))
        $path.AddRectangle((New-Object System.Drawing.RectangleF (-$hole / 2), (-$hole / 2), $hole, $hole))
        $m = New-Object System.Drawing.Drawing2D.Matrix
        $m.Rotate(-15); $m.Translate($cx, $cy, 'Append')
        $path.Transform($m)
        $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
        $silver = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(248, 250, 253)), ([System.Drawing.Color]::FromArgb(128, 140, 160)), 70
        if (-not $small) { $shadow = New-Object System.Drawing.Drawing2D.Matrix; $shadow.Translate($s * 0.02, $s * 0.03); $sp = $path.Clone(); $sp.Transform($shadow); $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(90, 0, 0, 0))), $sp) }
        $g.FillPath($silver, $path)
        $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 40, 46, 58)), ([Math]::Max(1, $s / 64))), $path)

        if (-not $small) {
            # crescent moon, top right
            $md = $s * 0.36; $mx = $s * 0.60; $my = $s * 0.04
            $outer = New-Object System.Drawing.Drawing2D.GraphicsPath; $outer.AddEllipse([float]$mx, [float]$my, [float]$md, [float]$md)
            $cut = New-Object System.Drawing.Drawing2D.GraphicsPath; $cut.AddEllipse([float]($mx + $md * 0.34), [float]($my - $md * 0.14), [float]$md, [float]$md)
            $moon = New-Object System.Drawing.Region $outer
            $moon.Exclude($cut)
            $g.FillRegion((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(170, 150, 255))), $moon)
            if ($s -ge 48) {
                # "Zz"
                $f1 = New-Object System.Drawing.Font 'Segoe UI', ([float]($s * 0.16)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
                $f2 = New-Object System.Drawing.Font 'Segoe UI', ([float]($s * 0.11)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
                $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 125, 250))
                $g.DrawString('Z', $f1, $white, [float]($s * 0.78), [float]($s * 0.34))
                $g.DrawString('z', $f2, $white, [float]($s * 0.89), [float]($s * 0.27))
            }
        }
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        , $ms.ToArray()
    }
    $fs = [System.IO.File]::Create($ico); $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    $bw.Close()
    # Also keep a PNG of the big icon for previews.
    [System.IO.File]::WriteAllBytes((Join-Path $src 'app-256.png'), $pngs[0])
}

# ---- compile ----
$csc = Get-ChildItem (Join-Path $tools 'roslyn\tasks\net472') -Filter csc.exe | Select-Object -First 1 -ExpandProperty FullName
$fw  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wv  = Join-Path $tools 'webview2\lib\net462'
$refs = 'System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll',
        'System.Security.dll','System.Web.dll','System.Web.Extensions.dll','System.Management.dll','Microsoft.CSharp.dll','System.IO.Compression.dll' |
        ForEach-Object { "/r:`"$fw\$_`"" }
$refs += "/r:`"$wv\Microsoft.Web.WebView2.Core.dll`"", "/r:`"$wv\Microsoft.Web.WebView2.WinForms.dll`""

# Start from an empty bin: the result is a single "Roblox AFK Hub.exe".
# (AfkHub-data, the portable settings/macros folder, is never touched.)
if (Test-Path $out) { Get-ChildItem $out -Force | Where-Object Name -ne 'AfkHub-data' | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $out | Out-Null

# Embedded resources (paths relative to the project root, because csc splits /resource on commas):
#   lib/*.dll    managed WebView2 libraries, loaded from memory   (Embedded.ResolveAssembly)
#   native/*.dll WebView2Loader.dll, unpacked at first run         (Embedded.Unpack)
#   ui/*         the HTML interface, unpacked at first run
Push-Location $root
$resources = @(
    "/resource:.tools\webview2\lib\net462\Microsoft.Web.WebView2.Core.dll,lib/Microsoft.Web.WebView2.Core.dll",
    "/resource:.tools\webview2\lib\net462\Microsoft.Web.WebView2.WinForms.dll,lib/Microsoft.Web.WebView2.WinForms.dll",
    "/resource:.tools\webview2\runtimes\win-x64\native\WebView2Loader.dll,native/WebView2Loader.dll"
) + (Get-ChildItem (Join-Path $src 'ui') -File -Recurse | ForEach-Object {
    $rel = $_.FullName.Substring($root.Length + 1)
    "/resource:$rel,ui/" + $_.FullName.Substring((Join-Path $src 'ui').Length + 1).Replace('\', '/')
})

# A response file keeps paths with commas/spaces intact (csc splits /r: lists on commas).
$rsp = Join-Path $env:TEMP 'afkhub-build.rsp'
$lines = @('/nologo', '/target:winexe', '/platform:x64', '/langversion:latest', '/optimize+', '/nullable:disable', '/warn:4',
           "/out:`"$out\Roblox AFK Hub.exe`"", "/win32icon:`"$ico`"", "/win32manifest:`"$src\app.manifest`"") + $refs + $resources +
         (Get-ChildItem $src -Filter *.cs | ForEach-Object { "`"$($_.FullName)`"" })
[System.IO.File]::WriteAllLines($rsp, $lines)
& $csc "@$rsp"
$code = $LASTEXITCODE
Remove-Item $rsp
Pop-Location
if ($code -ne 0) { throw "Build failed." }

$size = [math]::Round((Get-Item "$out\Roblox AFK Hub.exe").Length / 1MB, 1)
Write-Host "Built $out\Roblox AFK Hub.exe ($size MB, single portable file)"
