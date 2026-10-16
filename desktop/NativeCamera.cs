using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ZXing;
using ZXing.Common;

namespace FileXfer;

// Explicit user action is the only caller of Start. No device is opened on startup.
internal sealed class NativeCamera : IDisposable
{
    public readonly int Width, Height;
    private readonly int fps, workerCount;
    private readonly string device, captureExecutable;
    private readonly bool openWhenDone;
    private readonly OpticalReceiveService receiver;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly Channel<byte[]> frames;
    private Process? capture;
    private Task? running;
    private byte[]? preview;
    private int previewDirty, captured, decoded;
    private string error = "";
    private OpticalReceiveResult? result;
    private bool disposed;
    public byte[]? Preview => Volatile.Read(ref preview);
    public bool Stopped => running?.IsCompleted != false;
    public NativeCamera(string output, string device, int width, int fps, int workers, bool openWhenDone, string captureExecutable = "ffmpeg")
    {
        if (width != 960 && width != 1280 && width != 1920) throw new ArgumentException("Invalid camera width");
        if (fps != 30 && fps != 60) throw new ArgumentException("Invalid camera fps");
        if (workers < 1 || workers > 3) throw new ArgumentException("Invalid decoder worker count");
        if (!Regex.IsMatch(device, @"^/dev/video[0-9]+$")) throw new ArgumentException("Camera device must be /dev/videoN");
        this.captureExecutable = captureExecutable;
        Width = width; Height = width * 9 / 16; this.device = device; this.fps = fps; workerCount = workers; this.openWhenDone = openWhenDone;
        receiver = new OpticalReceiveService(output);
        frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(workers) { FullMode = BoundedChannelFullMode.DropOldest });
    }
    public void Start()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("V4L2 camera requires Linux");
        var info = new ProcessStartInfo(captureExecutable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "v4l2", "-framerate", fps.ToString(),
            "-video_size", $"{Width}x{Height}", "-i", device, "-an", "-vf", $"scale={Width}:{Height}:force_original_aspect_ratio=decrease,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" }) info.ArgumentList.Add(arg);
        capture = Process.Start(info) ?? throw new IOException("ffmpegを起動できませんでした。");
        running = CaptureAndDecode(capture);
    }
    private async Task CaptureAndDecode(Process process)
    {
        // Drain stderr concurrently, retain a bounded error tail.
        var errors = Task.Run(async () => { var buffer = new char[1024]; string tail = ""; int read;
            while ((read = await process.StandardError.ReadAsync(buffer)) > 0) { tail += new string(buffer, 0, read); if (tail.Length > 4096) tail = tail[^4096..]; }
            return tail;
        });
        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            var decoder = CreateDecoder();
            try
            {
                await foreach (var pixels in frames.Reader.ReadAllAsync(stop.Token))
                {
                    var bytes = Decode(decoder, pixels, Width, Height); Interlocked.Increment(ref decoded);
                    if (bytes == null) continue;
                    lock (gate)
                    {
                        if (stop.IsCancellationRequested) break;
                        var progress = receiver.AddFrame("native-camera", bytes, openWhenDone);
                        if (progress.Recognized) result = progress;
                        if (progress.Complete) { stop.Cancel(); break; }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { lock (gate) error = e.Message; stop.Cancel(); }
        })).ToArray();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                byte[] pixels = new byte[checked(Width * Height * 3)];
                await process.StandardOutput.BaseStream.ReadExactlyAsync(pixels, stop.Token);
                Volatile.Write(ref preview, pixels); Interlocked.Exchange(ref previewDirty, 1); Interlocked.Increment(ref captured);
                frames.Writer.TryWrite(pixels);
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { if (!stop.IsCancellationRequested) lock (gate) error = "カメラ映像が終了しました。デバイスと解像度/fpsを確認してください。"; }
        catch (Exception e) { lock (gate) error = e.Message; }
        finally
        {
            stop.Cancel(); frames.Writer.TryComplete();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(); await Task.WhenAll(workers);
            string details = await errors;
            lock (gate) { if (error.Length > 0 && details.Length > 0) error += " " + details.Trim(); }
        }
    }
    internal static BarcodeReaderGeneric CreateDecoder() => new() { AutoRotate = false,
        Options = new DecodingOptions { TryHarder = true, PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE } } };
    internal static byte[]? Decode(BarcodeReaderGeneric decoder, byte[] rgb, int width, int height)
    {
        var decoded = decoder.Decode(new RGBLuminanceSource(rgb, width, height, RGBLuminanceSource.BitmapFormat.RGB24));
        if (decoded?.ResultMetadata == null || !decoded.ResultMetadata.TryGetValue(ResultMetadataType.BYTE_SEGMENTS, out var value) || value is not IEnumerable<byte[]> segments) return null;
        return segments.SelectMany(segment => segment).ToArray(); // Binary segments, never lossy decoded text.
    }
    public object Status()
    {
        lock (gate) return new { Running = !Stopped, Device = device, Width, Height, RequestedFps = fps,
            Workers = workerCount, Captured = captured, Decoded = decoded, Error = error, Receive = result };
    }
    public bool TakePreviewDirty() => Interlocked.Exchange(ref previewDirty, 0) != 0;
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        stop.Cancel();
        if (capture != null)
        {
            try { if (!capture.HasExited) capture.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            running?.GetAwaiter().GetResult(); capture.Dispose();
        }
        receiver.Stop("native-camera"); stop.Dispose();
    }
}
