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
    private bool _hoverNewTab;

    // Drag-to-reorder state. A left press on a tab is a *candidate* drag; it becomes a real
    // reorder once the pointer moves past DragThreshold. + and close are tracked on press so
    // they only fire on a clean click (not at the end of a drag).
    private int _pressIndex = -1;
    private int _pressX;
    private bool _dragging;
    private bool _pressedNewTab;
    private int _pressedCloseIndex = -1;

    // Chrome-style animated drag: the grabbed tab floats under the cursor while the others
    // ease toward their slots. _grabDX is where inside the tab it was grabbed; _dragPointerX
    // tracks the cursor; _detaching lifts the tab when a release would tear it into a window.
    private int _grabDX;
    private int _dragPointerX;
    private bool _detaching;
    private TabDragGhost? _ghost;               // floating preview shown while tearing a tab out
    private readonly System.Windows.Forms.Timer _animTimer;
    private const float EaseFactor = 0.32f;    // per-tick easing toward the target slot

    private static Point GhostPosition()        // just below-right of the pointer
        => new(Cursor.Position.X - 90, Cursor.Position.Y + 16);

    private void ShowGhost(string title)
    {
        _ghost ??= new TabDragGhost();
        _ghost.Present(title, GhostPosition());
    }

    private void HideGhost() => _ghost?.Hide();

    private const int TabHeight = 34;
    private const int TabPadding = 16;
    private const int CloseButtonSize = 16;
    private const int MaxTabWidth = 220;
    private const int MinTabWidth = 100;
    private const int NewTabButtonSize = 24;   // the "+" button glyph box
    private const int DragThreshold = 5;        // px a press must move before it's a reorder drag

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

    /// <summary>Raised when the "+" new-tab button is clicked.</summary>
    public event EventHandler? NewTabClicked;

    /// <summary>Raised when a tab is dragged out of the bar (to tear it into a new window):
    /// (tab index, screen location of the drop).</summary>
    public event Action<int, Point>? TabDetached;

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

        _animTimer = new System.Windows.Forms.Timer { Interval = 15 };   // ~66 fps
        _animTimer.Tick += OnAnimTick;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _animTimer.Dispose(); _ghost?.Dispose(); }
        base.Dispose(disposing);
    }

    public int TabCount => _tabs.Count;

    public void AddTab(string title, object? tag = null)
    {
        _tabs.Add(new TabItem { Title = title, Tag = tag });
        SelectedIndex = _tabs.Count - 1;
        StartAnim();     // existing tabs ease to their new (narrower) slots; the new one snaps in
        Invalidate();
    }

    public void RemoveTabAt(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        _tabs.RemoveAt(index);

        if (_selectedIndex >= _tabs.Count)
            _selectedIndex = _tabs.Count - 1;

        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        StartAnim();     // remaining tabs slide to fill the gap
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

    // Logical (resting) slot of a tab — used for hit-testing (hover/close/click). Painting
    // uses PaintRect, which follows the eased AnimX so tabs slide instead of snapping.
    private Rectangle GetTabRect(int index) => new(SlotX(index), 0, GetTabWidth(), TabHeight);

    private int SlotX(int index) => 4 + index * GetTabWidth();

    private Rectangle PaintRect(int index)
    {
        EnsureAnimInit();
        return new Rectangle((int)MathF.Round(_tabs[index].AnimX), 0, GetTabWidth(), TabHeight);
    }

    // Seed AnimX for any tab not yet positioned (new tabs appear at their slot).
    private void EnsureAnimInit()
    {
        for (int i = 0; i < _tabs.Count; i++)
            if (float.IsNaN(_tabs[i].AnimX)) _tabs[i].AnimX = SlotX(i);
    }

    private void StartAnim() { if (!_animTimer.Enabled) _animTimer.Start(); }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        EnsureAnimInit();
        int tabWidth = GetTabWidth();
        bool moving = false;

        for (int i = 0; i < _tabs.Count; i++)
        {
            if (_dragging && i == _pressIndex)
            {
                // While tearing out, the tab lives only in the floating ghost — don't position
                // or paint it in the strip. While reordering in-strip, it tracks the cursor.
                if (_detaching) { moving = true; continue; }
                _tabs[i].AnimX = Math.Clamp(_dragPointerX - _grabDX, 4f, Math.Max(4, Width - tabWidth - 4));
                moving = true;
                continue;
            }
            float target = SlotX(VisualSlot(i));
            float cur = _tabs[i].AnimX;
            if (Math.Abs(target - cur) < 0.5f) _tabs[i].AnimX = target;
            else { _tabs[i].AnimX = cur + (target - cur) * EaseFactor; moving = true; }
        }

        Invalidate();
        if (!_dragging && !moving) _animTimer.Stop();
    }

    // While a tab is torn out, the tabs after it collapse left as if it were already gone.
    private int VisualSlot(int i) => (_detaching && _dragging && i > _pressIndex) ? i - 1 : i;

    private Rectangle GetCloseRect(int index)
    {
        var tabRect = GetTabRect(index);
        int x = tabRect.Right - CloseButtonSize - 8;
        int y = (TabHeight - CloseButtonSize) / 2;
        return new Rectangle(x, y, CloseButtonSize, CloseButtonSize);
    }

    /// <summary>The "+" new-tab button, sitting just past the last tab (clamped on-screen).</summary>
    private Rectangle GetNewTabRect()
    {
        int x = _tabs.Count == 0 ? 4 : GetTabRect(_tabs.Count - 1).Right + 6;
        x = Math.Min(x, Width - NewTabButtonSize - 4);
        int y = (TabHeight - NewTabButtonSize) / 2;
        return new Rectangle(x, y, NewTabButtonSize, NewTabButtonSize);
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

        EnsureAnimInit();

        // Paint non-dragged tabs at their eased positions, then the grabbed tab on top so it
        // floats over its neighbours while they slide out of the way. While tearing out, the
        // grabbed tab isn't drawn in the strip at all — the floating ghost represents it.
        for (int i = 0; i < _tabs.Count; i++)
            if (!(_dragging && i == _pressIndex))
                DrawTab(g, i, PaintRect(i));

        if (_dragging && !_detaching && _pressIndex >= 0 && _pressIndex < _tabs.Count)
            DrawTab(g, _pressIndex, PaintRect(_pressIndex));

        // "+" new-tab button
        var newRect = GetNewTabRect();
        if (_hoverNewTab)
        {
            using var hoverBrush = new SolidBrush(DarkTheme.Surface);
            FillRoundedRect(g, hoverBrush, newRect, 4);
        }
        var plusColor = _hoverNewTab ? DarkTheme.TextPrimary : DarkTheme.TextMuted;
        using var plusPen = new Pen(plusColor, 1.5f);
        int px = newRect.X + newRect.Width / 2;
        int py = newRect.Y + newRect.Height / 2;
        const int arm = 5;
        g.DrawLine(plusPen, px - arm, py, px + arm, py);
        g.DrawLine(plusPen, px, py - arm, px, py + arm);
    }

    private void DrawTab(Graphics g, int i, Rectangle rect)
    {
        bool isSelected = i == _selectedIndex;
        bool isHover = i == _hoverIndex;
        bool isDragged = _dragging && i == _pressIndex;

        if (isSelected)
        {
            using var brush = new SolidBrush(DarkTheme.SurfaceLight);
            FillRoundedRect(g, brush, new Rectangle(rect.X + 1, rect.Y + 2, rect.Width - 2, rect.Height - 2), 6, roundBottom: false);
            using var accentPen = new Pen(DarkTheme.Accent, 2);
            g.DrawLine(accentPen, rect.X + 6, rect.Y + 2, rect.Right - 6, rect.Y + 2);
        }
        else if (isHover)
        {
            using var brush = new SolidBrush(DarkTheme.Surface);
            FillRoundedRect(g, brush, new Rectangle(rect.X + 1, rect.Y + 2, rect.Width - 2, rect.Height - 2), 6, roundBottom: false);
        }

        var textColor = isSelected ? DarkTheme.TextPrimary : DarkTheme.TextSecondary;
        var titleRect = new Rectangle(rect.X + TabPadding, rect.Y, rect.Width - TabPadding * 2 - CloseButtonSize, rect.Height);
        TextRenderer.DrawText(g, _tabs[i].Title, Font, titleRect, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // Close button — hidden on the tab that's currently being dragged.
        if ((isSelected || isHover) && !isDragged)
        {
            int cx = rect.Right - CloseButtonSize - 8;
            int cy = rect.Y + (rect.Height - CloseButtonSize) / 2;
            var closeRect = new Rectangle(cx, cy, CloseButtonSize, CloseButtonSize);
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

    /// <summary>Which tab index the x-coordinate falls over (tabs are equal width), clamped.</summary>
    private int TabIndexAtX(int x)
    {
        if (_tabs.Count == 0) return -1;
        int idx = (x - 4) / GetTabWidth();
        return Math.Clamp(idx, 0, _tabs.Count - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        ResetPress();
        if (e.Button != MouseButtons.Left) return;

        // "+" takes priority — it can overlap the trailing edge of the last tab.
        if (GetNewTabRect().Contains(e.Location)) { _pressedNewTab = true; return; }

        for (int i = 0; i < _tabs.Count; i++)
        {
            if ((i == _selectedIndex || i == _hoverIndex) && GetCloseRect(i).Contains(e.Location))
            {
                _pressedCloseIndex = i;
                return;
            }
            if (GetTabRect(i).Contains(e.Location))
            {
                SelectedIndex = i;          // select on press so click-to-select still works
                _pressIndex = i;            // and mark it as a candidate for a reorder drag
                _pressX = e.X;
                _grabDX = e.X - SlotX(i);   // where inside the tab it was grabbed
                return;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // A held press on a tab that moves far enough becomes a live reorder drag.
        if (_pressIndex >= 0 && (e.Button & MouseButtons.Left) != 0)
        {
            if (!_dragging && Math.Abs(e.X - _pressX) > DragThreshold) _dragging = true;
            if (_dragging)
            {
                _dragPointerX = e.X;

                // Tear-out preview: lift the tab and show a floating ghost that follows the
                // cursor once the drag leaves the strip (off the window or dragged below it) —
                // the same test the drop uses in OnMouseUp.
                var form = FindForm();
                bool off = form != null && !form.Bounds.Contains(PointToScreen(e.Location));
                bool wasDetaching = _detaching;
                _detaching = _tabs.Count > 1 && (off || e.Y > Height + 40);
                if (_detaching)
                {
                    if (!wasDetaching) ShowGhost(_tabs[_pressIndex].Title);
                    _ghost?.MoveTo(GhostPosition());
                }
                else if (wasDetaching) HideGhost();

                // Reorder when the floating tab's CENTRE crosses into another slot (so grabbing
                // near a tab edge doesn't feel offset).
                int tabWidth = GetTabWidth();
                float floatLeft = Math.Clamp(e.X - _grabDX, 4f, Math.Max(4, Width - tabWidth - 4));
                int target = TabIndexAtX((int)(floatLeft + tabWidth / 2f));
                if (!_detaching && target >= 0 && target != _pressIndex)
                {
                    var item = _tabs[_pressIndex];
                    _tabs.RemoveAt(_pressIndex);
                    _tabs.Insert(target, item);
                    _selectedIndex = target;   // dragged tab stays selected (set field: no re-activate)
                    _pressIndex = target;
                }

                Cursor = Cursors.SizeAll;
                StartAnim();                   // the timer repaints; drives the slide/float
                return;                        // suppress hover changes while dragging
            }
        }

        int newHover = -1;
        int newCloseHover = -1;
        bool newTabHover = GetNewTabRect().Contains(e.Location);

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

        if (newHover != _hoverIndex || newCloseHover != _hoverCloseIndex || newTabHover != _hoverNewTab)
        {
            _hoverIndex = newHover;
            _hoverCloseIndex = newCloseHover;
            _hoverNewTab = newTabHover;
            Cursor = (newCloseHover >= 0 || newHover >= 0 || newTabHover) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) { ResetPress(); return; }

        // Drag ended away from the strip (off the window, or dragged well below it) with more
        // than one tab open → tear this tab out into a new window at the drop point.
        if (_dragging && _pressIndex >= 0 && _tabs.Count > 1)
        {
            var screenPt = PointToScreen(e.Location);
            var form = FindForm();
            bool droppedOffWindow = form != null && !form.Bounds.Contains(screenPt);
            bool draggedBelowStrip = e.Y > Height + 40;
            if (droppedOffWindow || draggedBelowStrip)
            {
                int idx = _pressIndex;
                ResetPress();
                Cursor = Cursors.Default;
                TabDetached?.Invoke(idx, screenPt);
                return;
            }
        }

        // Fire + / close only on a clean click (no drag happened). Selection already occurred
        // on press. A reorder drag is finalized live, so nothing else to do here.
        if (!_dragging)
        {
            if (_pressedNewTab && GetNewTabRect().Contains(e.Location))
                NewTabClicked?.Invoke(this, EventArgs.Empty);
            else if (_pressedCloseIndex >= 0 && GetCloseRect(_pressedCloseIndex).Contains(e.Location))
                TabCloseClicked?.Invoke(this, _pressedCloseIndex);
        }

        ResetPress();
        Cursor = Cursors.Default;
        StartAnim();     // ease the dropped tab back into its slot (tick stops once settled)
    }

    private void ResetPress()
    {
        _pressIndex = -1;
        _dragging = false;
        _detaching = false;
        _pressedNewTab = false;
        _pressedCloseIndex = -1;
        HideGhost();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        // Don't clear an active drag here — the control keeps mouse capture during a drag, so
        // the reorder continues even if the pointer briefly leaves the bar.
        _hoverIndex = -1;
        _hoverCloseIndex = -1;
        _hoverNewTab = false;
        if (_pressIndex < 0) Cursor = Cursors.Default;
        Invalidate();
    }

    private class TabItem
    {
        public string Title { get; set; } = "";
        public object? Tag { get; set; }
        public float AnimX = float.NaN;   // animated left edge; NaN = not yet positioned
    }
}

/// <summary>A small floating, non-activating, top-most preview shown while a tab is being torn
/// out of its window, so the detach is unmistakable. It never takes focus or mouse capture, so
/// the tab bar keeps receiving the drag.</summary>
internal sealed class TabDragGhost : Form
{
    private string _title = "";

    public TabDragGhost()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(190, 38);
        DoubleBuffered = true;
        BackColor = DarkTheme.SurfaceLight;
        Enabled = false;   // never interactive
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TOPMOST = 0x00000008;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    public void Present(string title, Point screenLocation)
    {
        _title = title;
        Location = screenLocation;
        Invalidate();
        if (!Visible) Show();
    }

    public void MoveTo(Point screenLocation) => Location = screenLocation;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = 12;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        using var bg = new SolidBrush(DarkTheme.Surface);
        using var border = new Pen(DarkTheme.Accent, 2);
        g.FillPath(bg, path);
        g.DrawPath(border, path);
        TextRenderer.DrawText(g, _title, DarkTheme.UIFont, r, DarkTheme.TextPrimary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
