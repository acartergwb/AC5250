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

    // Mouse text selection (linear / stream, like a text editor). Coordinates are cell
    // (row, col); the caret cell is inclusive. _hasSelection is set only once the drag
    // reaches a different cell than the anchor, so a plain click stays a cursor move.
    private bool _selecting;
    private bool _hasSelection;
    private int _anchorRow, _anchorCol;
    private int _caretRow, _caretCol;
    private static readonly Color SelectionFill = Color.FromArgb(90, 120, 170, 255);

    public event Action<Keys>? KeyInput;

    /// <summary>Raised on Ctrl+V / context-menu Paste with the clipboard text; the host
    /// window types it into the active field through the session's normal input path.</summary>
    public event Action<string>? PasteRequested;

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
        BuildContextMenu();
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        var copy = new ToolStripMenuItem("Copy\tCtrl+C", null, (_, _) => CopySelection());
        var paste = new ToolStripMenuItem("Paste\tCtrl+V", null, (_, _) => PasteFromClipboard());
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        // Only offer Copy when something is selected, and Paste when the clipboard has text.
        menu.Opening += (_, _) =>
        {
            copy.Enabled = _hasSelection;
            bool hasText; try { hasText = Clipboard.ContainsText(); } catch { hasText = false; }
            paste.Enabled = hasText;
        };
        ContextMenuStrip = menu;
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
        // The screen content changed under any live selection, so its coordinates are now
        // stale — drop it (also covers the redraw after the operator types).
        _selecting = false;
        _hasSelection = false;

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

        if (_hasSelection)
            DrawSelection(e.Graphics);
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
        // Clipboard shortcuts are handled here, ahead of the 5250 key mapper, so Ctrl+C/V
        // never reach the host as input. (KeyMapper leaves both unmapped today, but making
        // this explicit keeps it that way even if a future mapping is added.)
        if (e.Control && !e.Alt && !e.Shift && (e.KeyCode == Keys.C || e.KeyCode == Keys.V))
        {
            if (e.KeyCode == Keys.C) CopySelection();
            else PasteFromClipboard();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

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

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;   // right-click -> context menu
        if (_buffer == null || _cellWidth == 0 || _cellHeight == 0) return;

        // Begin a potential selection at the pressed cell. A plain click (no drag to another
        // cell) falls through to a cursor move on mouse-up; a drag builds a selection.
        (_anchorRow, _anchorCol) = CellFromPoint(e.X, e.Y);
        _caretRow = _anchorRow; _caretCol = _anchorCol;
        _selecting = true;
        if (_hasSelection) { _hasSelection = false; Invalidate(); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selecting || _buffer == null) return;

        var (r, c) = CellFromPoint(e.X, e.Y);
        if (r == _caretRow && c == _caretCol) return; // still in the same cell
        _caretRow = r; _caretCol = c;
        _hasSelection = r != _anchorRow || c != _anchorCol;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_selecting) return;
        _selecting = false;

        // No drag off the anchor cell -> treat it as a click and position the cursor.
        if (!_hasSelection && _buffer != null)
            _buffer.MoveCursorTo(_anchorRow, _anchorCol);
    }

    private (int row, int col) CellFromPoint(int x, int y)
    {
        if (_buffer == null || _cellWidth == 0 || _cellHeight == 0) return (0, 0);
        int col = Math.Clamp((x - _offsetX) / _cellWidth, 0, _buffer.Cols - 1);
        int row = Math.Clamp((y - _offsetY) / _cellHeight, 0, _buffer.Rows - 1);
        return (row, col);
    }

    // Selection is stored anchor->caret in whatever order the drag went; normalise to
    // reading order (top-left first) for painting and text extraction.
    private (int sr, int sc, int er, int ec) OrderedSelection()
    {
        bool anchorFirst = _anchorRow < _caretRow ||
                           (_anchorRow == _caretRow && _anchorCol <= _caretCol);
        return anchorFirst
            ? (_anchorRow, _anchorCol, _caretRow, _caretCol)
            : (_caretRow, _caretCol, _anchorRow, _anchorCol);
    }

    private void DrawSelection(Graphics g)
    {
        if (_buffer == null || _cellWidth == 0) return;
        var (sr, sc, er, ec) = OrderedSelection();
        using var brush = new SolidBrush(SelectionFill);
        for (int r = sr; r <= er; r++)
        {
            int c0 = r == sr ? sc : 0;
            int c1 = r == er ? ec : _buffer.Cols - 1;
            int x = _offsetX + c0 * _cellWidth;
            int y = _offsetY + r * _cellHeight;
            g.FillRectangle(brush, x, y, (c1 - c0 + 1) * _cellWidth, _cellHeight);
        }
    }

    /// <summary>Extract the selected screen text as lines (linear/stream order). Trailing
    /// blanks are trimmed per line; rows join with CRLF. Reads through
    /// <see cref="ScreenBuffer.DisplayCharAt"/>, so hidden password fields copy as blanks.</summary>
    private string GetSelectedText()
    {
        if (_buffer == null || !_hasSelection) return string.Empty;
        var (sr, sc, er, ec) = OrderedSelection();
        var sb = new System.Text.StringBuilder();
        for (int r = sr; r <= er; r++)
        {
            int c0 = r == sr ? sc : 0;
            int c1 = r == er ? ec : _buffer.Cols - 1;
            var line = new char[c1 - c0 + 1];
            for (int c = c0; c <= c1; c++)
                line[c - c0] = _buffer.DisplayCharAt(r, c);
            sb.Append(new string(line).TrimEnd());
            if (r < er) sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private void CopySelection()
    {
        if (!_hasSelection) return;
        string text = GetSelectedText();
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); } catch { /* clipboard held by another app */ }
    }

    private void PasteFromClipboard()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty; }
        catch { return; }                              // clipboard held by another app
        if (!string.IsNullOrEmpty(text))
            PasteRequested?.Invoke(text);
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
