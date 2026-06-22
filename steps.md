# Steps

## Compilation

- compilation Anonymizer.Cli.csproj -> anonymizer(.exe)
- téléchargement de UV
- création fichier compressé (7z) avec scripts/*.py et tools/uv(.exe)
- création fichier setup(.exe) en concaténant anonymizer(.exe) et le fichier compressé + magic

## Installation

- télécharge setup(.exe)
- exécute `setup` (commande `install` ou sans commande -> appel commande `install` en arrière plan ?) qui :
  - copie le fichier compressé dans un répertoire temporaire
  - l'extrait dans le répertoire d'installation
  - crée anonymizer(.exe) sans le fichier compressé dans le répertoire d'installation
  - ajoute le fichier de metadata (version, date, etc.)
  - lance le téléchargement des modèles (`anonymizer(.exe) download`) dans le répertoire d'installation
