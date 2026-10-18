using System;

namespace FileXfer
{
    /// <summary>An app: image for the page: its size and its pixels as BGRA, premultiplied alpha, top-down rows.</summary>
    internal interface IAppImage
    {
        int Width { get; }
        int Height { get; }
        /// <summary>Write Width * Height * 4 bytes.</summary>
        void CopyTo(Span<byte> destination);
    }

    /// <summary>Pixels kept for the page (the camera preview): BGRA, opaque.</summary>
    internal sealed class NativeBitmap : IAppImage
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public readonly byte[] Pixels;

        public NativeBitmap(int width, int height, byte[] pixels)
        {
            if (width <= 0 || height <= 0 || pixels == null || pixels.Length < width * height * 4) throw new ArgumentException("Invalid bitmap");
            Width = width; Height = height; Pixels = pixels;
        }

        public void CopyTo(Span<byte> destination) { Pixels.AsSpan(0, Width * Height * 4).CopyTo(destination); }

        /// <summary>Box-filtered downscale of opaque BGRA to at most maxWidth (aspect kept); the source when already small.</summary>
        public static NativeBitmap Downscale(byte[] bgra, int width, int height, int maxWidth)
        {
            if (width <= maxWidth) { var copy = new byte[width * height * 4]; Buffer.BlockCopy(bgra, 0, copy, 0, copy.Length); return new NativeBitmap(width, height, copy); }
            int factor = (width + maxWidth - 1) / maxWidth;
            int w = width / factor, h = height / factor;
            var pixels = new byte[w * h * 4];
            int area = factor * factor;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int b = 0, g = 0, r = 0;
                    for (int dy = 0; dy < factor; dy++)
                    {
                        int row = ((y * factor + dy) * width + x * factor) * 4;
                        for (int dx = 0; dx < factor; dx++) { int s = row + dx * 4; b += bgra[s]; g += bgra[s + 1]; r += bgra[s + 2]; }
                    }
                    int o = (y * w + x) * 4;
                    pixels[o] = (byte)(b / area); pixels[o + 1] = (byte)(g / area); pixels[o + 2] = (byte)(r / area); pixels[o + 3] = 255;
                }
            return new NativeBitmap(w, h, pixels);
        }
    }

    /// <summary>A QR code (quiet zone included) drawn with Scale x Scale pixels per module. The page shows it at one
    /// image pixel per device pixel, so every module keeps the same sharp size on screen.</summary>
    internal sealed class ScaledQr : IAppImage
    {
        private readonly byte[] _bits; // row-major, most significant bit first, dark = 1
        private readonly int _offset;
        public readonly int Side, Scale;

        private ScaledQr(byte[] bits, int offset, int side, int scale)
        {
            if (side <= 0 || scale < 1 || (long)side * scale > 8192) throw new ArgumentException("Invalid QR size");
            _bits = bits; _offset = offset; Side = side; Scale = scale;
        }

        public int Width { get { return Side * Scale; } }
        public int Height { get { return Side * Scale; } }

        /// <summary>The first frame of a FQR1 packed batch.</summary>
        public static ScaledQr FromPacked(byte[] packed, int scale)
        {
            if (packed == null || packed.Length < 12 || packed[0] != 0x46 || packed[1] != 0x51 || packed[2] != 0x52 || packed[3] != 0x31)
                throw new ArgumentException("Invalid packed QR batch");
            int side = packed[4] | packed[5] << 8;
            if (packed.Length < 12 + (side * side + 7) / 8) throw new ArgumentException("Invalid packed QR batch");
            return new ScaledQr(packed, 12, side, scale);
        }

        /// <summary>A QR matrix (dark = true) with a white quiet zone around it.</summary>
        public static ScaledQr FromMatrix(int width, int height, Func<int, int, bool> dark, int quietZone, int scale)
        {
            int side = Math.Max(width, height) + quietZone * 2;
            var bits = new byte[(side * side + 7) / 8];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (dark(x, y))
                    {
                        int bit = (y + quietZone) * side + x + quietZone;
                        bits[bit >> 3] |= (byte)(0x80 >> (bit & 7));
                    }
            return new ScaledQr(bits, 0, side, scale);
        }

        public ScaledQr WithScale(int scale) { return scale == Scale ? this : new ScaledQr(_bits, _offset, Side, scale); }

        public void CopyTo(Span<byte> destination)
        {
            int size = Side * Scale, stride = size * 4;
            for (int my = 0; my < Side; my++)
            {
                // Build the first pixel row of this module row, then repeat it for the module's height.
                var first = destination.Slice(my * Scale * stride, stride);
                for (int mx = 0; mx < Side; mx++)
                {
                    int bit = my * Side + mx;
                    byte v = (_bits[_offset + (bit >> 3)] & (0x80 >> (bit & 7))) != 0 ? (byte)0 : (byte)255;
                    var module = first.Slice(mx * Scale * 4, Scale * 4);
                    for (int k = 0; k < module.Length; k += 4) { module[k] = v; module[k + 1] = v; module[k + 2] = v; module[k + 3] = 255; }
                }
                for (int k = 1; k < Scale; k++) first.CopyTo(destination.Slice((my * Scale + k) * stride, stride));
            }
        }
    }
}
