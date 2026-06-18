param(
    [string]$Version = "latest"
)

$ErrorActionPreference = "Stop"

$Owner = "LsquaredTechnologies"
$Repo  = "Anonymizer"

Write-Host "Detecting OS..."
if ($PSVersionTable.OS -match "Linux") {
    $OS = "linux"
    $Suffix = ""
    $InstallDir = "$HOME/.local/share/anonymizer"
    $BinaryName = "setup"
}
else {
    $OS = "windows"
    $Suffix = ".exe"
    $InstallDir = Join-Path $env:LOCALAPPDATA "anonymizer"
    $BinaryName = "setup.exe"
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

$AnonCmd = Join-Path $InstallDir ("anonymizer" + $Suffix)

$running = Get-Process | Where-Object { $_.ProcessName -like "anonymizer*" } 2>$null
if ($running) {
    Write-Host "Stopping running instance..."
    $running | Stop-Process -Force
}

Write-Host "Running installer..."
& $FinalSetup install

Write-Host "Downloading models..."
& $FinalSetup download

Write-Host "Launching Anonymizer..."
Start-Process $AnonCmd -ArgumentList "start" -WindowStyle Hidden
