using System.Drawing.Drawing2D;

namespace FishEyes;

public sealed class ArrowOverlay : Form
{
    public static readonly Color WhiteColor = Color.FromArgb(65, 181, 255);
    public static readonly Color BlackColor = Color.FromArgb(255, 170, 57);
    private BoardFrame? frame;
    private Analysis? white, black;
    public bool CaptureExcluded { get; private set; }
    public int ArrowCount => (white?.Move is null ? 0 : 1) + (black?.Move is null ? 0 : 1);
    public ArrowOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; TopMost = true;
        BackColor = TransparencyKey = Color.Fuchsia;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        DoubleBuffered = true;
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x08000000 | 0x80; return cp; }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureExcluded = Native.SetWindowDisplayAffinity(Handle, 0x11);
    }
    public void SetMoves(BoardFrame board, Analysis? forWhite, Analysis? forBlack)
    {
        frame = board; white = forWhite; black = forBlack;
        Bounds = SystemInformation.VirtualScreen;
        if (!Visible) Show();
        Invalidate();
    }
    public void Clear() { frame = null; white = black = null; Hide(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (frame is null) return;
        var box = frame.Bounds; box.Offset(-Left, -Top);
        DrawMoves(e.Graphics, box, frame.Recognition.WhiteBottom, white, black);
    }
    public static PointF SquareCenter(string square, Rectangle bounds, bool whiteBottom)
    {
        ChessPosition.TrySquare(square, out int index);
        if (!whiteBottom) index = 63 - index;
        return new(bounds.Left + (index % 8 + .5f) * bounds.Width / 8, bounds.Top + (index / 8 + .5f) * bounds.Height / 8);
    }
    public static void DrawMoves(Graphics g, Rectangle box, bool whiteBottom, Analysis? white, Analysis? black)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var (analysis, color) in new[] { (white, WhiteColor), (black, BlackColor) })
        {
            if (analysis?.Move is not { } move) continue;
            var start = SquareCenter(move[..2], box, whiteBottom);
            var end = SquareCenter(move.Substring(2, 2), box, whiteBottom);
            float cell = box.Width / 8f, shaft = Math.Max(4, cell * .095f);
            float dx = end.X - start.X, dy = end.Y - start.Y, length = MathF.Sqrt(dx * dx + dy * dy);
            float ux = dx / length, uy = dy / length, head = cell * .32f;
            var neck = new PointF(end.X - ux * head, end.Y - uy * head);
            using var shadow = new Pen(Color.FromArgb(40, 44, 55), shaft + 3) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var pen = new Pen(color, shaft) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(shadow, start, neck); g.DrawLine(pen, start, neck);
            PointF[] triangle = [end, new(neck.X - uy * head * .55f, neck.Y + ux * head * .55f), new(neck.X + uy * head * .55f, neck.Y - ux * head * .55f)];
            using var brush = new SolidBrush(color);
            g.FillPolygon(brush, triangle); g.DrawPolygon(Pens.Black, triangle);
            g.FillEllipse(brush, start.X - shaft, start.Y - shaft, shaft * 2, shaft * 2);
            if (move.Length == 5)
            {
                using var font = new Font("Segoe UI", Math.Max(10, cell * .17f), FontStyle.Bold, GraphicsUnit.Pixel);
                g.DrawString("=" + char.ToUpperInvariant(move[4]), font, brush, end.X + shaft, end.Y + shaft);
            }
        }
    }
}
