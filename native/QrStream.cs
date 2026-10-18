using System;
using System.Collections.Generic;
using System.Threading;

namespace FileXfer
{
    /// <summary>QR frames of one display session, rendered ahead on a background thread so the page can show a new
    /// frame every display tick without waiting for the fountain encoder and the QR encoder. Frames are kept one bit
    /// per module and drawn at the size the page asks for.</summary>
    internal sealed class QrStream : IDisposable
    {
        private const int Ahead = 16;
        private readonly OpticalService _service;
        private readonly object _gate = new object();
        private readonly Dictionary<uint, byte[]> _ready = new Dictionary<uint, byte[]>();
        private readonly Thread _worker;
        private uint _wanted;
        private bool _stopped;
        private string _error;

        public readonly string Token;

        public QrStream(OpticalService service, string token)
        {
            _service = service; Token = token;
            _worker = new Thread(Run) { IsBackground = true, Name = "FileXfer QR frames", Priority = ThreadPriority.AboveNormal };
            _worker.Start();
        }

        public string Error { get { lock (_gate) return _error; } }

        /// <summary>The frame for this sequence number at scale pixels per module (rendered now when not ready yet).</summary>
        public ScaledQr Frame(uint sequence, int scale)
        {
            byte[] frame;
            lock (_gate)
            {
                _wanted = sequence;
                foreach (var old in new List<uint>(_ready.Keys)) if (unchecked(old - sequence) > Ahead) _ready.Remove(old);
                _ready.TryGetValue(sequence, out frame);
                Monitor.PulseAll(_gate);
            }
            if (frame == null)
            {
                frame = Render(sequence);
                lock (_gate) _ready[sequence] = frame;
            }
            return ScaledQr.FromPacked(frame, scale);
        }

        private byte[] Render(uint sequence)
        {
            byte[] packed;
            if (!_service.TryRenderPackedFrames(Token, sequence, 1, out packed)) throw new InvalidOperationException("QR の表示は終了しました。");
            return packed;
        }

        private void Run()
        {
            while (true)
            {
                uint next;
                lock (_gate)
                {
                    while (!_stopped && !NeedsWork(out next)) Monitor.Wait(_gate, 200);
                    if (_stopped) return;
                    NeedsWork(out next);
                }
                try
                {
                    var frame = Render(next);
                    lock (_gate) { if (unchecked(next - _wanted) <= Ahead) _ready[next] = frame; }
                }
                catch (Exception error)
                {
                    lock (_gate) { _error = error.Message; _stopped = true; }
                    return;
                }
            }
        }

        private bool NeedsWork(out uint next)
        {
            for (uint i = 1; i <= Ahead; i++)
            {
                next = unchecked(_wanted + i);
                if (!_ready.ContainsKey(next)) return true;
            }
            next = 0;
            return false;
        }

        public void Dispose()
        {
            lock (_gate) { _stopped = true; _ready.Clear(); Monitor.PulseAll(_gate); }
        }
    }
}
