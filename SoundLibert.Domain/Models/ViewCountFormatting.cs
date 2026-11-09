using System.Globalization;

namespace SoundLibert.Domain.Models;

/// <summary>
/// Formats a global YouTube view count the way YouTube/Spotify do it: a short
/// magnitude label ("1.2B views", "723M views", "45K views") with a dot decimal
/// separator and no trailing ".0".
/// </summary>
public static class ViewCountFormatting
{
    public static string FormatViews(long views)
    {
        if (views < 0) views = 0;
        return $"{FormatNumber(views)} views";
    }

    /// <summary>The same number for an audio track, which is played rather than viewed.</summary>
    public static string FormatPlays(long plays)
    {
        if (plays < 0) plays = 0;
        return $"{FormatNumber(plays)} plays";
    }

    /// <summary>The bare magnitude ("1.2B", "723M", "4,519"), without the unit.</summary>
    public static string FormatNumber(long value)
    {
        if (value >= 1_000_000_000) return Trim(value / 1_000_000_000.0) + "B";
        if (value >= 1_000_000) return Trim(value / 1_000_000.0) + "M";
        if (value >= 1_000) return Trim(value / 1_000.0) + "K";
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string Trim(double v)
    {
        // One decimal, but drop a trailing ".0" ("2.0M" -> "2M").
        var text = v.ToString("0.0", CultureInfo.InvariantCulture);
        return text.EndsWith(".0", StringComparison.Ordinal)
            ? text[..^2]
            : text;
    }
}
