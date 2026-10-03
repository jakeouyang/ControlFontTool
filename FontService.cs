using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ControlFontTool;

public static class FontService
{
    public const string Owner = "ControlFontTool/1.1";
    const string ManifestName = "controlfonttool.json";
    const string BackupRoot = ".ControlFontTool";
    static readonly string[] GameProcesses = { "Control", "Control_DX11", "Control_DX12" };

    public static class Slots
    {
        public const string UiDir = @"data\uiresources\p7";
        public const string FontsDir = UiDir + @"\fonts";
        // SC keeps the game's own slot names; TC/EN use dedicated decoupled slot names
        // that UiPatcher repoints the game CSS to (original NotoSansTC files stay untouched).
        public static readonly string[] SimplifiedChinese = { "NotoSansSC-Regular.otf", "NotoSansSC-Bold.otf" };
        public static readonly string[] TraditionalChinese = { "NotoSansZH-Regular.otf", "NotoSansZH-Bold.otf" };
        public static readonly string[] English = { "NotoSansEN-Regular.otf", "NotoSansEN-Bold.otf" };
        // Western display fonts are global (shared by every language in the game's CSS).
        public static readonly string[] Western =
        {
            "AkzidGrtskProReg.otf", "AkzidGrtskProBol.otf",
            "AktivGrotesk_Rg.ttf", "AktivGrotesk_Bd.ttf",
            "Interstate Bold Condensed.ttf", "ITCAvantGardePro-Bold.otf"
        };
    }

    sealed record UiEntry(string Mode, string WrittenHash, string? PreviousHash);
    sealed record Manifest(string Owner, string Installed, string? BackupDir,
        Dictionary<string, string> Fonts, Dictionary<string, UiEntry> Ui);

