# 🔐 Anonymizer

Anonymizer is a cross‑platform background application designed to anonymize, normalize, and preprocess
data streams before they reach your application.

It runs as a lightweight daemon on Windows and Linux, with a unified installer and a consistent CLI.

## 🚀 Features

- Cross‑platform (Windows, Linux)
- Unified installer (Pwsh, Bash)
- Automatic OS detection
- Automatic background start
- Self‑contained setup bundles (setup.zip)
- Semantic Versioning powered by git-cliff
- GitHub Actions automated release pipeline
- Extensible architecture

## 📦 Installation

### Windows

PowerShell:

```pwsh
iwr -useb https://github.com/LsquaredTechnologies/Anonymizer/releases/latest/download/install.ps1 | iex
```

Git‑Bash / MSYS2 / Cygwin:

```bash
curl -sL https://github.com/LsquaredTechnologies/Anonymizer/releases/latest/download/install.sh | bash
```

The installer will automatically:

- Detect Windows or Linux (WSL)
- Download setup.exe
- Copy it into %LOCALAPPDATA%/anonymizer
- Run:

  ```pwsh
  setup.exe install
  ```

### Linux

Bash

```bash
curl -sL https://github.com/LsquaredTechnologies/Anonymizer/releases/latest/download/install.sh | bash
```

The installer will automatically:

- Detect Linux (including WSL)
- Download setup
- Copy it into ~/.local/share/anonymizer
- Run:

  ```shell
  ./setup install
  ```

## 🛠️ Debugging

### Run in foreground (debug mode)

```shell
anonymizer run <pdf-file-or-directory>
```

### View logs

Linux:

```shell
journalctl -u anonymizer -f
```

Windows:

```pwsh
Get-EventLog -LogName Application -Source Anonymizer
```

## 🔄 Updating

Simply re-run the installer:

```shell
curl -sL https://github.com/LsquaredTechnologies/Anonymizer/releases/latest/download/install.sh | bash
```

The installer will automatically:

- Download the latest setup binary
- Replace binaries
- Restart the background process

## ❌ Uninstalling

### Linux

```shell
setup uninstall
rm -rf ~/.local/share/anonymizer
```

### Windows

```pwsh
setup.exe uninstall
Remove-Item "$env:LOCALAPPDATA\anonymizer" -Recurse -Force
```

## 🧩 Architecture Overview

```text
├── scripts/
│   ├── download_uv.ps1
│   ├── download_uv.sh
│   ├── install.ps1
│   └── install.sh
└── src/
    ├── Anonymizer.Cli/
    └── Anonymizer.Win/
```

## 🧪 Development

### Build

```shell
dotnet build
```

### Run

```shell
dotnet run --project src/Cli -- run <pdf-file-or-directory>
```

### Run background watcher locally

```shell
dotnet run --project src/Win
```

## 🚀 Release Workflow

Releases are fully automated using GitHub Actions + git‑cliff.

When you merge a pull request into main, the pipeline triggers three main phases:

### Prepare

- Computes next SemVer using git-cliff
- Generates the changelog
- Creates & pushes the new Git tag

### Build

- Builds Windows + Linux binaries
- Packages setup.zip archives
- Copies installers

### Release

- Publishes the GitHub Release
- Uploads artifacts:

  ```text
  ├── install.ps1
  ├── install.sh
  ├── setup-linux.zip
  └── setup-win.zip
  ```

> [!TIP]
> 💡 No manual tagging. No manual changelog. No manual release.

## 🤝 Contributing

Contributions are welcome!

### Fork the repository

Create a feature branch:

```shell
git checkout -b feature/my-awesome-feature
```

Follow Conventional Commits. Examples:

```text
feat: add anonymization rule engine
```

```text
fix: correct service restart on Linux
```

```text
docs: improve installation guide
```

```text
refactor!: remove legacy pipeline (BREAKING)
```

Submit a Pull Request describing your changes clearly.

## 🐞 Reporting Issues

- If you encounter a bug:
- Open an issue on GitHub.
- Include logs if possible.
- Describe precise steps to reproduce the problem.

## 📄 License

This project is licensed under the MIT License.

## ⭐ Support the Project

If you find this project useful, consider starring the repository. It helps visibility and encourages
future development!
