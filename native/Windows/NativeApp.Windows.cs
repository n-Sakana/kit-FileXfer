using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FileXfer
{
    internal sealed partial class NativeApp
    {
        private static List<CameraInfo> Cameras() { return MediaFoundationCamera.List(); }
        private static ICameraSource OpenCamera(string id, int width, int fps) { return new MediaFoundationCamera(id, width, fps); }

        // Runs on the window's UI thread (miniui_call), so the window is this thread's visible top-level window.
        static partial void RaiseWindow()
        {
            unsafe
            {
                nint found = 0;
                EnumThreadWindows(GetCurrentThreadId(), &FirstVisible, (nint)(&found));
                if (found == 0) return;
                if (IsIconic(found) != 0) ShowWindow(found, 9); // SW_RESTORE
                SetForegroundWindow(found);
            }
        }

        [UnmanagedCallersOnly]
        private static unsafe int FirstVisible(nint window, nint found)
        {
            if (IsWindowVisible(window) == 0) return 1;
            *(nint*)found = window;
            return 0;
        }

        [DllImport("user32.dll", ExactSpelling = true)] private static extern unsafe int EnumThreadWindows(uint thread, delegate* unmanaged<nint, nint, int> callback, nint state);
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int IsWindowVisible(nint window);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int IsIconic(nint window);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int ShowWindow(nint window, int command);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int SetForegroundWindow(nint window);
    }
}
