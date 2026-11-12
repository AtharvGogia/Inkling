using System.Runtime.InteropServices;
using Inkling.Ai;

namespace Inkling;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);

    [STAThread]
    private static int Main(string[] args)
    {
        // Developer check without the hotkey:  Inkling.exe --ask claude|agy "request" ["hint"]
        if (args.Length >= 3 && args[0] == "--ask") return AskFromCommandLine(args);
        // Build helper: regenerate Assets\inkling.ico from the drawing code.
        if (args.Length == 2 && args[0] == "--write-icon")
        {
            Ui.InklingArt.WriteIco(args[1], 16, 20, 24, 32, 40, 48, 64, 256);
            return 0;
        }

        using var mutex = new Mutex(true, "Inkling-single-instance", out bool first);
        if (!first)
        {
            MessageBox.Show("Inkling is already running (see the tray).", "Inkling", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Write("UI error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Fatal: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write("Task error: " + e.Exception.Message); e.SetObserved(); };

        try
        {
            Application.Run(new TrayApp());
        }
        catch (FormatException ex)
        {
            MessageBox.Show($"{ex.Message}\n\nFix it in {AppConfig.FilePath}", "Inkling", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return 0;
    }

    private static int AskFromCommandLine(string[] args)
    {
        AttachConsole(-1);
        var cfg = AppConfig.Load();
        using IAiBackend backend = args[1] == "agy" ? new AgyBackend(cfg) : new ClaudeBackend(cfg);
        var hint = $"Answer type wanted here: {(args.Length > 3 ? args[3] : cfg.DefaultHint)}.";
        try
        {
            for (int i = 1; i <= 2; i++) // twice: shows cold vs warm latency
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var raw = backend.AskAsync(args[2], hint, new CancellationTokenSource(TimeSpan.FromSeconds(cfg.TimeoutSeconds * 2)).Token)
                    .GetAwaiter().GetResult();
                Console.WriteLine($"[{backend.DisplayName}, run {i}, {sw.ElapsedMilliseconds} ms] {OutputCleaner.Clean(raw, cfg.SingleLine)}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }
}
