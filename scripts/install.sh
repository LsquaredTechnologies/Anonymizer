#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:-latest}"

OWNER="LsquaredTechnologies"
REPO="Anonymizer"

echo "🔍 Detecting OS..."
UNAME="$(uname -s | tr '[:upper:]' '[:lower:]')"
case "$UNAME" in
    linux*)
        OS="linux"
        INSTALL_DIR="${HOME}/.local/share/anonymizer"
        BINARY_NAME="setup"
        ANON_NAME="anonymizer"
        ;;
    mingw*|msys*|cygwin*)
        OS="windows"
        INSTALL_DIR="${LOCALAPPDATA}/anonymizer"
        BINARY_NAME="setup.exe"
        ANON_NAME="anonymizerw.exe"
        ;;
    *)
        echo "❌ Unsupported OS: $UNAME"
        exit 1
        ;;
esac

if [ "$VERSION" = "latest" ]; then
    URL="https://github.com/${OWNER}/${REPO}/releases/latest/download/${BINARY_NAME}"
else
    URL="https://github.com/${OWNER}/${REPO}/releases/download/${VERSION}/${BINARY_NAME}"
fi

TMP_DIR="$(mktemp -d)"
SETUP_PATH="${TMP_DIR}/${BINARY_NAME}"

echo "⬇️  Downloading Anonymizer installer..."
curl -fsSL "$URL" -o "$SETUP_PATH"

echo "📦 Installing into: $INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
cp "$SETUP_PATH" "$INSTALL_DIR/$BINARY_NAME"

if [ "$OS" = "linux" ]; then
    chmod +x "$INSTALL_DIR/$BINARY_NAME"
fi

SETUP="$INSTALL_DIR/$BINARY_NAME"
ANON="$INSTALL_DIR/$ANON_NAME"

if pgrep -f "anonymizer" >/dev/null 2>&1; then
    echo "🛑 Stopping running instance..."
    pkill -f "anonymizer" || true
fi

echo "⚙️  Running installer..."
"$SETUP" install

echo "🚀 Launching Anonymizer..."
if [ "$OS" = "windows" ]; then
    nohup "$ANON" >/dev/null 2>&1 &
else
    nohup "$ANON" start >/dev/null 2>&1 &
fi
