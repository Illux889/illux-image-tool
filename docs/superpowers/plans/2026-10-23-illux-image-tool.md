# Illux Image Tool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A small Windows program that converts images to 8-bit, upsamples to 200 ppi, makes JPEGs baseline and optionally wraps them in PDF 1.6. It can be called from Esko AE's Run Script task or used through watched folders, and installs and updates itself from GitHub Releases.

**Architecture:** One core library (`Illux.ImageTool.Core`) holds all image, PDF, batch, watcher, config and update logic. It has three thin front ends: a CLI exe (called by AE wrapper scripts), a Windows-service exe (watched folders + auto-update), and a WinForms settings exe. An Inno Setup installer, built by GitHub Actions on a version tag, ships everything self-contained.

**Tech Stack:** C# / .NET 10 (SDK 10.0.300 present locally), Magick.NET-Q16-HDRI-x64 (pin the newest 14.x on NuGet with no open advisory; ≥ 14.15.0), libjpeg-turbo 3.2.0 `jpegtran.exe` + `jpeg62.dll` (vc-x64), Microsoft.Extensions.Hosting.WindowsServices, xUnit, UglyToad.PdfPig (tests only), Inno Setup 6, GitHub Actions `windows-latest`.

**Spec:** `docs/superpowers/specs/2026-10-23-illux-image-tool-design.md` — read it before every task.

## Global Constraints

- Target: win-x64, Windows 10/11 and Windows Server 2019+; published **self-contained** (no separate .NET install).
- Exit codes / statuses everywhere: `0 = OK`, `1 = Warning`, `2 = Error`; batch status = worst file status.
- Operation parameters are exactly `8bit`, `upsample[=N]`, `baseline`; none given → all three on. `output=`, `missingppi=`, `quality=`, `filter=` never switch operations. Unknown parameter → Error.
- Defaults: target 200 ppi, output `same`, missing ppi = assume 72 + Warning, JPEG quality 95, filter Lanczos, upscale factor > 4 → Warning.
- Never convert colour, never auto-rotate, never strip ICC/EXIF/XMP/IPTC; keep file name and (unless PDF) format and TIFF compression.
- Never re-compress a JPEG unless 8bit/upsample actually changed it; progressive-only → lossless `jpegtran -copy all`.
- PDF: header `%PDF-1.6`, one page, page size = pixels / ppi × 72 pt, MediaBox = TrimBox, JPEG embedded byte-for-byte (`DCTDecode`), others `FlateDecode`, ICC → `ICCBased`, alpha → `SMask`, Adobe-APP14 CMYK JPEG → `/Decode [1 0 1 0 1 0 1 0]`.
- Outputs are written to a temp name in the destination folder and renamed when complete; on failure nothing is left behind.
- Watcher never deletes customer files; originals go to `Originals` (default: sibling of In) only after the output re-opens successfully.
- Shared paths: `C:\ProgramData\Illux Image Tool\` holds `config.json`, `logs\`, `runs\`, `updating.flag`.
- Updates: GitHub repo `Illux889/illux-image-tool`, full releases only, asset `IlluxImageTool-Setup-<version>.exe` + `<same>.sha256`, check at start + every 6 h, install only when idle, silent args `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`.
- Commit messages: conventional (`feat:`, `fix:`, `test:`, `build:`, `docs:`), ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Work on branch `feat/v1`.

## Review Focus

1. **Adobe CMYK JPEG → PDF colours:** Photoshop CMYK JPEGs must not come out as a negative; `Decode` is added only when the APP14 Adobe marker is present (Task 6 test `Pdf_AdobeCmykJpeg_GetsInvertedDecode` + `Pdf_PlainCmykJpeg_NoDecode`). Also do a manual visual check in Acrobat during Task 15.
2. **EXIF orientation:** a rotated phone JPEG must keep its pixels and orientation tag untouched (Task 5 test `Pipeline_KeepsExifOrientation_NoAutoRotate`).
3. **Paths with spaces and Danish letters (æøå) and UNC paths** through AE args and the jpegtran call (Task 2 `Split_UncAndDanishNames`, Task 4 `ToBaseline_PathWithSpacesAndDanishLetters`).
4. **Unwritable output folder / locked input file:** clean Error, no crash, no partial file (Task 7 `Batch_OutputFolderMissing_IsErrorNoCrash`, `Batch_LockedInput_IsError`).
5. **16-bit grayscale PNG with alpha:** becomes 8-bit and keeps alpha (Task 5 `Pipeline_Gray16AlphaPng_To8Bit_KeepsAlpha`).

---

## File Structure

```
IlluxImageTool.sln
Directory.Build.props                  # net10.0, nullable, version from /p:Version
src/Illux.ImageTool.Core/
  Options/ProcessingOptions.cs         # options record + enums
  Options/ParameterParser.cs           # AE/watcher parameter strings → options
  Ae/InputListParser.cs                # split %1
  Jpeg/JpegInspector.cs                # marker parser → JpegInfo
  Jpeg/JpegTran.cs                     # lossless progressive→baseline
  Imaging/ResolutionReader.cs          # ppi lookup in Photoshop order
  Imaging/ImagePipeline.cs             # 8bit / upsample / write, copy-unchanged
  Imaging/MagickSetup.cs               # resource limits
  Pdf/PdfImageWriter.cs                # PDF 1.6 single image
  Batch/FileProcessor.cs               # one file → outputs, temp+rename, naming
  Batch/BatchRunner.cs                 # many files → worst status, log lines
  Runtime/AppPaths.cs, RunLock.cs      # ProgramData paths, active-run locks
  Watch/WatcherConfig.cs, ConfigStore.cs, FolderScanner.cs
  Update/ReleaseClient.cs, Updater.cs
