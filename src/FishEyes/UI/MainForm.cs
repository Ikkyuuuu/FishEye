using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace FishEyes;

public sealed class MainForm : Form
{
    private readonly OverlayButton toggle = new() { IsToggle = true, AccessibleName = "Turn on analysis", TabIndex = 0 };
    private readonly DepthStepper depth = new() { TabIndex = 1 };
    private readonly OverlayButton close = new() { Text = "close", AccessibleName = "Close FishEyes", TabIndex = 2 };
    private readonly Label status = new() { Text = "Analysis paused", AutoSize = false };
    private readonly MoveCard whiteLabel = new() { Caption = "White", Text = "—", Accent = ArrowOverlay.WhiteColor };
    private readonly MoveCard blackLabel = new() { Caption = "Black", Text = "—", Accent = ArrowOverlay.BlackColor };
    private readonly Label detail = new() { Text = "Turn on to find your board", ForeColor = OverlayTheme.Muted, AutoSize = false };
    private readonly Label depthLabel = new() { Text = "Search depth", TextAlign = ContentAlignment.MiddleLeft, AutoSize = false };
    private readonly ToolTip tips = new() { AutoPopDelay = 15000, InitialDelay = 450, ReshowDelay = 150 };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly ScreenScanner scanner = new();
    private readonly EngineService engine;
    private readonly ArrowOverlay arrows = new();
    private Task<BoardFrame?>? captureTask;
    private bool captureBusy, closing, captureExcluded;
    private int generation, stableFrames;
    private string? lastPosition, currentKey, analyzingKey;
    private BoardFrame? currentFrame;
    private Analysis? whiteResult, blackResult;
    public bool IsRunning { get; private set; }
    public int CaptureCount { get; private set; }
    public int ArrowCount => arrows.ArrowCount;
    public BoardFrame? CurrentFrame => currentFrame;
    public bool ExcludesOwnWindows => captureExcluded && arrows.CaptureExcluded;
    public bool ArrowsClickThrough => (Native.GetWindowLongPtr(arrows.Handle, -20).ToInt64() & 0x20) != 0;

