using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FileXfer;
using MiniUI.Core;
using MiniUI.Core.Css;
using MiniUI.Platform.Linux;

internal static class NativeOpticalProbe
{
    internal static void Run(string root, CairoSurface? cairo)
    {
        void Check(bool success, string name) { if (!success) throw new Exception(name); Console.WriteLine("PASS " + name); }
        string input = Path.Combine(root, "optical-input"); Directory.CreateDirectory(input);
        byte[] expected = new byte[8192]; new Random(1729).NextBytes(expected);
        File.WriteAllBytes(Path.Combine(input, "roundtrip.bin"), expected);
        var source = FolderCatalog.Inspect(input);
        var encoder = new OpticalService();
        var session = encoder.Start(source, new[] { "roundtrip.bin" }, 1000, 30, "L");
        var decoder = NativeCamera.CreateDecoder();
        var receiver = new OpticalReceiveService(Path.Combine(root, "optical-output"));
        var surface = new PixelSurface(960, 540);
        OpticalReceiveResult? completed = null;
        var rawFrames = new List<byte[]>();
        for (uint seq = 0; seq < 100 && completed?.Complete != true; seq++)
        {
            Check(encoder.TryRenderPackedFrames(session.Token, seq, 1, out var packed), "existing encoder frame " + seq);
            byte[] frame = NativeOptical.SplitPacked(packed, seq).Single();
            NativeOptical.PaintQr(surface, new RectD(0, 0, 960, 540), frame);
            byte[] rgb = surface.CapturePixels(out int width, out int height);
            var decoded = NativeCamera.Decode(decoder, rgb, width, height);
            Check(decoded != null, "native rendered QR binary decode " + seq);
            rawFrames.Add(rgb);
            completed = receiver.AddFrame("synthetic", decoded!, false);
        }
        Check(completed?.Complete == true && completed.FramesCollected > 1, "native optical multi-frame synthetic transfer completes");
        string saved = Directory.GetFiles(completed!.OutputPath, "roundtrip.bin", SearchOption.AllDirectories).Single();
        Check(File.ReadAllBytes(saved).SequenceEqual(expected), "native optical output byte-for-byte match");
        Check(NativeCamera.Decode(decoder, new byte[960 * 540 * 3], 960, 540) == null, "blank frame ignored");
        bool rejected = false;
        try { NativeOptical.SplitPacked(new byte[12], 0).ToArray(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "malformed QR packet rejected");
        if (cairo != null)
        {
            cairo.Begin(); cairo.Clear(Color.White); cairo.End();
            NativePreview.Paint(cairo, new RectD(0, 0, 960, 540), rawFrames[0], 960, 540);
            var preview = cairo.CapturePixels(out int previewWidth, out int previewHeight);
            Check(NativeCamera.Decode(decoder, preview, previewWidth, previewHeight) != null, "native Cairo camera preview preserves decodable QR");
        }
        if (OperatingSystem.IsLinux())
        {
            string rawPath = Path.Combine(root, "synthetic-camera.rgb");
            using (var stream = File.Create(rawPath)) foreach (var frame in rawFrames) for (int repeat = 0; repeat < 3; repeat++) stream.Write(frame);
            string fixture = Path.Combine(root, "synthetic-camera-source");
            string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";
            // Explicit executable injection: this test cannot invoke real ffmpeg/V4L2.
            File.WriteAllText(fixture, "#!/usr/bin/env bash\nset -e\nfor ((i=0; i<" + (rawFrames.Count * 3) + "; i++)); do\n  dd if=" + Quote(rawPath) + " bs=1555200 skip=\"$i\" count=1 status=none\n  sleep 0.12\ndone\nsleep 30\n");
            File.SetUnixFileMode(fixture, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var camera = new NativeCamera(Path.Combine(root, "camera-pipe-output"), "/dev/video0", 960, 30, 1, false, fixture);
            camera.Start();
            var watch = Stopwatch.StartNew();
            while (!camera.Stopped && watch.ElapsedMilliseconds < 30000) Thread.Sleep(30);
            Check(camera.Stopped, "synthetic capture child exits on completion");
            string status = Encoding.UTF8.GetString(JsonCodec.Serialize(camera.Status()));
            Check(status.Contains("\"complete\":true") && status.Contains("\"error\":\"\""), "capture pipe and decoder worker complete without camera: " + status);
            var paths = Directory.GetFiles(Path.Combine(root, "camera-pipe-output"), "roundtrip.bin", SearchOption.AllDirectories);
            Check(paths.Length == 1 && File.ReadAllBytes(paths[0]).SequenceEqual(expected), "capture pipe saved bytes match");
        }
        Console.WriteLine("NOT TESTED: actual camera/lighting/display throughput. Synthetic QR is not camera acceptance.");
    }

    private sealed class PixelSurface : ISurface
    {
        private readonly byte[] pixels;
        public int Width { get; } public int Height { get; } public double Scale => 1;
        public PixelSurface(int width, int height) { Width = width; Height = height; pixels = new byte[width * height * 3]; }
        public void FillRect(RectD rect, Color color, double radius)
        {
            for (int y = Math.Max(0, (int)rect.Y); y < Math.Min(Height, (int)rect.Bottom); y++)
            for (int x = Math.Max(0, (int)rect.X); x < Math.Min(Width, (int)rect.Right); x++)
            { int i = (y * Width + x) * 3; pixels[i] = color.R; pixels[i + 1] = color.G; pixels[i + 2] = color.B; }
        }
        public byte[] CapturePixels(out int w, out int h) { w = Width; h = Height; return (byte[])pixels.Clone(); }
        public void Begin() { } public void End() { }
        public void Clear(Color c) => FillRect(new RectD(0, 0, Width, Height), c, 0);
        public void FillLinearGradient(RectD r, LinearGradientPaint g, double d) => throw new NotSupportedException();
        public void StrokeRect(RectD r, Color c, double w, double d) => throw new NotSupportedException();
        public void PushClip(RectD r) => throw new NotSupportedException();
        public void PushPolygonClip(PolygonClip p) => throw new NotSupportedException();
        public void PopClip() => throw new NotSupportedException();
        public void PushTransform(TransformValue t) => throw new NotSupportedException();
        public void PopTransform() => throw new NotSupportedException();
        public TextMetrics DrawText(string t, double x, double y, double w, TextStyle s, Color c) => throw new NotSupportedException();
        public TextMetrics MeasureText(string t, double w, TextStyle s) => throw new NotSupportedException();
        public double CaretOffset(string t, int p, TextStyle s) => throw new NotSupportedException();
        public int HitTestText(string t, double x, TextStyle s) => throw new NotSupportedException();
    }
}
