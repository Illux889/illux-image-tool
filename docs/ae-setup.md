# Using Illux Image Tool in Automation Engine

Illux Image Tool runs from a **Run Script** task. It converts images to 8 bit, resamples them to the ppi of the resolution rules (see below), makes JPEGs baseline, and can write a PDF.

It also takes **PDF files** (for example from CHILI Publish). Every image in the PDF gets the same treatment as an image file. A PDF in always gives **one PDF out**, with the same name, whatever `output=` says. The pages, page sizes and everything else in the PDF stay the same; only the pixels of the images change, and each image is still drawn at the same size.

## Set up (once)

1. **Install the tool on the machine that runs the scripts**, not on the Automation Engine (AE) server. This is the Windows machine with the Esko Script Runner. Run `IlluxImageTool-Setup-<version>.exe` there.
2. **Install the Esko Script Runner** on that machine, if it is not there yet.
3. **Register the Script Runner in Pilot:** Tools > Configure > Scripts. Add the machine name and port `1983`.
4. **Copy one script** into the Script Runner folder. The installer puts the scripts in `%ProgramFiles%\Illux Image Tool\scripts\ae`:
   - `IlluxImageTool.ps1` into `C:\Esko\bg_data_fastserverscrrunnt_v100\Scripts\PowerShell`
   - or `IlluxImageTool.bat` into `C:\Esko\bg_data_fastserverscrrunnt_v100\Scripts\BatchFile`

   You only need one of them. PowerShell is the better choice: Esko says it reports errors more reliably than a batch file.

   If these Script Runner folders already exist when you install, the installer offers to copy both scripts there for you ("Copy the Automation Engine scripts into the Esko Script Runner folders"). Those copies are replaced on every upgrade and removed when the tool is uninstalled, so do not edit them there; keep your own changes in a copy with another name.

   If the PowerShell script is blocked, the execution policy of the Script Runner machine must allow local scripts (for example `RemoteSigned`).
5. **Set up the Run Script task** in your ticket:

   | Field | Value |
   |---|---|
   | Run on | The Script Runner machine from step 3 |
   | Script Type | `PowerShell Script` (or `Batch File`) |
   | Script File | `IlluxImageTool.ps1` (or `IlluxImageTool.bat`) |
   | Transfer input file(s) to the Script Runner | Tick it if the Script Runner cannot reach the AE files directly |
   | Script Parameters | Click **Add** once for each parameter, one per line (see below) |

   AE adds the input files and the output folder by itself. You only add the parameters.

## Watched folders on a network share

The tool can also watch folders without Automation Engine (see the README). Its watcher service runs as Local System. For folders on a network share, choose another account with **Change account...** in the settings window. That account must be a **local Administrator** of the machine (or a managed service account in the local Administrators group), or the service cannot install updates.

## Parameters

Write each parameter on its own line. They are not case sensitive.

| Parameter | What it does |
|---|---|
| `8bit` | Reduce to 8 bits per channel if the image is deeper. |
| `resample` | Resample (up or down) to the target ppi of the matching resolution rule, if the ppi is outside that rule's range. `upsample` means the same. |
| `baseline` | Rewrite progressive JPEGs as baseline JPEGs. |
| `output=same` | Same format as the input. This is the default. |
| `output=pdf` | Write a PDF 1.6 with one page instead. |
| `output=both` | Write the image and the PDF. |
| `missingppi=72` | No resolution in the image: assume 72 ppi and give a warning. Default. |
| `missingppi=skip` | No resolution in the image: do not resample it and give a warning. |
| `missingppi=error` | No resolution in the image: treat it as an error. |
| `quality=95` | JPEG quality (1-100) when a JPEG has to be re-encoded. Default 95. |
| `filter=bicubic` | Resampling method, up and down. Also `lanczos` and `mitchell`. Default `bicubic` (Photoshop's plain Bicubic). |

A PDF input ignores `output=`: it always gives one PDF.

`8bit`, `resample` and `baseline` are the operations.

- Give none of them: all three run.
- Give one or more: only those run.
- The other parameters never switch an operation on or off. A ticket with only `output=pdf` still runs all three operations and then writes a PDF.
- An unknown parameter is an error.
- The ppi is not set in the ticket. `resample=300` (or `upsample=300`, which v0.1 accepted) is an error: "ppi is set in Illux Image Tool Settings (Resolution rules), not in the AE ticket".

## Resolution rules

The ppi comes from **Settings → Resolution rules**, the same for Automation Engine and the watched folders. Changes apply to the next run; nothing needs restarting.

The print size of an image in cm is pixels ÷ ppi × 2.54 (per side). The rules are checked from top to bottom and the first rule the print size fits is used. If the image's ppi is inside that rule's "leave alone" range it is not resampled; otherwise it is resampled to the rule's target ppi and keeps its print size. Width and height are checked separately: only a side outside the range is resampled.

Defaults:

| Rule | Applies to | Target | Left alone at |
|---|---|---|---|
| Small | Fits within 40 × 30 cm (either way round) | 300 ppi | 250–350 ppi |
| Large | All other images | 200 ppi | 150–250 ppi |

The task log names the rule, for example `300→200 ppi (rule "Large": 62.3 × 41.6 cm; 7360×4912 → 4907×3275, bicubic)`.

If the rules in `config.json` are not valid, the tool uses the defaults above and the task ends with a Warning that says so.

## PDF files

An image inside a PDF has no ppi of its own. Its print size is the size it is drawn at on the page, and its ppi is its pixels ÷ that size. The rules above then decide, as for an image file. Each message names the page and the image, for example `card.pdf: page 1, image 1: 120→200 ppi (rule "Large": 61.0 × 45.7 cm; 2882×2159 → 4803×3598, bicubic)`.

- If no image needs a change, the PDF is copied unchanged (`no change needed`).
- If any image fails, the whole PDF fails and nothing is written for it.
- Encrypted or signed PDFs, and Illustrator files (`.ai`), are not supported (Error).
- A PDF without images is copied with the Warning `no images found`. Small images written straight into the page (inline images) are not processed: Warning `N inline image(s) not processed`.
- An image used at different sizes is processed once, for its largest size, with a Warning.

## Task status

| Status | Meaning |
|---|---|
| OK | All files were processed. |
| Warning | All files were written, but something needs a look. For example, an image had no resolution, was enlarged by more than 4 times, a PDF had no images, or the resolution rules in the settings are not valid. |
| Error | At least one file failed. The other files are still written. |

The status of the task is the worst status of its files.

## First test

1. Make a ticket with the Run Script task and no parameters. Send in one or two images.
2. Run it and open the task log (task details).
3. Check the first two lines. They show what AE passed to the script:

   ```
   Inputs: <the input files, separated by ;>
   Output folder: <the output folder>
   ```

4. The lines after that show one result per file, and the last line is `Result: ...`.

If the first two lines do not look right, send them to us.

For the full list of parameters at any time, run `IlluxImageTool.exe --help` on the Script Runner machine.
