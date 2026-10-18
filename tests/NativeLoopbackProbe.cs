using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FileXfer.Probe;

/// <summary>Sends a folder through FileXfer's own optical path with no screen and no camera: the QR frames the page
/// shows (QrStream, drawn as ScaledQr images) are placed in camera-sized pictures and given to the desktop camera
/// receiver, whose decoded files must equal the originals.</summary>
internal static class NativeLoopbackProbe
{
    private static int _failures;

    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "filexfer-loopback-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string source = Path.Combine(root, "送る資料"), output = Path.Combine(root, "output");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "写真"));
            var random = new Random(20261017);
            var originals = new Dictionary<string, byte[]>
            {
                ["メモ.txt"] = Encoding.UTF8.GetBytes("光学転送のテストです。\n"),
                ["data.csv"] = Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Range(0, 400).Select(i => i + "," + i * i))),
                ["写真/IMG_0001.jpg"] = Bytes(random, 48_000),
                ["見積書.pdf"] = Bytes(random, 21_000),
            };
            foreach (var file in originals) File.WriteAllBytes(Path.Combine(source, file.Key.Replace('/', Path.DirectorySeparatorChar)), file.Value);

            var folder = new AppState(source).ReadFolder("optical");
            Check(folder.Files.Count == originals.Count, "the folder lists " + originals.Count + " files");
            var optical = new OpticalService();
            var started = optical.Start(folder, folder.Files.Select(f => f.Name).ToList(), 1000, 30, "L");
            using var frames = new QrStream(optical, started.Token);

            var first = frames.Frame(0, 3);
            Check(first.Width == first.Side * 3 && first.Height == first.Width, "a QR frame is drawn 3 pixels per module");
            var pixels = new byte[first.Width * first.Height * 4];
            first.CopyTo(pixels);
            Check(pixels[0] == 255 && pixels[3] == 255, "the quiet zone is white and opaque");

            var receiver = new OpticalReceiveService(output);
            var camera = new SyntheticCamera(frames, 1280, 720, 3);
            using var reader = new CameraReceiver(camera, receiver, 2, false);
            reader.Start();
            var clock = Stopwatch.StartNew();
            object status = reader.Status();
            while (clock.Elapsed < TimeSpan.FromSeconds(120))
            {
                status = reader.Status();
                if ((bool)Read(status, "Complete") || Read(status, "Error") != null) break;
                Thread.Sleep(100);
            }
            camera.Dispose();
            Check((bool)Read(status, "Complete"), "the receiver completes in " + clock.Elapsed.TotalSeconds.ToString("0.0") + " s (" + Read(status, "Captured") + " pictures, error: " + (Read(status, "Error") ?? "none") + ")");
            var result = Read(status, "Result") as OpticalReceiveResult;
            if (result != null && result.Complete)
            {
                foreach (var file in originals)
                {
                    string received = Path.Combine(result.OutputPath, file.Key.Replace('/', Path.DirectorySeparatorChar));
                    Check(File.Exists(received) && File.ReadAllBytes(received).SequenceEqual(file.Value), "received " + file.Key + " equals the original");
                }
            }
            optical.Stop(started.Token);
        }
        catch (Exception error)
        {
            Check(false, "no exception: " + error);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine(_failures == 0 ? "PASS" : "FAIL " + _failures);
        return _failures == 0 ? 0 : 1;
    }

    private static byte[] Bytes(Random random, int length) { var data = new byte[length]; random.NextBytes(data); return data; }

    private static object? Read(object status, string name) => status.GetType().GetProperty(name)?.GetValue(status);

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        if (!ok) _failures++;
    }

    /// <summary>A camera that films the sender's screen: each picture is one QR frame, centred on white.</summary>
    private sealed class SyntheticCamera : ICameraSource
    {
        private readonly QrStream _frames;
        private readonly int _scale;
        private Thread? _thread;
        private volatile bool _stopped;

        public SyntheticCamera(QrStream frames, int width, int height, int scale) { _frames = frames; Width = width; Height = height; _scale = scale; }

        public string Name => "synthetic";
        public int Width { get; }
        public int Height { get; }
        public double FramesPerSecond => 30;
        public string? Error => null;
        public bool Running => !_stopped;

        public void Start(Action<byte[], int, int> deliver)
        {
            _thread = new Thread(() =>
            {
                uint sequence = 0;
                while (!_stopped)
                {
                    var qr = _frames.Frame(sequence++, _scale);
                    var square = new byte[qr.Width * qr.Height * 4];
                    qr.CopyTo(square);
                    var picture = new byte[Width * Height * 4];
                    Array.Fill(picture, (byte)255);
                    int left = (Width - qr.Width) / 2, top = (Height - qr.Height) / 2;
                    for (int y = 0; y < qr.Height; y++)
                        Buffer.BlockCopy(square, y * qr.Width * 4, picture, ((top + y) * Width + left) * 4, qr.Width * 4);
                    deliver(picture, Width, Height);
                    Thread.Sleep(1000 / 30);
                }
            }) { IsBackground = true, Name = "synthetic camera" };
            _thread.Start();
        }

        public void Dispose() { _stopped = true; _thread?.Join(2000); }
    }
}
