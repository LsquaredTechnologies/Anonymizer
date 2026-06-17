#!/usr/bin/env bash
set -euo pipefail

OWNER="LsquaredTechnologies"
REPO="Anonymizer"

UNAME="$(uname -s | tr '[:upper:]' '[:lower:]')"

case "$UNAME" in
    linux*)
        OS="linux"
        SUFFIX="-linux"
        INSTALL_DIR="$HOME/.local/share/anonymizer"
        ;;
    mingw*|msys*|cygwin*)
        OS="windows"
        SUFFIX="-win"
        INSTALL_DIR="$LOCALAPPDATA/anonymizer"
        ;;
    *)
        echo "Operating system not supported: $UNAME"
        exit 1
        ;;
esac

ASSET="setup${SUFFIX}.zip"
TMP_DIR="$(mktemp -d)"

echo "Downloading latest release for $OS..."
curl -sL "https://github.com/${OWNER}/${REPO}/releases/latest/download/${ASSET}" \
    -o "${TMP_DIR}/setup.zip"

echo "Extracting..."
mkdir -p "${INSTALL_DIR}"
unzip -q "${TMP_DIR}/setup.zip" -d "${INSTALL_DIR}"

cd "${INSTALL_DIR}"

if [[ "$OS" == "windows" ]]; then
    SETUP="./anonymizer.exe"
else
    chmod +x anonymizer || true
    SETUP="./anonymizer"
fi

echo "Downloading models..."
$SETUP download

echo "Running installer..."
$SETUP install

echo "Launching application..."
$SETUP &

echo "Installation complete."
