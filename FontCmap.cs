using System.Text;

namespace ControlFontTool;

/// <summary>
/// Minimal TrueType/OpenType cmap reader: enough to report which codepoints of a
/// character set the font actually covers (format 4 and format 12 subtables).
/// Supports TTC containers (first face) and 'ttcf'/'OTTO'/'true'/glyf magic.
/// All sfnt fields are big-endian.
/// </summary>
public sealed class FontCmap
{
    readonly uint[] _starts, _ends, _glyphs;           // format 12 groups
    readonly ushort[] _f4Starts, _f4Ends, _f4Deltas;   // format 4 segments
    readonly int[] _f4RangeOffsets;                    // absolute offsets of idRangeOffset arrays (int.MaxValue when zero)
    readonly byte[] _data;

    FontCmap(byte[] data, uint[] starts, uint[] ends, uint[] glyphs,
        ushort[] f4Starts, ushort[] f4Ends, ushort[] f4Deltas, int[] f4RangeOffsets)
    {
        _data = data; _starts = starts; _ends = ends; _glyphs = glyphs;
        _f4Starts = f4Starts; _f4Ends = f4Ends; _f4Deltas = f4Deltas; _f4RangeOffsets = f4RangeOffsets;
    }

    static ushort BE16(byte[] b, int i) => (ushort)(b[i] << 8 | b[i + 1]);
    static uint BE32(byte[] b, int i) => (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);
    static uint Tag(byte[] b, int i) => BE32(b, i);

    public static FontCmap Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12) throw new InvalidDataException("Font file is too small.");
        if (Tag(bytes, 0) == 0x74746366 /*ttcf*/)
        {
            if (bytes.Length < 16) throw new InvalidDataException("TTC header is incomplete.");
            var offset = BE32(bytes, 12);
            if (offset + 12 > bytes.Length) throw new InvalidDataException("TTC face offset is out of range.");
            return Parse(bytes, (int)offset);
        }
        return Parse(bytes, 0);
    }

    static FontCmap Parse(byte[] bytes, int faceOffset)
    {
        uint magic = Tag(bytes, faceOffset);
        if (magic != 0x00010000 && magic != 0x4F54544F /*OTTO*/ && magic != 0x74727565 /*true*/)
            throw new InvalidDataException("Not a TrueType/OpenType font (or unsupported outline format).");
        int numTables = BE16(bytes, faceOffset + 4);
        int cmapOffset = -1;
        for (int i = 0; i < numTables; i++)
        {
            int rec = faceOffset + 12 + i * 16;
            if (rec + 16 > bytes.Length) break;
            if (Tag(bytes, rec) == 0x636D6170 /*cmap*/)
            {
                cmapOffset = (int)BE32(bytes, rec + 8);
                break;
            }
        }
        if (cmapOffset < 0 || faceOffset + cmapOffset + 4 > bytes.Length)
            throw new InvalidDataException("Font has no cmap table.");
        cmapOffset += faceOffset;

        int numSub = BE16(bytes, cmapOffset + 2);
        int bestF4 = -1, bestF12 = -1;
        for (int i = 0; i < numSub; i++)
        {
            int rec = cmapOffset + 4 + i * 8;
            if (rec + 8 > bytes.Length) break;
            int platform = BE16(bytes, rec);
            int encoding = BE16(bytes, rec + 2);
            int off = (int)BE32(bytes, rec + 4);
            int sub = cmapOffset + off;
            if (sub + 2 > bytes.Length) continue;
            int format = BE16(bytes, sub);
            if (format == 4 && bestF4 < 0 && (platform == 3 && encoding == 1 || platform == 0)) bestF4 = sub;
            if (format == 12 && bestF12 < 0 && (platform == 3 && encoding == 10 || platform == 0)) bestF12 = sub;
        }
        if (bestF4 < 0 && bestF12 < 0)
            throw new InvalidDataException("Font cmap has no supported subtable (format 4/12).");

        var f4Starts = Array.Empty<ushort>(); var f4Ends = Array.Empty<ushort>();
        var f4Deltas = Array.Empty<ushort>(); var f4Ranges = Array.Empty<int>();
        if (bestF4 >= 0)
        {
            int segCountX2 = BE16(bytes, bestF4 + 6);
            int segCount = segCountX2 / 2;
            int endBase = bestF4 + 14;
            int startBase = endBase + segCountX2 + 2;
            int deltaBase = startBase + segCountX2;
            int rangeBase = deltaBase + segCountX2;
            f4Starts = new ushort[segCount]; f4Ends = new ushort[segCount];
            f4Deltas = new ushort[segCount]; f4Ranges = new int[segCount];
            for (int i = 0; i < segCount; i++)
            {
                f4Ends[i] = BE16(bytes, endBase + i * 2);
                f4Starts[i] = BE16(bytes, startBase + i * 2);
                f4Deltas[i] = BE16(bytes, deltaBase + i * 2);
                int rangeOff = BE16(bytes, rangeBase + i * 2);
                f4Ranges[i] = rangeOff == 0 ? int.MaxValue : rangeBase + i * 2 + rangeOff;
            }
        }

        var gStarts = new List<uint>(); var gEnds = new List<uint>(); var gGlyphs = new List<uint>();
        if (bestF12 >= 0)
        {
            uint nGroups = BE32(bytes, bestF12 + 12);
            for (uint g = 0; g < nGroups; g++)
            {
                int rec = bestF12 + 16 + (int)g * 12;
                if (rec + 12 > bytes.Length) break;
                gStarts.Add(BE32(bytes, rec));
                gEnds.Add(BE32(bytes, rec + 4));
                gGlyphs.Add(BE32(bytes, rec + 8));
            }
        }

        return new FontCmap(bytes, gStarts.ToArray(), gEnds.ToArray(), gGlyphs.ToArray(),
            f4Starts, f4Ends, f4Deltas, f4Ranges);
    }

    public bool HasGlyph(uint codepoint)
    {
        for (int i = 0; i < _f4Starts.Length; i++)
        {
            if (codepoint > _f4Ends[i]) continue;
            if (codepoint < _f4Starts[i]) return false;
            if (_f4RangeOffsets[i] == int.MaxValue)
                return ((codepoint + _f4Deltas[i]) & 0xFFFF) != 0;
            int idx = _f4RangeOffsets[i] + (int)(codepoint - _f4Starts[i]) * 2;
            if (idx + 2 <= _data.Length)
            {
                var direct = BE16(_data, idx);
                if (direct != 0) return true;
            }
            return ((codepoint + _f4Deltas[i]) & 0xFFFF) != 0;
        }
        if (_starts.Length > 0)
        {
            int lo = 0, hi = _starts.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (codepoint > _ends[mid]) lo = mid + 1;
                else if (codepoint < _starts[mid]) hi = mid - 1;
                else return _glyphs[mid] + (codepoint - _starts[mid]) != 0;
            }
        }
        return false;
    }
}
