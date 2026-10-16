using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MiniUI.Core;
using MiniUI.Core.Engine;
using MiniUI.Platform.Linux;

namespace FileXfer;

internal static class MiniUIHost
{
    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception e) { Console.Error.WriteLine("FileXfer_MINIUI: " + e.Message); return 1; }
    }

    private static int Run(string[] args)
    {
        var appArgs = new System.Collections.Generic.List<string>();
        string? fontDir = null, screenshot = null;
        string dataDir = Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "ferry");
        bool mcp = false;
        int duration = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value");
            switch (args[i])
            {
                case "--data-dir": dataDir = Path.GetFullPath(Next()); break;
                case "--font-dir": fontDir = Path.GetFullPath(Next()); break;
                case "--screenshot": screenshot = Path.GetFullPath(Next()); break;
                case "--duration-ms": duration = int.Parse(Next()); if (duration <= 0) throw new ArgumentException("Invalid duration"); break;
                case "--mcp": mcp = true; break;
                default: appArgs.Add(args[i]); break;
            }
        }
        var app = AppOptions.Parse(appArgs.ToArray());
        if (app.ShowHelp)
        {
            AppOptions.WriteHelp(Console.Out);
            Console.WriteLine("MiniUI Linux: --data-dir DIR --font-dir DIR --mcp --duration-ms N --screenshot NEW.png\nNo PowerShell or browser. Requires GTK3/X11. Existing FileXfer processes are never replaced.\nOptical: native Cairo QR + ffmpeg V4L2/ZXing camera. Set FileXfer_CAMERA_DEVICE=/dev/videoN.");
            return 0;
        }
        if (!app.NoBrowser && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("MiniUI desktop requires Linux/X11. Use --no-browser for HTTP testing.");
        if (!app.NoBrowser && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
            throw new InvalidOperationException("X11 DISPLAY is missing. Run from a NixOS desktop terminal (Xwayland is required on Wayland).");
        Directory.CreateDirectory(dataDir);
        StaticAssets.Initialize(AppContext.BaseDirectory, null);
        using var shutdown = new CancellationTokenSource();
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; shutdown.Cancel(); });
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; shutdown.Cancel(); });
        using var server = new LocalServer(app.Port, new AppState(app.InitialPath), app.InitialMode, Path.Combine(dataDir, "output"), shutdown.Cancel);
        var uri = server.Start(); // An occupied port fails; do not terminate someone else's window.
        Console.Error.WriteLine("FileXfer: " + uri + " Output: " + Path.Combine(dataDir, "output"));
        var serverTask = server.RunAsync(shutdown.Token);
        try
        {
            if (app.NoBrowser)
            {
                if (duration > 0) shutdown.CancelAfter(duration);
                serverTask.GetAwaiter().GetResult();
            }
            else
            {
                using var optical = new NativeOptical(app.Port, Path.Combine(dataDir, "output"), Environment.GetEnvironmentVariable("FileXfer_CAMERA_DEVICE") ?? "/dev/video0");
                using var bridge = new ApiBridge(app.Port) { NativeCall = optical.Call };
                string entry = PrepareAssets(Path.Combine(dataDir, "ui"));
                var options = new EngineOptions { Root = Path.GetDirectoryName(entry)!, StateDir = Path.Combine(dataDir, "miniui"),
                    Width = 1280, Height = 900, Title = "FileXfer", HostCall = bridge.Call, Mcp = mcp };
                Directory.CreateDirectory(options.StateDir);
                using var fonts = new LinuxFonts(fontDir);
                using var window = new LinuxWindow(options.Width, options.Height, options.Title, fonts);
                using var fetcher = new LinuxFetcher();
                using var engine = new Engine(window, fetcher, options);
                engine.ConsoleSink = Console.Error.WriteLine;
                engine.LoadControl(entry);
                var elapsed = Stopwatch.StartNew();
                void SyncIme()
                {
                    var el = (engine.KeyboardDoc ?? engine.Control).Doc.FocusedElement;
                    bool editable = el != null && el.IsTextEditable && !el.HasAttribute("readonly") && !el.HasAttribute("disabled");
                    window.SyncTextInput(editable ? el : null, editable ? el!.EditableText : "", editable ? el!.SelectionEnd : 0, editable && el!.InputType == "password");
                }
                bool Pump()
                {
                    window.Pump(input => { engine.HandleInput(input); SyncIme(); });
                    if (shutdown.IsCancellationRequested || serverTask.IsCompleted || (duration > 0 && elapsed.ElapsedMilliseconds >= duration)) engine.Closed = true;
                    if (!engine.Closed && !window.Closed) { bool painted = engine.RunFrame(); optical.Tick(engine, window, painted); SyncIme(); }
                    return !engine.Closed && !window.Closed;
                }
                using var stdin = mcp ? Console.OpenStandardInput() : null;
                using var stdout = mcp ? Console.OpenStandardOutput() : null;
                var rpc = mcp ? new McpServer(engine, Pump, stdin!, stdout!) : null;
                rpc?.Start();
                while (Pump()) { rpc?.Process(); window.WaitForInput(Math.Min(engine.NextWakeMs(), mcp ? 8 : optical.WaitMs)); }
                if (screenshot != null)
                {
                    engine.EnsureRendered();
                    using var output = new FileStream(screenshot, FileMode.CreateNew);
                    output.Write(engine.CapturePng(out _, out _));
                }
                window.RequestClose();
            }
        }
        finally
        {
            shutdown.Cancel();
            try { serverTask.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
        return 0;
    }

    internal static string PrepareAssets(string directory)
    {
        Directory.CreateDirectory(directory);
        string Read(string name)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new FileNotFoundException(name);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        // Derive the layout from the existing page, without changing Windows/PWA assets.
        string html = Read("FileXfer.Web.index.html");
        html = Regex.Replace(html, @"<script\b[^>]*>.*?</script>", "", RegexOptions.Singleline);
        html = Regex.Replace(html, @"<link\b[^>]*>", "");
        foreach (var icon in new[] { "optical" })
            html = html.Replace("<svg aria-hidden=\"true\"><use href=\"#icon-" + icon + "\"></use></svg>", "QR");
        html = Regex.Replace(html, @"<svg\b[^>]*>.*?</svg>", "", RegexOptions.Singleline);
        html = html.Replace("</head>", "<link rel=\"stylesheet\" href=\"app.css\"><link rel=\"stylesheet\" href=\"miniui.css\"><script src=\"miniui.js\" defer></script></head>");
        html = Regex.Replace(html, @"<img([^>]*id=""pairingQr""[^>]*)>", "<div$1></div>");
        html = Regex.Replace(html, @"<canvas([^>]*)>.*?</canvas>", "<div$1></div>", RegexOptions.Singleline);
        html = Regex.Replace(html, @"<video([^>]*)>.*?</video>", "<div$1></div>", RegexOptions.Singleline);
        File.WriteAllText(Path.Combine(directory, "index.html"), html);
        File.WriteAllText(Path.Combine(directory, "app.css"), Read("FileXfer.Web.app.css"));
        File.WriteAllText(Path.Combine(directory, "miniui.css"), Read("FileXfer.MiniUI.miniui.css"));
        File.WriteAllText(Path.Combine(directory, "miniui.js"), Read("FileXfer.MiniUI.miniui.js"));
        return Path.Combine(directory, "index.html");
    }
}