src/Illux.ImageTool.Cli/Program.cs     # exe: IlluxImageTool.exe
src/Illux.ImageTool.Watcher/           # exe: IlluxImageToolWatcher.exe (service)
src/Illux.ImageTool.Settings/          # exe: IlluxImageToolSettings.exe (WinForms)
scripts/ae/IlluxImageTool.bat, IlluxImageTool.ps1
build/fetch-jpegtran.ps1               # downloads + verifies libjpeg-turbo 3.2.0
installer/IlluxImageTool.iss
.github/workflows/ci.yml, release.yml
tests/Illux.ImageTool.Tests/           # xUnit; fixtures generated at runtime by TestImages.cs
README.md, docs/ae-setup.md
```

Test images are generated in code (`tests/.../TestImages.cs`) with Magick.NET, never committed as binaries.

---

### Task 1: Solution scaffold + options and parameter parser

**Files:**
- Create: `IlluxImageTool.sln`, `Directory.Build.props`, `.gitignore`, `src/Illux.ImageTool.Core/Illux.ImageTool.Core.csproj`, `src/Illux.ImageTool.Core/Options/ProcessingOptions.cs`, `src/Illux.ImageTool.Core/Options/ParameterParser.cs`, `tests/Illux.ImageTool.Tests/Illux.ImageTool.Tests.csproj`
- Test: `tests/Illux.ImageTool.Tests/ParameterParserTests.cs`

**Interfaces:**
- Produces:
  - `[Flags] enum Operations { None = 0, EightBit = 1, Upsample = 2, Baseline = 4, All = 7 }`
  - `enum OutputMode { Same, Pdf, Both }`, `enum MissingPpiPolicy { Assume72, Skip, Error }`, `enum ResampleFilter { Lanczos, Mitchell, Bicubic }`, `enum Status { Ok = 0, Warning = 1, Error = 2 }`
  - `sealed record ProcessingOptions(Operations Operations, double TargetPpi, OutputMode Output, MissingPpiPolicy MissingPpi, int JpegQuality, ResampleFilter Filter)` with `static ProcessingOptions Default` = (All, 200, Same, Assume72, 95, Lanczos)
  - `static ProcessingOptions ParameterParser.Parse(IEnumerable<string> parameters)` — throws `ParameterException(string message)`; blank/whitespace entries ignored; case-insensitive; `upsample=N` requires 1 ≤ N ≤ 2400; `quality` 1–100.
  - `static class StatusExtensions { Status Worst(this Status a, Status b) }`

- [ ] **Step 1:** Create `.gitignore` (bin/, obj/, `third_party/`, `artifacts/`), `Directory.Build.props` (`TargetFramework net10.0`, `Nullable enable`, `ImplicitUsings enable`, `LangVersion latest`, `Version` default `0.0.0-dev`), solution, Core class library and xUnit test project referencing Core.
- [ ] **Step 2: Write failing tests** in `ParameterParserTests`:
  - `Parse_Empty_IsAllDefaults` → `ProcessingOptions.Default`
  - `Parse_OnlyOutputPdf_KeepsAllOperations` → Operations == All, Output == Pdf
  - `Parse_8bitOnly` → Operations == EightBit
  - `Parse_UpsampleWithValue` (`"Upsample=300"`) → Operations == Upsample, TargetPpi == 300
  - `Parse_Combination` (`"8bit","baseline","output=both","quality=90","missingppi=skip","filter=mitchell"`) → EightBit|Baseline, Both, 90, Skip, Mitchell
  - `Parse_Unknown_Throws` (`"8bitt"`) → `ParameterException` whose message contains `8bitt`
  - `Parse_BadValues_Throw` theory: `upsample=0`, `upsample=abc`, `quality=101`, `output=png`, `missingppi=maybe`
  - `Worst_ReturnsHighest` (Ok,Warning→Warning; Error,Warning→Error)
- [ ] **Step 3:** Run `dotnet test` → FAIL (types missing).
- [ ] **Step 4:** Implement the types above.
- [ ] **Step 5:** Run `dotnet test` → all PASS.
- [ ] **Step 6:** Commit `feat: solution scaffold and AE parameter parser`.

### Task 2: AE input list parser

**Files:**
- Create: `src/Illux.ImageTool.Core/Ae/InputListParser.cs`
- Test: `tests/Illux.ImageTool.Tests/InputListParserTests.cs`

**Interfaces:**
- Produces: `static IReadOnlyList<string> InputListParser.Split(string raw)` — trims surrounding quotes/whitespace, drops empty entries.

- [ ] **Step 1: Write failing tests:**
  - `Split_Semicolons` `"C:\\a\\x.jpg;C:\\b\\y.tif"` → 2 paths exact
  - `Split_ColonSeparatedKeepsDriveLetters` `"C:\\a\\x.jpg:D:\\b\\y.tif"` → `["C:\\a\\x.jpg","D:\\b\\y.tif"]`
  - `Split_UncAndDanishNames` `"\\\\srv\\job 1\\Ærø.jpg;\\\\srv\\job 1\\blå.tif"` → 2 paths exact
  - `Split_Single` and `Split_EmptyOrWhitespace_ReturnsEmpty`
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement: if the string contains `;`, split on `;`. Otherwise split on `:` only where the colon is **not** at index 1 of a path segment (drive letter), i.e. regex split `:(?=(?:[A-Za-z]:\\|\\\\))`.
- [ ] **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat: parse AE input file lists`.

