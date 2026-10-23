# Using Illux Image Tool in Automation Engine

Illux Image Tool runs from a **Run Script** task. It converts images to 8 bit, upsamples them to 200 ppi, makes JPEGs baseline, and can write a PDF.

## Set up (once)

1. **Install the tool on the machine that runs the scripts**, not on the Automation Engine (AE) server. This is the Windows machine with the Esko Script Runner. Run `IlluxImageTool-Setup-<version>.exe` there.
2. **Install the Esko Script Runner** on that machine, if it is not there yet.
3. **Register the Script Runner in Pilot:** Tools > Configure > Scripts. Add the machine name and port `1983`.
4. **Copy one script** from `scripts\ae` into the Script Runner folder:
   - `IlluxImageTool.ps1` into `C:\Esko\bg_data_fastserverscrrunnt_v100\Scripts\PowerShell`
   - or `IlluxImageTool.bat` into `C:\Esko\bg_data_fastserverscrrunnt_v100\Scripts\BatchFile`

   You only need one of them. PowerShell is the better choice: Esko says it reports errors more reliably than a batch file.
5. **Set up the Run Script task** in your ticket:

   | Field | Value |
   |---|---|
   | Run on | The Script Runner machine from step 3 |
   | Script Type | `PowerShell Script` (or `Batch File`) |
   | Script File | `IlluxImageTool.ps1` (or `IlluxImageTool.bat`) |
   | Transfer input file(s) to the Script Runner | Tick it if the Script Runner cannot reach the AE files directly |
   | Script Parameters | Click **Add** once for each parameter, one per line (see below) |

   AE adds the input files and the output folder by itself. You only add the parameters.

## Parameters

Write each parameter on its own line. They are not case sensitive.

| Parameter | What it does |
|---|---|
| `8bit` | Reduce to 8 bits per channel if the image is deeper. |
| `upsample` | Upsample to 200 ppi if the image is below that. |
| `upsample=300` | Upsample to the ppi you give (here 300). |
| `baseline` | Rewrite progressive JPEGs as baseline JPEGs. |
| `output=same` | Same format as the input. This is the default. |
| `output=pdf` | Write a PDF 1.6 with one page instead. |
| `output=both` | Write the image and the PDF. |
| `missingppi=72` | No resolution in the image: assume 72 ppi and give a warning. Default. |
| `missingppi=skip` | No resolution in the image: do not upsample it and give a warning. |
| `missingppi=error` | No resolution in the image: treat it as an error. |
| `quality=95` | JPEG quality (1-100) when a JPEG has to be re-encoded. Default 95. |
| `filter=lanczos` | Resampling method. Also `mitchell` and `bicubic`. Default `lanczos`. |

`8bit`, `upsample` and `baseline` are the operations.

- Give none of them: all three run.
- Give one or more: only those run.
- The other parameters never switch an operation on or off. A ticket with only `output=pdf` still runs all three operations and then writes a PDF.
- An unknown parameter is an error.

## Task status

| Status | Meaning |
|---|---|
| OK | All files were processed. |
| Warning | All files were written, but something needs a look. For example, an image had no resolution or was upsampled by more than 4 times. |
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
