using System.Drawing.Drawing2D;
using System.Diagnostics;
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
    private readonly Label depthLabel = new() { Text = "Target depth", TextAlign = ContentAlignment.MiddleLeft, AutoSize = false };
    private readonly ToolTip tips = new() { AutoPopDelay = 15000, InitialDelay = 450, ReshowDelay = 150 };
    private readonly BoardDisclosure disclosure = new() { TabIndex = 3 };
    private readonly BoardPreview boardPreview = new();
    private readonly Panel previewClip = new() { TabStop = false };
    private readonly System.Windows.Forms.Timer previewAnimation = new() { Interval = 10 };
    private readonly Stopwatch previewClock = new();
    private float previewProgress, previewStart;
    private double previewDuration = 150;
    private int previewBaseHeight, previewExtraHeight, previewPanelWidth;
    private Rectangle previewWorkingArea;
    private bool previewExpanded;
    private readonly ScreenScanner scanner = new();
    private readonly EngineService engine;
    private readonly ArrowOverlay arrows = new();
    private readonly AlwaysOnTop? alwaysOnTop;
    private readonly bool windowMode;
    private Task<BoardFrame?>? captureTask;
    private Task? captureLoop;
    private bool captureBusy, closing, captureExcluded;
    private int generation, stableFrames;
    private string? lastPosition, currentKey, analyzingKey;
    private BoardFrame? currentFrame;
    private Analysis? whiteResult, blackResult;
    public bool IsRunning { get; private set; }
    public int CaptureCount { get; private set; }
    public int ArrowCount => windowMode ? boardPreview.ArrowCount : arrows.ArrowCount;
    public BoardFrame? CurrentFrame => currentFrame;
    public bool ExcludesOwnWindows => captureExcluded && arrows.CaptureExcluded;
    public bool ArrowsClickThrough => (Native.GetWindowLongPtr(arrows.Handle, -20).ToInt64() & 0x20) != 0;

    public MainForm(EngineService? service = null, bool windowMode = false)
    {
        this.windowMode = windowMode;
        if (!windowMode) alwaysOnTop = new AlwaysOnTop(this, arrows);
        engine = service ?? new EngineService(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FishEyes", "analysis-cache-local-v1.json"));
        Text = "FishEyes · live chess";
        Icon = Branding.AppIcon;
        // All geometry is laid out from DeviceDpi in LayoutPanel. Mixing native
        // autoscaling with custom-painted geometry can clip the move cards.
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(300, 300);
        FormBorderStyle = windowMode ? FormBorderStyle.FixedSingle : FormBorderStyle.None;
        MaximizeBox = false; TopMost = !windowMode;
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
        previewClip.Controls.Add(boardPreview);
        Controls.AddRange([toggle, depthLabel, depth, status, whiteLabel, blackLabel, detail, close, disclosure, previewClip]);
        LayoutPanel();
        tips.SetToolTip(toggle, "Start or pause automatic analysis");
        tips.SetToolTip(depth, "Target depth 1–40. Local Stockfish searches up to 500 ms per side; actual depth may be lower.");
        tips.SetToolTip(close, "Close FishEyes (Alt+F4)");
        tips.SetToolTip(whiteLabel, "Blue arrow · assumes White is to move");
        tips.SetToolTip(blackLabel, "Orange arrow · assumes Black is to move");
        tips.SetToolTip(disclosure, "Show the pieces FishEyes detected. Uncertain positions are shown for inspection only.");
        disclosure.Click += (_, _) => TogglePreview();
        previewAnimation.Tick += (_, _) => AdvancePreview();
        close.Click += (_, _) => Close();
        toggle.Click += (_, _) => SetRunning(!IsRunning);
        depth.ValueChanged += (_, _) => { ResetView(); if (IsRunning) status.Text = "Checking position…"; };
        // Create the arrow HWND now so capture exclusion is established before capture.
        _ = arrows.Handle;
    }
    private void LayoutPanel()
    {
        float scale = DeviceDpi / 96f;
        int Px(int value) => (int)Math.Round(value * scale);
        void Place(Control control, int x, int y, int width, int height) => control.SetBounds(Px(x), Px(y), Px(width), Px(height));
        SuspendLayout();
        Place(close, 256, 12, 28, 28);
        Place(toggle, 220, 64, 60, 30);
        Place(status, 20, 65, 190, 28);
        Place(depthLabel, 20, 108, 148, 32);
        Place(depth, 174, 108, 106, 32);
        Place(whiteLabel, 20, 156, 124, 62);
        Place(blackLabel, 156, 156, 124, 62);
        Place(detail, 20, 230, 260, 18);
        Place(disclosure, 20, 260, 260, 28);
        ApplyPreviewLayout(refreshGeometry: true);
        ResumeLayout();
    }
    private void TogglePreview()
    {
        AdvancePreview();
        ApplyPreviewLayout(refreshGeometry: true);
        boardPreview.PrepareForAnimation();
        previewExpanded = !previewExpanded;
        disclosure.AccessibleName = previewExpanded ? "Hide detected board" : "Show detected board";
        previewStart = previewProgress;
        previewDuration = Math.Max(60, 150 * Math.Abs((previewExpanded ? 1 : 0) - previewStart));
        if (Visible && OverlayTheme.AnimationsEnabled)
        { previewClock.Restart(); previewAnimation.Start(); }
        else
        { previewAnimation.Stop(); previewClock.Reset(); previewProgress = previewExpanded ? 1 : 0; ApplyPreviewLayout(); }
    }
    private void AdvancePreview()
    {
        if (!previewClock.IsRunning) return;
        float t = (float)Math.Clamp(previewClock.Elapsed.TotalMilliseconds / previewDuration, 0, 1);
        // Respond immediately to the click, then settle gently at the endpoint.
        float eased = 1 - MathF.Pow(1 - t, 3);
        previewProgress = previewStart + ((previewExpanded ? 1 : 0) - previewStart) * eased;
        if (t >= 1) { previewAnimation.Stop(); previewClock.Reset(); }
        ApplyPreviewLayout();
    }
    private void ApplyPreviewLayout(bool refreshGeometry = false)
    {
        SuspendLayout(); previewClip.SuspendLayout();
        if (refreshGeometry)
        {
            float s = DeviceDpi / 96f;
            int Px(int value) => (int)Math.Round(value * s);
            previewWorkingArea = Screen.FromControl(this).WorkingArea;
            previewBaseHeight = Px(300); previewPanelWidth = Px(300);
            int frameHeight = SizeFromClientSize(Size.Empty).Height;
            previewExtraHeight = Math.Min(Px(296), Math.Max(Px(80), previewWorkingArea.Height - Px(316) - frameHeight));
            previewClip.SetBounds(Px(20), previewBaseHeight, Px(260), previewClip.Height);
            boardPreview.SetBounds(0, 0, Px(260), previewExtraHeight - Px(12));
        }
        int revealed = (int)Math.Round(previewExtraHeight * previewProgress);
        previewClip.Height = revealed;
        previewClip.Visible = revealed > 0;
        disclosure.Progress = previewProgress;
        var outerSize = SizeFromClientSize(new Size(previewPanelWidth, previewBaseHeight + revealed));
        int top = Visible ? Math.Max(previewWorkingArea.Top, Math.Min(Top, previewWorkingArea.Bottom - outerSize.Height)) : Top;
        // One native bounds update per frame instead of resize followed by move.
        SetBounds(Left, top, outerSize.Width, outerSize.Height);
        previewClip.ResumeLayout(false); ResumeLayout(false);
    }
    protected override void OnShown(EventArgs e)
    {
        LayoutPanel();
        alwaysOnTop?.Start();
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
        if (windowMode) { Invalidate(); return; }
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
        using var shape = OverlayTheme.Rounded(new RectangleF(.5f, .5f, ClientSize.Width - 1, ClientSize.Height - 1), 16 * s);
        using var border = new Pen(OverlayTheme.Border);
        g.DrawPath(border, shape);
        using var separator = new Pen(OverlayTheme.Border);
        g.DrawLine(separator, 20 * s, 52 * s, ClientSize.Width - 20 * s, 52 * s);
        g.DrawLine(separator, 20 * s, 254 * s, ClientSize.Width - 20 * s, 254 * s);
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
        // Screenshot mode deliberately remains visible to normal capture tools.
        captureExcluded = !windowMode && Native.SetWindowDisplayAffinity(Handle, 0x11);
    }
    public void SetRunning(bool running)
    {
        if (closing || IsRunning == running) return;
        IsRunning = running;
        ResetView();
        toggle.Active = running;
        status.Text = running ? "Finding board…" : "Analysis paused";
        detail.Text = running ? "Keep your chessboard visible" : "Turn on to find your board";
        if (running)
        {
            // A rapid Off/On reuses the existing loop until its current scan
            // finishes. Starting another would overlap screen recognition.
            if (captureLoop is null || captureLoop.IsCompleted) captureLoop = CaptureLoopAsync();
        }
    }
    private void ResetView(bool clearPreview = true)
    {
        engine.CancelPending();
        generation++; stableFrames = 0;
        lastPosition = currentKey = analyzingKey = null;
        currentFrame = null; whiteResult = blackResult = null;
        arrows.Clear();
        boardPreview.ClearMoves();
        whiteLabel.Text = "—"; blackLabel.Text = "—";
        if (clearPreview) boardPreview.UpdateBoard(null, IsRunning);
    }
    private async Task CaptureLoopAsync()
    {
        while (IsRunning && !closing)
        {
            await CaptureTickAsync();
            // Return to the UI message loop without adding a polling delay.
            // Recognition itself runs off-thread, with one capture at a time.
            await Task.Yield();
        }
    }
    private async Task CaptureTickAsync()
    {
        if (!IsRunning || captureBusy || closing) return;
        captureBusy = true;
        int capturedGeneration = generation;
        bool hideForCapture = !windowMode && !ExcludesOwnWindows;
        bool hadArrows = arrows.Visible;
        try
        {
            if (hideForCapture) { Hide(); arrows.Hide(); await Task.Delay(80); Native.DwmFlush(); }
            if (!IsRunning || closing) return;
            var monitors = Screen.AllScreens.Select(s => s.Bounds).ToArray();
            // Ignore our preview only inside the recognition image. The real
            // window stays visible in screenshots and never flashes hidden.
            Rectangle? ignoredWindow = windowMode && Visible && WindowState != FormWindowState.Minimized ? Bounds : null;
            captureTask = Task.Run(() => scanner.Scan(monitors, ignoredWindow));
            BoardFrame? frame = await captureTask;
            if (closing || !IsRunning || capturedGeneration != generation) return;
            CaptureCount++;
            boardPreview.UpdateBoard(scanner.LastObservation, IsRunning);
            if (frame is null)
            {
                ResetView(clearPreview: false);
                bool uncertain = scanner.LastObservation is not null;
                status.Text = uncertain ? "Check detected pieces" : "Finding board…";
                detail.Text = uncertain ? "Open Detected board to inspect" : "Keep the full chessboard visible";
                return;
            }
            currentFrame = frame;
            string position = frame.Recognition.Position.Placement;
            if (lastPosition != position) { stableFrames = 1; lastPosition = position; }
            else stableFrames++;
            string key = $"{position}|{depth.Value}";
            if (currentKey != key)
            {
                engine.CancelPending();
                currentKey = key; generation++;
                analyzingKey = null; whiteResult = blackResult = null;
                arrows.Clear();
                boardPreview.ClearMoves();
                whiteLabel.Text = "Waiting…"; blackLabel.Text = "Waiting…";
            }
            if (stableFrames < 2)
            {
                status.Text = "Checking position…";
                return;
            }
            status.Text = whiteResult is not null && blackResult is not null ? "Moves ready" : "Analyzing…";
            UpdateEngineDetail();
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
                UpdateEngineDetail();
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
    private void UpdateEngineDetail()
    {
        string reached = $"White {whiteResult?.Depth?.ToString() ?? "—"} · Black {blackResult?.Depth?.ToString() ?? "—"}";
        detail.Text = engine.CacheWarning is not null ? "Cache needs attention · hover for details"
            : whiteResult?.Depth is not null || blackResult?.Depth is not null
                ? $"Local · depth reached: {reached}" : "Local Stockfish · up to 500 ms per side";
        tips.SetToolTip(detail, engine.CacheWarning ?? "Runs offline. Each arrow assumes that color is to move. " +
            $"Depth reached: {reached}. Target depth is capped by the 500 ms search budget.");
    }
    private void ShowCurrentArrows()
    {
        if (currentFrame is not null) boardPreview.SetMoves(currentFrame, whiteResult, blackResult);
        if (!windowMode && currentFrame is not null && (whiteResult?.Move is not null || blackResult?.Move is not null))
            arrows.SetMoves(currentFrame, whiteResult, blackResult);
        alwaysOnTop?.Raise();
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        closing = true; IsRunning = false; generation++;
        alwaysOnTop?.Dispose();
        previewAnimation.Stop(); previewAnimation.Dispose(); tips.Dispose(); arrows.Dispose(); engine.Dispose();
        if (captureTask is { IsCompleted: false } task) _ = task.ContinueWith(_ => scanner.Dispose());
        else scanner.Dispose();
        base.OnFormClosed(e);
    }
}
