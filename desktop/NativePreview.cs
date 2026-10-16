using System;
using System.Runtime.InteropServices;
using MiniUI.Core;
using MiniUI.Platform.Linux;

namespace FileXfer;

// App-owned Cairo blit into the MiniUI Linux framebuffer; no engine source changes.
internal static class NativePreview
{
    [DllImport("libcairo.so.2")] private static extern nint cairo_image_surface_create_for_data(nint data, int format, int width, int height, int stride);
    [DllImport("libcairo.so.2")] private static extern void cairo_scale(nint context, double x, double y);
    [DllImport("libcairo.so.2")] private static extern void cairo_translate(nint context, double x, double y);
    internal static unsafe void Paint(CairoSurface target, RectD rect, byte[] rgb, int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (int src = 0, dst = 0; src < rgb.Length; src += 3, dst += 4)
        { pixels[dst] = rgb[src + 2]; pixels[dst + 1] = rgb[src + 1]; pixels[dst + 2] = rgb[src]; pixels[dst + 3] = 255; }
        fixed (byte* data = pixels)
        {
            nint image = cairo_image_surface_create_for_data((nint)data, 0, width, height, width * 4);
            nint context = 0;
            try
            {
                Cairo.Check(Cairo.cairo_surface_status(image), "camera preview");
                context = Cairo.cairo_create(target.NativeSurface);
                Cairo.cairo_rectangle(context, rect.X, rect.Y, rect.Width, rect.Height); Cairo.cairo_clip(context);
                cairo_translate(context, rect.X, rect.Y); cairo_scale(context, rect.Width / width, rect.Height / height);
                Cairo.cairo_set_source_surface(context, image, 0, 0); Cairo.cairo_paint(context);
                Cairo.Check(Cairo.cairo_status(context), "camera blit"); Cairo.cairo_surface_flush(target.NativeSurface);
            }
            finally { if (context != 0) Cairo.cairo_destroy(context); Cairo.cairo_surface_destroy(image); }
        }
    }
}
