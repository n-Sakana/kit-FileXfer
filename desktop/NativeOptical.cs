using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniUI.Core;
using MiniUI.Core.Engine;
using MiniUI.Core.Dom;
using MiniUI.Platform.Linux;

namespace FileXfer;

// FileXfer's optical display component. The wire format and server encoder stay unchanged.
// MiniUI owns the page, input, layout and text; this component paints the QR slot.
internal sealed class NativeOptical : IDisposable
{
    private readonly HttpClient client;
    private readonly string outputRoot;
    private readonly string cameraDevice;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Queue<byte[]> frames = new();
    private CancellationTokenSource? displayCancellation;
    private Task<byte[]>? fetching;
    private byte[]? current, pairing, paintedFrame;
    private bool pairingDirty;
    private string token = "", error = "";
    private int fps = 30, rendered;
    private uint sequence;
    private double nextFrame;
    private NativeCamera? camera;
    public NativeOptical(int port, string outputRoot, string cameraDevice)
    {
        this.outputRoot = outputRoot; this.cameraDevice = cameraDevice;
        client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(15) };
    }
    public string Call(JsonElement root)
    {
        switch (root.GetProperty("native").GetString())
        {
            case "pairing":
                string url = root.GetProperty("url").GetString() ?? "";
                if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new ArgumentException("Invalid pairing URL");
                pairing = TextQr(url); pairingDirty = true; break;
            case "display":
                StopDisplay();
                token = root.GetProperty("token").GetString() ?? "";
                if (!Guid.TryParseExact(token, "N", out _)) { token = ""; throw new ArgumentException("Invalid optical token"); }
                fps = Math.Clamp(root.GetProperty("fps").GetInt32(), 1, 60);
                displayCancellation = new CancellationTokenSource(); nextFrame = clock.Elapsed.TotalMilliseconds;
                break;
            case "stop-display": StopDisplay(); break;
            case "start-camera":
                if (camera != null && !camera.Stopped) throw new InvalidOperationException("カメラは既に動作しています。");
                camera?.Dispose();
                camera = new NativeCamera(outputRoot, cameraDevice, root.GetProperty("width").GetInt32(),
                    root.GetProperty("fps").GetInt32(), root.GetProperty("workers").GetInt32(), root.GetProperty("openWhenDone").GetBoolean());
                camera.Start();
                break;
            case "stop-camera": camera?.Dispose(); camera = null; break;
            case "status": break;
            default: throw new ArgumentException("Unknown native optical operation");
        }
        return Encoding.UTF8.GetString(JsonCodec.Serialize(new { Result = new {
            OutputRoot = outputRoot, Displaying = token.Length > 0, DisplayFrames = rendered, Error = error,
            Camera = camera?.Status() } }));
    }
    private void StopDisplay()
    {
        displayCancellation?.Cancel(); displayCancellation?.Dispose(); displayCancellation = null;
        fetching = null; frames.Clear(); current = null; paintedFrame = null; token = ""; error = ""; sequence = 0; rendered = 0;
    }
    public int WaitMs => token.Length > 0 ? 8 : 25;
    public void Tick(Engine engine, IWindow window, bool pagePainted)
    {
        bool changed = false;
        if (token.Length > 0 && error.Length == 0)
        {
            if (fetching?.IsCompleted == true)
            {
                try
                {
                    var packed = fetching.GetAwaiter().GetResult();
                    foreach (var frame in SplitPacked(packed, sequence)) frames.Enqueue(frame);
                    sequence = unchecked(sequence + (uint)BitConverter.ToUInt16(packed, 6));
                }
                catch (Exception e) { error = e.Message; }
                fetching = null;
            }
            if (fetching == null && frames.Count < 8 && error.Length == 0)
                fetching = client.GetByteArrayAsync($"/api/optical/frames?token={token}&seq={sequence}&count=8", displayCancellation!.Token);
            if (clock.Elapsed.TotalMilliseconds >= nextFrame && frames.Count > 0)
            {
                current = frames.Dequeue(); changed = true;
                nextFrame = clock.Elapsed.TotalMilliseconds + 1000.0 / fps; // Never burst through late frames.
            }
        }
        if (current != null && (pagePainted || changed) && Slot(engine, "qrFrame") is RectD qrRect)
        {
            try
            {
                PaintQr(window.Surface, qrRect, current); window.Present(qrRect);
                if (paintedFrame != current) { rendered++; paintedFrame = current; }
            }
            catch (InvalidOperationException e) { error = e.Message; }
        }
        if (token.Length == 0 && pairing != null && (pagePainted || pairingDirty) && Slot(engine, "pairingQr") is RectD pairRect)
        {
            PaintQr(window.Surface, pairRect, pairing); window.Present(pairRect); pairingDirty = false;
        }
        if (camera != null && Slot(engine, "cameraVideo") is RectD preview && window.Surface is CairoSurface cairo)
        {
            var frame = camera.Preview;
            if (frame != null && (pagePainted || camera.TakePreviewDirty()))
            {
                NativePreview.Paint(cairo, preview, frame, camera.Width, camera.Height);
                window.Present(preview);
            }
        }
    }
    private static RectD? Slot(Engine engine, string id)
    {
        var element = engine.Control.Doc.GetElementById(id);
        if (element == null || element.Box == null) return null;
        for (Node? node = element; node != null; node = node.Parent)
            if (node is Element el && el.HasAttribute("hidden")) return null;
        var rect = Bindings.ClientRect(engine.Control, element);
        if (rect.Width <= 0 || rect.Height <= 0 || rect.X < 0 || rect.Y < 0 || rect.Right > engine.Width || rect.Bottom > engine.Height) return null;
        return rect;
    }
    private static byte[] TextQr(string value)
    {
        var hints = new Dictionary<ZXing.EncodeHintType, object> { [ZXing.EncodeHintType.CHARACTER_SET] = "ISO-8859-1", [ZXing.EncodeHintType.DISABLE_ECI] = true };
        var qr = ZXing.QrCode.Internal.Encoder.encode(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(value)), ZXing.QrCode.Internal.ErrorCorrectionLevel.L, hints);
        int side = qr.Matrix.Width + 8;
        var frame = new byte[2 + (side * side + 7) / 8]; frame[0] = (byte)side; frame[1] = (byte)(side >> 8);
        for (int y = 0; y < qr.Matrix.Height; y++) for (int x = 0; x < qr.Matrix.Width; x++)
            if (qr.Matrix[x, y] != 0) { int bit = (y + 4) * side + x + 4; frame[2 + bit / 8] |= (byte)(0x80 >> (bit % 8)); }
        return frame;
    }
    internal static IEnumerable<byte[]> SplitPacked(byte[] packed, uint expectedSequence)
    {
        if (packed.Length < 12 || packed[0] != 0x46 || packed[1] != 0x51 || packed[2] != 0x52 || packed[3] != 0x31)
            throw new InvalidDataException("Invalid packed QR header");
        int side = BitConverter.ToUInt16(packed, 4), count = BitConverter.ToUInt16(packed, 6);
        if (side < 29 || side > 185 || count < 1 || count > 16 || BitConverter.ToUInt32(packed, 8) != expectedSequence)
            throw new InvalidDataException("Invalid packed QR metadata");
        int stride = (side * side + 7) / 8;
        if (packed.Length != 12 + count * stride) throw new InvalidDataException("Truncated packed QR");
        for (int i = 0; i < count; i++)
        {
            var frame = new byte[2 + stride]; frame[0] = (byte)side; frame[1] = (byte)(side >> 8);
            Buffer.BlockCopy(packed, 12 + i * stride, frame, 2, stride); yield return frame;
        }
    }
    internal static void PaintQr(ISurface surface, RectD slot, byte[] frame)
    {
        int side = BitConverter.ToUInt16(frame, 0);
        int pixels = (int)Math.Floor(Math.Min(slot.Width, slot.Height) * surface.Scale / side);
        if (pixels < 1) throw new InvalidOperationException("QR表示領域が小さすぎます。");
        double step = pixels / surface.Scale, size = side * step;
        double x0 = Math.Round((slot.X + (slot.Width - size) / 2) * surface.Scale) / surface.Scale;
        double y0 = Math.Round((slot.Y + (slot.Height - size) / 2) * surface.Scale) / surface.Scale;
        bool Dark(int x, int y) { int bit = y * side + x; return (frame[2 + bit / 8] & (0x80 >> (bit % 8))) != 0; }
        surface.Begin();
        try
        {
            surface.FillRect(slot, new Color(255, 255, 255), 0);
            for (int y = 0; y < side; y++) for (int x = 0; x < side;)
            {
                if (!Dark(x, y)) { x++; continue; }
                int start = x++; while (x < side && Dark(x, y)) x++;
                surface.FillRect(new RectD(x0 + start * step, y0 + y * step, (x - start) * step, step), new Color(0, 0, 0), 0);
            }
        }
        finally { surface.End(); }
    }
    public void Dispose() { StopDisplay(); camera?.Dispose(); client.Dispose(); }
}
