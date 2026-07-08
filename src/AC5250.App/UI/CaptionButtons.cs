namespace AC5250.UI;

/// <summary>Owner-drawn minimize / maximize-restore / close buttons for the custom title bar,
/// styled to match the dark theme (close turns red on hover, like Windows).</summary>
internal sealed class CaptionButtons : Control
{
    private readonly Form _form;
    private int _hover = -1;                 // 0 = min, 1 = max/restore, 2 = close
    private const int BtnW = 46;

    public CaptionButtons(Form form)
    {
        _form = form;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Width = BtnW * 3;
        BackColor = DarkTheme.Background;
    }

    private Rectangle BtnRect(int i) => new(i * BtnW, 0, BtnW, Height);
    private int ButtonAt(int x) => (x >= 0 && x < BtnW * 3) ? x / BtnW : -1;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = ButtonAt(e.X);
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1) { _hover = -1; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        switch (ButtonAt(e.X))
        {
            case 0: _form.WindowState = FormWindowState.Minimized; break;
            case 1: ToggleMaximize(); break;
            case 2: _form.Close(); break;
        }
    }

    public void ToggleMaximize() =>
        _form.WindowState = _form.WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal : FormWindowState.Maximized;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(DarkTheme.Background)) g.FillRectangle(bg, ClientRectangle);

        for (int i = 0; i < 3; i++)
        {
            var r = BtnRect(i);
            bool hot = _hover == i;
            if (hot)
            {
                using var hb = new SolidBrush(i == 2 ? Color.FromArgb(200, 60, 54) : DarkTheme.Surface);
                g.FillRectangle(hb, r);
            }

            var glyph = (hot && i == 2) ? Color.White : DarkTheme.TextSecondary;
            using var pen = new Pen(glyph, 1.4f);
            int cx = r.X + r.Width / 2, cy = r.Height / 2, s = 5;

            if (i == 0)                                   // minimize
                g.DrawLine(pen, cx - s, cy, cx + s, cy);
            else if (i == 1)                              // maximize / restore
            {
                if (_form.WindowState == FormWindowState.Maximized)
                {
                    g.DrawRectangle(pen, cx - s + 2, cy - s, s * 2 - 2, s * 2 - 2);   // front square
                    g.DrawLine(pen, cx - s, cy - s + 2, cx - s, cy + s);              // back square hint
                    g.DrawLine(pen, cx - s, cy - s + 2, cx + s - 2, cy - s + 2);
                }
                else g.DrawRectangle(pen, cx - s, cy - s, s * 2, s * 2);
            }
            else                                          // close
            {
                g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
                g.DrawLine(pen, cx + s, cy - s, cx - s, cy + s);
            }
        }
    }
}
