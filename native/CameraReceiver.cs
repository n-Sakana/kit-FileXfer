using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using ZXing;
using ZXing.Common;

namespace FileXfer
{
    /// <summary>A camera that delivers opaque BGRA frames on its own thread until disposed.</summary>
    internal interface ICameraSource : IDisposable
    {
        string Name { get; }
        int Width { get; }
        int Height { get; }
        double FramesPerSecond { get; }
        /// <summary>Why the camera stopped, in words for the user; null while it runs.</summary>
        string Error { get; }
        bool Running { get; }
        void Start(Action<byte[], int, int> frame);
    }

    internal sealed class CameraInfo
    {
        public CameraInfo(string id, string name) { Id = id; Name = name; }
        public string Id { get; private set; }
        public string Name { get; private set; }
    }

    /// <summary>Reads FileXfer QR frames from a camera: keeps the newest frame for the preview, decodes on worker
    /// threads around the last QR position (with a periodic full search) and feeds the fountain decoder.</summary>
    internal sealed class CameraReceiver : IDisposable
    {
        public const string ReceiverId = "desktop-camera";
        private const int PreviewWidth = 960;
        private readonly ICameraSource _camera;
        private readonly OpticalReceiveService _receiver;
        private readonly bool _openWhenDone;
        private readonly int _workers;
        private readonly object _gate = new object();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<Thread> _threads = new List<Thread>();
        private byte[] _frame; private int _frameWidth, _frameHeight; private long _frameNumber, _takenNumber;
        private NativeBitmap _preview; private int _previewSequence;
        private OpticalReceiveResult _result;
        private string _error;
        private bool _stopped, _complete;
        private long _captured, _decoded, _recognized;
        private double _lastRecognizedMs = -1, _startedMs;
        private double _lastCaptureMs = -1, _lastDecodeMs = -1;
        private Tracking _tracking;

        public CameraReceiver(ICameraSource camera, OpticalReceiveService receiver, int workers, bool openWhenDone)
        {
            _camera = camera; _receiver = receiver; _openWhenDone = openWhenDone; _workers = Math.Max(1, Math.Min(3, workers));
        }

        public void Start()
        {
            Diagnostics.Event("camera.start", "native", new { Workers = _workers, Camera = Diagnostics.Id(_camera.Name) });
            _receiver.Reset(ReceiverId);
            _startedMs = _clock.Elapsed.TotalMilliseconds;
            for (int i = 0; i < _workers; i++)
            {
                var thread = new Thread(Decode) { IsBackground = true, Name = "FileXfer QR decoder " + (i + 1) };
                _threads.Add(thread); thread.Start();
            }
            try { _camera.Start(OnFrame); }
            catch (Exception error) { Diagnostics.Failure("camera.start", "native", error); throw; }
        }

        private void OnFrame(byte[] bgra, int width, int height)
        {
            var stamp = Diagnostics.Stamp();
            var preview = NativeBitmap.Downscale(bgra, width, height, PreviewWidth);
            lock (_gate)
            {
                if (_stopped) return;
                Diagnostics.Sample("camera.capture", "native", Diagnostics.Milliseconds(stamp), new { Width = width, Height = height, Replaced = _frameNumber != _takenNumber });
                _captured++;
                _lastCaptureMs = _clock.Elapsed.TotalMilliseconds;
                _frame = bgra; _frameWidth = width; _frameHeight = height; _frameNumber++;
                _preview = preview; _previewSequence++;
                Monitor.Pulse(_gate);
            }
        }

        public NativeBitmap Preview { get { lock (_gate) return _preview; } }

        public bool Running { get { lock (_gate) return !_stopped && !_complete && _error == null && _camera.Running; } }

