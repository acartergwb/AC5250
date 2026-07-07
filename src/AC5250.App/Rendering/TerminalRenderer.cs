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
                    // A 5250 attribute byte occupies one screen position, is displayed as a
                    // blank, and applies to the FOLLOWING positions until the next attribute
                    // byte (GA21-9247 Functions Reference). So the visible colour of a reverse-
                    // video region normally lands on the character cells after the attribute,
                    // not the attribute cell itself. A window's side border is 1 column wide in
                    // ACS: the host writes it either as [reverse-attr][space][normal-attr] (the
                    // space carries the colour) or as [reverse-attr][normal-attr] with no space
                    // (nothing follows to carry it). Painting the attribute cell UNCONDITIONALLY
                    // made the first form render 2 columns wide (attr cell + space) and, where
                    // padding attributes doubled up, jagged. Fix: paint the attribute cell only
                    // when it is the last cell of its reverse run — i.e. the next cell is a
                    // non-reverse attribute byte, so no following character will carry the
                    // colour. Then the space form renders 1 column (via the char) and the
                    // no-space form renders 1 column (via this cell).
                    var here = FieldAttribute.DecodeDisplay(a);

                    // Detect whether this attribute byte introduces a field (the next cell is a
                    // field start). A field's leading attribute cell is never painted — its
                    // colour belongs to the field's own cells, not this gap cell — and it drives
                    // the running attribute differently from an inline attribute (see below).
                    int npos = row * cols + col + 1;
                    bool isFieldMarker = npos < rows * cols && buffer.IsFieldStart(npos / cols, npos % cols);

                    // Skip the last column too: by 5250 convention a last-column attribute is the
                    // leading attribute for the NEXT line's wrapped content (S2K parks a label/
                    // field's attribute in col 80 so it doesn't consume a visible column), so
                    // painting it drew a stray block at the right edge that ACS doesn't show.
                    if (!isFieldMarker && !here.NonDisplay && col < cols - 1
                        && NextIsNonReverseAttribute(buffer, row, col, cols))
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

    // True when the cell immediately to the right is a non-reverse attribute byte — meaning
    // this attribute cell is the LAST cell of its reverse run and no following character will
    // carry the colour, so the attribute cell itself must be painted (a 1-column border with no
    // trailing space). If the next cell is a character (it will render reverse via the running
    // attribute) or another reverse attribute (it will paint or defer in turn), this cell is
    // left blank so the run renders exactly 1 column wide, matching ACS.
    private static bool NextIsNonReverseAttribute(ScreenBuffer buffer, int row, int col, int cols)
    {
        int nextCol = col + 1;
        if (nextCol >= cols) return false;
        byte next = buffer.GetAttributeByteAt(row, nextCol);
        if (next is < 0x20 or > 0x3F) return false; // a character cell — it carries the colour
        return !FieldAttribute.DecodeDisplay(next).Reverse;
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
