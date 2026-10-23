# Illux Image Tool

A small Windows program that prepares images for print. It is made to be called by Esko Automation Engine, but it also runs from the command line and from watched folders.

## What it does

- **8 bit:** reduces images with more than 8 bits per channel to 8 bits.
- **Upsample:** raises the resolution to 200 ppi (or a value you choose) when it is lower.
- **Baseline:** rewrites progressive JPEGs as baseline JPEGs, without losing quality when possible.
- **PDF:** can also write a PDF 1.6 with the image on one page.

Colours, ICC profiles and metadata are never changed.

## Download

Get the newest installer from the [Latest release](https://github.com/Illux889/illux-image-tool/releases/latest) page. Download `IlluxImageTool-Setup-<version>.exe`.

## Install

1. Run the installer and accept the administrator prompt.
2. The installer is not code-signed yet, so Windows SmartScreen may say "Windows protected your PC". Click **More info**, then **Run anyway**.

Windows 10/11 and Windows Server 2019 or newer, 64-bit. You do not need to install .NET.

## Use from Esko Automation Engine

See [docs/ae-setup.md](docs/ae-setup.md).

## Watched folders

Open **Illux Image Tool Settings** from the Start menu. Add a folder, choose what to do with the files that arrive in it, and save. Originals are moved to an `Originals` folder, never deleted.

## Updates

The tool checks GitHub for a new version when it starts and every 6 hours. If there is one, a popup appears, and the settings window shows an **Update now** button. Nothing is installed until you click.

## Build from source

You need the .NET SDK 10 and 7-Zip (to unpack jpegtran).

```powershell
./build/fetch-jpegtran.ps1
dotnet test
```

To build the installer you also need Inno Setup 6.4 or newer: `./build/build-installer.ps1 -Version 1.2.3`.

## License

MIT, see [LICENSE](LICENSE).
