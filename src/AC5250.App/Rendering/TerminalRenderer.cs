using AC5250.Model;
using AC5250.Protocol;

namespace AC5250.Rendering;

public static class TerminalRenderer
{
    public static void Render(
        Graphics g,
        ScreenBuffer buffer,
        Font font,
        int cellWidth,
        int cellHeight,
        int offsetX,
        int offsetY,
        ColorScheme colors,
        bool cursorVisible)
    {
        g.Clear(colors.Background);

        var textFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
            | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;

        int rows = buffer.Rows, cols = buffer.Cols;

        // Current display attribute, scanned left-to-right, top-to-bottom. A 5250
        // attribute byte sets the characteristics for all following characters
        // until the next attribute byte, so field text and free-form text are both
        // colored correctly by this single pass.
        var cur = new FieldAttribute.DisplayAttr(Field5250Color.Green, false, false, false, false, false);

        using var inputBg = new SolidBrush(colors.InputFieldBackground);

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                int x = offsetX + col * cellWidth;
                int y = offsetY + row * cellHeight;
                var rect = new Rectangle(x, y, cellWidth, cellHeight);

                byte a = buffer.GetAttributeByteAt(row, col);
                if (a >= 0x20 && a <= 0x3F)
                {
                    // The attribute byte occupies a blank cell that is itself displayed
                    // with the attribute it defines. So a reverse-video attribute (e.g.
                    // the red-reverse cells that frame a pop-up window) must paint this
                    // cell as a colored block, and an underline attribute must underline
                    // it — otherwise the window's left/right border columns (which are
                    // bare attribute cells, not filled blanks) render as invisible gaps.
                    var here = FieldAttribute.DecodeDisplay(a);

                    // Detect whether this attribute byte introduces a field (the next cell is a
                    // field start). A field's leading attribute cell is not painted here — its
                    // colour belongs to the field's own cells, not this gap cell — and it drives
                    // the running attribute differently from an inline attribute (see below).
                    int npos = row * cols + col + 1;
                    bool isFieldMarker = npos < rows * cols && buffer.IsFieldStart(npos / cols, npos % cols);

                    // Paint the attribute cell only for INLINE attributes — a window's
                    // red-reverse border column shows as a colored block, etc. A field's
                    // leading attribute belongs to the field's own cells, not this gap
                    // cell; painting it would add a stray underline/fill one column left
                    // of every input field (making fields look shifted a space right).
                    // Don't paint an attribute cell in the last column. By 5250 convention a
                    // last-column attribute is the *leading* attribute for the NEXT line's
                    // wrapped content (S2K parks a label/field's attribute in col 80 so it
                    // doesn't consume a visible column on the content line). Painting it drew a
                    // stray reverse block / underline at the right edge that ACS doesn't show.
                    // The running attribute is still updated below so the wrapped text is
                    // coloured correctly.
                    if (!isFieldMarker && !here.NonDisplay && col < cols - 1)
                    {
                        if (here.Reverse)
                        {
                            using var rb = new SolidBrush(colors.ColorFor(here.Color));
                            g.FillRectangle(rb, rect);
                        }
                        else if (here.Underline)
                        {
                            using var up = new Pen(colors.ColorFor(here.Color));
                            g.DrawLine(up, x, y + cellHeight - 1, x + cellWidth - 1, y + cellHeight - 1);
                        }
                        else if (TryHealBorderGap(buffer, row, col, rows, out var healColor))
                        {
                            using var rb = new SolidBrush(colors.ColorFor(healColor));
                            g.FillRectangle(rb, rect);
                        }
                    }

                    // Running-attribute update. An inline (non-field) attribute persists until
                    // the next attribute byte. A field's leading attribute is different: its own
                    // cells carry the field attribute (handled via eff below), but the positions
                    // AFTER the field revert to normal — matching ACS. So at a field marker we
                    // reset the running attribute to normal rather than letting either the
                    // preceding attribute (e.g. a reverse-video "Qty." label wrapping in from the
                    // prior row) OR the field's own attribute bleed into the dead space past the
                    // field. The former painted a stray reverse block; the latter drew a stray
                    // underline to the row's edge.
                    cur = isFieldMarker
                        ? new FieldAttribute.DisplayAttr(Field5250Color.Green, false, false, false, false, false)
                        : here;
                    continue;
                }

                var field = buffer.GetFieldAt(row, col);
                bool isInput = field != null && !field.Attribute.IsBypass;

                // Field cells take the field's own attribute; free text takes the
                // running character attribute.
                FieldAttribute.DisplayAttr eff = field != null
                    ? new FieldAttribute.DisplayAttr(field.Attribute.Color, field.Attribute.IsReverse,
                        field.Attribute.IsUnderline, field.Attribute.IsNonDisplay,
                        field.Attribute.IsColumnSeparator, field.Attribute.IsBlink)
                    : cur;

                Color fg = colors.ColorFor(eff.Color);

                if (isInput)
                    g.FillRectangle(inputBg, rect);

                if (eff.Reverse && !eff.NonDisplay)
                {
                    using var rb = new SolidBrush(fg);
                    g.FillRectangle(rb, rect);
                }

                if (!eff.NonDisplay)
                {
                    byte eb = field != null
                        ? field.GetCharAt(field.GetIndexForPosition(row, col, cols))
                        : buffer.GetCharAt(row, col);
                    char ch = Ebcdic.ToAscii(eb);
                    if (ch > ' ' && ch <= '~')
                    {
                        Color textColor = eff.Reverse ? colors.Background : fg;
                        TextRenderer.DrawText(g, ch.ToString(), font, rect, textColor, Color.Transparent, textFlags);
                    }
                }

                if (eff.Underline)
                {
                    using var up = new Pen(eff.NonDisplay ? colors.InputUnderline : fg);
                    g.DrawLine(up, x, y + cellHeight - 1, x + cellWidth - 1, y + cellHeight - 1);
                }

                if (eff.ColumnSeparator && !eff.NonDisplay)
                {
                    using var cp = new Pen(colors.ColumnSeparator);
                    g.DrawLine(cp, x, y, x, y + cellHeight - 1);
                }
            }
        }

        if (cursorVisible && buffer.CursorRow < rows && buffer.CursorCol < cols)
        {
            int cx = offsetX + buffer.CursorCol * cellWidth;
            int cy = offsetY + buffer.CursorRow * cellHeight;
            using var cursorBrush = new SolidBrush(Color.FromArgb(200, colors.CursorColor));
            if (buffer.InsertMode)
                g.FillRectangle(cursorBrush, cx, cy + cellHeight / 2, cellWidth, cellHeight / 2);
            else
                g.FillRectangle(cursorBrush, cx, cy + cellHeight - 3, cellWidth, 3);
        }
    }

    // A pop-up window's vertical frame is a column of reverse-video attribute cells. When the
    // host redraws a blank interior row it sometimes writes a non-reverse attribute over that
    // column (e.g. XBGTLOC row 11 gets 0x20 where the rows directly above and below carry 0x29
    // red-reverse), leaving a one-row black notch in the frame. IBM ACS renders these frames
    // solid regardless; we match it by filling the gap with the border colour when the same
    // column directly above AND below are both reverse-video attribute cells of the same colour.
    // The pattern (a lone non-reverse attribute cell vertically flanked by matching reverse
    // attribute cells) is specific to a broken frame column and does not occur in body text.
    private static bool TryHealBorderGap(
        ScreenBuffer buffer, int row, int col, int rows, out Field5250Color color)
    {
        color = Field5250Color.Green;
        if (row <= 0 || row >= rows - 1) return false;

        byte above = buffer.GetAttributeByteAt(row - 1, col);
        byte below = buffer.GetAttributeByteAt(row + 1, col);
        if (above is < 0x20 or > 0x3F || below is < 0x20 or > 0x3F) return false;

        var a = FieldAttribute.DecodeDisplay(above);
        var b = FieldAttribute.DecodeDisplay(below);
        if (!a.Reverse || !b.Reverse || a.NonDisplay || b.NonDisplay || a.Color != b.Color)
            return false;

        color = a.Color;
        return true;
    }

    public static void RenderStatusBar(
        Graphics g,
        ScreenBuffer buffer,
        Font font,
        int y,
        int width,
        int height,
        ColorScheme colors,
        string hostInfo)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var bgBrush = new SolidBrush(colors.StatusBarBackground);
        g.FillRectangle(bgBrush, 0, y, width, height);

        using var borderPen = new Pen(Color.FromArgb(40, colors.StatusBarText));
        g.DrawLine(borderPen, 0, y, width, y);

        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
        int pad = 8;

        TextRenderer.DrawText(g, hostInfo, font, new Rectangle(pad, y, width / 3, height),
            colors.StatusBarText, Color.Transparent, flags);

        int cx = width / 2 - 60;
        int badgeY = y + (height - 14) / 2;
        int badgeH = 14;

        if (buffer.InputInhibited)
            cx = DrawStatusBadge(g, "X II", cx, badgeY, badgeH, font, Color.FromArgb(200, 90, 80), Color.FromArgb(40, 200, 90, 80));
        if (buffer.InsertMode)
            cx = DrawStatusBadge(g, "INS", cx, badgeY, badgeH, font, Color.FromArgb(200, 180, 60), Color.FromArgb(40, 200, 180, 60));
        if (buffer.MessageWaiting)
            cx = DrawStatusBadge(g, "MW", cx, badgeY, badgeH, font, Color.FromArgb(72, 199, 142), Color.FromArgb(40, 72, 199, 142));
        if (buffer.SystemAvailable)
            cx = DrawStatusBadge(g, "SA", cx, badgeY, badgeH, font, Color.FromArgb(100, 160, 100), Color.FromArgb(25, 100, 160, 100));

        string pos = $"{buffer.CursorRow + 1:D2}:{buffer.CursorCol + 1:D3}";
        var dimColor = Color.FromArgb(140, colors.StatusBarText);
        TextRenderer.DrawText(g, pos, font, new Rectangle(width - 80 - pad, y, 80, height),
            dimColor, Color.Transparent, flags | TextFormatFlags.Right);
    }

    private static int DrawStatusBadge(Graphics g, string text, int x, int y, int h, Font font, Color fg, Color bg)
    {
        var size = TextRenderer.MeasureText(text, font);
        int w = size.Width + 6;
        var rect = new Rectangle(x, y, w, h);

        using var bgBrush = new SolidBrush(bg);
        using var borderPen = new Pen(Color.FromArgb(60, fg));
        FillRoundedRect(g, bgBrush, rect, 3);
        DrawRoundedRect(g, borderPen, rect, 3);

        TextRenderer.DrawText(g, text, font, rect, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        return x + w + 6;
    }

    private static void FillRoundedRect(Graphics g, Brush brush, Rectangle rect, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private static void DrawRoundedRect(Graphics g, Pen pen, Rectangle rect, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.DrawPath(pen, path);
    }
}
