using System.Windows;
using System.Windows.Media;
using CcDirector.Terminal.Core;

namespace CcDirector.Terminal.Rendering;

/// <summary>
/// Draws box-drawing Unicode characters (U+2500-U+257F) as geometric lines
/// instead of text glyphs. This produces pixel-perfect results matching
/// Windows Terminal, avoiding the thick grey bar effect from anti-aliased
/// font rendering.
/// </summary>
internal static class BoxDrawingHelper
{
    private static readonly Dictionary<Color, Pen> PenCache = new();
    private static readonly object PenCacheLock = new();

    /// <summary>
    /// Returns true if the character is in the box-drawing range and was drawn.
    /// Caller should skip text rendering when this returns true.
    /// </summary>
    internal static bool TryDrawBoxChar(DrawingContext dc, char ch, Color fg,
        double x, double y, double cellWidth, double cellHeight)
    {
        if (ch < '\u2500' || ch > '\u257F')
            return false;

        var pen = GetPen(fg);
        double cx = x + cellWidth / 2;
        double cy = y + cellHeight / 2;
        double left = x;
        double right = x + cellWidth;
        double top = y;
        double bottom = y + cellHeight;

        // Snap to pixel for crisp lines
        cx = Math.Round(cx) + 0.5;
        cy = Math.Round(cy) + 0.5;
        left = Math.Round(left);
        right = Math.Round(right);
        top = Math.Round(top);
        bottom = Math.Round(bottom);

        switch (ch)
        {
            // Horizontal lines
            case '\u2500': // light horizontal
            case '\u2501': // heavy horizontal (draw same as light)
                dc.DrawLine(pen, new Point(left, cy), new Point(right, cy));
                return true;

            // Dashed horizontal variants (Claude Code separator uses these)
            case '\u2504': // light triple dash horizontal
            case '\u2505': // heavy triple dash horizontal
            case '\u2508': // light quadruple dash horizontal
            case '\u2509': // heavy quadruple dash horizontal
            case '\u254C': // light double dash horizontal
            case '\u254D': // heavy double dash horizontal
                DrawDashedHorizontal(dc, fg, left, right, cy, cellWidth);
                return true;

            // Vertical lines
            case '\u2502': // light vertical
            case '\u2503': // heavy vertical
                dc.DrawLine(pen, new Point(cx, top), new Point(cx, bottom));
                return true;

            // Dashed vertical variants
            case '\u2506': // light triple dash vertical
            case '\u2507': // heavy triple dash vertical
            case '\u250A': // light quadruple dash vertical
            case '\u250B': // heavy quadruple dash vertical
            case '\u254E': // light double dash vertical
            case '\u254F': // heavy double dash vertical
                DrawDashedVertical(dc, fg, cx, top, bottom, cellHeight);
                return true;

            // Corners
            case '\u250C': // light down and right
            case '\u250D': case '\u250E': case '\u250F':
                dc.DrawLine(pen, new Point(cx, cy), new Point(right, cy));
                dc.DrawLine(pen, new Point(cx, cy), new Point(cx, bottom));
                return true;

            case '\u2510': // light down and left
            case '\u2511': case '\u2512': case '\u2513':
                dc.DrawLine(pen, new Point(left, cy), new Point(cx, cy));
                dc.DrawLine(pen, new Point(cx, cy), new Point(cx, bottom));
                return true;

            case '\u2514': // light up and right
            case '\u2515': case '\u2516': case '\u2517':
                dc.DrawLine(pen, new Point(cx, top), new Point(cx, cy));
                dc.DrawLine(pen, new Point(cx, cy), new Point(right, cy));
                return true;

            case '\u2518': // light up and left
            case '\u2519': case '\u251A': case '\u251B':
                dc.DrawLine(pen, new Point(cx, top), new Point(cx, cy));
                dc.DrawLine(pen, new Point(left, cy), new Point(cx, cy));
                return true;

            // Tees
            case '\u251C': // light vertical and right
            case '\u251D': case '\u251E': case '\u251F':
            case '\u2520': case '\u2521': case '\u2522': case '\u2523':
                dc.DrawLine(pen, new Point(cx, top), new Point(cx, bottom));
                dc.DrawLine(pen, new Point(cx, cy), new Point(right, cy));
                return true;

            case '\u2524': // light vertical and left
            case '\u2525': case '\u2526': case '\u2527':
            case '\u2528': case '\u2529': case '\u252A': case '\u252B':
                dc.DrawLine(pen, new Point(cx, top), new Point(cx, bottom));
                dc.DrawLine(pen, new Point(left, cy), new Point(cx, cy));
                return true;

            case '\u252C': // light down and horizontal
            case '\u252D': case '\u252E': case '\u252F':
            case '\u2530': case '\u2531': case '\u2532': case '\u2533':
                dc.DrawLine(pen, new Point(left, cy), new Point(right, cy));
                dc.DrawLine(pen, new Point(cx, cy), new Point(cx, bottom));
                return true;

            case '\u2534': // light up and horizontal
            case '\u2535': case '\u2536': case '\u2537':
            case '\u2538': case '\u2539': case '\u253A': case '\u253B':
                dc.DrawLine(pen, new Point(left, cy), new Point(right, cy));
                dc.DrawLine(pen, new Point(cx, top), new Point(cx, cy));
                return true;

            // Cross
            case '\u253C': // light vertical and horizontal
            case '\u253D': case '\u253E': case '\u253F':
            case '\u2540': case '\u2541': case '\u2542': case '\u2543':
            case '\u2544': case '\u2545': case '\u2546': case '\u2547':
            case '\u2548': case '\u2549': case '\u254A': case '\u254B':
                dc.DrawLine(pen, new Point(left, cy), new Point(right, cy));
                dc.DrawLine(pen, new Point(cx, top), new Point(cx, bottom));
                return true;

            // Double-line box drawing
            case '\u2550': // double horizontal
                DrawDoubleLine(dc, pen, left, right, cy, true, cellHeight);
                return true;

            case '\u2551': // double vertical
                DrawDoubleLine(dc, pen, cx, top, bottom, false, cellWidth);
                return true;

            case '\u2552': // down single and right double
                dc.DrawLine(pen, new Point(cx, cy - 1.5), new Point(right, cy - 1.5));
                dc.DrawLine(pen, new Point(cx, cy + 1.5), new Point(right, cy + 1.5));
                dc.DrawLine(pen, new Point(cx, cy), new Point(cx, bottom));
                return true;

            case '\u2553': // down double and right single
                dc.DrawLine(pen, new Point(cx, cy), new Point(right, cy));
                dc.DrawLine(pen, new Point(cx - 1.5, cy), new Point(cx - 1.5, bottom));
                dc.DrawLine(pen, new Point(cx + 1.5, cy), new Point(cx + 1.5, bottom));
                return true;

            case '\u2554': // double down and right
                dc.DrawLine(pen, new Point(cx, cy - 1.5), new Point(right, cy - 1.5));
                dc.DrawLine(pen, new Point(cx, cy + 1.5), new Point(right, cy + 1.5));
                dc.DrawLine(pen, new Point(cx - 1.5, cy - 1.5), new Point(cx - 1.5, bottom));
                dc.DrawLine(pen, new Point(cx + 1.5, cy + 1.5), new Point(cx + 1.5, bottom));
                return true;

            case '\u2555': // down single and left double
                dc.DrawLine(pen, new Point(left, cy - 1.5), new Point(cx, cy - 1.5));
                dc.DrawLine(pen, new Point(left, cy + 1.5), new Point(cx, cy + 1.5));
                dc.DrawLine(pen, new Point(cx, cy), new Point(cx, bottom));
                return true;

            case '\u2556': // down double and left single
                dc.DrawLine(pen, new Point(left, cy), new Point(cx, cy));
                dc.DrawLine(pen, new Point(cx - 1.5, cy), new Point(cx - 1.5, bottom));
                dc.DrawLine(pen, new Point(cx + 1.5, cy), new Point(cx + 1.5, bottom));
                return true;

            case '\u2557': // double down and left
                dc.DrawLine(pen, new Point(left, cy - 1.5), new Point(cx, cy - 1.5));
                dc.DrawLine(pen, new Point(left, cy + 1.5), new Point(cx, cy + 1.5));
                dc.DrawLine(pen, new Point(cx - 1.5, cy - 1.5), new Point(cx - 1.5, bottom));
                dc.DrawLine(pen, new Point(cx + 1.5, cy + 1.5), new Point(cx + 1.5, bottom));
                return true;

            case '\u2558': // up single and right double
                dc.DrawLine(pen, new Point(cx, top), new Point(cx, cy));
                dc.DrawLine(pen, new Point(cx, cy - 1.5), new Point(right, cy - 1.5));
                dc.DrawLine(pen, new Point(cx, cy + 1.5), new Point(right, cy + 1.5));
                return true;

            case '\u2559': // up double and right single
                dc.DrawLine(pen, new Point(cx - 1.5, top), new Point(cx - 1.5, cy));
                dc.DrawLine(pen, new Point(cx + 1.5, top), new Point(cx + 1.5, cy));
                dc.DrawLine(pen, new Point(cx, cy), new Point(right, cy));
                return true;

            case '\u255A': // double up and right
                dc.DrawLine(pen, new Point(cx - 1.5, top), new Point(cx - 1.5, cy + 1.5));
                dc.DrawLine(pen, new Point(cx + 1.5, top), new Point(cx + 1.5, cy - 1.5));
                dc.DrawLine(pen, new Point(cx, cy - 1.5), new Point(right, cy - 1.5));
                dc.DrawLine(pen, new Point(cx, cy + 1.5), new Point(right, cy + 1.5));
                return true;

            case '\u255B': // up single and left double
                dc.DrawLine(pen, new Point(cx, top), new Point(cx, cy));
                dc.DrawLine(pen, new Point(left, cy - 1.5), new Point(cx, cy - 1.5));
                dc.DrawLine(pen, new Point(left, cy + 1.5), new Point(cx, cy + 1.5));
                return true;

            case '\u255C': // up double and left single
                dc.DrawLine(pen, new Point(cx - 1.5, top), new Point(cx - 1.5, cy));
                dc.DrawLine(pen, new Point(cx + 1.5, top), new Point(cx + 1.5, cy));
                dc.DrawLine(pen, new Point(left, cy), new Point(cx, cy));
                return true;

            case '\u255D': // double up and left
                dc.DrawLine(pen, new Point(cx - 1.5, top), new Point(cx - 1.5, cy + 1.5));
                dc.DrawLine(pen, new Point(cx + 1.5, top), new Point(cx + 1.5, cy - 1.5));
                dc.DrawLine(pen, new Point(left, cy - 1.5), new Point(cx, cy - 1.5));
                dc.DrawLine(pen, new Point(left, cy + 1.5), new Point(cx, cy + 1.5));
                return true;

            case '\u255E': // vertical single and right double
                dc.DrawLine(pen, new Point(cx, top), new Point(cx, bottom));
                dc.DrawLine(pen, new Point(cx, cy - 1.5), new Point(right, cy - 1.5));
                dc.DrawLine(pen, new Point(cx, cy + 1.5), new Point(right, cy + 1.5));
                return true;

            case '\u255F': // vertical double and right single
                dc.DrawLine(pen, new Point(cx - 1.5, top), new Point(cx - 1.5, bottom));
                dc.DrawLine(pen, new Point(cx + 1.5, top), new Point(cx + 1.5, bottom));
                dc.DrawLine(pen, new Point(cx, cy), new Point(right, cy));
                return true;

            case '\u2560': // double vertical and right
                dc.DrawLine(pen, new Point(cx - 1.5, top), new Point(cx - 1.5, bottom));
                dc.DrawLine(pen, new Point(cx + 1.5, top), new Point(cx + 1.5, cy - 1.5));
                dc.DrawLine(pen, new Point(cx + 1.5, cy + 1.5), new Point(cx + 1.5, bottom));
                dc.DrawLine(pen, new Point(cx + 1.5, cy - 1.5), new Point(right, cy - 1.5));
                dc.DrawLine(pen, new Point(cx + 1.5, cy + 1.5), new Point(right, cy + 1.5));
                return true;

            case '\u2561': // vertical single and left double
                dc.DrawLine(pen, new Point(cx, top), new Point(cx, bottom));
                dc.DrawLine(pen, new Point(left, cy - 1.5), new Point(cx, cy - 1.5));
                dc.DrawLine(pen, new Point(left, cy + 1.5), new Point(cx, cy + 1.5));
                return true;

            case '\u2562': // vertical double and left single
                dc.DrawLine(pen, new Point(cx - 1.5, top), new Point(cx - 1.5, bottom));
                dc.DrawLine(pen, new Point(cx + 1.5, top), new Point(cx + 1.5, bottom));
                dc.DrawLine(pen, new Point(left, cy), new Point(cx, cy));
                return true;

            case '\u2563': // double vertical and left
                dc.DrawLine(pen, new Point(cx + 1.5, top), new Point(cx + 1.5, bottom));
                dc.DrawLine(pen, new Point(cx - 1.5, top), new Point(cx - 1.5, cy - 1.5));
                dc.DrawLine(pen, new Point(cx - 1.5, cy + 1.5), new Point(cx - 1.5, bottom));
                dc.DrawLine(pen, new Point(left, cy - 1.5), new Point(cx - 1.5, cy - 1.5));
                dc.DrawLine(pen, new Point(left, cy + 1.5), new Point(cx - 1.5, cy + 1.5));
                return true;

            case '\u2564': // down single and horizontal double
                DrawDoubleLine(dc, pen, left, right, cy, true, cellHeight);
                dc.DrawLine(pen, new Point(cx, cy), new Point(cx, bottom));
                return true;

            case '\u2565': // down double and horizontal single
                dc.DrawLine(pen, new Point(left, cy), new Point(right, cy));
                dc.DrawLine(pen, new Point(cx - 1.5, cy), new Point(cx - 1.5, bottom));
                dc.DrawLine(pen, new Point(cx + 1.5, cy), new Point(cx + 1.5, bottom));
                return true;

            case '\u2566': // double down and horizontal
                dc.DrawLine(pen, new Point(left, cy - 1.5), new Point(right, cy - 1.5));
                dc.DrawLine(pen, new Point(left, cy + 1.5), new Point(cx - 1.5, cy + 1.5));
                dc.DrawLine(pen, new Point(cx + 1.5, cy + 1.5), new Point(right, cy + 1.5));
                dc.DrawLine(pen, new Point(cx - 1.5, cy + 1.5), new Point(cx - 1.5, bottom));
                dc.DrawLine(pen, new Point(cx + 1.5, cy + 1.5), new Point(cx + 1.5, bottom));
                return true;

            case '\u2567': // up single and horizontal double
                DrawDoubleLine(dc, pen, left, right, cy, true, cellHeight);
                dc.DrawLine(pen, new Point(cx, top), new Point(cx, cy));
                return true;

            case '\u2568': // up double and horizontal single
                dc.DrawLine(pen, new Point(left, cy), new Point(right, cy));
                dc.DrawLine(pen, new Point(cx - 1.5, top), new Point(cx - 1.5, cy));
                dc.DrawLine(pen, new Point(cx + 1.5, top), new Point(cx + 1.5, cy));
                return true;

            case '\u2569': // double up and horizontal
                dc.DrawLine(pen, new Point(left, cy + 1.5), new Point(right, cy + 1.5));
                dc.DrawLine(pen, new Point(left, cy - 1.5), new Point(cx - 1.5, cy - 1.5));
                dc.DrawLine(pen, new Point(cx + 1.5, cy - 1.5), new Point(right, cy - 1.5));
                dc.DrawLine(pen, new Point(cx - 1.5, top), new Point(cx - 1.5, cy - 1.5));
                dc.DrawLine(pen, new Point(cx + 1.5, top), new Point(cx + 1.5, cy - 1.5));
                return true;

            case '\u256A': // vertical single and horizontal double
                dc.DrawLine(pen, new Point(cx, top), new Point(cx, bottom));
                DrawDoubleLine(dc, pen, left, right, cy, true, cellHeight);
                return true;

            case '\u256B': // vertical double and horizontal single
                dc.DrawLine(pen, new Point(left, cy), new Point(right, cy));
                dc.DrawLine(pen, new Point(cx - 1.5, top), new Point(cx - 1.5, bottom));
                dc.DrawLine(pen, new Point(cx + 1.5, top), new Point(cx + 1.5, bottom));
                return true;

            case '\u256C': // double vertical and horizontal
                dc.DrawLine(pen, new Point(cx - 1.5, top), new Point(cx - 1.5, cy - 1.5));
                dc.DrawLine(pen, new Point(cx + 1.5, top), new Point(cx + 1.5, cy - 1.5));
                dc.DrawLine(pen, new Point(cx - 1.5, cy + 1.5), new Point(cx - 1.5, bottom));
                dc.DrawLine(pen, new Point(cx + 1.5, cy + 1.5), new Point(cx + 1.5, bottom));
                dc.DrawLine(pen, new Point(left, cy - 1.5), new Point(cx - 1.5, cy - 1.5));
                dc.DrawLine(pen, new Point(left, cy + 1.5), new Point(cx - 1.5, cy + 1.5));
                dc.DrawLine(pen, new Point(cx + 1.5, cy - 1.5), new Point(right, cy - 1.5));
                dc.DrawLine(pen, new Point(cx + 1.5, cy + 1.5), new Point(right, cy + 1.5));
                return true;

            default:
                // Unhandled box-drawing char in range - fall back to text rendering
                return false;
        }
    }

