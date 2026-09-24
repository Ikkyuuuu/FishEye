using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace FishEyes;

internal static class OverlayTheme
{
    public static readonly Color Background = Color.FromArgb(20, 24, 30);
    public static readonly Color Surface = Color.FromArgb(30, 35, 43);
    public static readonly Color Border = Color.FromArgb(47, 54, 64);
    public static readonly Color Text = Color.FromArgb(237, 241, 246);
    public static readonly Color Muted = Color.FromArgb(148, 159, 174);
    public static readonly Color Accent = Color.FromArgb(157, 231, 202);

    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class OverlayButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsToggle { get; init; }
    private bool active, hover;
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 16 };
    private readonly Stopwatch animationClock = new();
    private float progress, animationStart;
    private const double DurationMs = 190;
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);
    private static bool AnimationsEnabled => !SystemParametersInfo(0x1042, 0, out int enabled, 0) || enabled != 0;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => active;
        set
        {
            if (active == value) return;
            AdvanceAnimation();
            active = value;
            AccessibleName = value ? "Turn off analysis" : "Turn on analysis";
            animationStart = progress;
            if (IsHandleCreated && Visible && AnimationsEnabled)
            { animationClock.Restart(); animation.Start(); }
            else { animation.Stop(); animationClock.Reset(); progress = value ? 1 : 0; }
            Invalidate();
        }
    }
    public OverlayButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        ForeColor = OverlayTheme.Muted;
        BackColor = OverlayTheme.Background;
        UseVisualStyleBackColor = false;
        animation.Tick += (_, _) => { AdvanceAnimation(); Invalidate(); };
    }
    private void AdvanceAnimation()
    {
        if (!animationClock.IsRunning) return;
        float t = (float)Math.Clamp(animationClock.Elapsed.TotalMilliseconds / DurationMs, 0, 1);
        float eased = 1 - MathF.Pow(1 - t, 3);
        progress = animationStart + ((active ? 1 : 0) - animationStart) * eased;
        if (t >= 1) { animation.Stop(); animationClock.Reset(); }
    }
    private static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * amount), (int)(from.G + (to.G - from.G) * amount), (int)(from.B + (to.B - from.B) * amount));
    protected override void Dispose(bool disposing)
    {
        if (disposing) animation.Dispose();
        base.Dispose(disposing);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        float s = DeviceDpi / 96f;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var rect = new RectangleF(1, 1, Width - 2, Height - 2);
        using var shape = OverlayTheme.Rounded(rect, IsToggle ? (Height - 2) / 2 : 8 * s);
        Color fill = IsToggle ? Blend(Color.FromArgb(56, 64, 76), OverlayTheme.Accent, progress)
            : hover ? Color.FromArgb(48, 56, 68) : BackColor;
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, shape);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(OverlayTheme.Accent, s);
            g.DrawPath(focus, shape);
        }
        if (IsToggle)
        {
            float diameter = Height - 10 * s;
            float x = 5 * s + (Width - diameter - 10 * s) * progress;
            // Crossfade the labels behind the sliding thumb so rapid reversals
            // continue from the currently displayed position without a jump.
            using var font = new Font("Segoe UI", 8, FontStyle.Bold);
            var onBounds = new Rectangle(3, 0, (int)(Width - diameter - 8 * s), Height);
            var offBounds = new Rectangle((int)(diameter + 7 * s), 0, (int)(Width - diameter - 10 * s), Height);
            TextRenderer.DrawText(g, "On", font, onBounds, Blend(fill, OverlayTheme.Background, progress),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "Off", font, offBounds, Blend(fill, OverlayTheme.Text, 1 - progress),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using var knob = new SolidBrush(Blend(OverlayTheme.Text, OverlayTheme.Background, progress));
            g.FillEllipse(knob, x, 5 * s, diameter, diameter);
        }
        else
        {
            using var pen = new Pen(Enabled ? (hover ? OverlayTheme.Text : ForeColor) : OverlayTheme.Border, 1.5f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float cx = Width / 2f, cy = Height / 2f, r = (Text == "close" ? 4 : 4.5f) * s;
            if (Text == "close")
            {
                g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
                g.DrawLine(pen, cx - r, cy + r, cx + r, cy - r);
            }
            else
            {
                g.DrawLine(pen, cx - r, cy, cx + r, cy);
                if (Text == "+") g.DrawLine(pen, cx, cy - r, cx, cy + r);
            }
        }
    }
}