### Task 3: JPEG inspector

**Files:**
- Create: `src/Illux.ImageTool.Core/Jpeg/JpegInspector.cs`, `tests/Illux.ImageTool.Tests/TestImages.cs`
- Test: `tests/Illux.ImageTool.Tests/JpegInspectorTests.cs`
- Modify: add `Magick.NET-Q16-HDRI-x64` to Core (pinned exact version) — confirm on nuget.org that the chosen version has no vulnerability banner.

**Interfaces:**
- Produces:
  - `enum JpegCoding { Baseline, ExtendedSequential, Progressive, Lossless, Arithmetic }`
  - `sealed record JpegInfo(JpegCoding Coding, int Precision, int Width, int Height, int Components, bool HasAdobeMarker, int AdobeTransform, (double X, double Y, int Units)? JfifDensity, byte[]? IccProfile)` — `Units` as in JFIF (0 none, 1 dpi, 2 dpcm).
  - `static JpegInfo JpegInspector.Inspect(Stream s)` / `Inspect(string path)` — throws `InvalidDataException` if not a JPEG. ICC reassembled from all `APP2 "ICC_PROFILE\0"` chunks in sequence order.
  - `TestImages` (tests): `string Write(string dir, string name, int w, int h, ColorSpace cs, int depth, double? ppi, bool progressive = false, bool alpha = false, byte[]? icc = null, int quality = 92)` — generates a gradient+noise image with Magick.NET and returns its path. Progressive via `Interlace.Jpeg`; `ppi == null` writes no density (strip JFIF density: `Density = new Density(0,0)` / undefined units — verify by inspecting). `TestImages.SrgbIcc` exposes `ColorProfile.SRGB.ToByteArray()`; `TestImages.CmykIcc` = `ColorProfile.USWebCoatedSWOP.ToByteArray()`.

