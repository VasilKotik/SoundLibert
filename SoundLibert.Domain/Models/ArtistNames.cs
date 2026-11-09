using System.Text;

namespace SoundLibert.Domain.Models;

/// <summary>
/// One artist goes by several spellings: a song can say "Океан Ельзи" where the catalogue
/// says "Okean Elzy". Comparing the two letter by letter left such artists without a
/// picture and without a page.
/// </summary>
public static class ArtistNames
{
    private static readonly Dictionary<char, string> Latin = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['ґ'] = "g", ['д'] = "d", ['е'] = "e",
        ['є'] = "ye", ['ё'] = "e", ['ж'] = "zh", ['з'] = "z", ['и'] = "y", ['і'] = "i", ['ї'] = "yi",
        ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p",
        ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e",
        ['ю'] = "yu", ['я'] = "ya"
    };

    /// <summary>True when two spellings read as one name, whichever alphabet each is in.</summary>
    public static bool SoundTheSame(string? one, string? other)
    {
        var a = Spoken(one);
        var b = Spoken(other);
        if (a.Length < 3 || b.Length < 3) return false;
        if (a == b) return true;

        // Transliteration has no single spelling ("Ирина" is Iryna and Irina), so a few
        // letters may differ; the length of the name decides how many.
        return Distance(a, b) <= Math.Max(a.Length, b.Length) / 5;
    }

    /// <summary>
    /// The name in plain Latin letters and digits, nothing else. "y" and "i" count as one:
    /// that is the usual difference between two spellings of one name (Skryabin, Skriabin).
    /// </summary>
    private static string Spoken(string? name)
    {
        var spoken = new StringBuilder();
        foreach (var letter in (name ?? string.Empty).ToLowerInvariant())
        {
            if (Latin.TryGetValue(letter, out var latin)) spoken.Append(latin);
            else if (char.IsLetterOrDigit(letter)) spoken.Append(letter);
        }
        return spoken.Replace('y', 'i').ToString();
    }

    /// <summary>How many letters to change to turn one into the other.</summary>
    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var replace = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(replace, Math.Min(previous[j], current[j - 1]) + 1);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
