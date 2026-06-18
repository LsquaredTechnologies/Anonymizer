#!/bin/bash
set -e

TARGET_DIR="${1:-"./src/Cli/tools"}"

UV_URL_LINUX="https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-unknown-linux-gnu.tar.gz"
UV_URL_WIN="https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip"

TAR_PATH="uv_linux.tar.gz"
ZIP_PATH="uv_windows.zip"
EXTRACT_LINUX="uv_linux_temp"
EXTRACT_WIN="uv_windows_temp"

echo "Creating target directory: $TARGET_DIR..."
mkdir -p "$TARGET_DIR"

echo "Downloading uv (Linux)..."
curl -L "$UV_URL_LINUX" -o "$TAR_PATH"

echo "Downloading uv.exe (Windows)..."
curl -L "$UV_URL_WIN" -o "$ZIP_PATH"

echo "Extracting Linux archive..."
mkdir -p "$EXTRACT_LINUX"
tar -xzf "$TAR_PATH" -C "$EXTRACT_LINUX"

echo "Extracting Windows archive..."
mkdir -p "$EXTRACT_WIN"
unzip -q "$ZIP_PATH" -d "$EXTRACT_WIN"

echo "Copying uv (Linux) to $TARGET_DIR/uv..."
cp "$EXTRACT_LINUX/uv-x86_64-unknown-linux-gnu/uv" "$TARGET_DIR/uv"
chmod +x "$TARGET_DIR/uv"

echo "Searching for uv.exe in extracted Windows files..."
UV_EXE_PATH=$(find "$EXTRACT_WIN" -type f -name "uv.exe" | head -n 1)

if [ -z "$UV_EXE_PATH" ]; then
    echo "ERROR: uv.exe not found in extracted ZIP!"
    exit 1
fi

echo "Found uv.exe at: $UV_EXE_PATH"
cp "$UV_EXE_PATH" "$TARGET_DIR/uv.exe"

echo "Cleaning up..."
rm -f "$TAR_PATH" "$ZIP_PATH"
rm -rf "$EXTRACT_LINUX" "$EXTRACT_WIN"

echo "Done! uv and uv.exe are ready in '$TARGET_DIR'."