- [ ] **Step 1: Write failing tests:**
  - `Inspect_Baseline_Rgb` → Baseline, 8, w/h, 3 components
  - `Inspect_Progressive` → Progressive
  - `Inspect_CmykFromMagick_HasAdobeMarker` → 4 components, `HasAdobeMarker` true (ImageMagick writes APP14 for CMYK; assert what it actually writes and document it)
  - `Inspect_Density` (ppi 150) → JfifDensity (150,150,1)
  - `Inspect_IccRoundTrip` → bytes equal `TestImages.SrgbIcc`
  - `Inspect_NotJpeg_Throws` (PNG file)
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement a marker walker (SOI, then segments until SOS; SOF0=Baseline, SOF1=Extended, SOF2=Progressive, SOF3/7/11/15=Lossless, SOF9/10/11/13/14/15 arithmetic as Arithmetic). **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat: JPEG marker inspector`.

### Task 4: Lossless progressive → baseline (jpegtran)

**Files:**
- Create: `build/fetch-jpegtran.ps1`, `src/Illux.ImageTool.Core/Jpeg/JpegTran.cs`
- Modify: Core csproj (copy `third_party/libjpeg-turbo/jpegtran.exe` and `jpeg62.dll` to output `tools\jpegtran\`)
- Test: `tests/Illux.ImageTool.Tests/JpegTranTests.cs`

**Interfaces:**
- Produces: `static class JpegTran { string ExePath { get; } Task ToBaselineAsync(string inputPath, string outputPath, CancellationToken ct = default); }` — runs `jpegtran.exe -copy all -outfile "<out>" "<in>"` (no `-optimize`, so standard Huffman tables = Photoshop "Baseline (Standard)"), `ExePath` = `<AppContext.BaseDirectory>\tools\jpegtran\jpegtran.exe`; non-zero exit or missing exe → `JpegTranException` with stderr text. Uses `ProcessStartInfo.ArgumentList` (no manual quoting).

- [ ] **Step 1:** Write `build/fetch-jpegtran.ps1`: downloads `https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/3.2.0/libjpeg-turbo-3.2.0-vc-x64.exe`, verifies a pinned SHA-256 (compute once, write the constant into the script), extracts with `7z x` only `bin\jpegtran.exe` and `bin\jpeg62.dll` (plus `LICENSE.md`/`README.ijg`) into `third_party/libjpeg-turbo/`. Idempotent. Locally install 7-Zip first if missing (`winget install 7zip.7zip`). If the exe needs other DLLs (check with `dumpbin /dependents` or by running `jpegtran -version`), extract those too.
- [ ] **Step 2: Write failing tests:**
  - `ToBaseline_ProgressiveBecomesBaseline_PixelsIdentical`: progressive RGB → output `JpegInspector` Coding == Baseline; decode both with Magick and `Compare(…, ErrorMetric.Absolute) == 0`.
  - `ToBaseline_KeepsIccAndExif`: ICC bytes equal; EXIF profile present.
  - `ToBaseline_CmykProgressive` → Baseline, identical pixels.
  - `ToBaseline_PathWithSpacesAndDanishLetters` (`"mappe æøå/fil 1.jpg"`).
  - `ToBaseline_CorruptInput_Throws` → `JpegTranException`.
- [ ] **Step 3:** Run → FAIL. **Step 4:** Implement. **Step 5:** Run → PASS.
- [ ] **Step 6:** Commit `feat: lossless baseline conversion via jpegtran`.

### Task 5: Resolution reader + image pipeline

**Files:**
- Create: `src/Illux.ImageTool.Core/Imaging/ResolutionReader.cs`, `ImagePipeline.cs`, `MagickSetup.cs`
- Test: `tests/Illux.ImageTool.Tests/ResolutionReaderTests.cs`, `ImagePipelineTests.cs`

**Interfaces:**
- Consumes: `ProcessingOptions`, `JpegInspector`, `JpegTran`.
- Produces:
  - `sealed record Resolution(double X, double Y)`
  - `static Resolution? ResolutionReader.Read(IMagickImage<float> image)` — order: Photoshop 8BIM ResolutionInfo (resource 0x03ED in the `8bim` profile), then the image density from the file's own header (JFIF / TIFF / PNG pHYs), then EXIF X/YResolution. Converts px/cm → ppi. Density 0 or units undefined with no other source → `null`. Verify Photoshop's order against Adobe docs/real files while implementing; adjust the order if needed and note it in a code comment.
  - `sealed record PipelineResult(Status Status, IReadOnlyList<string> Messages, string OutputPath, Resolution FinalResolution, bool Changed)`
  - `static Task<PipelineResult> ImagePipeline.RunAsync(string inputPath, string outputPath, ProcessingOptions options, CancellationToken ct)` — writes exactly one image file at `outputPath` (same format as input). Logic:
    1. Detect format from content (Magick `MagickImageInfo` / ping). Reject → `UnsupportedImageException(reason)` for: PSD/PSB, PDF, RAW, animated GIF/WebP (>1 frame), multi-page TIFF, JPEG with Coding Lossless/Arithmetic or Precision 12.
    2. Decide `needs8Bit` (depth > 8 and EightBit on), `needsUpsample` (Upsample on and resolution X or Y < target, after missing-ppi policy), `needsBaseline` (JPEG, Progressive, Baseline on).
    3. Nothing needed → byte-copy input to output (`Changed = false`).
    4. JPEG with only `needsBaseline` → `JpegTran.ToBaselineAsync`.
    5. Otherwise load with Magick (no auto-orient), resample (`Resize` with `IgnoreAspectRatio`, new size `round(w * target / ppiX)` × `round(h * target / ppiY)`, filter from options), set `Density` to target ppi (PixelsPerInch), `Depth = 8` if needs8Bit, keep ICC/EXIF/XMP/IPTC profiles and TIFF `Compression`; JPEG: `Quality = options.JpegQuality`, `Interlace = NoInterlace`, keep original sampling factor (`jpeg:sampling-factor` from input); write.
    6. Messages, one per action, e.g. `16→8 bit`, `150→200 ppi (2480×3508 → 3307×4677, Lanczos)`, `progressive→baseline (lossless)` / `(re-encoded, q95)`, `no change needed`.
    7. Warnings: missing ppi assumed 72 / skipped; factor > 4.
    8. `MissingPpiPolicy.Error` with no resolution → Status Error, nothing written.
  - `static void MagickSetup.Configure()` — idempotent; sets `ResourceLimits.Memory` to 50 % of `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`, and a temp dir under `%TEMP%\IlluxImageTool`.
- [ ] **Step 1: Write failing tests** (all images from `TestImages`; read outputs back with Magick):
  - `Read_JpegDensity150` → (150,150); `Read_PngPhys` (300); `Read_TiffDpcm` (59.055 dpcm ≈ 150 ppi, tolerance 0.1); `Read_NoDensity_IsNull`.
  - `Pipeline_Tiff16Rgb150_To8Bit200`: depth 8, density 200, size scaled by 200/150, ICC bytes equal, compression unchanged (LZW in → LZW out).
  - `Pipeline_Png16_To8Bit_SizeUnchangedAt300ppi` → depth 8, same pixel size.
  - `Pipeline_Gray16AlphaPng_To8Bit_KeepsAlpha`.
  - `Pipeline_Cmyk16Tiff_StaysCmyk` → ColorSpace CMYK, ICC equal.
  - `Pipeline_ProgressiveJpegOnly_IsLossless` → pixels identical, Baseline.
  - `Pipeline_ProgressiveJpeg100ppi_ReencodedBaseline200` → Baseline, density 200, message contains `re-encoded, q95`.
  - `Pipeline_Already8Bit300ppiBaseline_CopiedByteForByte` → file bytes equal, `Changed == false`.
  - `Pipeline_Above200_NotDownsampled` (300 ppi stays 300).
  - `Pipeline_OnlyBaselineOperation_DoesNotUpsample` (options Baseline only, 100 ppi progressive → still 100 ppi).
  - `Pipeline_MissingPpi_Assume72_Warning` → size × 200/72, Warning; `Pipeline_MissingPpi_Skip_Warning` → pixels unchanged; `Pipeline_MissingPpi_Error` → Error, no output file.
  - `Pipeline_Factor5_Warning` (40 ppi).
  - `Pipeline_KeepsExifOrientation_NoAutoRotate` (EXIF Orientation=6 → same tag, same width/height orientation).
  - `Pipeline_MultiPageTiff_Unsupported`, `Pipeline_AnimatedGif_Unsupported`, `Pipeline_Psd_Unsupported`.
  - `Pipeline_Float32Tiff_To8Bit`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5: Filter check:** write `tests/.../FilterComparison` (skipped by default, `[Fact(Skip=…)]`) that upsamples a photo-like test image 150→200 with Lanczos / Mitchell / Bicubic and saves them to `artifacts/filter-compare/` for a visual look; keep Lanczos unless it shows obvious ringing, record the decision in a comment.
- [ ] **Step 6:** Commit `feat: image pipeline (8-bit, upsample, baseline)`.

### Task 6: PDF 1.6 writer

**Files:**
- Create: `src/Illux.ImageTool.Core/Pdf/PdfImageWriter.cs`
- Test: `tests/Illux.ImageTool.Tests/PdfImageWriterTests.cs` (add `UglyToad.PdfPig` to tests only)

**Interfaces:**
- Consumes: `JpegInspector`, `Resolution`.
- Produces: `static void PdfImageWriter.Write(string imagePath, Resolution resolution, string pdfPath)` — objects: Catalog, Pages, Page (`MediaBox` = `TrimBox` = `[0 0 W H]`, W = px / ppiX × 72, H = px / ppiY × 72), content stream `q W 0 0 H 0 0 cm /Im0 Do Q`, Image XObject, optional ICC stream (`/N` = components, `/Alternate` = DeviceRGB/CMYK/Gray), optional SMask, Info (`/Producer (Illux Image Tool <version>)`, `/CreationDate`). Header `%PDF-1.6` + binary comment line. Classic xref table.
  - JPEG: bytes embedded unchanged, `/Filter /DCTDecode`, `/BitsPerComponent 8`, ColorSpace from components (1 Gray, 3 RGB, 4 CMYK) or ICCBased from `JpegInfo.IccProfile`; `/Decode [1 0 1 0 1 0 1 0]` when 4 components **and** `HasAdobeMarker` (img2pdf rule).
  - Other formats: read with Magick, export raw channels (`ToByteArray` for "RGB"/"CMYK"/"I" plus separate "A"), at the image depth (8 or 16 → `BitsPerComponent`), compress with `ZLibStream` (zlib header required by FlateDecode), ICC from `GetColorProfile()`.
- [ ] **Step 1: Write failing tests** (open results with PdfPig and by scanning the raw bytes):
  - `Pdf_HeaderIs16` → file starts with `%PDF-1.6`
  - `Pdf_PageSize_FromPpi` (4724×3150 @ 200 → 1700.64 × 1134 pt ±0.01; 600×400 mm)
  - `Pdf_Jpeg_EmbeddedByteForByte` → the JPEG file bytes occur verbatim in the PDF; `/DCTDecode` present
  - `Pdf_Tiff_IsFlateLossless` → PdfPig image raw decoded bytes equal the source pixel bytes
  - `Pdf_IccBased_WhenProfilePresent`; `Pdf_DeviceRgb_WhenNoProfile`
  - `Pdf_AdobeCmykJpeg_GetsInvertedDecode`; `Pdf_PlainCmykJpeg_NoDecode` (craft one by removing APP14 from a Magick CMYK JPEG in the test)
  - `Pdf_PngAlpha_HasSMask`
  - `Pdf_OpensInPdfPig_OnePage`
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat: PDF 1.6 single-image writer`.

