# Illux Image Tool — PDF input — Design

Date: 2026-11-05
Status: Draft for approval
Builds on: `2026-11-04-v0.2-resample-range-and-mac-design.md` (released as v0.2.0) and the rulings in
`docs/superpowers/decisions/` (still apply unless changed here).

## 1. Intent

Illux sends every customer file through **Chili Publish**, which exports it as a **PDF**. Chili only lowers
images to 300 ppi. It does not upscale, does not convert 16-bit to 8-bit, and does none of the other things this
tool does. That is why the tool exists, and today it refuses PDFs ("PDF input is not supported").

What Illux wants (their words, summarised): when a PDF comes in, the tool opens it, takes the image(s), does
exactly what it does to a normal image file, and puts them back so the PDF has **the same size**.

What these PDFs look like (Illux):
- Usually one PNG or JPEG image per page; other image types can occur.
- Nothing else is on the page: no text, cut lines or vector graphics.
- Usually one page, but **several pages can occur and must work**.

## 2. Goals

- PDF files are accepted everywhere an image file is accepted today: the AE command line and watched folders, on
  Windows and macOS.
- Every image on every page goes through the same steps and the same shared settings as a normal image file:
  16/32→8 bit, the size rules with bicubic scaling up or down, progressive→baseline JPEG.
- **Only a PDF comes out** (Illux), with the same file name. Same number of pages, same order, same page sizes, and
  each image drawn in the same place at the same size on the page. Only the pixels inside the images change.
- Out, Error, Originals, name clashes, temp folders, logs and messages work as they do for image files.

## 3. Non-goals

- No new settings. The existing resolution rules and operations apply to images inside PDFs.
- No image files out of a PDF (no extraction).
- No changes to text, vectors, colours, colour profiles, page boxes, metadata or anything else in the PDF.
- No rendering/flattening pages to images.
- No encrypted or password-protected PDFs (they go to Error).
- No changes to AE tickets or folders (Illux sets those up themselves).

## 4. Anti-goals

- A PDF that looks or prints differently, apart from the pixel count of its images.
- A page, an image or part of the file silently left out or left unprocessed.
- A half-processed PDF in Out. Either every image is done, or the whole PDF goes to Error.
- A PDF that AE or a RIP can no longer open.

## 5. How it works

### 5.1 The ppi of an image inside a PDF

An image in a PDF has no ppi of its own. Its ppi is **its pixel count divided by the size it is drawn at** on the
page. That drawn size comes from the page's drawing instructions (the transformation in force where the image is
painted). This drawn size in cm is the "print size" the size rules use, exactly like the print size of a normal
image file. After scaling, the image is drawn at the same size, so only its ppi changes.

- ppi is worked out per axis of the image, also when the image is rotated or mirrored on the page: the drawn length
  of the image's width and of its height (the lengths of the two image axes after the transformation).
- The rule is picked from those two drawn lengths, and each axis is handled exactly as for a normal image file
  today (ruling R13): an axis outside the rule's range is scaled to the target ppi, and an axis inside it is left
  alone. The drawing instructions are not touched, so the drawn size and shape never change.
- `missingppi` does not apply: an image in a PDF always has a drawn size.

### 5.2 Which images