    private static void DrawDashedHorizontal(DrawingContext dc, Color fg,
        double left, double right, double cy, double cellWidth)
    {
        var dashPen = GetDashedPen(fg);
        dc.DrawLine(dashPen, new Point(left, cy), new Point(right, cy));
    }

    private static void DrawDashedVertical(DrawingContext dc, Color fg,
        double cx, double top, double bottom, double cellHeight)
    {
        var dashPen = GetDashedPen(fg);
        dc.DrawLine(dashPen, new Point(cx, top), new Point(cx, bottom));
    }

    private static void DrawDoubleLine(DrawingContext dc, Pen pen,
        double start, double end, double center, bool horizontal, double thickness)
    {
        double offset = 1.5;
        if (horizontal)
        {
            dc.DrawLine(pen, new Point(start, center - offset), new Point(end, center - offset));
            dc.DrawLine(pen, new Point(start, center + offset), new Point(end, center + offset));
        }
        else
        {
            dc.DrawLine(pen, new Point(center - offset, start), new Point(center - offset, end));
            dc.DrawLine(pen, new Point(center + offset, start), new Point(center + offset, end));
        }
    }

    private static Pen GetPen(Color color)
    {
        lock (PenCacheLock)
        {
            if (!PenCache.TryGetValue(color, out var pen))
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                pen = new Pen(brush, 1);
                pen.Freeze();
                PenCache[color] = pen;
            }
            return pen;
        }
    }

    private static Pen GetDashedPen(Color color)
    {
        // Use a unique key by shifting alpha to distinguish from solid pens
        var key = Color.FromArgb(254, color.R, color.G, color.B);
        lock (PenCacheLock)
        {
            if (!PenCache.TryGetValue(key, out var pen))
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                pen = new Pen(brush, 1)
                {
                    DashStyle = DashStyles.Dash
                };
                pen.Freeze();
                PenCache[key] = pen;
            }
            return pen;
        }
    }
}
