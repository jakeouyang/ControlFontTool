using System.Reflection;
using System.Text;

namespace ControlFontTool;

[Flags]
public enum FontCoverage
{
    SimplifiedChinese = 1,
    TraditionalChinese = 2,
    English = 4
}

public static class FontCharacterSets
{
    static readonly Lazy<IReadOnlySet<uint>> Simplified = new(() => ReadStandardTable("charset-gb2312.txt"));
    static readonly Lazy<IReadOnlySet<uint>> Traditional = new(() => ReadStandardTable("charset-big5.txt"));

    static IReadOnlySet<uint> ReadStandardTable(string file)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("ControlFontTool.Resources." + file)
            ?? throw new InvalidDataException("Missing embedded character table: " + file);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().EnumerateRunes().Select(r => (uint)r.Value).ToHashSet();
    }

    /// <summary>Basic Latin, Latin-1, common punctuation, symbols, CJK punctuation and fullwidth forms.</summary>
    static IEnumerable<uint> BaseSet()
    {
        for (var c = 0x20u; c < 0x7Fu; c++) yield return c;                 // ASCII
        for (var c = 0xA0u; c < 0x100u; c++) yield return c;                // Latin-1 Supplement
        foreach (var c in new[] { 0x20ACu /*€*/, 0x2122u /*™*/, 0x2126u /*Ω*/, 0x2103u /*℃*/, 0x2116u /*№*/ })
            yield return c;
        for (var c = 0x2010u; c <= 0x2027u; c++) yield return c;            // hyphens, quotes, ellipsis
        for (var c = 0x2030u; c <= 0x203Bu; c++) yield return c;            // per-mille, primes, references
        for (var c = 0x2190u; c <= 0x2193u; c++) yield return c;            // arrows
        for (var c = 0x2460u; c <= 0x2473u; c++) yield return c;            // circled numbers
        for (var c = 0x25A0u; c <= 0x25CFu; c++) yield return c;            // geometric shapes
        for (var c = 0x2605u; c <= 0x2606u; c++) yield return c;            // stars
        for (var c = 0x2660u; c <= 0x2667u; c++) yield return c;            // card suits
        for (var c = 0x3000u; c <= 0x303Fu; c++) yield return c;            // CJK punctuation
        for (var c = 0x3099u; c <= 0x309Cu; c++) yield return c;            // kana voicing marks
        for (var c = 0xFF01u; c <= 0xFF5Eu; c++) yield return c;            // fullwidth forms
        for (var c = 0xFFE0u; c <= 0xFFE5u; c++) yield return c;            // fullwidth signs
        foreach (var c in new[] { 0x00B0u, 0x00B1u, 0x00B2u, 0x00B3u, 0x00B5u, 0x00B7u, 0x00D7u, 0x00F7u,
                                   0x2022u, 0x2032u, 0x2033u, 0x2212u /*−*/, 0x2260u, 0x2264u, 0x2265u, 0x2248u })
            yield return c;
    }

    /// <summary>Union of the base set plus each selected language's table.</summary>
    public static List<uint> Create(FontCoverage coverage)
    {
        if (coverage == 0 || (coverage & ~(FontCoverage.SimplifiedChinese | FontCoverage.TraditionalChinese | FontCoverage.English)) != 0)
            throw new ArgumentException("Select at least one character set.", nameof(coverage));
        var result = new SortedSet<uint>(BaseSet());
        if ((coverage & FontCoverage.SimplifiedChinese) != 0) result.UnionWith(Simplified.Value);
        if ((coverage & FontCoverage.TraditionalChinese) != 0) result.UnionWith(Traditional.Value);
        return result.ToList();
    }

    public static bool HasAnyCjk(this IReadOnlyCollection<uint> codes) => codes.Any(c => c is >= 0x3400 and <= 0x9FFF or >= 0x30000 and <= 0x3134F);

    public static string Describe(FontCoverage coverage, bool chinese) => string.Join(" + ",
        new[] {
            (FontCoverage.SimplifiedChinese, chinese ? "简体中文" : "Simplified Chinese"),
            (FontCoverage.TraditionalChinese, chinese ? "繁體中文" : "Traditional Chinese"),
            (FontCoverage.English, chinese ? "英文" : "English")
        }.Where(item => (coverage & item.Item1) != 0).Select(item => item.Item2));
}
