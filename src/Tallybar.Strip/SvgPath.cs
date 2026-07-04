using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace Tallybar;

/// <summary>
/// Minimal SVG path-data ("d" attribute) to GDI+ GraphicsPath converter. Supports
/// M/L/H/V/C/S/Q/T/A/Z in both absolute and relative forms, including arc-to-bezier.
/// Enough to render simple single-path brand marks.
/// </summary>
internal static class SvgPath
{
    public static GraphicsPath Parse(string d, FillMode fillMode)
    {
        var path = new GraphicsPath { FillMode = fillMode };
        var r = new Reader(d);
        float cx = 0, cy = 0, sx = 0, sy = 0;    // current point, subpath start
        float lcx = 0, lcy = 0;                  // last cubic control (for S)
        float lqx = 0, lqy = 0;                  // last quad control (for T)
        char cmd = ' ', prev = ' ';
        bool open = false;

        while (!r.End)
        {
            char c = r.PeekCommand();
            if (c != '\0') { cmd = c; r.Next(); }
            else if (cmd == 'M') cmd = 'L';
            else if (cmd == 'm') cmd = 'l';

            switch (cmd)
            {
                case 'M': cx = r.Num(); cy = r.Num(); path.StartFigure(); sx = cx; sy = cy; open = true; break;
                case 'm': cx += r.Num(); cy += r.Num(); path.StartFigure(); sx = cx; sy = cy; open = true; break;
                case 'L': { float x = r.Num(), y = r.Num(); path.AddLine(cx, cy, x, y); cx = x; cy = y; break; }
                case 'l': { float x = cx + r.Num(), y = cy + r.Num(); path.AddLine(cx, cy, x, y); cx = x; cy = y; break; }
                case 'H': { float x = r.Num(); path.AddLine(cx, cy, x, cy); cx = x; break; }
                case 'h': { float x = cx + r.Num(); path.AddLine(cx, cy, x, cy); cx = x; break; }
                case 'V': { float y = r.Num(); path.AddLine(cx, cy, cx, y); cy = y; break; }
                case 'v': { float y = cy + r.Num(); path.AddLine(cx, cy, cx, y); cy = y; break; }
                case 'C':
                {
                    float c1x = r.Num(), c1y = r.Num(), c2x = r.Num(), c2y = r.Num(), x = r.Num(), y = r.Num();
                    path.AddBezier(cx, cy, c1x, c1y, c2x, c2y, x, y);
                    lcx = c2x; lcy = c2y; cx = x; cy = y; break;
                }
                case 'c':
                {
                    float c1x = cx + r.Num(), c1y = cy + r.Num(), c2x = cx + r.Num(), c2y = cy + r.Num(),
                          x = cx + r.Num(), y = cy + r.Num();
                    path.AddBezier(cx, cy, c1x, c1y, c2x, c2y, x, y);
                    lcx = c2x; lcy = c2y; cx = x; cy = y; break;
                }
                case 'S':
                {
                    (float c1x, float c1y) = prev is 'C' or 'c' or 'S' or 's' ? (2 * cx - lcx, 2 * cy - lcy) : (cx, cy);
                    float c2x = r.Num(), c2y = r.Num(), x = r.Num(), y = r.Num();
                    path.AddBezier(cx, cy, c1x, c1y, c2x, c2y, x, y);
                    lcx = c2x; lcy = c2y; cx = x; cy = y; break;
                }
                case 's':
                {
                    (float c1x, float c1y) = prev is 'C' or 'c' or 'S' or 's' ? (2 * cx - lcx, 2 * cy - lcy) : (cx, cy);
                    float c2x = cx + r.Num(), c2y = cy + r.Num(), x = cx + r.Num(), y = cy + r.Num();
                    path.AddBezier(cx, cy, c1x, c1y, c2x, c2y, x, y);
                    lcx = c2x; lcy = c2y; cx = x; cy = y; break;
                }
                case 'Q':
                {
                    float qx = r.Num(), qy = r.Num(), x = r.Num(), y = r.Num();
                    QuadBezier(path, cx, cy, qx, qy, x, y); lqx = qx; lqy = qy; cx = x; cy = y; break;
                }
                case 'q':
                {
                    float qx = cx + r.Num(), qy = cy + r.Num(), x = cx + r.Num(), y = cy + r.Num();
                    QuadBezier(path, cx, cy, qx, qy, x, y); lqx = qx; lqy = qy; cx = x; cy = y; break;
                }
                case 'T':
                {
                    (float qx, float qy) = prev is 'Q' or 'q' or 'T' or 't' ? (2 * cx - lqx, 2 * cy - lqy) : (cx, cy);
                    float x = r.Num(), y = r.Num();
                    QuadBezier(path, cx, cy, qx, qy, x, y); lqx = qx; lqy = qy; cx = x; cy = y; break;
                }
                case 't':
                {
                    (float qx, float qy) = prev is 'Q' or 'q' or 'T' or 't' ? (2 * cx - lqx, 2 * cy - lqy) : (cx, cy);
                    float x = cx + r.Num(), y = cy + r.Num();
                    QuadBezier(path, cx, cy, qx, qy, x, y); lqx = qx; lqy = qy; cx = x; cy = y; break;
                }
                case 'A':
                {
                    float rx = r.Num(), ry = r.Num(), rot = r.Num(); int la = r.Flag(), sw = r.Flag();
                    float x = r.Num(), y = r.Num();
                    Arc(path, cx, cy, rx, ry, rot, la, sw, x, y); cx = x; cy = y; break;
                }
                case 'a':
                {
                    float rx = r.Num(), ry = r.Num(), rot = r.Num(); int la = r.Flag(), sw = r.Flag();
                    float x = cx + r.Num(), y = cy + r.Num();
                    Arc(path, cx, cy, rx, ry, rot, la, sw, x, y); cx = x; cy = y; break;
                }
                case 'Z':
                case 'z':
                    if (open) path.CloseFigure();
                    cx = sx; cy = sy; open = false; break;
                default:
                    r.Next(); break; // unknown; skip
            }
            prev = cmd;
        }
        return path;
    }