### Task 7: File processor + batch runner

**Files:**
- Create: `src/Illux.ImageTool.Core/Batch/FileProcessor.cs`, `BatchRunner.cs`
- Test: `tests/Illux.ImageTool.Tests/BatchTests.cs`

**Interfaces:**
- Consumes: `ImagePipeline`, `PdfImageWriter`, `ProcessingOptions`, `Status`.
- Produces:
  - `enum NamePolicy { Overwrite, Suffix }`
  - `sealed record FileResult(string Input, Status Status, IReadOnlyList<string> Messages, IReadOnlyList<string> Outputs)`
  - `static Task<FileResult> FileProcessor.ProcessAsync(string inputPath, string outputFolder, ProcessingOptions options, NamePolicy namePolicy, ISet<string> claimedNames, CancellationToken ct)` — target names: `<name>.<ext>` and/or `<name>.pdf` per `OutputMode`. Name already in `claimedNames` (same batch) → Error. Existing file: `Overwrite` replaces, `Suffix` uses `name (2).ext`, `(3)`…. Work in `outputFolder\.illux-tmp-<guid>\` (same volume), then `File.Move(…, overwrite)` into place; delete temp folder in `finally`. Any exception (unsupported, IO, `MagickResourceLimitErrorException`, `OutOfMemoryException`) → Error with a readable message, no output.
  - `static Task<Status> BatchRunner.RunAsync(IReadOnlyList<string> inputs, string outputFolder, ProcessingOptions options, TextWriter log, CancellationToken ct)` — `NamePolicy.Overwrite`; one log line per file: `<file name>: <messages joined by "; ">` prefixed `WARNING:` / `ERROR:` when not OK; final line `Result: OK|Warning|Error (n files)`; returns worst status. Empty input list → Warning `No input files`.
- [ ] **Step 1: Write failing tests:**
  - `Batch_ThreeFiles_OneCorrupt_OthersWritten_StatusError`
  - `Batch_UnchangedFileStillWritten`
  - `Batch_OutputPdf_WritesOnlyPdf`; `Batch_OutputBoth_WritesImageAndPdf`
  - `Batch_SameBaseName_SecondIsError` (`a.jpg` + `a.tif` with `output=pdf`)
  - `Batch_ExistingOutput_Overwritten`
  - `Suffix_Policy_AddsNumber` (FileProcessor directly)
  - `Batch_OutputFolderMissing_IsErrorNoCrash` (non-existent and non-creatable path such as `Z:\nope` → Error)
  - `Batch_LockedInput_IsError` (open with `FileShare.None` during the run)
  - `Batch_NoTempFolderLeftBehind`
  - `Batch_LogLinesFormat` (exact `Result:` line)
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat: batch processing with AE output contract`.

### Task 8: CLI exe + run locks

