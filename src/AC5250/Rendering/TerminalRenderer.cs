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
        ColorScheme colors,
        bool cursorVisible)
    {
        g.Clear(colors.Background);

        using var normalBrush = new SolidBrush(colors.Normal);
        using var hiBrush = new SolidBrush(colors.HighIntensity);
        using var nonDisplayBrush = new SolidBrush(colors.NonDisplay);
        using var reverseBrush = new SolidBrush(colors.Reverse);
        using var underlinePen = new Pen(colors.Underline, 1);
        using var colSepPen = new Pen(colors.ColumnSeparator, 1);
        using var inputBgBrush = new SolidBrush(colors.InputFieldBackground);
        using var attrBrush = new SolidBrush(colors.FieldAttributeMarker);

        var textFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

        for (int row = 0; row < buffer.Rows; row++)
        {
            for (int col = 0; col < buffer.Cols; col++)
            {
                int x = col * cellWidth;
                int y = row * cellHeight;

                // Check if this position is a field attribute marker
                if (buffer.IsFieldAttributeAt(row, col))
                {
                    continue; // attribute positions are blank
                }

                // Get the field at this position for display attributes
                var field = buffer.GetFieldAt(row, col);
                bool isInputField = field != null && !field.Attribute.IsBypass;
                bool isNonDisplay = field?.Attribute.IsNonDisplay == true;
                bool isHighIntensity = field?.Attribute.IsHighIntensity == true;
                bool isUnderline = field?.Attribute.IsUnderline == true;
                bool isColumnSep = field?.Attribute.IsColumnSeparator == true;
                bool isReverse = field?.Attribute.IsReverse == true;

                // Background for input fields
                if (isInputField && !isNonDisplay)
                {
                    g.FillRectangle(inputBgBrush, x, y, cellWidth, cellHeight);
                }

                // Get character
                byte ebcdic;
                if (field != null)
                {
                    int idx = field.GetIndexForPosition(row, col, buffer.Cols);
                    ebcdic = field.GetCharAt(idx);
                }
                else
                {
                    ebcdic = buffer.GetCharAt(row, col);
                }

                char ch = Ebcdic.ToAscii(ebcdic);

                // Pick brush based on attributes
                Brush textBrush;
                if (isNonDisplay)
                    textBrush = nonDisplayBrush;
                else if (isReverse)
                {
                    g.FillRectangle(reverseBrush, x, y, cellWidth, cellHeight);
                    textBrush = new SolidBrush(colors.Background);
                }
                else if (isHighIntensity)
                    textBrush = hiBrush;
                else
                    textBrush = normalBrush;

                // Draw character
                if (ch > ' ')
                {
                    var rect = new Rectangle(x, y, cellWidth, cellHeight);
                    TextRenderer.DrawText(g, ch.ToString(), font, rect,
                        ((SolidBrush)textBrush).Color, Color.Transparent, textFlags);
                }

                // Underline
                if (isUnderline && !isNonDisplay)
                {
                    g.DrawLine(underlinePen, x, y + cellHeight - 1, x + cellWidth, y + cellHeight - 1);
                }

                // Column separator
                if (isColumnSep && !isNonDisplay)
                {
                    g.DrawLine(colSepPen, x + cellWidth - 1, y, x + cellWidth - 1, y + cellHeight);
                }

                // Dispose reverse brush if we created one
                if (isReverse && !isNonDisplay)
                    textBrush.Dispose();
            }
        }

        // Draw cursor
        if (cursorVisible && buffer.CursorRow < buffer.Rows && buffer.CursorCol < buffer.Cols)
        {
            int cx = buffer.CursorCol * cellWidth;
            int cy = buffer.CursorRow * cellHeight;

            using var cursorBrush = new SolidBrush(Color.FromArgb(180, colors.CursorColor));

            if (buffer.InsertMode)
            {
                // Half-block cursor for insert mode
                g.FillRectangle(cursorBrush, cx, cy + cellHeight / 2, cellWidth, cellHeight / 2);
            }
            else
            {
                // Underline cursor for normal mode
                g.FillRectangle(cursorBrush, cx, cy + cellHeight - 2, cellWidth, 2);
            }
        }
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

        // Top border line
        using var borderPen = new Pen(Color.FromArgb(40, colors.StatusBarText));
        g.DrawLine(borderPen, 0, y, width, y);

        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
        int pad = 8;

        // Left side: connection info
        TextRenderer.DrawText(g, hostInfo, font, new Rectangle(pad, y, width / 3, height),
            colors.StatusBarText, Color.Transparent, flags);

        // Center: status indicators as individual badges
        int cx = width / 2 - 60;
        int badgeY = y + (height - 14) / 2;
        int badgeH = 14;

        if (buffer.InputInhibited)
            cx = DrawStatusBadge(g, "X II", cx, badgeY, badgeH, font,
                Color.FromArgb(180, 70, 70), Color.FromArgb(40, 180, 70, 70));
        if (buffer.InsertMode)
            cx = DrawStatusBadge(g, "INS", cx, badgeY, badgeH, font,
                Color.FromArgb(200, 180, 60), Color.FromArgb(40, 200, 180, 60));
        if (buffer.MessageWaiting)
            cx = DrawStatusBadge(g, "MW", cx, badgeY, badgeH, font,
                Color.FromArgb(72, 199, 142), Color.FromArgb(40, 72, 199, 142));
        if (buffer.SystemAvailable)
            cx = DrawStatusBadge(g, "SA", cx, badgeY, badgeH, font,
                Color.FromArgb(100, 160, 100), Color.FromArgb(25, 100, 160, 100));

        // Right side: cursor position
        string pos = $"{buffer.CursorRow + 1:D2}:{buffer.CursorCol + 1:D3}";
        var dimColor = Color.FromArgb(140, colors.StatusBarText);
        TextRenderer.DrawText(g, pos, font, new Rectangle(width - 80 - pad, y, 80, height),
            dimColor, Color.Transparent, flags | TextFormatFlags.Right);
    }

    private static int DrawStatusBadge(Graphics g, string text, int x, int y, int h, Font font,
        Color fg, Color bg)
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