    private static void QuadBezier(GraphicsPath p, float x0, float y0, float qx, float qy, float x1, float y1)
    {
        // Elevate a quadratic to a cubic.
        float c1x = x0 + 2f / 3f * (qx - x0), c1y = y0 + 2f / 3f * (qy - y0);
        float c2x = x1 + 2f / 3f * (qx - x1), c2y = y1 + 2f / 3f * (qy - y1);
        p.AddBezier(x0, y0, c1x, c1y, c2x, c2y, x1, y1);
    }

    private static void Arc(GraphicsPath p, float x0, float y0, float rx, float ry, float rotDeg,
        int largeArc, int sweep, float x1, float y1)
    {
        if (rx == 0 || ry == 0) { p.AddLine(x0, y0, x1, y1); return; }
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        double phi = rotDeg * Math.PI / 180.0;
        double cosP = Math.Cos(phi), sinP = Math.Sin(phi);

        double dx = (x0 - x1) / 2.0, dy = (y0 - y1) / 2.0;
        double x1p = cosP * dx + sinP * dy, y1p = -sinP * dx + cosP * dy;

        double rxs = rx * rx, rys = ry * ry, x1ps = x1p * x1p, y1ps = y1p * y1p;
        double lambda = x1ps / rxs + y1ps / rys;
        if (lambda > 1) { double s = Math.Sqrt(lambda); rx = (float)(rx * s); ry = (float)(ry * s); rxs = rx * rx; rys = ry * ry; }

        double sign = largeArc == sweep ? -1 : 1;
        double num = rxs * rys - rxs * y1ps - rys * x1ps;
        double coef = sign * Math.Sqrt(Math.Max(0, num) / (rxs * y1ps + rys * x1ps));
        double cxp = coef * rx * y1p / ry, cyp = -coef * ry * x1p / rx;

        double cxc = cosP * cxp - sinP * cyp + (x0 + x1) / 2.0;
        double cyc = sinP * cxp + cosP * cyp + (y0 + y1) / 2.0;

        double startAngle = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        double delta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (sweep == 0 && delta > 0) delta -= 2 * Math.PI;
        else if (sweep == 1 && delta < 0) delta += 2 * Math.PI;

        int segs = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2)));
        double segDelta = delta / segs;
        double t = 8.0 / 3.0 * Math.Sin(segDelta / 4) * Math.Sin(segDelta / 4) / Math.Sin(segDelta / 2);

        double ang = startAngle;
        double px = x0, py = y0;
        for (int i = 0; i < segs; i++)
        {
            double a2 = ang + segDelta;
            double cos1 = Math.Cos(ang), sin1 = Math.Sin(ang), cos2 = Math.Cos(a2), sin2 = Math.Sin(a2);

            (double ex, double ey) = Point(cxc, cyc, rx, ry, cosP, sinP, cos2, sin2);
            double c1x = px + t * (cosP * (-rx * sin1) - sinP * (ry * cos1));
            double c1y = py + t * (sinP * (-rx * sin1) + cosP * (ry * cos1));
            double c2x = ex - t * (cosP * (-rx * sin2) - sinP * (ry * cos2));
            double c2y = ey - t * (sinP * (-rx * sin2) + cosP * (ry * cos2));

            p.AddBezier((float)px, (float)py, (float)c1x, (float)c1y, (float)c2x, (float)c2y, (float)ex, (float)ey);
            px = ex; py = ey; ang = a2;
        }
    }

    private static (double, double) Point(double cx, double cy, double rx, double ry,
        double cosP, double sinP, double cosA, double sinA)
    {
        double x = rx * cosA, y = ry * sinA;
        return (cx + cosP * x - sinP * y, cy + sinP * x + cosP * y);
    }

    private static double Angle(double ux, double uy, double vx, double vy)
    {
        double dot = ux * vx + uy * vy;
        double len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        double a = Math.Acos(Math.Clamp(dot / len, -1, 1));
        return ux * vy - uy * vx < 0 ? -a : a;
    }

    private sealed class Reader(string s)
    {
        private int _i;

        public bool End => _i >= s.Length;
        public void Next() => _i++;

        private void SkipSep()
        {
            while (_i < s.Length && (s[_i] == ' ' || s[_i] == ',' || s[_i] == '\t' || s[_i] == '\n' || s[_i] == '\r'))
                _i++;
        }

        public char PeekCommand()
        {
            SkipSep();
            if (_i < s.Length && char.IsLetter(s[_i])) return s[_i];
            return '\0';
        }

        public int Flag()
        {
            SkipSep();
            char c = s[_i]; _i++;
            return c == '1' ? 1 : 0;
        }

        public float Num()
        {
            SkipSep();
            int start = _i;
            if (_i < s.Length && (s[_i] == '+' || s[_i] == '-')) _i++;
            bool dot = false;
            while (_i < s.Length)
            {
                char c = s[_i];
                if (c is >= '0' and <= '9') _i++;
                else if (c == '.') { if (dot) break; dot = true; _i++; }
                else if (c is 'e' or 'E') { _i++; if (_i < s.Length && (s[_i] == '+' || s[_i] == '-')) _i++; }
                else break;
            }
            return float.Parse(s[start.._i], CultureInfo.InvariantCulture);
        }
    }
}
