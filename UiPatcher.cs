using System.Text;
using System.Text.RegularExpressions;

namespace ControlFontTool;

/// <summary>
/// Repoints the game's language-specific font families inside the .ui (rbin) resources
/// so that English / Traditional Chinese get their own decoupled font slots:
///
///   English : hijacks the unused-by-this-tool Japanese  slot — "NatoSansJP"→"NatoSansEN",
///             "NotoSansJP-*.otf"→"NotoSansEN-*.otf", ":lang(ja)"→":lang(en)"
///   Trad.   : hijacks the Korean slot — "NatoSansKR"→"NatoSansZH",
///             "NotoSansKR-*.otf"→"NotoSansZH-*.otf", ":lang(ko)"→":lang(zhtw)"
///
/// All replacements are length-preserving: ":lang(zhtw)" is two bytes longer than
/// ":lang(ko)", the difference is reclaimed from insignificant whitespace inside the
/// same CSS rule (after ':', ',', around '{'). Original NotoSansTC/SC files are never touched.
/// </summary>
public static class UiPatcher
{
    public static readonly string[] UiFiles =
    {
        "hud.ui", "menu.ui", "system.ui", "photo.ui", "loadingscreen.ui",
        "intro.ui", "splash.ui", "persistent.ui"
    };

    static readonly (string From, string To)[] EnglishSwaps =
    {
        ("NatoSansJP", "NatoSansEN"),
        ("NotoSansJP-Regular.otf", "NotoSansEN-Regular.otf"),
        ("NotoSansJP-Bold.otf", "NotoSansEN-Bold.otf"),
        (":lang(ja)", ":lang(en)"),
    };

    static readonly (string From, string To)[] TraditionalSwaps =
    {
        ("NatoSansKR", "NatoSansZH"),
        ("NotoSansKR-Regular.otf", "NotoSansZH-Regular.otf"),
        ("NotoSansKR-Bold.otf", "NotoSansZH-Bold.otf"),
    };

    public static byte[] Apply(byte[] input, bool english, bool traditional, out int replacements)
    {
        var text = Encoding.Latin1.GetString(input);
        replacements = 0;
        if (english)
        {
            replacements += ApplySwaps(ref text, EnglishSwaps);
            replacements += RewriteLangRules(ref text, ":lang(ja)", ":lang(en)");
        }
        if (traditional)
        {
            replacements += ApplySwaps(ref text, TraditionalSwaps);
            replacements += RewriteLangRules(ref text, ":lang(ko)", ":lang(zhtw)");
        }
        return Encoding.Latin1.GetBytes(text);
    }

    static int ApplySwaps(ref string text, (string From, string To)[] swaps)
    {
        var count = 0;
        foreach (var (from, to) in swaps)
        {
            if (from.Length != to.Length) throw new InvalidOperationException("internal: swap must be length-preserving");
            count += CountOccurrences(text, from);
            text = text.Replace(from, to);
        }
        return count;
    }

    static int CountOccurrences(string text, string pattern)
    {
        var count = 0; var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0) { count++; index += pattern.Length; }
        return count;
    }

    static int RewriteLangRules(ref string text, string oldSelector, string newSelector)
    {
        var deficit = newSelector.Length - oldSelector.Length;
        var count = 0;
        var pattern = new Regex(Regex.Escape(oldSelector) + @"\s*\{[^}]*\}", RegexOptions.Compiled);
        text = pattern.Replace(text, match => ConvertRule(match.Value, oldSelector, newSelector, deficit, ref count));
        return count;
    }

    static string ConvertRule(string rule, string oldSelector, string newSelector, int deficit, ref int count)
    {
        var body = rule.Substring(oldSelector.Length);
        var reclaimed = ReclaimWhitespace(body, deficit);
        if (reclaimed == null)
            throw new InvalidDataException(
                $"CSS rule lacks reclaimable whitespace for {oldSelector} -> {newSelector}: {rule[..Math.Min(120, rule.Length)]}");
        count++;
        return newSelector + reclaimed;
    }

    /// <summary>Removes <paramref name="count"/> whitespace chars from insignificant positions (outside quotes,
    /// adjacent to '{', '}', ':', ',', ';'). Returns null when not enough can be reclaimed.</summary>
    static string? ReclaimWhitespace(string body, int count)
    {
        if (count <= 0) return body;
        var removable = new List<int>();
        var inString = false;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c == '"') { inString = !inString; continue; }
            if (inString || !char.IsWhiteSpace(c)) continue;
            int prev = i - 1, next = i + 1;
            while (prev >= 0 && char.IsWhiteSpace(body[prev])) prev--;
            while (next < body.Length && char.IsWhiteSpace(body[next])) next++;
            char p = prev >= 0 ? body[prev] : '{';
            char n = next < body.Length ? body[next] : '}';
            if (p is '{' or ':' or ',' or ';' or '}' || n is '{' or '}' or ':' or ',')
                removable.Add(i);
        }
        if (removable.Count < count) return null;
        var keep = new HashSet<int>(removable.Take(removable.Count - count));
        var sb = new StringBuilder(body.Length - count);
        for (int i = 0; i < body.Length; i++)
            if (!removable.Contains(i) || keep.Contains(i))
                sb.Append(body[i]);
        return sb.ToString();
    }
}
