using System.Globalization;

namespace Illux.ImageTool.Options;

public sealed class ParameterException(string message) : Exception(message);

/// <summary>Parses the Automation Engine parameter list into <see cref="ProcessingOptions"/>.</summary>
public static class ParameterParser
{
    public static ProcessingOptions Parse(IEnumerable<string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var options = ProcessingOptions.Default;
        var operations = Operations.None;

        foreach (var raw in parameters)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var text = raw.Trim();
            var eq = text.IndexOf('=');
            var name = (eq < 0 ? text : text[..eq]).Trim().ToLowerInvariant();
            var value = eq < 0 ? null : text[(eq + 1)..].Trim();

            switch (name)
            {
                case "8bit" when value is null:
                    operations |= Operations.EightBit;
                    break;
                case "baseline" when value is null:
                    operations |= Operations.Baseline;
                    break;
                case "upsample":
                    operations |= Operations.Upsample;
                    if (value is not null)
                    {
                        options = options with { TargetPpi = ParseInt(raw, value, 1, 2400) };
                    }
                    break;
                case "output":
                    options = options with { Output = ParseEnum<OutputMode>(raw, value) };
                    break;
                case "missingppi":
                    options = options with { MissingPpi = ParseEnum<MissingPpiPolicy>(raw, value) };
                    break;
                case "filter":
                    options = options with { Filter = ParseEnum<ResampleFilter>(raw, value) };
                    break;
                case "quality":
                    options = options with { JpegQuality = ParseInt(raw, value, 1, 100) };
                    break;
                default:
                    throw new ParameterException($"Unknown parameter '{raw}'.");
            }
        }

        return operations == Operations.None ? options : options with { Operations = operations };
    }

    private static int ParseInt(string raw, string? value, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min || n > max)
        {
            throw new ParameterException($"Invalid value in '{raw}': expected a whole number from {min} to {max}.");
        }
        return n;
    }

    private static T ParseEnum<T>(string raw, string? value) where T : struct, Enum
    {
        var names = Enum.GetNames<T>();
        var match = names.FirstOrDefault(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return Enum.Parse<T>(match);
        }
        var allowed = string.Join(", ", names.Select(n => n.ToLowerInvariant()));
        throw new ParameterException($"Invalid value in '{raw}': expected one of {allowed}.");
    }
}
