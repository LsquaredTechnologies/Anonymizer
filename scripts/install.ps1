$ErrorActionPreference = "Stop"

$Owner = "LsquaredTechnologies"
$Repo  = "Anonymizer"

if ($PSVersionTable.OS -match "Linux") {
    $OS = "linux"
    $Suffix = "-linux"
    $InstallDir = "$HOME/.local/share/anonymizer"
}
else {
    $OS = "windows"
    $Suffix = "-win"
    $InstallDir = Join-Path $env:LOCALAPPDATA "anonymizer"
}

$Asset = "setup$Suffix.zip"
$TempDir = New-Item -ItemType Directory -Path ([IO.Path]::GetTempPath()) -Name ("anonymizer_" + [guid]::NewGuid())
$ZipPath = Join-Path $TempDir "setup.zip"

Write-Host "Downloading latest release for $OS..."
$Url = "https://github.com/$Owner/$Repo/releases/latest/download/$Asset"
echo "Invoke-WebRequest -Uri $Url -OutFile $ZipPath"
Invoke-WebRequest -Uri $Url -OutFile $ZipPath

Write-Host "Extracting..."
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Expand-Archive $ZipPath $InstallDir -Force

Set-Location $InstallDir

if ($OS -eq "windows") {
    $SetupCmd = ".\anonymizer.exe"
}
else {
    chmod +x anonymizer | Out-Null
    $SetupCmd = "./anonymizer"
}

Write-Host "Downloading models..."
& $SetupCmd download

Write-Host "Running installer..."
& $SetupCmd install

Write-Host "Installation complete."
