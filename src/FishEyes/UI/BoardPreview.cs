using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace FishEyes;

internal sealed class BoardDisclosure : Button
{
    private bool hover;
    private float progress;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float Progress { get => progress; set { progress = value; Invalidate(); } }
    public BoardDisclosure()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        BackColor = OverlayTheme.Background; ForeColor = OverlayTheme.Muted;
        UseVisualStyleBackColor = false; Cursor = Cursors.Hand;
        Text = "Detected board"; AccessibleName = "Show detected board";
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        float s = DeviceDpi / 96f;
        var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        Color ink = hover ? OverlayTheme.Text : OverlayTheme.Muted;
        if (hover)
        {
            using var path = OverlayTheme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 7 * s);
            using var fill = new SolidBrush(OverlayTheme.Surface); g.FillPath(fill, path);
        }
        // A small board mark keeps the footer distinct from the analysis switch.
        using var pen = new Pen(ink, s);
        float left = 8 * s, top = (Height - 12 * s) / 2;
        g.DrawRectangle(pen, left, top, 12 * s, 12 * s);
        g.DrawLine(pen, left + 6 * s, top, left + 6 * s, top + 12 * s);
        g.DrawLine(pen, left, top + 6 * s, left + 12 * s, top + 6 * s);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(30 * s), 0, Width - (int)(60 * s), Height), ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        float cx = Width - 14 * s, cy = Height / 2f, bend = (1 - 2 * progress) * 2.5f * s;
        using var chevron = new Pen(ink, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(chevron, new PointF[] { new(cx - 4 * s, cy - bend), new(cx, cy + bend), new(cx + 4 * s, cy - bend) });
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -2, -2), OverlayTheme.Accent, BackColor);
    }
}