    public MainForm(EngineService? service = null)
    {
        engine = service ?? new EngineService(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FishEyes", "analysis-cache-v1.json"));
        Text = "FishEyes · live chess";
        Icon = Branding.AppIcon;
        // All geometry is laid out from DeviceDpi in LayoutPanel. Mixing native
        // autoscaling with custom-painted geometry can clip the move cards.
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(300, 252);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false; TopMost = true;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9);
        BackColor = OverlayTheme.Background; ForeColor = OverlayTheme.Text;
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Right - Width - 24, area.Top + 24);
        status.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        status.TextAlign = ContentAlignment.MiddleLeft;
        detail.Font = new Font("Segoe UI", 8);
        detail.AutoEllipsis = true;
        Controls.AddRange([toggle, depthLabel, depth, status, whiteLabel, blackLabel, detail, close]);
        LayoutPanel();
        tips.SetToolTip(toggle, "Start or pause automatic analysis");
        tips.SetToolTip(depth, "Depth 1–15. Higher values search further and may take longer.");
        tips.SetToolTip(close, "Close FishEyes (Alt+F4)");
        tips.SetToolTip(whiteLabel, "Blue arrow · assumes White is to move");
        tips.SetToolTip(blackLabel, "Orange arrow · assumes Black is to move");
        close.Click += (_, _) => Close();
        toggle.Click += (_, _) => SetRunning(!IsRunning);
        depth.ValueChanged += (_, _) => { ResetView(); if (IsRunning) status.Text = "Checking position…"; };
        timer.Tick += async (_, _) => await CaptureTickAsync();
        // Create the arrow HWND now so capture exclusion is established before capture.
        _ = arrows.Handle;
    }
    private void LayoutPanel()
    {
        float scale = DeviceDpi / 96f;
        int Px(int value) => (int)Math.Round(value * scale);
        void Place(Control control, int x, int y, int width, int height) => control.SetBounds(Px(x), Px(y), Px(width), Px(height));
        SuspendLayout();
        ClientSize = new Size(Px(300), Px(252));
        Place(close, 256, 12, 28, 28);
        Place(toggle, 220, 64, 60, 30);
        Place(status, 20, 65, 190, 28);
        Place(depthLabel, 20, 108, 148, 32);
        Place(depth, 174, 108, 106, 32);
        Place(whiteLabel, 20, 156, 124, 62);
        Place(blackLabel, 156, 156, 124, 62);
        Place(detail, 20, 230, 260, 18);
        ResumeLayout();
    }
    protected override void OnShown(EventArgs e)
    {
        LayoutPanel();
        base.OnShown(e);
        var area = Screen.FromControl(this).WorkingArea;
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayoutPanel();
    }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 0 || Height <= 0) return;
        using var shape = OverlayTheme.Rounded(new RectangleF(0, 0, Width, Height), 16 * DeviceDpi / 96f);
        var old = Region; Region = new Region(shape); old?.Dispose();
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float s = DeviceDpi / 96f;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = OverlayTheme.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 16 * s);
        using var border = new Pen(OverlayTheme.Border);
        g.DrawPath(border, shape);
        using var separator = new Pen(OverlayTheme.Border);
        g.DrawLine(separator, 20 * s, 52 * s, Width - 20 * s, 52 * s);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(Branding.Logo, new RectangleF(16 * s, 10 * s, 38 * s, 38 * s));
        using var title = new Font("Segoe UI", 11, FontStyle.Bold);
        TextRenderer.DrawText(g, "FishEyes", title, new Rectangle((int)(60 * s), (int)(14 * s), (int)(160 * s), (int)(30 * s)),
            OverlayTheme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.Y < 52 * DeviceDpi / 96f)
        {
            ReleaseCapture();
            var point = PointToScreen(e.Location);
            int coordinates = (point.X & 0xffff) | (point.Y << 16);
            SendMessage(Handle, 0xA1, new IntPtr(2), new IntPtr(coordinates));
        }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        captureExcluded = Native.SetWindowDisplayAffinity(Handle, 0x11);
    }
    public void SetRunning(bool running)
    {
        if (closing || IsRunning == running) return;
        IsRunning = running;
        ResetView();
        toggle.Active = running;
        status.Text = running ? "Finding board…" : "Analysis paused";
        detail.Text = running ? "Keep your chessboard visible" : "Turn on to find your board";
        if (running) { timer.Start(); _ = CaptureTickAsync(); }
        else { timer.Stop(); engine.CancelPending(); }
    }
    private void ResetView()
    {
        generation++; stableFrames = 0;
        lastPosition = currentKey = analyzingKey = null;
        currentFrame = null; whiteResult = blackResult = null;
        arrows.Clear();
        whiteLabel.Text = "—"; blackLabel.Text = "—";
    }
    private async Task CaptureTickAsync()
    {
        if (!IsRunning || captureBusy || closing) return;
        captureBusy = true;
        int capturedGeneration = generation;
        bool hideForCapture = !ExcludesOwnWindows;
        bool hadArrows = arrows.Visible;
        try
        {
            if (hideForCapture) { Hide(); arrows.Hide(); await Task.Delay(80); Native.DwmFlush(); }
            if (!IsRunning || closing) return;
            var monitors = Screen.AllScreens.Select(s => s.Bounds).ToArray();
            captureTask = Task.Run(() => scanner.Scan(monitors));
            BoardFrame? frame = await captureTask;
            CaptureCount++;
            if (closing || !IsRunning || capturedGeneration != generation) return;
            if (frame is null)
            {
                ResetView();
                status.Text = "Finding board…";
                detail.Text = "Keep the full chessboard visible";
                return;
            }
            currentFrame = frame;
            string position = frame.Recognition.Position.Placement;
            if (lastPosition != position) { stableFrames = 1; lastPosition = position; }
            else stableFrames++;
            string key = $"{position}|{depth.Value}";
            if (currentKey != key)
            {
                currentKey = key; generation++;
                analyzingKey = null; whiteResult = blackResult = null;
                arrows.Clear();
                whiteLabel.Text = "Waiting…"; blackLabel.Text = "Waiting…";
            }
            if (stableFrames < 2)
            {
                status.Text = "Checking position…";
                return;
            }
            status.Text = whiteResult is not null && blackResult is not null ? "Moves ready" : "Analyzing…";
            detail.Text = engine.CacheWarning is null ? "Updates automatically as you play" : "Cache needs attention · hover for details";
            tips.SetToolTip(detail, engine.CacheWarning ?? "Each arrow assumes that color is to move.");
            ShowCurrentArrows();
            if (analyzingKey != key && (whiteResult is null || blackResult is null))
            {
                analyzingKey = key;
                _ = AnalyzeBothAsync(frame, key, (int)depth.Value, generation);
            }
        }
        catch (Exception e)
        {
            if (!closing && IsRunning) { ResetView(); status.Text = "Can't read the board"; detail.Text = "Trying again · hover for details"; tips.SetToolTip(detail, e.Message); }
        }
        finally
        {
            captureBusy = false;
            if (hideForCapture && !closing)
            {
                Show();
                if (hadArrows && IsRunning) ShowCurrentArrows();
            }
        }
    }
    private async Task AnalyzeBothAsync(BoardFrame frame, string key, int requestedDepth, int requestedGeneration)
    {
        bool StillCurrent() => !closing && IsRunning && requestedGeneration == generation && key == currentKey;
        async Task AnalyzeSide(bool white)
        {
            if ((white ? whiteResult : blackResult) is not null) return;
            var label = white ? whiteLabel : blackLabel;
            label.Text = "Thinking…";
            try
            {
                var result = await engine.AnalyzeAsync(frame.Recognition.Position, white, requestedDepth);
                if (!StillCurrent()) return;
                if (white) whiteResult = result.Value; else blackResult = result.Value;
                string summary = result.Value.Move ?? result.Value.Status;
                if (result.Value.Move is not null)
                    summary = $"{result.Value.Move[..2]} → {result.Value.Move.Substring(2, 2)}" + (result.Value.Move.Length == 5 ? $" ={char.ToUpperInvariant(result.Value.Move[4])}" : "");
                label.Text = summary == "Turn not legal" ? "Not this turn" : summary;
                if (whiteResult is not null && blackResult is not null) status.Text = "Moves ready";
                ShowCurrentArrows();
            }
            catch (OperationCanceledException) when (closing) { }
            catch (Exception e)
            {
                if (StillCurrent()) { label.Text = "Retrying…"; status.Text = "Waiting for engine"; detail.Text = "Retrying automatically · hover for details"; tips.SetToolTip(detail, e.Message); }
            }
        }
        await Task.WhenAll(AnalyzeSide(true), AnalyzeSide(false));
        if (StillCurrent()) analyzingKey = null;
    }
    private void ShowCurrentArrows()
    {
        if (currentFrame is not null && (whiteResult?.Move is not null || blackResult?.Move is not null))
            arrows.SetMoves(currentFrame, whiteResult, blackResult);
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        closing = true; IsRunning = false; generation++;
        timer.Stop(); timer.Dispose(); tips.Dispose(); arrows.Dispose(); engine.Dispose();
        if (captureTask is { IsCompleted: false } task) _ = task.ContinueWith(_ => scanner.Dispose());
        else scanner.Dispose();
        base.OnFormClosed(e);
    }
}