**Files:**
- Create: `src/Illux.ImageTool.Core/Runtime/AppPaths.cs`, `RunLock.cs`, `src/Illux.ImageTool.Cli/Illux.ImageTool.Cli.csproj` (`AssemblyName IlluxImageTool`), `Program.cs`
- Test: `tests/Illux.ImageTool.Tests/CliTests.cs`, `RunLockTests.cs`

**Interfaces:**
- Produces:
  - `static class AppPaths { string Root /* C:\ProgramData\Illux Image Tool, overridable via env ILLUX_IMAGETOOL_ROOT for tests */; string ConfigFile; string LogsDir; string RunsDir; string UpdatingFlag; }`
  - `sealed class RunLock : IDisposable { static RunLock Acquire(); static bool AnyActive(); }` — file `runs\<pid>-<guid>.lock` opened `FileShare.None` + `DeleteOnClose`; `AnyActive` = any lock file that cannot be opened (stale files that can be opened are deleted).
  - `static Task<int> CliApp.RunAsync(string[] args, TextWriter stdout)` (in Cli project, `Program.Main` just calls it):
    - `IlluxImageTool.exe ae "<%1>" "<%2>" [params…]` → `InputListParser.Split`, `ParameterParser.Parse`, `BatchRunner.RunAsync`, exit = status.
    - `IlluxImageTool.exe run <file-or-folder> --out <folder> [params…]` → same, folder = all files in it (non-recursive).
    - `IlluxImageTool.exe --version`, `--help` (parameter reference text).
    - Parameter error → print `ERROR: <message>`, exit 2. Unhandled exception → `ERROR: <message>`, exit 2 (no stack traces on stdout; full detail to `logs\cli-YYYYMMDD.log`).
    - Holds a `RunLock` for the whole run; calls `MagickSetup.Configure()`.
- [ ] **Step 1: Write failing tests:** `Cli_Ae_ProcessesAndReturnsZero`, `Cli_Ae_BadParam_Returns2WithMessage`, `Cli_Run_Folder`, `Cli_Version_PrintsVersion`, `RunLock_ActiveWhileHeld_InactiveAfterDispose`, `RunLock_StaleFileIgnored`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat: command-line tool for AE and manual runs`.

### Task 9: AE wrapper scripts + AE guide

**Files:**
- Create: `scripts/ae/IlluxImageTool.bat`, `scripts/ae/IlluxImageTool.ps1`, `docs/ae-setup.md`
- Test: `tests/Illux.ImageTool.Tests/AeScriptTests.cs`

**Interfaces:**
- Consumes: CLI `ae` command; `AppPaths.UpdatingFlag`.
- Produces: both scripts locate the exe via env `ILLUX_IMAGETOOL_EXE` (tests) else `%ProgramFiles%\Illux Image Tool\IlluxImageTool.exe`; wait while `updating.flag` exists (check every 5 s, max 10 min, then exit 2 with `ERROR: update did not finish`); call `ae %1 %2 %3…` forwarding **all** parameters (bat: loop with `shift` beyond %9); exit with the exe's exit code. PS1 uses Esko's positional `Param` pattern with `[Parameter(ValueFromRemainingArguments)]` for the extras.
- [ ] **Step 1: Write failing tests** (run via `cmd /c` and `powershell -NoProfile -ExecutionPolicy Bypass -File`, with `ILLUX_IMAGETOOL_EXE` pointing at the built CLI): `Bat_ForwardsArgsAndExitCode` (bad param → exit 2), `Bat_TwelveParams_AllForwarded` (use 11 blank params + `8bit` → only 8-bit runs), `Ps1_ForwardsArgsAndExitCode`, `Bat_WaitsForUpdatingFlag` (flag removed after 2 s → still succeeds).
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Write `docs/ae-setup.md` (short, simple words): install on the separate machine, install Esko Script Runner, register it in Pilot (Configure → Scripts, host, port 1983), copy the script to `C:\Esko\bg_data_fastserverscrrunnt_v100\Scripts\BatchFile` (or `PowerShell`), Run Script ticket fields, parameter table, status meaning, and a first test where the user reads the first two lines of the task log. The CLI prints them in `ae` mode as `Inputs: <raw %1>` and `Output folder: <%2>`; add them to `CliApp` with the test `Cli_Ae_LogsRawInputsAndOutputFolder`.
- [ ] **Step 6:** Commit `feat: Esko AE wrapper scripts and setup guide`.

### Task 10: Watched folders (config + scanner)

**Files:**
- Create: `src/Illux.ImageTool.Core/Watch/WatcherConfig.cs`, `ConfigStore.cs`, `FolderScanner.cs`
- Test: `tests/Illux.ImageTool.Tests/WatcherTests.cs`

**Interfaces:**
- Consumes: `FileProcessor` (with `NamePolicy.Suffix`), `ParameterParser`, `AppPaths`.
- Produces:
  - `sealed class WatchedFolder { string Name; string In; string Out; string Error; string? Originals; List<string> Parameters; bool Enabled = true; string OriginalsOrDefault => Originals ?? Path.Combine(Path.GetDirectoryName(In)!, "Originals"); }`
  - `sealed class WatcherConfig { List<WatchedFolder> Folders = []; bool AutoUpdate = true; int PollSeconds = 5; }`
  - `static class ConfigStore { WatcherConfig Load(string? path = null); void Save(WatcherConfig c, string? path = null); }` — JSON (System.Text.Json, indented); missing file → default config; invalid JSON → throws `ConfigException`.
  - `sealed class FolderScanner { FolderScanner(WatchedFolder folder, ILogger log); bool Busy { get; } Task ScanOnceAsync(CancellationToken ct); }` — a file is processed once it was seen with the same size and last-write time on two consecutive scans **and** opens with `FileShare.None`. Skips hidden/system files, names starting with `~$` or `.`, extensions `.tmp .part .crdownload`, and the `.illux-tmp-*` folder. Success → output(s) in Out, original moved to Originals (suffix on clash) after re-opening the output(s) with Magick/PdfPig-free check (`File.OpenRead` + length > 0 + for images `MagickImageInfo` read). Error → original moved to Error + `<name>.error.txt` with the messages. Warning → treated as success, warning text appended to `Out\_warnings.log`. Invalid `Parameters` → folder logged as misconfigured and skipped (no file moves). Missing In folder → logged once per minute, skipped.
- [ ] **Step 1: Write failing tests** (temp dirs, call `ScanOnceAsync` twice to pass the stability check): `Scan_ProcessesStableFile_ToOut_OriginalToOriginals`, `Scan_FileStillGrowing_NotProcessed`, `Scan_CorruptFile_ToErrorWithTxt`, `Scan_OutNameClash_GetsSuffix`, `Scan_IgnoresTempAndHiddenFiles`, `Scan_BadParameters_NoFilesMoved`, `ConfigStore_RoundTrip`, `ConfigStore_Missing_ReturnsDefault`, `Scan_NeverDeletesOriginal` (after any outcome the original bytes exist in Originals or Error).
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat: watched folder scanner and config`.

