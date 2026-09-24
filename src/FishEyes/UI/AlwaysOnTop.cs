using System.Runtime.InteropServices;

namespace FishEyes;

/// <summary>Keeps both overlay windows above applications without activating them.</summary>
internal sealed class AlwaysOnTop : IDisposable
{
    private readonly Form panel, arrows;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 750 };
    private readonly WinEventCallback foregroundChanged;
    private IntPtr hook;
    private bool disposed, queued;

    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint firstEvent, uint lastEvent, IntPtr module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    public AlwaysOnTop(Form panel, Form arrows)
    {
        this.panel = panel; this.arrows = arrows;
        foregroundChanged = (_, _, _, _, _, _, _) => QueueRaise();
        timer.Tick += (_, _) => Raise();
    }
    public void Start()
    {
        if (disposed || timer.Enabled) return;
        // EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT. Keep the delegate
        // rooted until the hook is removed. The timer also handles applications
        // which reorder topmost windows without changing the foreground window.
        hook = SetWinEventHook(3, 3, IntPtr.Zero, foregroundChanged, 0, 0, 0);
        timer.Start(); Raise();
    }
    private void QueueRaise()
    {
        if (disposed || queued || !panel.IsHandleCreated) return;
        queued = true;
        try
        {
            panel.BeginInvoke((Action)(() => { queued = false; if (!disposed) Raise(); }));
        }
        catch (InvalidOperationException) { queued = false; }
    }
    public void Raise()
    {
        if (disposed || panel.IsDisposed || !panel.Visible || panel.WindowState == FormWindowState.Minimized) return;
        static void Lift(Form window)
        {
            if (window.IsDisposed || !window.IsHandleCreated || !window.Visible) return;
            // HWND_TOPMOST; SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE.
            // No SHOWWINDOW flag: capture hiding and pause stay intact.
            SetWindowPos(window.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x13);
        }
        Lift(arrows);
        Lift(panel);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop(); timer.Dispose();
        if (hook != IntPtr.Zero) { UnhookWinEvent(hook); hook = IntPtr.Zero; }
    }
}
