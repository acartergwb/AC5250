using AC5250.Model;

namespace AC5250.Rendering;

public class TerminalControl : UserControl
{
    private ScreenBuffer? _buffer;
    private Font _terminalFont;
    private int _cellWidth;
    private int _cellHeight;
    private bool _cursorVisible = true;
    private readonly System.Windows.Forms.Timer _cursorTimer;
    private ColorScheme _colors = ColorScheme.Classic;
    private string _hostInfo = "Disconnected";
    private int _statusBarHeight = 20;

    public event Action<Keys>? KeyInput;

    public TerminalControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Selectable,
            true);

        BackColor = Color.Black;
        _terminalFont = new Font("Consolas", 14f, FontStyle.Regular);

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
            BeginInvoke(Invalidate);
        else
            Invalidate();
    }

    private void InvalidateCursor()
    {
        if (_buffer == null || _cellWidth == 0) return;

        int x = _buffer.CursorCol * _cellWidth;
        int y = _buffer.CursorRow * _cellHeight;
        if (InvokeRequired)
            BeginInvoke(() => Invalidate(new Rectangle(x, y, _cellWidth, _cellHeight)));
        else
            Invalidate(new Rectangle(x, y, _cellWidth, _cellHeight));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RecalculateFont();
        Invalidate();
    }

    private void RecalculateFont()
    {
        if (_buffer == null || Width == 0 || Height == 0) return;

        int availableHeight = Height - _statusBarHeight;
        if (availableHeight <= 0) return;

        // Calculate cell size from available space
        _cellWidth = Width / _buffer.Cols;
        _cellHeight = availableHeight / _buffer.Rows;

        if (_cellWidth < 1) _cellWidth = 1;
        if (_cellHeight < 1) _cellHeight = 1;

        // Find the largest font that fits in the cell
        float fontSize = Math.Min(_cellWidth * 1.3f, _cellHeight * 0.85f);
        if (fontSize < 6) fontSize = 6;

        _terminalFont?.Dispose();
        _terminalFont = new Font("Consolas", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);

        _statusBarHeight = Math.Max(18, _cellHeight);
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
            _cellWidth, _cellHeight, _colors, _cursorVisible);

        int statusY = _buffer.Rows * _cellHeight;
        TerminalRenderer.RenderStatusBar(
            e.Graphics, _buffer, _terminalFont,
            statusY, Width, _statusBarHeight, _colors, _hostInfo);
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
        // else: let KeyPress fire for printable characters

        // Reset cursor blink on keystroke
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

        int row = e.Y / _cellHeight;
        int col = e.X / _cellWidth;

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
        }
        base.Dispose(disposing);
    }
}