    static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    static string HashBytes(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>
    /// Loads a pristine .ui file: embedded resource first, then a Resources/Ui folder next to the
    /// executable. Game UI assets are copyrighted and are NOT distributed with the source; see
    /// README ("Preparing the UI resources") for the one-time extraction step.
    /// </summary>
    static byte[] EmbeddedUi(string file)
    {
        var resource = typeof(FontService).Assembly
            .GetManifestResourceStream($"ControlFontTool.Resources.Ui.{file}");
        if (resource != null)
        {
            using (resource)
            {
                var output = new MemoryStream();
                resource.CopyTo(output);
                return output.ToArray();
            }
        }
        var diskPath = Path.Combine(AppContext.BaseDirectory, "Resources", "Ui", file);
        if (File.Exists(diskPath)) return File.ReadAllBytes(diskPath);
        throw new InvalidDataException(
            "Pristine UI resource missing: " + file +
            "（未找到原始 .ui 资源）。请先从游戏档案中提取 data\\uiresources\\p7\\*.ui 并放入 Resources\\Ui 目录，" +
            "详见 README 的 Preparing the UI resources 章节。");
    }

    public static string ValidateGame(string game)
    {
        if (!Directory.Exists(game)) return "Game directory does not exist.";
        if (!File.Exists(Path.Combine(game, "Control.exe"))) return "Control.exe not found — is this the game root folder?";
        return "";
    }

    static void EnsureNotRunning()
    {
        foreach (var name in GameProcesses)
            if (Process.GetProcessesByName(name).Length > 0)
                throw new IOException("Exit Control before installing or restoring fonts.");
    }

    public static void Install(string game, string font, Action<string> report,
        FontCoverage coverage = FontCoverage.SimplifiedChinese | FontCoverage.English,
        bool western = false, bool chinese = false)
    {
        game = Path.GetFullPath(game);
        var validationError = ValidateGame(game);
        if (validationError.Length > 0) throw new InvalidDataException(validationError);
        if (!File.Exists(font)) throw new FileNotFoundException("Font file not found.", font);
        if (!File.Exists(Path.Combine(game, "iphlpapi.dll")))
            report(chinese ? "警告：未检测到 Loose Files Loader (iphlpapi.dll)，游戏不会读取散装字体文件！" :
                "WARNING: Loose Files Loader (iphlpapi.dll) not found — the game will NOT load loose fonts!");
        EnsureNotRunning();

        string T(string en, string zh) => chinese ? zh : en;

        report(T("Reading font…", "正在读取字体…"));
        var cmap = FontCmap.Load(font);
        var requested = FontCharacterSets.Create(coverage);
        var supported = requested.Where(cmap.HasGlyph).ToList();
        if (supported.Count == 0)
            throw new InvalidDataException(T("The selected font has no usable glyphs.", "所选字体没有任何可用字形。"));
        if (((coverage & FontCoverage.SimplifiedChinese) != 0 || (coverage & FontCoverage.TraditionalChinese) != 0)
            && !supported.HasAnyCjk())
            throw new InvalidDataException(T(
                "The selected font has no Chinese (CJK) glyphs — refusing to build a Chinese font library.",
                "所选字体不包含任何汉字字形，无法生成中文字库。"));
        report($"{FontCharacterSets.Describe(coverage, chinese)} — {supported.Count:N0}/{requested.Count:N0} " + T("characters covered.", "个字符已覆盖。"));
        var missing = requested.Except(supported).ToArray();
        if (missing.Length > 0)
        {
            var samples = missing.Where(c => c >= 0x3000).Concat(missing.Where(c => c < 0x3000)).Take(12)
                .Select(c => char.ConvertFromUtf32((int)c));
            report(T($"Missing {missing.Length:N0} characters (not embedded), e.g. {string.Concat(samples)}",
                     $"缺字 {missing.Length:N0} 个（不会嵌入），例如：{string.Concat(samples)}"));
        }

        var uiDir = Path.Combine(game, Slots.UiDir);
        var fontsDir = Path.Combine(game, Slots.FontsDir);
        var previous = ReadManifest(fontsDir);
        bool english = (coverage & FontCoverage.English) != 0;
        bool traditional = (coverage & FontCoverage.TraditionalChinese) != 0;

        // Refuse silently-orphaning a previous repoint when its language is no longer selected.
        if (File.Exists(Path.Combine(uiDir, "hud.ui")))
        {
            var probe = File.ReadAllText(Path.Combine(uiDir, "hud.ui"), Encoding.Latin1);
            if (!english && probe.Contains("NatoSansEN", StringComparison.Ordinal))
                throw new InvalidDataException(T(
                    "A previous English font repoint was detected but English is not selected. Restore first, or include English.",
                    "检测到先前的英文字体重定向，但本次未勾选英文。请先还原，或勾选英文。"));
            if (!traditional && probe.Contains("NatoSansZH", StringComparison.Ordinal))
                throw new InvalidDataException(T(
                    "A previous Traditional Chinese font repoint was detected but Traditional Chinese is not selected. Restore first, or include Traditional Chinese.",
                    "检测到先前的繁体字体重定向，但本次未勾选繁体中文。请先还原，或勾选繁体中文。"));
        }

        var backupDir = previous?.BackupDir ??
            Path.Combine(game, BackupRoot, "backups",
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));

        // ---- UI repointing (composes with any existing loose .ui, e.g. icon mods) ----
        var uiEntries = new Dictionary<string, UiEntry>();
        foreach (var file in UiPatcher.UiFiles)
        {
            var loosePath = Path.Combine(uiDir, file);
            var baseBytes = EmbeddedUi(file);
            string mode = "created";
            string? previousHash = null;
            bool chain = false;
            if (File.Exists(loosePath))
            {
                baseBytes = File.ReadAllBytes(loosePath);
                previousHash = HashBytes(baseBytes);
                mode = "modified";
                // Chained reinstall: the current file is exactly what our previous install
                // wrote, so the existing backup still holds the true pre-tool state.
                if (previous?.Ui != null && previous.Ui.TryGetValue(file, out var prev) && previousHash == prev.WrittenHash)
                {
                    mode = prev.Mode; previousHash = prev.PreviousHash; chain = true;
                }
                else
                {
                    Directory.CreateDirectory(backupDir);
                    File.WriteAllBytes(Path.Combine(backupDir, file), baseBytes);
                }
            }
            var patched = UiPatcher.Apply(baseBytes, english, traditional, out var replacements);
            var alreadyPatched = (english && Encoding.Latin1.GetString(patched).Contains("NatoSansEN", StringComparison.Ordinal)) ||
                                 (traditional && Encoding.Latin1.GetString(patched).Contains("NatoSansZH", StringComparison.Ordinal));
            if (replacements == 0 && !alreadyPatched) continue;   // file carries no language font CSS
            Directory.CreateDirectory(uiDir);
            File.WriteAllBytes(loosePath, patched);
            uiEntries[file] = new UiEntry(mode, HashBytes(patched), previousHash);
            var note = replacements == 0 ? T("already repointed", "已是重定向状态") : $"{replacements} {T("bindings", "处绑定")}";
            report(T($"UI repointed: {file} ({note}, {(mode == "created" ? "new loose file" : chain ? "existing loose file updated (chained)" : "existing loose file updated")})",
                     $"已重定向 UI：{file}（{note}，{(mode == "created" ? "新建散装文件" : chain ? "更新已有散装文件（链式）" : "更新已有散装文件")}）"));
        }
        if (uiEntries.Count > 0)
            report(T("English now uses NotoSansEN slots; Traditional Chinese uses NotoSansZH slots. Japanese/Korean UI falls back to the default font.",
                     "英文改用 NotoSansEN 槽位，繁體中文改用 NotoSansZH 槽位；日文/韩文界面回退为默认字体。"));

        // Write the manifest before touching font files so a mid-install crash stays recoverable.
        var manifest = new Manifest(Owner, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            uiEntries.Count > 0 ? backupDir : null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), uiEntries);
        var manifestPath = Path.Combine(fontsDir, ManifestName);
        Directory.CreateDirectory(fontsDir);
        WriteManifest(manifestPath, manifest);

