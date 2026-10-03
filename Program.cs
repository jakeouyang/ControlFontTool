using System.Runtime.InteropServices;

namespace ControlFontTool;

internal static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 0) { Application.Run(new MainForm()); return 0; }
        AttachConsole(-1);
        try
        {
            switch (args)
            {
                case ["install", var game, var font, .. var options]:
                    FontService.Install(game, font, Console.WriteLine, ParseCoverage(options),
                        options.Contains("--western"), chinese: options.Contains("--zh"));
                    break;
                case ["restore", var game, .. var options]:
                    FontService.Restore(game, Console.WriteLine, chinese: options.Contains("--zh"));
                    break;
                case ["validate-game", var game]:
                    if (FontService.ValidateGame(game) is { Length: > 0 } error) throw new InvalidDataException(error);
                    Console.WriteLine("VALID");
                    break;
                case ["--render-ui", var output, .. var options]:
                    try
                    {
                        using (var form = new MainForm())
                        {
                            form.SelectInterfaceLanguage(options.Contains("--zh"));
                            form.Show(); Application.DoEvents();
                            using var bitmap = new Bitmap(form.Width, form.Height);
                            form.DrawToBitmap(bitmap, form.ClientRectangle);
                            bitmap.Save(output);
                        }
                        Console.WriteLine("UI rendered: " + output);
                    }
                    catch (Exception renderEx)
                    {
                        File.WriteAllText(Path.ChangeExtension(output, ".err.txt"), renderEx.ToString());
                        throw;
                    }
                    break;
                default:
                    throw new ArgumentException(
                        "Commands: install <game> <font> [--sc] [--tc] [--en] [--western] [--zh] | restore <game> [--zh] | validate-game <game> | --render-ui <png> [--zh]");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            Console.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    static FontCoverage ParseCoverage(string[] options)
    {
        if (options.Length == 0) return FontCoverage.SimplifiedChinese | FontCoverage.English;
        FontCoverage coverage = 0;
        foreach (var option in options)
            coverage |= option switch
            {
                "--sc" => FontCoverage.SimplifiedChinese,
                "--tc" => FontCoverage.TraditionalChinese,
                "--en" => FontCoverage.English,
                _ => 0
            };
        if (coverage == 0) throw new ArgumentException("Select at least one language: --sc --tc --en");
        return coverage;
    }
}
