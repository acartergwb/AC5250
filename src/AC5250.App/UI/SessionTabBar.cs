using AC5250.Session;

namespace AC5250.UI;

/// <summary>
/// Custom owner-drawn tab bar for sessions — replaces the ugly default TabControl.
/// </summary>
public class SessionTabBar : Control
{
    private readonly List<TabItem> _tabs = new();
    private int _selectedIndex = -1;
    private int _hoverIndex = -1;
    private int _hoverCloseIndex = -1;
    private const int TabHeight = 34;
    private const int TabPadding = 16;
    private const int CloseButtonSize = 16;
    private const int MaxTabWidth = 220;
    private const int MinTabWidth = 100;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex != value && value >= -1 && value < _tabs.Count)
            {
                _selectedIndex = value;
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }
        }
    }

    public event EventHandler? SelectedIndexChanged;
    public event EventHandler<int>? TabCloseClicked;

    public SessionTabBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        Height = TabHeight;
        Dock = DockStyle.Top;
        BackColor = DarkTheme.Background;
        Font = DarkTheme.UIFont;
    }

    public int TabCount => _tabs.Count;

    public void AddTab(string title, object? tag = null)
    {
        _tabs.Add(new TabItem { Title = title, Tag = tag });
        SelectedIndex = _tabs.Count - 1;
        Invalidate();
    }

    public void RemoveTabAt(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        _tabs.RemoveAt(index);

        if (_selectedIndex >= _tabs.Count)
            _selectedIndex = _tabs.Count - 1;

        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    public void SetTabTitle(int index, string title)
    {
        if (index < 0 || index >= _tabs.Count) return;
        _tabs[index].Title = title;
        Invalidate();
    }

    public object? GetTabTag(int index)
    {
        if (index < 0 || index >= _tabs.Count) return null;
        return _tabs[index].Tag;
    }

    public int FindTabByTag(object tag)
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].Tag == tag) return i;
        }
        return -1;
    }

    private int GetTabWidth()
    {
        if (_tabs.Count == 0) return MinTabWidth;
        int available = Width - 8;
        int w = available / _tabs.Count;
        return Math.Clamp(w, MinTabWidth, MaxTabWidth);
    }

    private Rectangle GetTabRect(int index)
    {
        int tabWidth = GetTabWidth();
        return new Rectangle(4 + index * tabWidth, 0, tabWidth, TabHeight);
    }

    private Rectangle GetCloseRect(int index)
    {
        var tabRect = GetTabRect(index);
        int x = tabRect.Right - CloseButtonSize - 8;
        int y = (TabHeight - CloseButtonSize) / 2;
        return new Rectangle(x, y, CloseButtonSize, CloseButtonSize);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Background
        using var bgBrush = new SolidBrush(DarkTheme.Background);
        g.FillRectangle(bgBrush, ClientRectangle);

        // Bottom border
        using var borderPen = new Pen(DarkTheme.BorderSubtle);
        g.DrawLine(borderPen, 0, Height - 1, Width, Height - 1);

        int tabWidth = GetTabWidth();

        for (int i = 0; i < _tabs.Count; i++)
        {
            var rect = GetTabRect(i);
            bool isSelected = i == _selectedIndex;
            bool isHover = i == _hoverIndex;

            // Tab background
            if (isSelected)
            {
                using var brush = new SolidBrush(DarkTheme.SurfaceLight);
                var tabArea = new Rectangle(rect.X + 1, rect.Y + 2, rect.Width - 2, rect.Height - 2);
                FillRoundedRect(g, brush, tabArea, 6, roundBottom: false);

                // Active indicator line at top
                using var accentPen = new Pen(DarkTheme.Accent, 2);
                g.DrawLine(accentPen, rect.X + 6, rect.Y + 2, rect.Right - 6, rect.Y + 2);
            }
            else if (isHover)
            {
                using var brush = new SolidBrush(DarkTheme.Surface);
                var tabArea = new Rectangle(rect.X + 1, rect.Y + 2, rect.Width - 2, rect.Height - 2);
                FillRoundedRect(g, brush, tabArea, 6, roundBottom: false);
            }

            // Tab title
            var textColor = isSelected ? DarkTheme.TextPrimary : DarkTheme.TextSecondary;
            var titleRect = new Rectangle(rect.X + TabPadding, rect.Y, rect.Width - TabPadding * 2 - CloseButtonSize, rect.Height);
            TextRenderer.DrawText(g, _tabs[i].Title, Font, titleRect, textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // Close button
            if (isSelected || isHover)
            {
                var closeRect = GetCloseRect(i);
                bool closeHover = i == _hoverCloseIndex;

                if (closeHover)
                {
                    using var closeBgBrush = new SolidBrush(Color.FromArgb(60, DarkTheme.Danger));
                    FillRoundedRect(g, closeBgBrush, closeRect, 4);
                }

                var closeColor = closeHover ? DarkTheme.Danger : DarkTheme.TextMuted;
                using var closePen = new Pen(closeColor, 1.5f);
                int m = 4;
                g.DrawLine(closePen, closeRect.X + m, closeRect.Y + m, closeRect.Right - m, closeRect.Bottom - m);
                g.DrawLine(closePen, closeRect.Right - m, closeRect.Y + m, closeRect.X + m, closeRect.Bottom - m);
            }
        }
    }

    private static void FillRoundedRect(Graphics g, Brush brush, Rectangle rect, int radius, bool roundBottom = true)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);

        if (roundBottom)
        {
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        }
        else
        {
            path.AddLine(rect.Right, rect.Bottom, rect.X, rect.Bottom);
        }

        path.CloseFigure();
        g.FillPath(brush, path);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int newHover = -1;
        int newCloseHover = -1;

        for (int i = 0; i < _tabs.Count; i++)
        {
            if (GetTabRect(i).Contains(e.Location))
            {
                newHover = i;
                if (GetCloseRect(i).Contains(e.Location))
                    newCloseHover = i;
                break;
            }
        }

        if (newHover != _hoverIndex || newCloseHover != _hoverCloseIndex)
        {
            _hoverIndex = newHover;
            _hoverCloseIndex = newCloseHover;
            Cursor = newCloseHover >= 0 ? Cursors.Hand : (newHover >= 0 ? Cursors.Hand : Cursors.Default);
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        _hoverCloseIndex = -1;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);

        for (int i = 0; i < _tabs.Count; i++)
        {
            if (GetCloseRect(i).Contains(e.Location))
            {
                TabCloseClicked?.Invoke(this, i);
                return;
            }
            if (GetTabRect(i).Contains(e.Location))
            {
                SelectedIndex = i;
                return;
            }
        }
    }

    private class TabItem
    {
        public string Title { get; set; } = "";
        public object? Tag { get; set; }
    }
}