        private void Decode()
        {
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = false,
                Options = new DecodingOptions { TryHarder = true, PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE } }
            };
            byte[] gray = new byte[0];
            while (true)
            {
                byte[] frame; int width, height; long number; Tracking tracking;
                lock (_gate)
                {
                    while (!_stopped && !_complete && (_frame == null || _frameNumber == _takenNumber)) Monitor.Wait(_gate, 250);
                    if (_stopped || _complete) return;
                    frame = _frame; width = _frameWidth; height = _frameHeight; number = _frameNumber; _takenNumber = number;
                    tracking = _tracking;
                }
                // Search near the last QR; every sixth frame (or after misses) the whole image, like the browser reader.
                var region = Region(width, height, tracking, number, _clock.Elapsed.TotalMilliseconds);
                int size = region.Width * region.Height;
                if (gray.Length < size) gray = new byte[size];
                for (int y = 0; y < region.Height; y++)
                {
                    int src = ((region.Y + y) * width + region.X) * 4, dst = y * region.Width;
                    for (int x = 0; x < region.Width; x++, src += 4) gray[dst + x] = (byte)((frame[src] * 29 + frame[src + 1] * 150 + frame[src + 2] * 77) >> 8);
                }
                var stamp = Diagnostics.Stamp();
                Result result = null;
                try { result = reader.Decode(new RGBLuminanceSource(gray, region.Width, region.Height, RGBLuminanceSource.BitmapFormat.Gray8)); }
                catch (Exception error) { Diagnostics.Sample("camera.decode_exception", "native", 0,
                    new { Type = error.GetType().Name, Code = error.HResult, Site = "CameraReceiver.Decode:ZXing.Decode" }); result = null; }
                byte[] bytes = Bytes(result);
                Diagnostics.Sample(result == null ? "camera.decode_miss" : "camera.qr_read", "native",
                    Diagnostics.Milliseconds(stamp), new { CropWidth = region.Width, CropHeight = region.Height });
                if (result != null && bytes == null) Diagnostics.Sample("camera.non_binary_qr", "native", 0, null);
                OpticalReceiveResult progress = null;
                string failure = null;
                if (bytes != null)
                {
                    try { progress = _receiver.AddFrame(ReceiverId, bytes, _openWhenDone); }
                    catch (Exception error) { Diagnostics.Failure("camera.receive", "native", error); failure = error.Message; }
                }
                lock (_gate)
                {
                    _decoded++;
                    _lastDecodeMs = _clock.Elapsed.TotalMilliseconds;
                    if (failure != null) { _error = failure; Monitor.PulseAll(_gate); return; }
                    if (progress != null && progress.Recognized)
                    {
                        _recognized++;
                        _lastRecognizedMs = _clock.Elapsed.TotalMilliseconds;
                        _result = progress;
                        var points = result.ResultPoints;
                        if (points != null && points.Length >= 3)
                        {
                            float left = points.Min(p => p.X) + region.X, right = points.Max(p => p.X) + region.X;
                            float top = points.Min(p => p.Y) + region.Y, bottom = points.Max(p => p.Y) + region.Y;
                            _tracking = new Tracking { Left = left, Right = right, Top = top, Bottom = bottom, Width = width, Height = height, At = _lastRecognizedMs };
                        }
                        if (progress.Complete) { _complete = true; Monitor.PulseAll(_gate); }
                    }
                    else if (_tracking != null) _tracking.Misses++;
                }
            }
        }

        private static byte[] Bytes(Result result)
        {
            object value;
            if (result == null || result.ResultMetadata == null || !result.ResultMetadata.TryGetValue(ResultMetadataType.BYTE_SEGMENTS, out value)) return null;
            var segments = value as IEnumerable<byte[]>;
            return segments == null ? null : segments.SelectMany(segment => segment).ToArray(); // binary segments, never decoded text
        }

        private sealed class Tracking { public float Left, Right, Top, Bottom; public int Width, Height, Misses; public double At; }
        private struct Rect { public int X, Y, Width, Height; }

        private static Rect Region(int width, int height, Tracking t, long frame, double now)
        {
            var full = new Rect { X = 0, Y = 0, Width = width, Height = height };
            // Finder-pattern centres sit inside the symbol: pad generously so the quiet zone is in the crop.
            if (t == null || t.Width != width || t.Height != height || now - t.At > 1200 || t.Misses >= 3 || frame % 6 == 0) return full;
            float span = Math.Max(t.Right - t.Left, t.Bottom - t.Top);
            float pad = Math.Max(48, span * 0.45f);
            int left = Math.Max(0, (int)(t.Left - pad)), top = Math.Max(0, (int)(t.Top - pad));
            int right = Math.Min(width, (int)Math.Ceiling(t.Right + pad)), bottom = Math.Min(height, (int)Math.Ceiling(t.Bottom + pad));
            if (right - left < 64 || bottom - top < 64) return full;
            return new Rect { X = left, Y = top, Width = right - left, Height = bottom - top };
        }

        public object Status()
        {
            lock (_gate)
            {
                double now = _clock.Elapsed.TotalMilliseconds;
                Diagnostics.Sample("camera.status", "native", 0, new { Captured = _captured, Decoded = _decoded,
                    Recognized = _recognized, Running = _camera.Running, Failed = _error != null || _camera.Error != null,
                    Width = _camera.Width, Height = _camera.Height, Fps = _camera.FramesPerSecond,
                    CaptureAgeMs = now - (_lastCaptureMs >= 0 ? _lastCaptureMs : _startedMs),
                    DecodeAgeMs = now - (_lastDecodeMs >= 0 ? _lastDecodeMs : _startedMs),
                    ReadAgeMs = now - (_lastRecognizedMs >= 0 ? _lastRecognizedMs : _startedMs) });
                return new
                {
                    Running = !_stopped && !_complete && _error == null && _camera.Running,
                    Complete = _complete,
                    Error = _error ?? _camera.Error,
                    Camera = _camera.Name,
                    Width = _camera.Width,
                    Height = _camera.Height,
                    Fps = Math.Round(_camera.FramesPerSecond, 1),
                    Captured = _captured,
                    Decoded = _decoded,
                    Recognized = _recognized,
                    PreviewSequence = _previewSequence,
                    // Milliseconds since a FileXfer QR was last read (since start when none was read yet).
                    QuietMs = (long)(now - (_lastRecognizedMs >= 0 ? _lastRecognizedMs : _startedMs)),
                    Result = _result
                };
            }
        }

        public void Dispose()
        {
            lock (_gate) { if (_stopped) return; _stopped = true; Monitor.PulseAll(_gate); }
            Diagnostics.Event("camera.stop", "native", new { Complete = _complete, Failed = _error != null || _camera.Error != null, Captured = _captured, Decoded = _decoded, Recognized = _recognized });
            try { _camera.Dispose(); } catch (Exception) { }
            foreach (var thread in _threads) thread.Join(2000);
            if (!_complete) _receiver.Stop(ReceiverId);
        }
    }
}