### Task 11: Updater

**Files:**
- Create: `src/Illux.ImageTool.Core/Update/ReleaseClient.cs`, `Updater.cs`
- Test: `tests/Illux.ImageTool.Tests/UpdaterTests.cs`

**Interfaces:**
- Consumes: `AppPaths`, `RunLock.AnyActive()`, a `Func<bool> watcherBusy`.
- Produces:
  - `sealed record ReleaseInfo(Version Version, Uri InstallerUrl, Uri ChecksumUrl)`
  - `sealed class ReleaseClient { ReleaseClient(HttpClient http); Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct); }` — `GET https://api.github.com/repos/Illux889/illux-image-tool/releases/latest` with `User-Agent: IlluxImageTool/<version>`; ignores drafts/pre-releases (the endpoint already does); tag `vX.Y.Z` → `Version`; asset names exactly `IlluxImageTool-Setup-X.Y.Z.exe` and `.exe.sha256` (file content: hex hash, optionally followed by file name). Missing assets → null.
  - `sealed class Updater { Updater(ReleaseClient client, HttpClient http, Version current, Func<bool> watcherBusy, Func<string, Process?> startInstaller, ILogger log); Task<UpdateOutcome> RunOnceAsync(CancellationToken ct); }` — `enum UpdateOutcome { UpToDate, Disabled, Busy, ChecksumFailed, Started, Failed }`. Flow: config `AutoUpdate` false → Disabled; newer? download both to `%TEMP%\IlluxImageTool\update\`; verify SHA-256 (mismatch → delete, ChecksumFailed); wait until `!RunLock.AnyActive() && !watcherBusy()` (re-check every 10 s, give up after 30 min → Busy, retry next cycle); create `updating.flag`; start installer with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="<logs>\update-<version>.log"` → Started. The installer removes the flag at the end (Task 14).
