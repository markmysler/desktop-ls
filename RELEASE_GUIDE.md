# Release Guide

This guide walks you through publishing a new release of DesktopLS to GitHub and winget.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) installed
- [GitHub CLI (`gh`)](https://cli.github.com/) installed and authenticated
- Repository pushed to GitHub at `https://github.com/markmysler/desktop-ls.git`

---

## Step 1: Build the Release Binary

1. **Clean previous builds:**
   ```powershell
   Remove-Item -Recurse -Force src\DesktopLS\bin\Release -ErrorAction SilentlyContinue
   ```

2. **Publish the self-contained executable:**
   ```powershell
   dotnet publish src/DesktopLS/DesktopLS.csproj -c Release
   ```

   **Note:** If `dotnet` is not in your PATH, use the full path:
   ```powershell
   & 'C:\Program Files\dotnet\dotnet.exe' publish src/DesktopLS/DesktopLS.csproj -c Release
   ```

3. **Verify the output:**
   The executable will be at:
   ```
   src\DesktopLS\bin\Release\net8.0-windows\win-x64\publish\DesktopLS.exe
   ```

4. **Test the release build:**
   ```powershell
   & ".\src\DesktopLS\bin\Release\net8.0-windows\win-x64\publish\DesktopLS.exe"
   ```
   Verify that it works correctly before proceeding.

---

## Step 2: Create a GitHub Release

### Option A: Using GitHub CLI (Recommended)

1. **Tag the release:**
   ```powershell
   git tag v1.0.0
   git push origin v1.0.0
   ```
   Replace `1.0.0` with your version number following [Semantic Versioning](https://semver.org/).

2. **Create the release with the binary:**
   ```powershell
   gh release create v1.0.0 `
     "src\DesktopLS\bin\Release\net8.0-windows\win-x64\publish\DesktopLS.exe" `
     --title "DesktopLS v1.0.0" `
     --notes "Release notes here..."
   ```

3. **Customize the release notes** with:
   - New features
   - Bug fixes
   - Breaking changes (if any)
   - System requirements

   Example:
   ```powershell
   gh release create v1.0.0 `
     "src\DesktopLS\bin\Release\net8.0-windows\win-x64\publish\DesktopLS.exe" `
     --title "DesktopLS v1.0.0" `
     --notes "## Features
   - Desktop folder redirection with icon position memory
   - Back/Forward/Up navigation with keyboard shortcuts
   - Path autocomplete with Tab cycling
   - Settings menu with run-on-startup toggle
   - Auto-hide when another window is maximized

   ## System Requirements
   - Windows 10 or Windows 11 (x64)
   - No additional software required"
   ```

### Option B: Using GitHub Web UI

1. **Go to:** https://github.com/markmysler/desktop-ls/releases/new

2. **Fill in the form:**
   - **Tag version:** `v1.0.0` (create new tag)
   - **Release title:** `DesktopLS v1.0.0`
   - **Description:** Write release notes (see example above)

3. **Upload the binary:**
   - Drag and drop `DesktopLS.exe` from:
     ```
     src\DesktopLS\bin\Release\net8.0-windows\win-x64\publish\DesktopLS.exe
     ```

4. **Click "Publish release"**

---

## Step 3: Get SHA256 Hash (for winget)

Get the SHA256 hash of your **local** release binary (you'll need this later for the winget manifest).

```powershell
$binaryPath = "src\DesktopLS\bin\Release\net8.0-windows\win-x64\publish\DesktopLS.exe"
$hash = (Get-FileHash $binaryPath -Algorithm SHA256).Hash
Write-Host "SHA256: $hash"
Write-Host ""
Write-Host "Save this hash for later!"
```

**Important:** Copy this hash somewhere - you'll need it when creating the winget manifest in Step 4.

---

## Step 4: Publish to winget

### First-Time Setup

1. **Fork the winget-pkgs repository:**
   ```
   https://github.com/microsoft/winget-pkgs
   ```
   Click "Fork" in the top-right.

2. **Clone your fork:**
   ```powershell
   git clone https://github.com/YOUR_USERNAME/winget-pkgs
   cd winget-pkgs
   ```

3. **Add upstream remote:**
   ```powershell
   git remote add upstream https://github.com/microsoft/winget-pkgs
   ```

### Creating the Manifest

1. **Create the package directory:**
   ```powershell
   mkdir manifests\m\MarkMysler\DesktopLS\1.0.0
   cd manifests\m\MarkMysler\DesktopLS\1.0.0
   ```

2. **Create the version manifest** (`MarkMysler.DesktopLS.yaml`):
   ```yaml
   # Created using wingetcreate
   PackageIdentifier: MarkMysler.DesktopLS
   PackageVersion: 1.0.0
   DefaultLocale: en-US
   ManifestType: version
   ManifestVersion: 1.6.0
   ```

3. **Create the installer manifest** (`MarkMysler.DesktopLS.installer.yaml`):
   ```yaml
   # Created using wingetcreate
   PackageIdentifier: MarkMysler.DesktopLS
   PackageVersion: 1.0.0
   InstallerType: portable
   Commands:
     - DesktopLS
   Installers:
     - Architecture: x64
       InstallerUrl: https://github.com/markmysler/desktop-ls/releases/download/v1.0.0/DesktopLS.exe
       InstallerSha256: YOUR_SHA256_HASH_HERE
   ManifestType: installer
   ManifestVersion: 1.6.0
   ```
   **Replace `YOUR_SHA256_HASH_HERE`** with the hash from Step 3.

4. **Create the locale manifest** (`MarkMysler.DesktopLS.locale.en-US.yaml`):
   ```yaml
   # Created using wingetcreate
   PackageIdentifier: MarkMysler.DesktopLS
   PackageVersion: 1.0.0
   PackageLocale: en-US
   Publisher: Mark Mysler
   PackageName: DesktopLS
   License: MIT
   LicenseUrl: https://github.com/markmysler/desktop-ls/blob/main/LICENSE
   ShortDescription: Lightweight Windows desktop navigation bar for browsing folders
   Description: |-
     Point your desktop at any folder on disk — browse, navigate back and forward,
     and switch between folders just like a file manager, all while keeping your
     icons arranged exactly where you left them.
   Tags:
     - desktop
     - file-manager
     - navigation
     - windows
     - productivity
   ManifestType: defaultLocale
   ManifestVersion: 1.6.0
   ```

### Submitting to winget

1. **Navigate back to repository root:**
   ```powershell
   cd ..\..\..\..\..\
   ```

2. **Create a new branch:**
   ```powershell
   git checkout -b DesktopLS-1.0.0
   ```

3. **Stage and commit the manifests:**
   ```powershell
   git add manifests/m/MarkMysler/DesktopLS/1.0.0/
   git commit -m "Add MarkMysler.DesktopLS version 1.0.0"
   ```

4. **Push to your fork:**
   ```powershell
   git push origin DesktopLS-1.0.0
   ```

5. **Create a Pull Request:**
   - Go to: https://github.com/YOUR_USERNAME/winget-pkgs
   - Click "Compare & pull request"
   - Title: `Add MarkMysler.DesktopLS version 1.0.0`
   - Description: Brief description of the package
   - Click "Create pull request"

6. **Wait for validation:**
   - Automated tests will run
   - Microsoft reviewers will validate the submission
   - Once approved, the package will be published to winget

---

## Step 5: Update README (if needed)

If this is the first release, update the installation instructions in README.md to reflect that winget is now available (once the PR is merged):

```markdown
### Option 1 — winget (recommended)

```
winget install MarkMysler.DesktopLS
```
```

---

## Future Releases

For subsequent releases:

1. **Update version numbers** in:
   - The git tag (`v1.1.0`)
   - The release title
   - All three winget manifest files

2. **Follow the same steps above**

3. **For winget updates:**
   - Create a new version directory: `manifests/m/MarkMysler/DesktopLS/1.1.0/`
   - Copy and update the three manifest files with the new version
   - New PR with branch name like `DesktopLS-1.1.0`

---

## Version Numbering

Follow [Semantic Versioning](https://semver.org/):
- **Major (1.0.0 → 2.0.0):** Breaking changes
- **Minor (1.0.0 → 1.1.0):** New features, backwards compatible
- **Patch (1.0.0 → 1.0.1):** Bug fixes, backwards compatible

---

## Troubleshooting

### winget validation fails

- Check that all SHA256 hashes match
- Verify the download URL is accessible
- Ensure all required manifest fields are present
- Check the [winget validation documentation](https://github.com/microsoft/winget-pkgs/blob/master/AUTHORING_MANIFESTS.md)

### GitHub release fails

- Ensure you have write access to the repository
- Check that the tag doesn't already exist
- Verify the binary path is correct

### Binary doesn't work

- Test the Release build locally before uploading
- Check that it's truly self-contained (run on a machine without .NET SDK)
- Verify it works on both Windows 10 and 11
