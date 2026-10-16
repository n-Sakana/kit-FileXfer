using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FileXfer;
using MiniUI.Core;
using MiniUI.Core.Engine;
using MiniUI.Tests;
using MiniUI.Platform.Linux;

internal static class MiniUIProbe
{
    static int Main(string[] args)
    {
        // No GTK, real desktop, picker, clipboard, output launcher or camera.
        string root = Path.GetFullPath(Path.Combine("artifacts", "miniui-probe-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "note.txt"), "FileXfer MiniUI integration\n日本語");
        File.WriteAllText(Path.Combine(input, "large.txt"), new string('L', 1048577));
        File.WriteAllText(Path.Combine(input, "omit.svg"), "<svg/>");
        using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
        int port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        using var shutdown = new CancellationTokenSource();
        StaticAssets.Initialize(AppContext.BaseDirectory, null!);
        using var server = new LocalServer(port, new AppState(input), "optical", Path.Combine(root, "output"), shutdown.Cancel);
        var registry = (RemoteControlRegistry)typeof(LocalServer).GetField("_remotes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(server)!;
        server.Start(); var serving = server.RunAsync(shutdown.Token);
        try
        {
            using var optical = new NativeOptical(port, Path.Combine(root, "output"), "/dev/video0");
            using var bridge = new ApiBridge(port) { NativeCall = optical.Call };
            string entry = MiniUIHost.PrepareAssets(Path.Combine(root, "ui"));
            var fake = new FakeWindow(); fake.Fake.Width = 1280; fake.Fake.Height = 900;
            using var fonts = args.Contains("--render") ? new LinuxFonts(null) : null;
            using var cairo = fonts == null ? null : new CairoSurface(1280, 900, 1, fonts);
            IWindow window = cairo == null ? fake : new OffscreenWindow(cairo);
            using var engine = new Engine(window, new NullFetcher(), new EngineOptions {
                Root = Path.GetDirectoryName(entry)!, StateDir = Path.Combine(root, "state"),
                Headless = true, HostCall = bridge.Call });
            engine.ConsoleSink = Console.WriteLine;
            engine.LoadControl(entry);
            string JS(string script) => engine.Control.Realm.ToStringValue(engine.Control.Realm.RunScript(script, "probe.js"));
            void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
            void Until(Func<bool> condition, string name)
            {
                var watch = Stopwatch.StartNew();
                bool passed;
                while (!(passed = condition()) && watch.ElapsedMilliseconds < 12000) { bool painted = engine.RunFrame(); optical.Tick(engine, window, painted); fake.Fake.Commands.Clear(); Thread.Sleep(10); }
                Check(passed, name);
            }
            void Click(string selector)
            {
                engine.RunFrame();
                string expression = "document.querySelector(" + System.Text.Json.JsonSerializer.Serialize(selector) + ")";
                using var rect = System.Text.Json.JsonDocument.Parse(JS("JSON.stringify(" + expression + ".getBoundingClientRect())"));
                var r = rect.RootElement;
                double x = r.GetProperty("x").GetDouble() + r.GetProperty("width").GetDouble() / 2;
                double y = r.GetProperty("y").GetDouble() + r.GetProperty("height").GetDouble() / 2;
                engine.HandleInput(new InputEvent { Kind = InputKind.PointerDown, X = x, Y = y });
                engine.HandleInput(new InputEvent { Kind = InputKind.PointerUp, X = x, Y = y });
            }
            Until(() => JS("document.querySelectorAll('#opticalFiles .file-row').length") == "3", "MiniUI JS + real HTTP folder listing");
            Check(JS("document.querySelectorAll('[data-page-panel]').length") == "1", "only optical page remains");
            Click("[data-selection-mode=optical][data-selection-action=none]");
            Check(JS("document.querySelectorAll('#opticalFiles [aria-checked=true]').length") == "0", "clear optical selection");
            Click("[data-selection-mode=optical][data-selection-action=all]");
            Check(JS("document.querySelectorAll('#opticalFiles [aria-checked=true]').length") == "3", "select all optical");
            Click("#showQr");
            Until(() => JS("document.getElementById('qrOverlay').hidden") == "false", "native QR overlay opens through real optical API");
            Until(() => int.Parse(System.Text.Json.JsonDocument.Parse(optical.Call(System.Text.Json.JsonDocument.Parse("{\"native\":\"status\"}").RootElement)).RootElement.GetProperty("result").GetProperty("displayFrames").GetRawText()) > 0, "native QR frames fetched and advanced");
            if (cairo != null) {
                File.WriteAllBytes(Path.Combine(root, "qr-offscreen.png"), engine.CapturePng(out _, out _));
                var rgb = cairo.CapturePixels(out var width, out var height);
                Check(NativeCamera.Decode(NativeCamera.CreateDecoder(), rgb, width, height) != null, "QR decodes from real Cairo/MiniUI composed framebuffer");
            }
            Click("#closeQr"); Check(JS("document.getElementById('qrOverlay').hidden") == "true", "native QR overlay closes");
            registry.Heartbeat("miniui-probe-phone", "Synthetic phone");
            Until(() => JS("document.getElementById('showRemoteQr').disabled") == "false", "remote heartbeat enables controls");
            Click("#showRemoteQr");
            Until(() => JS("document.getElementById('toast').textContent").Contains("1 台"), "MiniUI sends remote QR command through real HTTP");
            var command = registry.Heartbeat("miniui-probe-phone", "Synthetic phone").Command;
            Check(command != null && command.Action == "showQr" && command.Files.Count == 3 && command.FrameBytes == 1000 && command.FramesPerSecond == 30 && command.ErrorCorrection == "L", "PWA command contract and optical defaults preserved");
            Check(bridge.Call("{\"path\":\"http://example.com/\"}").Contains("error"), "bridge rejects external destinations");
            Check(bridge.Call("{\"path\":\"/api/shutdown\",\"body\":{}}").Contains("error"), "bridge rejects lifecycle mutation");
            foreach (var diagnostic in engine.Control.Doc.Diagnostics)
                Console.WriteLine("DIAGNOSTIC " + diagnostic.Code + " " + diagnostic.Message);
            Check(!engine.Control.Doc.Diagnostics.Any(d => d.Code.StartsWith("JS_")), "no MiniUI script diagnostics");
            NativeOpticalProbe.Run(root, cairo);
            Console.WriteLine("Evidence: " + root + "\nNOT TESTED: visible GTK window/native input, camera, mobile devices, GTK picker/IME.");
            return 0;
        }
        finally { shutdown.Cancel(); serving.GetAwaiter().GetResult(); }
    }
}