internal sealed class DepthStepper : UserControl
{
    private readonly OverlayButton minus = new() { Text = "−", AccessibleName = "Decrease search depth", TabIndex = 1 };
    private readonly OverlayButton plus = new() { Text = "+", AccessibleName = "Increase search depth", TabIndex = 2 };
    private readonly TextBox input = new()
    {
        BorderStyle = BorderStyle.None, TextAlign = HorizontalAlignment.Center, MaxLength = 2,
        BackColor = OverlayTheme.Surface, ForeColor = OverlayTheme.Text,
        Font = new Font("Segoe UI", 10, FontStyle.Bold), AccessibleName = "Search depth, 1 to 15", TabIndex = 0
    };
    private int value = 12;
    public event EventHandler? ValueChanged;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => value;
        set
        {
            int next = Math.Clamp(value, 1, 15);
            bool changed = this.value != next;
            this.value = next; input.Text = next.ToString();
            minus.Enabled = next > 1; plus.Enabled = next < 15;
            if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public DepthStepper()
    {
        DoubleBuffered = true;
        BackColor = OverlayTheme.Background;
        minus.BackColor = plus.BackColor = OverlayTheme.Surface;
        Controls.AddRange([minus, input, plus]);
        input.Text = value.ToString();
        minus.Click += (_, _) => Value--;
        plus.Click += (_, _) => Value++;
        input.Enter += (_, _) => input.SelectAll();
        input.Leave += (_, _) => Commit();
        input.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Commit(); input.SelectAll(); e.SuppressKeyPress = true; }
            else if (e.KeyCode is Keys.Up or Keys.Down)
            { Value += e.KeyCode == Keys.Up ? 1 : -1; e.SuppressKeyPress = true; }
        };
    }
    private void Commit() => Value = int.TryParse(input.Text, out int number) ? number : Value;
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (input is null) return;
        int button = (int)(30 * DeviceDpi / 96f);
        minus.SetBounds(2, 2, button, Height - 4);
        plus.SetBounds(Width - button - 2, 2, button, Height - 4);
        input.SetBounds(button + 3, (Height - input.PreferredHeight) / 2, Width - button * 2 - 6, input.PreferredHeight);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = OverlayTheme.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 9 * DeviceDpi / 96f);
        using var fill = new SolidBrush(OverlayTheme.Surface);
        using var line = new Pen(OverlayTheme.Border);
        e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(line, shape);
    }
}

internal sealed class MoveCard : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Caption { get; init; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Accent { get; init; }
    public MoveCard()
    {
        DoubleBuffered = true;
        BackColor = OverlayTheme.Background;
        AccessibleRole = AccessibleRole.StaticText;
        TabStop = false;
    }
    protected override void OnTextChanged(EventArgs e)
    { base.OnTextChanged(e); AccessibleName = $"{Caption}: {Text}"; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = DeviceDpi / 96f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = OverlayTheme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 10 * s);
        using var background = new SolidBrush(OverlayTheme.Surface);
        using var accent = new SolidBrush(Accent);
        g.FillPath(background, shape);
        g.FillEllipse(accent, 12 * s, 13 * s, 5 * s, 5 * s);
        using var caption = new Font("Segoe UI", 8.5f);
        using var move = new Font("Segoe UI", Text.Contains('→') ? 12 : 10, Text.Contains('→') ? FontStyle.Bold : FontStyle.Regular);
        TextRenderer.DrawText(g, Caption, caption, new Rectangle((int)(23 * s), (int)(7 * s), Width - (int)(32 * s), (int)(18 * s)), OverlayTheme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, Text, move, new Rectangle((int)(12 * s), (int)(29 * s), Width - (int)(24 * s), (int)(25 * s)),
            Text.Contains('→') ? OverlayTheme.Text : OverlayTheme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }
}