- [ ] **Step 1: Write failing tests** (fake `HttpMessageHandler`): `Latest_ParsesTagAndAssets`, `Latest_MissingAsset_Null`, `Run_SameVersion_UpToDate`, `Run_Newer_ChecksumOk_StartsInstallerWithSilentArgs` (captures args via `startInstaller`), `Run_ChecksumMismatch_NotStarted_FileDeleted`, `Run_ActiveRunLock_WaitsThenStarts` (short timeouts injectable), `Run_Disabled`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat: automatic updates from GitHub Releases`.

### Task 12: Watcher Windows service

**Files:**
- Create: `src/Illux.ImageTool.Watcher/Illux.ImageTool.Watcher.csproj` (`AssemblyName IlluxImageToolWatcher`, Worker SDK, `Microsoft.Extensions.Hosting.WindowsServices`), `Program.cs`, `WatchWorker.cs`, `UpdateWorker.cs`
- Test: `tests/Illux.ImageTool.Tests/WatchWorkerTests.cs`

**Interfaces:**
- Consumes: `ConfigStore`, `FolderScanner`, `Updater`, `MagickSetup`.
- Produces: service name `IlluxImageToolWatcher`, display name `Illux Image Tool Watcher`. `WatchWorker`: reloads `config.json` when its last-write time changes, keeps one `FolderScanner` per enabled folder, scans every `PollSeconds`, exposes `bool Busy` (any scanner busy). `UpdateWorker`: `Updater.RunOnceAsync` at start (after 1 min) and every 6 h, plus immediately when a file `check-now.flag` appears in `AppPaths.Root` (written by Settings; delete after reading). Logging: rolling daily file `logs\watcher-YYYYMMDD.log` (simple custom `ILoggerProvider`, keep 30 days) + EventLog source `Illux Image Tool` for Warning and above.
- [ ] **Step 1: Write failing tests:** `Worker_PicksUpConfigChange` (add a folder to config while running, file gets processed), `Worker_BusyWhileProcessing`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5: Manual check:** `sc create` the built exe locally in an elevated shell, start it, drop a test JPEG into a configured In folder, confirm it appears in Out; `sc delete` afterwards.
- [ ] **Step 6:** Commit `feat: watched-folder Windows service`.

### Task 13: Settings window

**Files:**
- Create: `src/Illux.ImageTool.Settings/Illux.ImageTool.Settings.csproj` (`net10.0-windows`, `UseWindowsForms`, `AssemblyName IlluxImageToolSettings`, manifest `requireAdministrator`), `MainForm.cs`, `FolderDialog.cs`, `ServiceControl.cs`
- Test: `tests/Illux.ImageTool.Tests/ServiceControlTests.cs` (only the non-UI argument building)

**Interfaces:**
- Consumes: `ConfigStore`, `ParameterParser` (validate before save), `AppPaths`.
- Produces: one window with:
  - Watched folders list; Add / Edit / Remove. Edit dialog: Name, In, Out, Error, Originals (browse buttons), checkboxes **8-bit**, **Upsample to [200] ppi**, **Baseline JPEG**, output dropdown (Same / PDF / Both), missing-ppi dropdown, JPEG quality. Saved as parameter strings.
  - Service: status label, Start / Stop, **Change account…** (user + password → `ChangeServiceConfigW` P/Invoke; empty = LocalSystem).
  - Updates: installed version, last check time (from log/state file `update-state.json` written by `UpdateWorker`), **Automatic updates** checkbox, **Check now** (writes `check-now.flag`).
  - Buttons: Open log folder, AE parameter help (shows `IlluxImageTool.exe --help` text).
- [ ] **Step 1: Write failing tests** for `ServiceControl.BuildAccountName(string user)` (`"bob"` → `.\bob`, `"DOM\bob"` unchanged, `""` → `LocalSystem`) and `FolderDialog.ToParameters(...)`/`FromParameters(...)` round trip.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5: Manual check:** run the exe, add a folder, confirm `config.json` contents, toggle auto-update, Check now creates the flag.
- [ ] **Step 6:** Commit `feat: settings window`.

### Task 14: Installer

**Files:**
- Create: `installer/IlluxImageTool.iss`, `build/build-installer.ps1`

**Interfaces:**
- Consumes: published folders of Cli, Watcher, Settings (self-contained win-x64, `PublishSingleFile=false`, shared output folder `artifacts/publish/`), `third_party/libjpeg-turbo`, `scripts/ae`, `LICENSE`, `docs/ae-setup.md`.
- Produces: `artifacts/installer/IlluxImageTool-Setup-<version>.exe` + `.exe.sha256`.
  - Install dir `{autopf}\Illux Image Tool` (admin install, 64-bit mode), fixed `AppId` GUID (generate once, never change), `AppVersion` from `/DMyVersion=`.
  - Creates `C:\ProgramData\Illux Image Tool\` with Users-modify rights; never overwrites an existing `config.json`.
  - Before files are copied (`PrepareToInstall`): `sc stop IlluxImageToolWatcher` and wait until stopped.
  - After install: `sc create` (first install) or keep (upgrade), `start= auto`, description, recovery `sc failure … restart`; start service; delete `updating.flag`.
  - Copies `scripts\ae\*` into `C:\Esko\bg_data_fastserverscrrunnt_v100\Scripts\BatchFile` and `\PowerShell` if those folders exist (task checkbox, on by default).
  - Start menu: "Illux Image Tool Settings", "AE setup guide".
  - Uninstall: stop + delete service, remove program files; keep ProgramData (config/logs).
  - `build/build-installer.ps1 -Version x.y.z`: runs `fetch-jpegtran.ps1`, `dotnet publish` ×3 with `/p:Version`, `iscc /DMyVersion=x.y.z installer\IlluxImageTool.iss`, writes the `.sha256` file (`<hex>  <filename>`).
- [ ] **Step 1:** Install Inno Setup locally if missing (`winget install JRSoftware.InnoSetup`). Write script + build script.
- [ ] **Step 2:** Run `build/build-installer.ps1 -Version 0.1.0` → installer + sha256 exist.
- [ ] **Step 3: Manual check (elevated):** install silently `/VERYSILENT`, confirm service running, CLI `--version` = 0.1.0, files in Program Files; build 0.1.1 and install over it silently → service restarted, config kept, version 0.1.1; uninstall → service gone.
- [ ] **Step 4:** Commit `build: Inno Setup installer`.

### Task 15: GitHub Actions, README, first release

**Files:**
- Create: `.github/workflows/ci.yml`, `.github/workflows/release.yml`, `README.md`

**Interfaces:**
- Consumes: `build/fetch-jpegtran.ps1`, `build/build-installer.ps1`.
- Produces:
  - `ci.yml`: on push/PR, `windows-latest`, setup-dotnet 10.0.x, fetch jpegtran, `dotnet test -c Release`.
  - `release.yml`: on tag `v*.*.*`: tests, `choco install innosetup -y` if `iscc` missing, `build-installer.ps1 -Version <tag without v>`, create GitHub Release (`gh release create`) with the installer and `.sha256`, auto-generated notes. `permissions: contents: write`.
  - `README.md` in simple words: what it does, download link to latest release, install, AE setup link, watched folders, updates, SmartScreen note, build from source.
- [ ] **Step 1:** Write workflows + README. Push branch, confirm `ci.yml` passes on GitHub (`gh run watch`).
- [ ] **Step 2:** Merge to `main` (after user OK), tag `v0.1.0`, push tag, confirm the release has both assets.
- [ ] **Step 3: Manual end-to-end (user's PC):** download installer from the release page, install, run the CLI on sample files, set up a watched folder, open a produced PDF in Acrobat (check CMYK colours are not inverted, page size correct, Document Properties shows PDF 1.6).
- [ ] **Step 4: Update test:** tag `v0.1.1` (trivial change), confirm the installed copy updates itself after "Check now".
- [ ] **Step 5:** Commit `build: CI, release workflow and README`.