        // ---- Font files ----
        var slots = new List<string>();
        void AddRange(IEnumerable<string> files)
        {
            foreach (var f in files)
                if (!slots.Contains(f, StringComparer.OrdinalIgnoreCase))
                    slots.Add(f);
        }
        if ((coverage & FontCoverage.SimplifiedChinese) != 0) AddRange(Slots.SimplifiedChinese);
        if (traditional) AddRange(Slots.TraditionalChinese);
        if (english) AddRange(Slots.English);
        if (english && western) AddRange(Slots.Western);

        // Clean up font slots from a previous install that are no longer part of the selection.
        if (previous?.Fonts != null)
            foreach (var old in previous.Fonts.Keys)
                if (!slots.Contains(old, StringComparer.OrdinalIgnoreCase) && File.Exists(Path.Combine(fontsDir, old)))
                {
                    File.Delete(Path.Combine(fontsDir, old));
                    report(T($"Removed previous slot: {old}", $"已移除先前槽位：{old}"));
                }

        var fontHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var slot in slots)
        {
            var destination = Path.Combine(fontsDir, slot);
            File.Copy(font, destination, true);
            fontHashes[slot] = Hash(destination);
            total += new FileInfo(destination).Length;
            report(T($"Installed slot: {slot} ({new FileInfo(destination).Length / 1048576.0:F1} MB)",
                     $"已安装槽位：{slot}（{new FileInfo(destination).Length / 1048576.0:F1} MB）"));
        }

        manifest = manifest with { Fonts = fontHashes };
        WriteManifest(manifestPath, manifest);
        report(T($"Done — {slots.Count} font slots, {total / 1048576.0:F1} MB total in {fontsDir}.",
                 $"完成 — 共 {slots.Count} 个槽位，合计 {total / 1048576.0:F1} MB，位于 {fontsDir}。"));
        report(T("Launch the game and check the UI in the selected language.", "启动游戏，在所选语言下检查菜单与字幕效果。"));
    }

    static Manifest? ReadManifest(string fontsDir)
    {
        var path = Path.Combine(fontsDir, ManifestName);
        if (!File.Exists(path)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path));
            return manifest?.Owner == Owner ? manifest : null;
        }
        catch { return null; }
    }

    static void WriteManifest(string path, Manifest manifest) =>
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

    public static void Restore(string game, Action<string> report, bool chinese = false)
    {
        game = Path.GetFullPath(game);
        var validationError = ValidateGame(game);
        if (validationError.Length > 0) throw new InvalidDataException(validationError);
        EnsureNotRunning();
        string T(string en, string zh) => chinese ? zh : en;

        var manifestPath = Path.Combine(game, Slots.FontsDir, ManifestName);
        if (!File.Exists(manifestPath))
        {
            report(T("No ControlFontTool manifest found; nothing to restore.",
                     "未找到本工具的安装清单，无需还原。"));
            return;
        }
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
        if (manifest?.Owner != Owner || manifest.Fonts == null)
            throw new IOException(T("Manifest is not owned by this tool; refusing to touch it.",
                                    "清单文件不属于本工具，已停止操作。"));

        var fontsDir = Path.Combine(game, Slots.FontsDir);
        var removed = 0;
        foreach (var (relative, hash) in manifest.Fonts)
        {
            var path = Path.Combine(fontsDir, relative);
            if (!File.Exists(path)) { report(T($"Already absent: {relative}", $"文件本就不存在：{relative}")); continue; }
            if (Hash(path) != hash)
                report(T($"Warning: {relative} was modified externally; deleting it anyway.",
                         $"警告：{relative} 已被外部修改，仍将删除。"));
            File.Delete(path);
            removed++;
            report(T($"Removed: {relative}", $"已移除：{relative}"));
        }

        var uiRestored = 0;
        if (manifest.Ui is { Count: > 0 })
        {
            foreach (var (file, entry) in manifest.Ui)
            {
                var loosePath = Path.Combine(game, Slots.UiDir, file);
                if (entry.Mode == "created")
                {
                    if (!File.Exists(loosePath)) continue;
                    if (Hash(loosePath) != entry.WrittenHash)
                        report(T($"Warning: {file} was modified externally; deleting it anyway.", $"警告：{file} 已被外部修改，仍将删除。"));
                    File.Delete(loosePath);
                    uiRestored++;
                    report(T($"Removed loose UI file: {file}", $"已删除散装 UI 文件：{file}"));
                }
                else // modified — restore the backed-up previous loose file (keeps other mods intact)
                {
                    var backupPath = manifest.BackupDir == null ? null : Path.Combine(manifest.BackupDir, file);
                    if (backupPath == null || !File.Exists(backupPath))
                    {
                        report(T($"Warning: backup for {file} is missing; leaving the current file in place.", $"警告：{file} 的备份缺失，保留当前文件。"));
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(loosePath)!);
                    File.Copy(backupPath, loosePath, true);
                    uiRestored++;
                    report(T($"Restored previous UI file: {file}", $"已还原先前的 UI 文件：{file}"));
                }
            }
        }

        File.Delete(manifestPath);
        report(T($"Restored: {removed} font file(s) and {uiRestored} UI file(s). The game's original fonts are active again.",
                 $"还原完成：移除 {removed} 个字体文件、恢复 {uiRestored} 个 UI 文件，游戏原版字体已恢复生效。"));
        if (manifest.BackupDir != null && Directory.Exists(manifest.BackupDir))
            report(T($"Backups kept at: {manifest.BackupDir}", $"备份保留在：{manifest.BackupDir}"));
    }
}