- Every image drawn on every page, including images inside form objects (nested drawings) on the page.
- An image object used more than once (on several pages, or at different sizes) is processed **once**, using its
  **largest** drawn size, so it is never below the target anywhere. When the sizes differ, add a Warning.
  *(Builder's call; Illux does not expect this to happen.)*
- Images that are only masks for other images (soft masks) are not processed on their own. When their image is
  scaled, the soft mask is scaled by the same factors (it keeps its own pixel size relative to the image), so they
  still line up. *(Builder's call.)*
- Rare cases that go to Error with a clear message rather than risk a wrong result: a soft mask with `/Matte`
  (pre-blended), JPEG 2000 with its own mask (`/SMaskInData`), a colour-key mask (`/Mask` array) on an image that
  needs scaling, and a stencil mask (`/ImageMask`) that needs scaling. Chili is not expected to produce them.
  *(Builder's call.)*
- Palette (Indexed) images that need scaling are expanded to the palette's base colour space first, so the colours
  stay the same. 1-bit images that need scaling become 8-bit grayscale, as for normal image files (ruling R18;
  Illux: 1-bit line art is scaled too). *(Builder's call.)*
- Inline images (small images written straight into the page instructions): do not process them; give a Warning per
  PDF. *(Builder's call; Chili is not expected to produce them.)*
- A PDF with no images is copied unchanged with a Warning "no images found".
- When no image needs any change, the PDF in Out is a byte-for-byte copy of the input ("no change needed"), as for
  image files.

### 5.3 Image data

- JPEG data (DCT) stays JPEG. Like a normal JPEG: progressive becomes baseline; when only that is needed it is done
  losslessly, otherwise it is re-encoded at the configured JPEG quality.
- Losslessly compressed data (Flate, LZW, run-length, uncompressed) is written back losslessly (Flate).
- Other encodings (JPEG 2000, JBIG2, CCITT) are processed if ImageMagick can decode them and are written back
  losslessly. If not, the PDF goes to Error with a clear message. *(Builder's call.)*
- An image that needs no change is left as it is, byte for byte.
- Colours are never converted: colour space, ICC profile, CMYK and decode arrays stay as they are.

### 5.4 Everything else in the file

Everything that is not image pixel data is kept: page sizes and boxes, drawing instructions, fonts, annotations,
metadata, output intents. The PDF version is not lowered.

### 5.5 Errors and messages

- Encrypted, damaged or unreadable PDF → Error with a short reason.
- Any image that cannot be processed (unsupported encoding, would exceed the 65,000 px limit, …) → the whole PDF goes
  to Error, nothing in Out.
- Messages say which page and image each line is about, e.g.
  `page 2, image 1: 150→200 ppi (rule "Large": 61.0 × 45.7 cm; 3600×2700 → 4803×3602, catrom)`.
- Warnings use the same rules as image files (for example an upscale above 4×). A PDF's overall status, exit code
  and AE result are worked out exactly like an image file's: an Error anywhere means Error, otherwise any Warning
  means Warning.

### 5.6 Output settings for PDF input

The watched-folder/AE output setting (`same` / `pdf` / `both`) does not create extra files for a PDF input: a PDF in
always gives exactly one PDF out. *(Builder's call, from "only the PDF".)*

## 6. PDF library

- Must have a licence that fits this MIT project (MIT, BSD or Apache 2.0) and run on Windows x64 and macOS arm64.
- **PDFsharp is ruled out**: version 6 is published under AGPL / commercial licence (checked 2026-11-05 on
  docs.pdfsharp.net and its GitHub README).
- Candidates, both Apache 2.0:
  - **QPDF 11+**: a small command-line program bundled with the tool, the same way as jpegtran. It can rewrite
    stream objects (data and dictionary) through its JSON format (`--update-from-json`) and writes a clean new file.
    Handles many malformed PDFs well. *(Preferred, builder's call.)*
  - **PdfPig** (.NET library): reads PDFs and can write an incremental update that replaces objects. The old image
    data would stay inside the file, making it larger. Does not support encrypted files.
- QPDF cannot tell how big an image is drawn (it does not read page drawing instructions). So reading the pages
  (drawn sizes, nested form objects) needs a .NET reader such as PdfPig, and writing can be QPDF. Two components are
  fine. Possible fallback: PdfPig alone with an incremental update (bigger files). *(Builder's call.)*
- The first plan task is a short spike with real Chili PDFs to confirm the choice before building on it.

## 7. Testing

- Unit and integration tests with small generated PDFs: one page with JPEG; PNG-type image with alpha (soft mask);
  several pages; an image drawn rotated; an image inside a form object; one image used twice; no images;
  encrypted → Error; an image needing no change → unchanged bytes.
- Every test checks that page count, page sizes and the drawing instructions are unchanged, and that each image's new
  pixel size and resulting ppi are what the rules ask for.
- Real Chili Publish sample PDFs from Illux run through the tool before release (see open item).
- CI on Windows and macOS as today. Install smoke tests include one PDF going through a watched folder.

## 8. Open item for Illux

- A few real PDFs exported by Chili Publish (ideally one with a JPEG, one with a PNG, and one with several pages) to
  test against.
