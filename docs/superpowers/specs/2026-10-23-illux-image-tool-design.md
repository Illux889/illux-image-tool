# Illux Image Tool — Design

Date: 2026-10-23
Status: Draft for approval
Repository: https://github.com/Illux889/illux-image-tool (public, MIT licence)

## 1. Intent and why

Illux runs Esko Automation Engine (AE). Orders arrive, AE workflows start on
their own, and files are checked and prepared automatically. Some image
preparation steps are today done in Adobe Photoshop, driven from AE. Photoshop
is far too large and heavy for the handful of things Illux actually needs.

Illux Image Tool is a small, open-source Windows program that does **only**
these jobs, at Photoshop-level image quality, and that AE can call as a normal
workflow step:

1. **Reduce bit depth** – images with 16 (or more) bits per channel become 8
   bits per channel.
2. **Upsample to a minimum resolution** – images under 200 ppi are resampled
   (real new pixels, not just a changed number) up to 200 ppi, keeping their
   physical size.
3. **Baseline JPEG** – progressive JPEGs are rewritten as baseline
   (standard/sequential) JPEGs.
4. **Optional PDF output** – the finished image is wrapped in a PDF 1.6 with as
   little as possible done to it.

Each of these can be switched on or off independently for every run ("we need
freedom"): all at once, one at a time, or any combination.

## 2. Goals

- Callable from AE's **Run Script** task, fully unattended, returning
  OK / Warning / Error so the AE workflow can carry on or branch.
- A **watched-folder** mode that runs in the background and processes any file
  dropped into a configured folder.
- Photoshop-grade quality for the three image operations.
- A normal Windows **installer** downloadable from the GitHub repository's
  Releases page, built automatically by GitHub Actions.
- **Easy updates:** when a new version is released on GitHub, the program
  notices it and offers it with a popup and an "Update now" button; it only
  installs after someone clicks (see §11a).
- Small and focused: nothing beyond the features listed here.
- Open source.

## 3. Non-goals

- No image editor, no viewer, no layers, no PSD/PSB support (PSDs never go
  through these workflows).
- No processing of incoming PDFs (PDFs are only ever an output, for now).
- No designing of Illux's AE workflows. We deliver the tool, ready-made AE
  scripts and short setup instructions; Illux places the step in its
  workflows.
- No downsampling of images above 200 ppi.
- No colour conversion (RGB stays RGB, CMYK stays CMYK).
- No macOS/Linux build.

## 4. Anti-goals (would be a failure even if it "works")

- Visibly worse image quality than Photoshop for the same operation.
- Changing anything the user did not ask for: colours, colour profile,
  cropping, metadata, file name, file format (unless PDF output was chosen).
- Re-compressing a JPEG when it is not necessary.
- Silently passing a broken or unprocessed file on as if it were fine. AE must
  always get an honest OK / Warning / Error.
- Becoming a heavy program again (target: installer well under 100 MB, starts
  in about a second).

## 5. Users and environment

- Runs on a **separate Windows machine, not the AE server**, so a problem in
  the tool can never affect AE (Illux's requirement). That machine also runs
  **Esko's standalone Script Runner** (free download from the AE server's
  `http://<AEserver>:9000/#/downloads` → Apps & Tools → Tools), registered in
  AE under Pilot → Tools → Configure → Scripts (host + port, default 1983). The
  Run Script task then uses *Run on: <that machine>*. Running on the AE server
  via the on-board Script Runner should still work, but is not the target.
- Target OS: 64-bit Windows 10/11 or Windows Server 2019+.
- Script Runner on Windows runs as a service, so the tool must work without a
  logged-in user and without any window or prompt.
- Operated by prepress staff; configured once, then runs unattended.
- Must not require .NET or any other runtime to be installed separately.

## 6. How AE calls it (confirmed from Esko documentation)

Source: Esko, *Running Scripts in Automation Engine* (AE 24/25.03 user guide,
chapters 5, 7.3 and 9).

- The **Run Script** task runs a script of type *Batch File* or *PowerShell
  Script* either on the AE server itself ("on-board Script Runner") or on a
  Windows machine with the standalone Script Runner.
- Scripts live in the Script Runner machine's local folder
  (default `C:\Esko\bg_data_fastserverscrrunnt_v100\Scripts\BatchFile` /
  `...\PowerShell`) or centrally on the AE server
  (`C:\Esko\bg_data_fastserver_v100\Scripts\...`). The installer offers to copy
  the wrapper scripts into the local Script Runner folder if it exists.
- Arguments:
  - `%1` – the task's input files, **one string, paths separated by `;`**
    (Esko's PowerShell example splits on `:`; the tool must cope with both
    while not breaking Windows drive letters like `C:\` or UNC paths `\\server\`).
  - `%2` – the output folder. *AE continues the workflow with the files written
    there; if nothing is written, AE continues with the original inputs.*
  - `%3, %4, …` – the "Script Parameters" typed into the task ticket.
- Exit code → task status: **0 = OK, 1 = Warning, 2 = Error**.
- Text written to standard output appears in the task details / log.
- AE may run several instances in parallel ("Allow parallel processing"); the
  tool must be safe to run concurrently.
- Input paths may be on AE containers (UNC/network paths). With a standalone
  Script Runner, the task either sends a file specification on an AE container
  (the machine must be able to reach that share) or, with *Transfer input
  file(s) to the Script Runner* ticked, copies the files to the Script Runner
  machine first. The tool must work in both cases.

### What we ship for AE

- `IlluxImageTool.bat` and `IlluxImageTool.ps1` wrapper scripts that forward
  the arguments to the installed executable and pass its exit code back to AE.
  PowerShell is offered because Esko notes it reports errors more reliably than
  batch files.
- A short guide: where to copy the script, how to set up the Run Script task,
  and the parameter reference.

### Script parameters (one per "Add" in the AE ticket)

There are three **operation parameters**: `8bit`, `upsample`, `baseline`. All
three operations are **on** when none of them is given. As soon as any
operation parameter is given, only the named operations run. All other
parameters (`output`, `missingppi`, `quality`, `filter`) are settings and never
switch operations on or off — so a ticket with only `output=pdf` runs all three
operations and then writes a PDF (my call). Proposed syntax (case-insensitive):

| Parameter | Meaning |
|---|---|
| `8bit` | Reduce to 8 bits per channel if deeper |
| `upsample` / `upsample=200` | Upsample to the given ppi (default 200) if below it |
| `baseline` | Rewrite progressive JPEGs as baseline |
| `output=same` / `output=pdf` / `output=both` | Output format (default `same`) |
| `missingppi=72` / `missingppi=skip` / `missingppi=error` | What to do when an image has no resolution stored (see §8.2) |
| `quality=95` | JPEG quality when a JPEG has to be re-encoded (see §8.3) |
| `filter=…` | Resampling method override (see §8.2) |

Unknown parameters → Error, with a clear message (catches typos in tickets).

### Output contract for AE

- Every successfully handled input file produces its output file(s) in `%2`,
  **including files that needed no change** (copied byte-for-byte), so
  multi-file tasks never lose good files from the flow.
- A file that fails writes **nothing** to `%2` and the task exits 2 (Error), so
  AE sends it down the workflow's error path; the original is never passed on
  as if it had been processed (my call). Other files in the same task are
  still processed and written.
- Unsupported or unexpected inputs (PSD/PSB, PDF, camera RAW, 12-bit JPEG,
  animated GIF/WebP, multi-page TIFF, unreadable/corrupt files) → Error for
  that file, with the reason in the log (my call).
- File name is kept; only the extension changes for PDF output. Existing files
  of the same name in `%2` are overwritten (re-runs); two inputs in one task
  that would produce the same output name (e.g. `a.jpg` + `a.tif` →
  `a.pdf`) → Error for the second one (my call). Output is written to a
  temporary name and renamed at the end, so parallel or interrupted runs never
  leave half-written files.
- The exact `%1`/`%2` strings must be captured once from a real Run Script task
  on Illux's Script Runner machine (first acceptance check) to confirm the
  separator and path forms; the parser accepts `;`-separated and
  `:`-separated lists without breaking `C:\` or `\\server\share` paths.
- Status per task = worst status of all files:
  - **OK** – all files processed or needed nothing.
  - **Warning** – processed, but something worth a look (e.g. resolution was
    missing and assumed, very large upscale factor, a file type where an
    operation does not apply).
  - **Error** – a file could not be read/written/processed. Nothing partial is
    left in the output folder for that file.
- One clear log line per file and operation, e.g.
  `order123.jpg: 16→8 bit; 150→200 ppi (2480×3508 → 3307×4677, Lanczos); progressive→baseline (re-encoded, q95); PDF 1.6 written`.

## 7. Watched-folder mode

- Runs as a **Windows service** ("Illux Image Tool Watcher"), starts with
  Windows, needs no logged-in user.
- Any number of watched folders, each with its own settings (same options as
  the AE parameters) and its own folders:
  - **In** – watched.
  - **Out** – finished files.
  - **Error** – files that failed, each with a `.txt` next to it explaining why.
  - **Originals** – where the untouched input is moved after success. The
    tool **never deletes** customer files (my call); this folder defaults to
    an `Originals` folder next to In, and the original is only moved after the
    output has been written completely and re-opened successfully.
- If a file with the same name already exists in Out, the new one gets a
  suffix (`name (2).jpg`) instead of overwriting (my call).
- Only picks up a file once it is completely written (size stable and the file
  can be opened exclusively), so half-copied files are never processed.
- Ignores temporary/hidden files and its own output.
- Processes existing files on start-up, then watches for new ones.
- Runs as the built-in LocalSystem account by default (fine for local
  folders, which is how Illux will first test it on a PC). The settings window
  lets an admin switch the service to a named Windows account, needed when
  watched folders are on network shares (`\\server\share`; mapped drive
  letters are not used by services). Final account for the separate machine is
  still to be decided by Illux (my call for now).
- Handles one file at a time per watched folder by default.
- Writes a rolling log file and important events to the Windows Event Log.

## 8. Image processing

### 8.1 Supported input

JPEG, PNG, TIFF (including LZW/ZIP/uncompressed, 8/16/32-bit, RGB, CMYK,
Grayscale), plus other common raster formats: BMP, GIF, WebP. Colour modes RGB,
CMYK, Grayscale (with or without alpha). Not supported: PSD/PSB, PDF input,
camera RAW.

Multi-page TIFFs: only single-image files are expected; a multi-page TIFF →
Error (my call, avoids silently dropping pages).

Order of operations for one file:
`read → 8-bit → upsample → write (baseline for JPEG) → optional PDF wrap`.

### 8.2 Bit depth and resolution

- **8-bit:** any image with more than 8 bits per channel is reduced to 8.
  Rounding to nearest, no dithering by default (my call; an option for
  dithering can be added). Already-8-bit images are untouched.
- **Upsample:** if the stored horizontal or vertical resolution is below the
  target (default 200 ppi), the image is resampled so it reaches the target at
  the **same physical size**; the stored resolution becomes the target.
  Images at or above the target are not touched.
- **Resampling quality:** high-quality filter equivalent to Photoshop's
  enlargement options; default proposed as Lanczos (my call — the builder
  should compare Lanczos vs. Mitchell/bicubic on real sample images and pick
  the one closest to Photoshop's "Bicubic Smoother", keeping the other as a
  `filter=` option). Resampling is done at the image's working bit depth
  before writing.
- **Missing resolution:** default is to assume 72 ppi like Photoshop does and
  report a **Warning** (my call); `missingppi=` lets a workflow choose `skip`
  (leave pixels as they are, Warning) or `error`.
- **Where resolution is read from:** the builder must verify and copy the
  order Photoshop uses when a file carries several resolution tags (for JPEG:
  Photoshop resolution block, JFIF density, EXIF), and write the new
  resolution consistently into all of them.
- **Large enlargements:** an upscale factor above 4× (e.g. 50 → 200 ppi) is
  still performed but reported as a **Warning** so it is visible in AE (my
  call; no hard limit).
- Non-square resolutions (different x/y) are resampled per axis to the target.

### 8.3 JPEG handling

- **Progressive → baseline, nothing else needed:** done **losslessly** by
  rearranging the existing compressed data (the same technique as
  libjpeg-turbo's `jpegtran`), so not a single pixel changes. This is better
  than Photoshop, which always re-compresses.
- **JPEG that must be re-encoded** (8-bit or upsample applied): written once as
  baseline at high quality, default quality 95 (my call; overridable with
  `quality=`), keeping the original chroma subsampling and embedded ICC
  profile.
- Already-baseline JPEGs with nothing else to do are copied unchanged.

### 8.4 Memory and very large images

Largest expected image size and machine RAM are not known yet (my call): the
tool sets ImageMagick's memory/disk resource limits from the machine's
available RAM, uses a temporary disk cache for very large images, and turns
any out-of-memory or resource-limit failure into a clean **Error** for that
file, never a crash or hang.

### 8.5 What is preserved

Embedded ICC colour profile, EXIF/XMP/IPTC metadata (with resolution fields
updated), alpha channel, TIFF compression type, file name.

## 9. PDF output (PDF 1.6)

"Do as little as possible to the PDF."

- One page, one image, nothing else on it: no margins, marks, text or
  annotations.
- Page size = the image's physical size at its final resolution
  (e.g. 4724 × 3150 px at 200 ppi → 600 × 400 mm). MediaBox = TrimBox =
  page size.
- **No re-compression:**
  - JPEG → embedded byte-for-byte (`DCTDecode`), after the baseline step. CMYK
    JPEGs written by Adobe software (inverted) get the correct `Decode` array.
  - Everything else → lossless `FlateDecode`.
- **Colours untouched:** embedded ICC profile written as an `ICCBased` colour
  space; without a profile, plain `DeviceRGB` / `DeviceCMYK` / `DeviceGray`.
  No colour conversion ever.
- Alpha channel → soft mask (`SMask`).
- Header `%PDF-1.6`; plain PDF, no PDF/X stamp (can be added later if AE
  preflight needs it). Basic document info (Producer = Illux Image Tool,
  creation date).
- `output=both` writes the processed image and the PDF.

## 10. Settings window

A small Windows app (Start menu: "Illux Image Tool Settings") to:

- Add/edit/remove watched folders and their options.
- Start/stop the watcher service and see its status.
- Open the log folder.
- Show the version and the AE parameter reference.
- Show "Update available" with an **Update now** button, and "Check for
  updates" (see §11a).
- Runs without admin rights; only the actions that need them (service
  start/stop, service account) ask for elevation.

Settings are stored in one JSON file under `C:\ProgramData\Illux Image Tool\`.

## 11. Installer and releases

- One Windows installer (x64) that installs the command-line tool, the
  watcher service, the settings window, and the AE wrapper scripts, and adds a
  Start menu entry. Clean uninstall.
- Self-contained: no separate .NET/runtime installation needed.
- Built and published by **GitHub Actions**: pushing a version tag (e.g.
  `v1.0.0`) builds, tests and attaches the installer to a GitHub Release.
- The repository is public (MIT licence), so the installer and updates can be
  downloaded without a GitHub login or key.
- The installer is an **Inno Setup** `.exe` (free, no licence fees), which can
  upgrade an existing installation silently (`/VERYSILENT`). WiX was dropped
  because WiX v6+ carries a maintenance-fee EULA for companies above
  US$10,000 revenue.
- Every release also publishes a SHA-256 checksum file for the installer.
- The installer is not code-signed initially, so Windows SmartScreen will show
  a "Windows protected your PC" prompt on first install ("More info → Run
  anyway"). Code signing can be added later with a certificate.

## 11a. Updates — checked automatically, installed only after a click

Illux: "No automatic updates. We need to confirm … by popup, or clicking in
the software … it needs to be done very easily."

- The watcher service checks the GitHub Releases of
  `Illux889/illux-image-tool` (public API, no key) at start-up and every
  6 hours. It **never installs on its own**; it only records that a new
  version is available (`update-state.json` in ProgramData).
- **Popup:** a small tray helper (the settings app started with `--tray`, run
  automatically at Windows log-in for every user) shows a Windows popup:
  "Illux Image Tool X.Y.Z is available. [Update now] [Later]". "Later" hides it
  until the next version or the next log-in.
- **In the software:** the settings window shows a clear banner
  "Version X.Y.Z is ready" with an **Update now** button, plus "Check for
  updates" for an instant check. Machines where nobody is logged in (servers)
  are updated this way.
- Clicking **Update now** asks the service to install. The service downloads
  the installer, checks it against the published SHA-256 checksum, waits until
  idle (no watched-folder file in progress, no command-line/AE run active),
  then installs silently (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`). The
  window shows the progress: downloading → waiting for running jobs →
  installing → done. A download that fails the check is discarded and shown as
  an error, never installed.
- Command-line runs register themselves (lock file) so the install waits for
  them; the AE wrapper scripts wait while an update is being installed.
- Watched folders keep working across an update: the service stops, the
  installer upgrades, the service starts again and picks up any files that
  arrived.
- Every check and update is written to the log and the Windows Event Log.
- Only full releases are offered, never drafts or pre-releases.

## 12. Technology (builder's starting point)

My calls, to be confirmed against current documentation during planning:

- **C# on .NET 10 (LTS)**, published self-contained for win-x64.
- **Magick.NET** (ImageMagick, Q16-HDRI build, Apache-2.0) for reading,
  bit-depth reduction, resampling and writing images. Pin to the newest
  release with no open security advisories.
- **Lossless progressive→baseline:** libjpeg-turbo's `jpegtran` technique,
  via `jpegtran.exe` + `jpeg62.dll` from the official libjpeg-turbo 3.2.0
  `vc-x64` release, fetched by a build script with a pinned SHA-256.
- **PDF writer:** a small purpose-built writer inside the project (a single
  image on a single page is a few hundred lines), so JPEG bytes and ICC
  profiles go in untouched and the output is fully under our control.
- Watcher: .NET Worker Service hosted as a Windows service.
- Settings window: WinForms (small, no extra runtime).
- Installer: Inno Setup 6 (silent upgrades, free for commercial use).
- Automated tests with small sample images covering every operation, colour
  mode and output type, plus a check that the PDF opens and is version 1.6.

## 13. Left to the builder

- Exact project layout, class design, logging library.
- Exact resampling filter default (see §8.2), backed by a visual comparison.
- How the watcher detects "file finished copying" (within the rules in §7).

## 14. Acceptance checks

- From an AE Run Script task (batch and PowerShell), a 16-bit 150 ppi
  progressive-free TIFF comes out 8-bit 200 ppi; a progressive JPEG comes out
  baseline with identical pixels; with `output=pdf` a PDF 1.6 page of the
  right physical size is produced; the task ends OK.
- Wrong parameter → task ends in Error with a readable message.
- An unreadable/corrupt file → Error, nothing left in the output folder for it.
- Watched folder: dropping files processes them into Out; a corrupt file lands
  in Error with an explanation; works after reboot without anyone logged in.
- Installer installs and uninstalls cleanly on Windows Server and Windows 11.
- Publishing a new release shows the popup / banner on an installed copy
  within one check interval (or at once with "Check for updates"); nothing is
  installed until "Update now" is clicked; the update then completes without
  interrupting a file being processed.