/// <summary>Renders the recognized position, never pixels copied from the screen.</summary>
public sealed class BoardPreview : Control
{
    private BoardFrame? frame;
    private bool running;
    private Analysis? whiteMove, blackMove;
    private Bitmap? renderedBoard;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int RenderCount { get; private set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public BoardFrame? Frame => frame;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ArrowCount => (whiteMove?.Move is null ? 0 : 1) + (blackMove?.Move is null ? 0 : 1);
    public BoardPreview()
    {
        DoubleBuffered = true; TabStop = false;
        BackColor = OverlayTheme.Background;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Detected chessboard";
    }
    public void UpdateBoard(BoardFrame? value, bool isRunning)
    {
        bool changed = running != isRunning || frame?.Recognition.Position.Placement != value?.Recognition.Position.Placement
            || frame?.Recognition.WhiteBottom != value?.Recognition.WhiteBottom || frame?.IsReliable != value?.IsReliable;
        if (!isRunning || value is null || !value.IsReliable || frame?.Recognition.Position.Placement != value.Recognition.Position.Placement)
        { changed |= ArrowCount != 0; whiteMove = blackMove = null; }
        frame = value; running = isRunning;
        AccessibleDescription = value is null ? (running ? "No board detected" : "Analysis paused")
            : $"{(value.IsReliable ? "Detected" : "Uncertain")} position: {value.Recognition.Position.Placement}. {(value.Recognition.WhiteBottom ? "White" : "Black")} at bottom.";
        if (changed) InvalidateBoard();
    }
    public void ClearMoves()
    {
        if (whiteMove is null && blackMove is null) return;
        whiteMove = blackMove = null; InvalidateBoard();
    }
    public void SetMoves(BoardFrame forBoard, Analysis? white, Analysis? black)
    {
        if (!running || frame is not { IsReliable: true }
            || frame.Recognition.Position.Placement != forBoard.Recognition.Position.Placement) return;
        var nextWhite = white?.Move is { } wm && frame.Recognition.Position.IsLegal(wm, true) ? white : null;
        var nextBlack = black?.Move is { } bm && frame.Recognition.Position.IsLegal(bm, false) ? black : null;
        if (whiteMove?.Move == nextWhite?.Move && blackMove?.Move == nextBlack?.Move) return;
        whiteMove = nextWhite; blackMove = nextBlack;
        InvalidateBoard();
    }
    private void InvalidateBoard()
    {
        renderedBoard?.Dispose(); renderedBoard = null; Invalidate();
    }
    internal void PrepareForAnimation()
    {
        if (renderedBoard is not null || Width <= 0 || Height <= 0) return;
        var bitmap = new Bitmap(Width, Height);
        bitmap.SetResolution(DeviceDpi, DeviceDpi);
        try { using var graphics = Graphics.FromImage(bitmap); RenderBoard(graphics); }
        catch { bitmap.Dispose(); throw; }
        renderedBoard = bitmap; RenderCount++;
    }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); InvalidateBoard(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); InvalidateBoard(); }
    protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); InvalidateBoard(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { renderedBoard?.Dispose(); renderedBoard = null; }
        base.Dispose(disposing);
    }
    public char PieceAtDisplaySquare(int row, int column)
    {
        if (row is < 0 or > 7 || column is < 0 or > 7) throw new ArgumentOutOfRangeException();
        if (frame is null) return '.';
        int index = row * 8 + column;
        return frame.Recognition.Position.Squares[frame.Recognition.WhiteBottom ? index : 63 - index];
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        PrepareForAnimation();
        if (renderedBoard is not null) e.Graphics.DrawImageUnscaled(renderedBoard, 0, 0);
    }
    private void RenderBoard(Graphics g)
    {
        float s = DeviceDpi / 96f;
        g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        float size = Math.Min(Width - 16 * s, Height - 40 * s);
        if (size <= 0) return;
        float left = (Width - size + 12 * s) / 2, cell = size / 8;
        using var light = new SolidBrush(Color.FromArgb(163, 181, 181));
        using var dark = new SolidBrush(Color.FromArgb(67, 87, 99));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var coordinates = new Font("Segoe UI", 7.5f);
        bool whiteBottom = frame?.Recognition.WhiteBottom ?? true;
        for (int row = 0; row < 8; row++) for (int col = 0; col < 8; col++)
        {
            var square = new RectangleF(left + col * cell, row * cell, cell, cell);
            g.FillRectangle((row + col) % 2 == 0 ? light : dark, square);
            char piece = PieceAtDisplaySquare(row, col);
            if (piece != '.' && PreviewPieces.For(piece) is { } sprite) g.DrawImage(sprite, square);
        }
        if (frame is { IsReliable: true })
        {
            // Reuse the screen-overlay renderer so colors, orientation and
            // promotion labels always describe the same engine results.
            var state = g.Save();
            g.SetClip(new RectangleF(left, 0, size, size));
            ArrowOverlay.DrawMoves(g, Rectangle.Round(new RectangleF(left, 0, size, size)), whiteBottom, whiteMove, blackMove);
            g.Restore(state);
        }
        for (int i = 0; i < 8; i++)
        {
            string rank = (whiteBottom ? 8 - i : i + 1).ToString();
            string file = ((char)('a' + (whiteBottom ? i : 7 - i))).ToString();
            TextRenderer.DrawText(g, rank, coordinates, Rectangle.Round(new RectangleF(0, i * cell, left - 3 * s, cell)), OverlayTheme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, file, coordinates, Rectangle.Round(new RectangleF(left + i * cell, size + 3 * s, cell, 14 * s)), OverlayTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }
        if (frame is null)
        {
            using var veil = new SolidBrush(Color.FromArgb(210, OverlayTheme.Background));
            g.FillRectangle(veil, left, 0, size, size);
            using var emptyFont = new Font("Segoe UI", 9);
            TextRenderer.DrawText(g, running ? "Looking for your board…" : "Turn on to see detected pieces", emptyFont,
                Rectangle.Round(new RectangleF(left + 10 * s, 0, size - 20 * s, size)), OverlayTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
        string caption = frame is null ? (running ? "Keep the full board visible" : "Analysis paused")
            : $"{(frame.IsReliable ? "Detected" : "Uncertain · check pieces")} · {(whiteBottom ? "White" : "Black")} below";
        using var captionFont = new Font("Segoe UI", 8);
        TextRenderer.DrawText(g, caption, captionFont, new Rectangle(0, (int)(size + 22 * s), Width, (int)(18 * s)),
            frame is { IsReliable: false } ? ArrowOverlay.BlackColor : OverlayTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}
