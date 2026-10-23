namespace Illux.ImageTool.Cli;

/// <summary>The text printed by <c>--help</c>; the settings window shows the same text by running <c>IlluxImageTool.exe --help</c>.</summary>
public static class CliHelp
{
    public const string Text = """
        Illux Image Tool - converts images to 8 bit, upsamples them to 200 ppi, makes JPEGs baseline and can wrap them in a PDF.

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
          upsample             Upsample to 200 ppi if the image is below that.
          upsample=N           Upsample to N ppi (for example upsample=300) if the image is below that.
          baseline             Rewrite progressive JPEGs as baseline JPEGs.
          output=same          Write the same format as the input (default).
          output=pdf           Write a PDF 1.6 with the image on one page instead.
          output=both          Write both the image and the PDF.
          missingppi=72        Image has no resolution stored: assume 72 ppi and give a warning (default).
          missingppi=skip      Image has no resolution stored: do not upsample it and give a warning.
          missingppi=error     Image has no resolution stored: treat it as an error.
          quality=N            JPEG quality (1-100) when a JPEG has to be re-encoded. Default 95.
          filter=lanczos       Resampling method for upsampling (default).
          filter=mitchell      Softer resampling method.
          filter=bicubic       Bicubic resampling method.

        8bit, upsample and baseline are the operations. If none of them is given, all three run.
        As soon as one is given, only the named ones run. The other parameters never switch an
        operation on or off. An unknown parameter is an error.
        """;
}
