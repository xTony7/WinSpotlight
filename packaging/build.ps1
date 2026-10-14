# Builds a self-contained WinSpotlight into dist\WinSpotlight (no Python or .NET needed on the target PC),
# then an installer dist\WinSpotlight-Setup.exe if Inno Setup is installed.
#   pip install pyinstaller -r requirements.txt ; powershell -File packaging\build.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist\WinSpotlight"
$work = Join-Path $root "build"
Remove-Item $dist, $work -Recurse -Force -ErrorAction SilentlyContinue

# 1. WinUI front end, self-contained
dotnet publish "$root\winui\WinSpotlight.csproj" -c Release -r win-x64 --self-contained -o $dist
if ($LASTEXITCODE) { throw "dotnet publish failed" }
# Unpackaged WinUI publish omits the compiled XAML (.xbf) and the app's .pri; without them the window fails to load.
$bin = "$root\winui\bin\Release\net10.0-windows10.0.22621.0\win-x64"
Get-ChildItem $bin -Recurse -Include *.xbf, WinSpotlight.pri | ForEach-Object {
    $dest = Join-Path $dist $_.FullName.Substring($bin.Length + 1)
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item $_.FullName $dest -Force
}

# 2. Python backend as a windowless exe (Qt is only for the legacy front end, so leave it out)
python -m PyInstaller "$PSScriptRoot\backend_entry.py" --name winspotlight-backend --noconsole --noconfirm `
    --paths $root --distpath "$work\py" --workpath "$work\tmp" --specpath $work `
    --exclude-module PySide6 --collect-all fastembed --collect-all onnxruntime --collect-all tokenizers `
    --collect-submodules winspotlight
if ($LASTEXITCODE) { throw "PyInstaller failed" }
Move-Item "$work\py\winspotlight-backend" "$dist\backend"

# 3. Installer
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc "/O$root\dist" "$PSScriptRoot\installer.iss"
} else {
    Write-Host "Inno Setup not found; skipping installer. dist\WinSpotlight\WinSpotlight.exe runs as-is."
}
