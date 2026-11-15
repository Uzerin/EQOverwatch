using System.Globalization;
using System.Text;

namespace EQOverwatch.Core.Maps;

/// <summary>A line on an EverQuest map: <c>L x1, y1, z1, x2, y2, z2, r, g, b</c> (map coordinates).</summary>
public sealed record MapLine(double X1, double Y1, double Z1, double X2, double Y2, double Z2, byte R, byte G, byte B);

/// <summary>
/// A label on an EverQuest map: <c>P x, y, z, r, g, b, size, Text_With_Underscores</c>. Map coordinates are the /loc numbers with
/// their signs turned round: /loc prints "Y, X, Z", and the map's x is minus that X and its y minus that Y.
/// </summary>
public sealed record MapLabel(double X, double Y, double Z, byte R, byte G, byte B, int Size, string Text)
{
    /// <summary>The /loc numbers (Y, X, Z, as EQ prints them) for this label's spot.</summary>
    public (double Y, double X, double Z) Loc => (-Y, -X, Z);

    /// <summary>A label at a /loc spot (Y, X, Z as EQ prints them).</summary>
    public static MapLabel AtLoc(double locY, double locX, double locZ, byte r, byte g, byte b, int size, string text) =>
        new(-locX, -locY, locZ, r, g, b, size, text);
}

/// <summary>
/// One map file (a zone's base map or one of its layers 1 to 3): its lines and labels. Lines are kept exactly as they were read,
/// so saving a layer only changes its labels.
/// </summary>
public sealed class MapLayer
{
    public MapLayer(IReadOnlyList<MapLine> lines, IReadOnlyList<MapLabel> labels, IReadOnlyList<string> otherText)
    {
        Lines = lines;
        Labels = labels;
        OtherText = otherText;
    }

    public static MapLayer Empty { get; } = new(Array.Empty<MapLine>(), Array.Empty<MapLabel>(), Array.Empty<string>());

    public IReadOnlyList<MapLine> Lines { get; }

    public IReadOnlyList<MapLabel> Labels { get; }

    /// <summary>Every line of the file that is not a label, as it was (map lines and anything else), kept for saving.</summary>
    public IReadOnlyList<string> OtherText { get; }

    public bool IsEmpty => Lines.Count == 0 && Labels.Count == 0;

    /// <summary>Reads a map file's text. Lines that cannot be read are kept as they are and not drawn.</summary>
    public static MapLayer Parse(IEnumerable<string> fileLines)
    {
        ArgumentNullException.ThrowIfNull(fileLines);
        var lines = new List<MapLine>();
        var labels = new List<MapLabel>();
        var other = new List<string>();
        foreach (string raw in fileLines)
        {
            string text = raw.TrimEnd('\r');
            if (TryParseLabel(text, out MapLabel? label))
            {
                labels.Add(label);
                continue;
            }

            if (text.Trim().Length > 0)
            {
                other.Add(text);
            }

            if (TryParseLine(text, out MapLine? line))
            {
                lines.Add(line);
            }
        }

        return new MapLayer(lines, labels, other);
    }

    /// <summary>The same layer with other labels; its lines stay as they are.</summary>
    public MapLayer WithLabels(IEnumerable<MapLabel> labels) => new(Lines, labels.ToList(), OtherText);

    /// <summary>The file's text: its other lines as they were, then its labels.</summary>
    public string ToFileText()
    {
        var text = new StringBuilder();
        foreach (string line in OtherText)
        {
            text.Append(line).Append("\r\n");
        }

        foreach (MapLabel label in Labels)
        {
            text.Append(FormatLabel(label)).Append("\r\n");
        }

        return text.ToString();
    }

    public static string FormatLabel(MapLabel label)
    {
        string name = string.Join('_', label.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"P {label.X:0.0000}, {label.Y:0.0000}, {label.Z:0.0000},  {label.R}, {label.G}, {label.B},  {Math.Clamp(label.Size, 1, 3)},  {name}");
    }

    public static bool TryParseLabel(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MapLabel? label)
    {
        label = null;
        string trimmed = text.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != 'P' || trimmed[1] != ' ')
        {
            return false;
        }

        string[] parts = trimmed[2..].Split(',', 8);
        if (parts.Length < 8
            || !TryNumber(parts[0], out double x) || !TryNumber(parts[1], out double y) || !TryNumber(parts[2], out double z)
            || !TryByte(parts[3], out byte r) || !TryByte(parts[4], out byte g) || !TryByte(parts[5], out byte b)
            || !TryNumber(parts[6], out double size))
        {
            return false;
        }

        string name = parts[7].Trim().Replace('_', ' ');
        label = new MapLabel(x, y, z, r, g, b, (int)Math.Round(size), name);
        return true;
    }

    private static bool TryParseLine(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MapLine? line)
    {
        line = null;
        string trimmed = text.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != 'L' || trimmed[1] != ' ')
        {
            return false;
        }

        string[] parts = trimmed[2..].Split(',');
        if (parts.Length < 9
            || !TryNumber(parts[0], out double x1) || !TryNumber(parts[1], out double y1) || !TryNumber(parts[2], out double z1)
            || !TryNumber(parts[3], out double x2) || !TryNumber(parts[4], out double y2) || !TryNumber(parts[5], out double z2)
            || !TryByte(parts[6], out byte r) || !TryByte(parts[7], out byte g) || !TryByte(parts[8], out byte b))
        {
            return false;
        }

        line = new MapLine(x1, y1, z1, x2, y2, z2, r, g, b);
        return true;
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static bool TryByte(string text, out byte value)
    {
        value = 0;
        if (!TryNumber(text, out double number))
        {
            return false;
        }

        value = (byte)Math.Clamp(Math.Round(number), 0, 255);
        return true;
    }
}
