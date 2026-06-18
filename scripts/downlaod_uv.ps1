param(
    [string]$TargetDir = "./src/Cli/tools"
)

$UV_URL_LINUX = "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-unknown-linux-gnu.tar.gz"
$UV_URL_WIN   = "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip"

$TarPath      = "uv_linux.tar.gz"
$ZipPath      = "uv_windows.zip"
$ExtractLinux = "uv_linux_temp"
$ExtractWin   = "uv_windows_temp"

Write-Host "Creating target directory: $TargetDir..."
New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null

Write-Host "Downloading uv (Linux)..."
Invoke-WebRequest -Uri $UV_URL_LINUX -OutFile $TarPath

Write-Host "Downloading uv.exe (Windows)..."
Invoke-WebRequest -Uri $UV_URL_WIN -OutFile $ZipPath

Write-Host "Extracting Linux archive..."
New-Item -ItemType Directory -Force -Path $ExtractLinux | Out-Null
tar -xzf $TarPath -C $ExtractLinux

Write-Host "Extracting Windows archive (using .NET)..."
New-Item -ItemType Directory -Force -Path $ExtractWin | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $ExtractWin)
} catch {
    Write-Host "ZIP already extracted, continuing..."
}

Write-Host "Searching for uv.exe..."
$UvExe = Get-ChildItem -Path $ExtractWin -Recurse -Filter "uv.exe" | Select-Object -First 1

if (-not $UvExe) {
    Write-Host "`nERROR: uv.exe not found in extracted ZIP!" -ForegroundColor Red
    exit 1
}

Write-Host "Found uv.exe at: $($UvExe.FullName)"
Copy-Item $UvExe.FullName "$TargetDir/uv.exe" -Force

Write-Host "Copying uv (Linux)..."
Copy-Item "$ExtractLinux/uv-x86_64-unknown-linux-gnu/uv" "$TargetDir/uv" -Force

Write-Host "Cleaning up..."
Remove-Item $TarPath, $ZipPath -Force
Remove-Item $ExtractLinux, $ExtractWin -Recurse -Force

Write-Host "`nDone! uv and uv.exe are ready in '$TargetDir'."
