using AC5250.Model;

namespace AC5250.Rendering;

public class TerminalControl : UserControl
{
    private ScreenBuffer? _buffer;
    private Font _terminalFont;
    private readonly Font _statusFont;
    private int _cellWidth;
    private int _cellHeight;
    private int _offsetX;
    private int _offsetY;
    private int _fitRows = -1;
    private int _fitCols = -1;
    private bool _cursorVisible = true;
    private readonly System.Windows.Forms.Timer _cursorTimer;
    private ColorScheme _colors = ColorScheme.Color5250;
    private string _hostInfo = "Disconnected";
    private const int StatusBarHeight = 22;

    public event Action<Keys>? KeyInput;

    public TerminalControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable,
            true);

        BackColor = Color.Black;
        _terminalFont = new Font("Consolas", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
        _statusFont = new Font("Consolas", 9f, FontStyle.Regular);

        _cursorTimer = new System.Windows.Forms.Timer { Interval = 530 };
        _cursorTimer.Tick += (_, _) =>
        {
            _cursorVisible = !_cursorVisible;
            InvalidateCursor();
        };
        _cursorTimer.Start();

        TabStop = true;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ColorScheme Colors
    {
        get => _colors;
        set { _colors = value; Invalidate(); }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string HostInfo
    {
        get => _hostInfo;
        set { _hostInfo = value; Invalidate(); }
    }

    public void AttachBuffer(ScreenBuffer buffer)
    {
        if (_buffer != null)
            _buffer.ScreenChanged -= OnScreenChanged;

        _buffer = buffer;
        _buffer.ScreenChanged += OnScreenChanged;
        RecalculateFont();
        Invalidate();
    }

    public void DetachBuffer()
    {
        if (_buffer != null)
        {
            _buffer.ScreenChanged -= OnScreenChanged;
            _buffer = null;
        }
        Invalidate();
    }

    private void OnScreenChanged()
    {
        if (InvokeRequired)
            BeginInvoke(HandleScreenChanged);
        else
            HandleScreenChanged();
    }

    private void HandleScreenChanged()
    {
        // If the host switched screen size (24x80 <-> 27x132), refit the font/grid.
        if (_buffer != null && (_buffer.Rows != _fitRows || _buffer.Cols != _fitCols))
            RecalculateFont();
        Invalidate();
    }

    private void InvalidateCursor()
    {
        if (_buffer == null || _cellWidth == 0) return;

        int x = _offsetX + _buffer.CursorCol * _cellWidth;
        int y = _offsetY + _buffer.CursorRow * _cellHeight;
        var r = new Rectangle(x, y, _cellWidth, _cellHeight);
        if (InvokeRequired)
            BeginInvoke(() => Invalidate(r));
        else
            Invalidate(r);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RecalculateFont();
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        // Grab focus when shown so the operator can type immediately (no Tab needed).
        if (Visible && IsHandleCreated)
            BeginInvoke(() => { if (Visible && CanFocus) Focus(); });
    }

    private void RecalculateFont()
    {
        if (_buffer == null || Width <= 0 || Height <= 0) return;

        int availableHeight = Height - StatusBarHeight;
        if (availableHeight <= 0) return;

        // Pick the largest Consolas size whose monospace cell fits the whole grid,
        // keeping the character's natural aspect ratio (so 132-col wide screens are
        // not stretched). The grid is then centered in the control.
        Font? best = null;
        int bestW = 1, bestH = 1;
        for (float size = 6f; size <= 48f; size += 0.5f)
        {
            var f = new Font("Consolas", size, FontStyle.Regular, GraphicsUnit.Pixel);
            Size cs = TextRenderer.MeasureText("0", f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            int cw = Math.Max(1, cs.Width), ch = Math.Max(1, cs.Height);
            if (cw * _buffer.Cols <= Width && ch * _buffer.Rows <= availableHeight)
            {
                best?.Dispose();
                best = f; bestW = cw; bestH = ch;
            }
            else
            {
                f.Dispose();
                break; // sizes only grow; once one doesn't fit, stop
            }
        }

        if (best == null)
        {
            best = new Font("Consolas", 6f, FontStyle.Regular, GraphicsUnit.Pixel);
            Size cs = TextRenderer.MeasureText("0", best, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            bestW = Math.Max(1, cs.Width); bestH = Math.Max(1, cs.Height);
        }

        _terminalFont?.Dispose();
        _terminalFont = best;
        _cellWidth = bestW;
        _cellHeight = bestH;
        _offsetX = Math.Max(0, (Width - bestW * _buffer.Cols) / 2);
        _offsetY = Math.Max(0, (availableHeight - bestH * _buffer.Rows) / 2);
        _fitRows = _buffer.Rows;
        _fitCols = _buffer.Cols;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_buffer == null || _cellWidth == 0)
        {
            e.Graphics.Clear(_colors.Background);
            return;
        }

        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        TerminalRenderer.Render(
            e.Graphics, _buffer, _terminalFont,
            _cellWidth, _cellHeight, _offsetX, _offsetY, _colors, _cursorVisible);

        TerminalRenderer.RenderStatusBar(
            e.Graphics, _buffer, _statusFont,
            Height - StatusBarHeight, Width, StatusBarHeight, _colors, _hostInfo);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Tab:
            case Keys.Up:
            case Keys.Down:
            case Keys.Left:
            case Keys.Right:
            case Keys.Escape:
            case Keys.Enter:
                return true;
            default:
                return base.IsInputKey(keyData);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var action = Input.KeyMapper.Map(e.KeyData);
        if (action.Type != Input.KeyActionType.None)
        {
            KeyInput?.Invoke(e.KeyData);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        _cursorVisible = true;
        _cursorTimer.Stop();
        _cursorTimer.Start();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _cursorVisible = true;
        _cursorTimer.Start();
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _cursorTimer.Stop();
        _cursorVisible = false;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        Focus();

        if (_buffer == null || _cellWidth == 0 || _cellHeight == 0) return;

        int col = (e.X - _offsetX) / _cellWidth;
        int row = (e.Y - _offsetY) / _cellHeight;

        if (row >= 0 && row < _buffer.Rows && col >= 0 && col < _buffer.Cols)
        {
            _buffer.MoveCursorTo(row, col);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cursorTimer.Dispose();
            _terminalFont.Dispose();
            _statusFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
