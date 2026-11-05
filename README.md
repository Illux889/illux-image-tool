# Illux Image Tool

A small program for Windows and macOS (Apple silicon) that prepares images for print. It is made to be called by Esko Automation Engine, but it also runs from the command line and from watched folders.

## What it does

- **8 bit:** reduces images with more than 8 bits per channel to 8 bits.
- **Resample:** brings the resolution to a target ppi (bicubic) when it is outside the range of its resolution rule. See [Resolution rules](#resolution-rules).
- **Baseline:** rewrites progressive JPEGs as baseline JPEGs, without losing quality when possible.
- **PDF:** can also write a PDF 1.6 with the image on one page.
- **PDF input:** PDF files (for example from CHILI Publish) are accepted too. Every image in the PDF gets the same treatment, using the size it is drawn at on the page as its print size. A PDF in always gives one PDF out, with the same name, whatever `output=` says; its pages, page sizes and everything else stay the same. Encrypted and signed PDFs, and Illustrator files, are not supported.

Colours and ICC profiles are never changed, and EXIF, XMP and IPTC metadata are kept as they are (the pixel size recorded inside EXIF is not updated). Only what an operation needs changes: the pixel size and the stored resolution after resampling, and the bit depth. Photoshop layers inside a TIFF are dropped when its pixels are rewritten (the flattened image is kept, and the file gets a warning).

## Download

Get the newest version from the [Latest release](https://github.com/Illux889/illux-image-tool/releases/latest) page. Download `IlluxImageTool-Setup-<version>.exe` for Windows or `IlluxImageTool-<version>-macOS-arm64.pkg` for Mac.

## Install

1. Run the installer and accept the administrator prompt.
2. The installer is not code-signed yet, so Windows SmartScreen may say "Windows protected your PC". Click **More info**, then **Run anyway**.

Windows 10/11 and Windows Server 2019 or newer, 64-bit. You do not need to install .NET.

### Mac

Apple silicon, macOS 15 or newer. Open the `.pkg`. It is not signed, so macOS blocks it the first time: open **System Settings → Privacy & Security**, scroll down and click **Open Anyway** next to the package, then confirm with your password. If an install fails, run the `.pkg` again.

Uninstall: `sudo "/Library/Application Support/Illux Image Tool/uninstall.sh"`

## Resolution rules

The print size in cm is pixels ÷ ppi × 2.54. The rules in the settings are checked from top to bottom, and the first one the print size fits is used.
If the image's ppi is inside that rule's "leave alone" range, nothing changes. Otherwise it is resampled to the rule's target ppi and keeps its print size.
For an image inside a PDF, the print size is the size it is drawn at on the page.
Width and height are checked separately, and every rule has its own target and range. Changes apply to the next file, with no restart.

| Rule | Applies to | Target | Left alone at |
|---|---|---|---|
| Small | Fits within 40 × 30 cm | 300 ppi | 250-350 ppi |
| Large | All other images | 200 ppi | 150-250 ppi |

## Use from Esko Automation Engine

See [docs/ae-setup.md](docs/ae-setup.md).

## Watched folders

Open **Illux Image Tool Settings** from the Start menu. Windows asks for administrator permission, because the settings apply to the whole computer. Add a folder and choose what to do with the files that arrive in it; each change is saved as soon as you click **OK**. Originals are moved to an `Originals` folder, never deleted.

Logs: `C:\ProgramData\Illux Image Tool\logs` holds the command-line and Automation Engine runs. The watcher service logs into `service-logs` next to it, which only administrators can open; the settings window shows it with **Show service log**.

**Mac:** watched folders must be on the local disk, for example `/Users/Shared/...`. Desktop, Documents, Downloads, external drives and network shares are not supported in v0.2. Create the In folder yourself; Out, Error and Originals are created when they are first needed.

**Mac account:** the Mac processes watched folders as the account that was logged in when the package was first installed (the settings window shows it), never as root. That account must be able to read In and write Out, Error and Originals, and it owns the files it makes; its log is in `/Library/Application Support/Illux Image Tool/folder-logs`. To change it, run `sudo "/Library/Application Support/Illux Image Tool/uninstall.sh"`, delete `folder-account` in that folder, and install again while logged in as the new account.

### Service account and folder permissions (Windows)

The watcher service runs as Local System, which cannot reach network shares. To watch a folder on a share, click **Change account...** in the settings window and enter an account that can reach it. That account must be a **local Administrator** of this computer (or a managed service account in the local Administrators group). Otherwise the service cannot install updates, and **Update now** fails. The settings window warns you if the account is not in the local Administrators group.

The service writes into the Out, Error and Originals folders with the rights of its account. Ordinary users should be able to add and take files *inside* the watched folders, but not delete, rename or replace the folders themselves. The service refuses to write through a folder or file that has been swapped for a link.

## Updates

The tool checks GitHub for a new version when it starts and every 6 hours. If there is one, a popup appears, and the settings window shows an **Update now** button. Nothing is installed until you click. **Update now** waits until no file is being converted.

If you run a newer installer by hand instead, pause Automation Engine first: a manual install closes a conversion that is still running, and that task then ends with an error.

## Build from source

You need the .NET SDK 10 and 7-Zip (to unpack jpegtran).

```powershell
./build/fetch-jpegtran.ps1
dotnet test
```

To build the installer you also need Inno Setup 6.4 or newer: `./build/build-installer.ps1 -Version 1.2.3`. On a Mac: `./build/fetch-jpegtran.sh`, `dotnet test`, then `./installer/macos/build-pkg.sh 1.2.3`.

## License

MIT, see [LICENSE](LICENSE).
