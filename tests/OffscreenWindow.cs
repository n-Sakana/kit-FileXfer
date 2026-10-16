using System;
using MiniUI.Core;

// A real CPU surface without GTK/X11, system input, clipboard or a visible window.
internal sealed class OffscreenWindow : IWindow
{
    public ISurface Surface { get; }
    public OffscreenWindow(ISurface surface) { Surface = surface; }
    public bool Closed { get; private set; }
    public double Scale => Surface.Scale;
    public bool Pump(Action<InputEvent> deliver) => false;
    public void WaitForInput(int timeoutMs) { }
    public void Present(RectD dirty) { }
    public void SetTitle(string title) { }
    public void SetCaretRect(RectD rect) { }
    public void SetFullscreen(bool enabled) { }
    public void RequestClose() { Closed = true; }
    public string ClipboardGetText() => "";
    public void ClipboardSetText(string text) => throw new NotSupportedException();
}
