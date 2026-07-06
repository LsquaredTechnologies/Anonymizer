param(
    [string]$Version = "latest"
)

$ErrorActionPreference = "Stop"

$Owner = "LsquaredTechnologies"
$Repo  = "Anonymizer"

Write-Host "Detecting OS..."
if ($PSVersionTable.OS -match "Linux") {
    $OS = "linux"
    $InstallDir = "$HOME/.local/share/anonymizer"
    $BinaryName = "setup"
    $AnonName = Join-Path $InstallDir "anonymizer"
}
else {
    $OS = "windows"
    $InstallDir = Join-Path $env:LOCALAPPDATA "anonymizer"
    $BinaryName = "setup.exe"
    $AnonName = Join-Path $InstallDir "anonymizerw.exe"
}

if ($Version -eq "latest") {
    $Url = "https://github.com/$Owner/$Repo/releases/latest/download/$BinaryName"
} else {
    $Url = "https://github.com/$Owner/$Repo/releases/download/$Version/$BinaryName"
}

$Temp = Join-Path ([IO.Path]::GetTempPath()) ("anonymizer_" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $Temp | Out-Null

$SetupPath = Join-Path $Temp $BinaryName

Write-Host "Downloading Anonymizer installer for $OS..."
try {
    Invoke-WebRequest -Uri $Url -OutFile $SetupPath -UseBasicParsing
}
catch {
    Write-Host "Failed to download installer."
    Write-Host " URL: $Url"
    Write-Host " Expected binary: $BinaryName"
    throw
}

Write-Host "Installing into $InstallDir..."
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

$FinalSetup = Join-Path $InstallDir $BinaryName
Copy-Item $SetupPath $FinalSetup -Force

if ($OS -eq "linux") {
    chmod +x $FinalSetup
}

$running = Get-Process | Where-Object { $_.ProcessName -like "anonymizer*" } 2>$null
if ($running) {
    Write-Host "Stopping running instance..."
    $running | Stop-Process -Force
}

Write-Host "Running installer..."
& $FinalSetup install

Write-Host "Launching Anonymizer..."
if ($OS -eq "windows") {
    Start-Process "cmd.exe" -ArgumentList "/c start `"`" /b anonymizerw"
}
else {
    chmod +x $AnonName
    Start-Process $AnonName -ArgumentList "start" -WindowStyle Hidden
}
