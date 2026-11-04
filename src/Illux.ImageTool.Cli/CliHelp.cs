namespace Illux.ImageTool.Cli;

/// <summary>The text printed by <c>--help</c>; the settings window shows the same text (it uses this constant directly).</summary>
public static class CliHelp
{
    public const string Text = """
        Illux Image Tool - converts images to 8 bit, resamples them to the ppi of the resolution rules, makes JPEGs
        baseline and can wrap them in a PDF.

        Usage:
          IlluxImageTool.exe ae "<input files>" "<output folder>" [parameters]
              For Automation Engine. The input files are one string separated by ";".
          IlluxImageTool.exe run <file-or-folder> --out <output folder> [parameters]
              Manual run. A folder means the files directly in it (not sub folders).
          IlluxImageTool.exe --version
          IlluxImageTool.exe --help

        Exit code: 0 = OK, 1 = Warning, 2 = Error.

        Parameters (not case sensitive):
          8bit                 Reduce to 8 bits per channel if the image is deeper.
          resample             Resample (up or down) to the target ppi of the first matching resolution rule
                               when the ppi is outside that rule's range. upsample means the same.
          baseline             Rewrite progressive JPEGs as baseline JPEGs.
          output=same          Write the same format as the input (default).
          output=pdf           Write a PDF 1.6 with the image on one page instead.
          output=both          Write both the image and the PDF.
          missingppi=72        Image has no resolution stored: assume 72 ppi and give a warning (default).
          missingppi=skip      Image has no resolution stored: do not resample it and give a warning.
          missingppi=error     Image has no resolution stored: treat it as an error.
          quality=N            JPEG quality (1-100) when a JPEG has to be re-encoded. Default 95.
          filter=bicubic       Resampling method, up and down (default; Photoshop's plain Bicubic).
          filter=lanczos       Sharper resampling method.
          filter=mitchell      Softer resampling method.

        8bit, resample and baseline are the operations. If none of them is given, all three run.
        As soon as one is given, only the named ones run. The other parameters never switch an
        operation on or off. An unknown parameter is an error.

        Resolution rules (Illux Image Tool Settings; checked top to bottom, the first that fits is used):
        the print size is pixels / ppi. Defaults:
          Small   fits within 40 x 30 cm   target 300 ppi, left alone at 250-350 ppi
          Large   all other images         target 200 ppi, left alone at 150-250 ppi
        The ppi cannot be set in a parameter: resample=N is an error.
        """;
}
