using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace FileXfer
{
    /// <summary>Video capture through Media Foundation's source reader (raw COM vtables, Native AOT safe).
    /// The reader converts the camera's format to RGB32; frames are delivered as opaque BGRA.</summary>
    internal sealed unsafe class MediaFoundationCamera : ICameraSource
    {
        private readonly string _id;
        private readonly int _wantedWidth, _wantedFps;
        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _running;
        private volatile string _error;
        private string _name = "";
        private int _width, _height;
        private double _fps;

        public MediaFoundationCamera(string id, int width, int fps) { _id = id ?? ""; _wantedWidth = width; _wantedFps = fps; }

        public string Name { get { return _name; } }
        public int Width { get { return _width; } }
        public int Height { get { return _height; } }
        public double FramesPerSecond { get { return _fps; } }
        public string Error { get { return _error; } }
        public bool Running { get { return _running; } }

        public static List<CameraInfo> List()
        {
            var result = new List<CameraInfo>();
            bool com = CoInitializeEx(0, COINIT_MULTITHREADED) >= 0;
            try
            {
                if (MFStartup(MF_VERSION, MFSTARTUP_LITE) < 0) return result;
                try
                {
                    void** devices = null; uint count = 0;
                    if (EnumDevices(&devices, &count) < 0) return result;
                    try
                    {
                        for (uint i = 0; i < count; i++)
                            result.Add(new CameraInfo(ReadString(devices[i], ref MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK), ReadString(devices[i], ref MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME)));
                    }
                    finally { for (uint i = 0; i < count; i++) Release(devices[i]); CoTaskMemFree(devices); }
                }
                finally { MFShutdown(); }
            }
            finally { if (com) CoUninitialize(); }
            return result;
        }

        public void Start(Action<byte[], int, int> frame)
        {
            _running = true;
            _thread = new Thread(() => Capture(frame)) { IsBackground = true, Name = "FileXfer camera" };
            _thread.Start();
        }

        private void Capture(Action<byte[], int, int> deliver)
        {
            bool com = CoInitializeEx(0, COINIT_MULTITHREADED) >= 0;
            bool mf = false;
            void* source = null, reader = null, attributes = null;
            try
            {
                Check(MFStartup(MF_VERSION, MFSTARTUP_LITE), "MFStartup"); mf = true;
                void** devices = null; uint count = 0;
                Check(EnumDevices(&devices, &count), "MFEnumDeviceSources");
                try
                {
                    if (count == 0) throw new CameraException("カメラが見つかりません。");
                    uint pick = 0;
                    for (uint i = 0; i < count; i++) if (_id.Length > 0 && ReadString(devices[i], ref MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK) == _id) pick = i;
                    _name = ReadString(devices[pick], ref MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
                    Guid iid = IID_IMFMediaSource;
                    int hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)V(devices[pick])[33])(devices[pick], &iid, &source); // IMFActivate::ActivateObject
                    if (hr == unchecked((int)0x80070005)) throw new CameraException("カメラを使う許可がありません。Windows の設定の「プライバシーとセキュリティ」>「カメラ」で、デスクトップ アプリにカメラの使用を許可してください。");
                    if (hr == unchecked((int)0xC00D3704) || hr == unchecked((int)0x800700AA)) throw new CameraException("カメラを開けません。ほかのアプリが使っていないか確かめてください。");
                    Check(hr, "ActivateObject");
                }
                finally { for (uint i = 0; i < count; i++) Release(devices[i]); CoTaskMemFree(devices); }

                Check(MFCreateAttributes(&attributes, 2), "MFCreateAttributes");
                SetUInt32(attributes, ref MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
                SetUInt32(attributes, ref MF_LOW_LATENCY, 1);
                Check(MFCreateSourceReaderFromMediaSource(source, attributes, &reader), "MFCreateSourceReaderFromMediaSource");
                ChooseFormat(reader);
                while (!_stop)
                {
                    uint stream, flags; long time; void* sample = null;
                    int hr = ((delegate* unmanaged[Stdcall]<void*, uint, uint, uint*, uint*, long*, void**, int>)V(reader)[9])(reader, FIRST_VIDEO_STREAM, 0, &stream, &flags, &time, &sample); // ReadSample
                    if (hr < 0) throw new CameraException(hr == unchecked((int)0xC00D3EA2) || hr == unchecked((int)0x80070491) ? "カメラが外れました。" : "カメラの映像を読めません (0x" + hr.ToString("x8") + ")。");
                    if ((flags & 0x2) != 0) throw new CameraException("カメラの映像が終わりました。"); // END OF STREAM
                    if ((flags & 0x1) != 0) throw new CameraException("カメラでエラーが起きました。");
                    if (sample == null) continue;
                    try { var pixels = Copy(sample); if (pixels != null && !_stop) deliver(pixels, _width, _height); }
                    finally { Release(sample); }
                }
            }
            catch (CameraException error) { _error = error.Message; }
            catch (Exception error) { _error = "カメラを使えません: " + error.Message; }
            finally
            {
                _running = false;
                if (reader != null) Release(reader);
                if (attributes != null) Release(attributes);
                if (source != null) { ((delegate* unmanaged[Stdcall]<void*, int>)V(source)[12])(source); Release(source); } // IMFMediaSource::Shutdown
                if (mf) MFShutdown();
                if (com) CoUninitialize();
            }
        }

        /// <summary>The native format closest to the requested width and frame rate, then RGB32 output.</summary>
        private void ChooseFormat(void* reader)
        {
            int bestIndex = -1; double bestScore = double.MaxValue; int bw = 0, bh = 0; double bfps = 0;
            for (uint i = 0; ; i++)
            {
                void* type = null;
                int hr = ((delegate* unmanaged[Stdcall]<void*, uint, uint, void**, int>)V(reader)[5])(reader, FIRST_VIDEO_STREAM, i, &type); // GetNativeMediaType
                if (hr < 0) break;
                try
                {
                    ulong size, rate; Guid subtype;
                    if (GetUInt64(type, ref MF_MT_FRAME_SIZE, out size) < 0 || GetUInt64(type, ref MF_MT_FRAME_RATE, out rate) < 0 || GetGuid(type, ref MF_MT_SUBTYPE, out subtype) < 0) continue;
                    int w = (int)(size >> 32), h = (int)(size & 0xffffffff);
                    double fps = (uint)(rate & 0xffffffff) == 0 ? 0 : (double)(uint)(rate >> 32) / (uint)(rate & 0xffffffff);
                    if (w <= 0 || h <= 0 || w > 4096 || h > 4096) continue;
                    double score = Math.Abs(w - _wantedWidth) * 10 + Math.Max(0, _wantedFps - fps) * 40 + Math.Max(0, fps - _wantedFps) + (w * 9 != h * 16 ? 30 : 0);
                    if (subtype == MFVideoFormat_MJPG) score += 5;
                    if (score < bestScore) { bestScore = score; bestIndex = (int)i; bw = w; bh = h; bfps = fps; }
                }
                finally { Release(type); }
            }
            if (bestIndex >= 0)
            {
                void* native = null;
                if (((delegate* unmanaged[Stdcall]<void*, uint, uint, void**, int>)V(reader)[5])(reader, FIRST_VIDEO_STREAM, (uint)bestIndex, &native) >= 0)
                {
                    ((delegate* unmanaged[Stdcall]<void*, uint, uint*, void*, int>)V(reader)[7])(reader, FIRST_VIDEO_STREAM, null, native); // SetCurrentMediaType
                    Release(native);
                }
            }
            void* output = null;
            Check(MFCreateMediaType(&output), "MFCreateMediaType");
            try
            {
                SetGuid(output, ref MF_MT_MAJOR_TYPE, ref MFMediaType_Video);
                SetGuid(output, ref MF_MT_SUBTYPE, ref MFVideoFormat_RGB32);
                Check(((delegate* unmanaged[Stdcall]<void*, uint, uint*, void*, int>)V(reader)[7])(reader, FIRST_VIDEO_STREAM, null, output), "SetCurrentMediaType RGB32");
            }
            finally { Release(output); }
            void* current = null;
            Check(((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)V(reader)[6])(reader, FIRST_VIDEO_STREAM, &current), "GetCurrentMediaType");
            try
            {
                ulong size, rate;
                if (GetUInt64(current, ref MF_MT_FRAME_SIZE, out size) >= 0) { _width = (int)(size >> 32); _height = (int)(size & 0xffffffff); }
                else { _width = bw; _height = bh; }
                _fps = GetUInt64(current, ref MF_MT_FRAME_RATE, out rate) >= 0 && (uint)(rate & 0xffffffff) != 0 ? (double)(uint)(rate >> 32) / (uint)(rate & 0xffffffff) : bfps;
            }
            finally { Release(current); }
            if (_width <= 0 || _height <= 0) throw new CameraException("カメラの解像度を決められません。");
        }

        private byte[] Copy(void* sample)
        {
            void* buffer = null, buffer2d = null;
            Check(((delegate* unmanaged[Stdcall]<void*, void**, int>)V(sample)[41])(sample, &buffer), "ConvertToContiguousBuffer");
            try
            {
                int w = _width, h = _height, row = w * 4;
                var pixels = new byte[row * h];
                Guid iid = IID_IMF2DBuffer;
                if (((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)V(buffer)[0])(buffer, &iid, &buffer2d) >= 0 && buffer2d != null)
                {
                    byte* scan0; int pitch;
                    Check(((delegate* unmanaged[Stdcall]<void*, byte**, int*, int>)V(buffer2d)[3])(buffer2d, &scan0, &pitch), "Lock2D");
                    try { for (int y = 0; y < h; y++) Marshal.Copy((nint)(scan0 + (long)y * pitch), pixels, y * row, row); }
                    finally { ((delegate* unmanaged[Stdcall]<void*, int>)V(buffer2d)[4])(buffer2d); }
                }
                else
                {
                    byte* data; uint max, length;
                    Check(((delegate* unmanaged[Stdcall]<void*, byte**, uint*, uint*, int>)V(buffer)[3])(buffer, &data, &max, &length), "Lock");
                    try { if (length < row * h) return null; Marshal.Copy((nint)data, pixels, 0, row * h); }
                    finally { ((delegate* unmanaged[Stdcall]<void*, int>)V(buffer)[4])(buffer); }
                }
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                return pixels;
            }
            finally { if (buffer2d != null) Release(buffer2d); Release(buffer); }
        }

        public void Dispose()
        {
            _stop = true;
            var thread = _thread;
            if (thread != null && thread != Thread.CurrentThread) thread.Join(3000);
        }

        private sealed class CameraException : Exception { public CameraException(string message) : base(message) { } }

        // ---------------------------------------------------------------- Media Foundation (mfplat, mf, mfreadwrite)
        private const uint MF_VERSION = 0x00020070, MFSTARTUP_LITE = 1, COINIT_MULTITHREADED = 0, FIRST_VIDEO_STREAM = 0xFFFFFFFC;
        private static Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = new Guid("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
        private static Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID = new Guid("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
        private static Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME = new Guid("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
        private static Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK = new Guid("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
        private static Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new Guid("0f81da2c-b537-4672-a8b2-a681b17307a3");
        private static Guid MF_LOW_LATENCY = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        private static Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        private static Guid MF_MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00aa00389b71");
        private static Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_MJPG = new Guid("47504a4d-0000-0010-8000-00aa00389b71");
        private static readonly Guid IID_IMFMediaSource = new Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66");
        private static readonly Guid IID_IMF2DBuffer = new Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");

        private static void** V(void* p) { return *(void***)p; }
        private static void Release(void* p) { if (p != null) ((delegate* unmanaged[Stdcall]<void*, uint>)V(p)[2])(p); }
        private static void Check(int hr, string what) { if (hr < 0) throw new CameraException("カメラを使えません (" + what + " 0x" + hr.ToString("x8") + ")。"); }

        private static int EnumDevices(void*** devices, uint* count)
        {
            void* attributes = null;
            int hr = MFCreateAttributes(&attributes, 1);
            if (hr < 0) return hr;
            try
            {
                SetGuid(attributes, ref MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, ref MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
                return MFEnumDeviceSources(attributes, devices, count);
            }
            finally { Release(attributes); }
        }

        // IMFAttributes slots: GetUINT64 8, GetGUID 10, GetAllocatedString 13, SetUINT32 21, SetGUID 24.
        private static int GetUInt64(void* attributes, ref Guid key, out ulong value)
        {
            ulong v = 0; int hr;
            fixed (Guid* k = &key) hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, ulong*, int>)V(attributes)[8])(attributes, k, &v);
            value = v; return hr;
        }
        private static int GetGuid(void* attributes, ref Guid key, out Guid value)
        {
            Guid v; int hr;
            fixed (Guid* k = &key) hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, int>)V(attributes)[10])(attributes, k, &v);
            value = v; return hr;
        }
        private static string ReadString(void* attributes, ref Guid key)
        {
            char* text = null; uint length = 0; int hr;
            fixed (Guid* k = &key) hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, char**, uint*, int>)V(attributes)[13])(attributes, k, &text, &length);
            if (hr < 0 || text == null) return "";
            try { return new string(text, 0, (int)length); } finally { CoTaskMemFree(text); }
        }
        private static void SetUInt32(void* attributes, ref Guid key, uint value)
        {
            fixed (Guid* k = &key) ((delegate* unmanaged[Stdcall]<void*, Guid*, uint, int>)V(attributes)[21])(attributes, k, value);
        }
        private static void SetGuid(void* attributes, ref Guid key, ref Guid value)
        {
            fixed (Guid* k = &key) fixed (Guid* v = &value) ((delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, int>)V(attributes)[24])(attributes, k, v);
        }

        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateAttributes(void** attributes, uint initialSize);
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(void** type);
        [DllImport("mf.dll", ExactSpelling = true)] private static extern int MFEnumDeviceSources(void* attributes, void*** sources, uint* count);
        [DllImport("mfreadwrite.dll", ExactSpelling = true)] private static extern int MFCreateSourceReaderFromMediaSource(void* source, void* attributes, void** reader);
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(nint reserved, uint coinit);
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoTaskMemFree(void* memory);
    }
}
