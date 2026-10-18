using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using MiniUI.Core;
using MiniUI.Core.Engine;
using MiniUI.Platform.Linux;

namespace FileXfer;

/// <summary>FileXfer on Linux: MiniUI (GTK3/X11) shows ui/index.html, the same page as on Windows, and NativeApp
/// answers its uiHost.call and app: images in this process (the Windows build loads the same code as MiniUI.App.dll).
/// --no-browser and --help run only the HTTP server, as before.</summary>
internal static class MiniUIHost
{
    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception e) { Console.Error.WriteLine("FileXfer: " + e.Message); return 1; }
    }

    private static int Run(string[] args)
    {
        string? home = null, fontDir = null, stateDir = null, screenshot = null;
        bool mcp = false;
        double duration = 0;
        var appArgs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing value for " + args[i - 1]);
            switch (args[i])
            {
                case "--home": home = Path.GetFullPath(Next()); break;
                case "--font-dir": fontDir = Path.GetFullPath(Next()); break;
                case "--state-dir": stateDir = Path.GetFullPath(Next()); break;
                case "--screenshot": screenshot = Path.GetFullPath(Next()); break;
                case "--duration-ms": duration = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--mcp": mcp = true; break;
                default: appArgs.Add(args[i]); break;
            }
        }
        home ??= DefaultHome();
        if (appArgs.Contains("--no-browser") || appArgs.Contains("--help") || appArgs.Contains("-h"))
            return Program.Run(home, appArgs.ToArray());

        string entry = Path.Combine(home, "ui", "index.html");
        if (!File.Exists(entry)) throw new FileNotFoundException("ui/index.html が見つかりません: " + entry);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
            throw new InvalidOperationException("X11 の DISPLAY がありません。デスクトップの端末から起動してください。");
        stateDir ??= Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "FileXfer", "miniui");
        Directory.CreateDirectory(stateDir);

        var native = NativeApp.Instance;
        var arguments = new List<string> { "--home", home };
        arguments.AddRange(appArgs);
        var options = new EngineOptions
        {
            Root = Path.GetDirectoryName(entry)!, StateDir = stateDir, Width = 1180, Height = 800, Title = "FileXfer", Mcp = mcp,
            HostCall = native.Call, HostImage = name => Image(native.Image(name)), Arguments = arguments.ToArray()
        };

        int stopping = 0;
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; Interlocked.Exchange(ref stopping, 1); });
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; Interlocked.Exchange(ref stopping, 1); });
        try
        {
            using var fonts = new LinuxFonts(fontDir);
            using var window = new LinuxWindow(options.Width, options.Height, options.Title, fonts);
            using var fetcher = new LinuxFetcher();
            using var engine = new Engine(window, fetcher, options);
            engine.ConsoleSink = Console.Error.WriteLine;
            engine.LoadControl(entry);
            var clock = Stopwatch.StartNew();
            void SyncIme()
            {
                var el = (engine.KeyboardDoc ?? engine.Control).Doc.FocusedElement;
                bool editable = el != null && el.IsTextEditable && !el.HasAttribute("readonly") && !el.HasAttribute("disabled");
                window.SyncTextInput(editable ? el : null, editable ? el!.EditableText : "", editable ? el!.SelectionEnd : 0, editable && el!.InputType == "password");
            }
            bool Pump()
            {
                window.Pump(input => { engine.HandleInput(input); SyncIme(); });
                if (Volatile.Read(ref stopping) != 0 || (duration > 0 && clock.Elapsed.TotalMilliseconds >= duration)) engine.Closed = true;
                if (!engine.Closed && !window.Closed) { engine.RunFrame(); SyncIme(); }
                return !engine.Closed && !window.Closed;
            }
            using var stdin = mcp ? Console.OpenStandardInput() : null;
            using var stdout = mcp ? Console.OpenStandardOutput() : null;
            var rpc = mcp ? new McpServer(engine, Pump, stdin!, stdout!) : null;
            rpc?.Start();
            SyncIme();
            while (Pump())
            {
                rpc?.Process();
                window.WaitForInput(Math.Min(engine.NextWakeMs(), mcp ? 8 : 1000));
            }
            if (screenshot != null)
            {
                engine.EnsureRendered();
                using var output = new FileStream(screenshot, FileMode.CreateNew);
                output.Write(engine.CapturePng(out _, out _));
            }
            window.RequestClose();
        }
        finally
        {
            native.Dispose();
        }
        return 0;
    }

    /// <summary>The FileXfer folder: two levels above artifacts/miniui-linux when that is where this host runs.</summary>
    private static string DefaultHome()
    {
        string here = AppContext.BaseDirectory;
        string up = Path.GetFullPath(Path.Combine(here, "..", ".."));
        return File.Exists(Path.Combine(up, "ui", "index.html")) ? up : Path.GetFullPath(here);
    }

    private static ImageData? Image(IAppImage? image)
    {
        if (image == null) return null;
        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyTo(pixels);
        return new ImageData(image.Width, image.Height, pixels);
    }
}
