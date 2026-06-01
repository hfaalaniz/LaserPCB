using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LaserPCB.Models;

namespace LaserPCB.Core;

/// <summary>
/// Parsea SVG exportado por KiCad (File → Plot → SVG).
/// Extrae trazos de cobre como segmentos de línea para el láser.
/// Soporta: line, polyline, path (M/L/H/V/Z), rect, circle.
/// </summary>
public class SvgParser
{
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    // Escala: KiCad exporta en px (96 dpi). 1 mm = 3.7795 px
    private const float PX_TO_MM = 1f / 3.7795f;

    public SvgParseResult Parse(string svgContent)
    {
        var result = new SvgParseResult();

        try
        {
            var doc  = XDocument.Parse(svgContent);
            var ns   = doc.Root!.Name.Namespace;

            // Leer viewBox para escala real
            var scale = GetScaleFromViewBox(doc.Root, ns);

            // Recolectar todos los elementos gráficos
            var elements = doc.Root
                .Descendants()
                .Where(e => e.Name.LocalName is "line" or "polyline" or "path" or "rect" or "circle");

            foreach (var el in elements)
            {
                var segs = el.Name.LocalName switch
                {
                    "line"     => ParseLine(el, scale),
                    "polyline" => ParsePolyline(el, scale),
                    "path"     => ParsePath(el, scale),
                    "rect"     => ParseRect(el, scale),
                    "circle"   => ParseCircle(el, scale),
                    _          => Enumerable.Empty<LaserSegment>()
                };

                result.Path.Segments.AddRange(segs);
            }

            result.Path.RecalcBounds();
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    // ─── Escala desde viewBox ─────────────────────────────────────────────

    private static float GetScaleFromViewBox(XElement root, XNamespace ns)
    {
        var vb = root.Attribute("viewBox")?.Value;
        if (string.IsNullOrEmpty(vb)) return PX_TO_MM;

        var parts = vb.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return PX_TO_MM;

        // width del SVG en mm (si tiene unidad) vs viewBox width en px
        var widthAttr = root.Attribute("width")?.Value ?? "";
        if (widthAttr.EndsWith("mm") &&
            float.TryParse(widthAttr.Replace("mm", ""), NumberStyles.Float, CI, out var widthMm) &&
            float.TryParse(parts[2], NumberStyles.Float, CI, out var viewW) &&
            viewW > 0)
        {
            return widthMm / viewW;
        }

        return PX_TO_MM;
    }

    // ─── Parsers por elemento ─────────────────────────────────────────────

    private static IEnumerable<LaserSegment> ParseLine(XElement el, float scale)
    {
        var x1 = Attr(el, "x1") * scale;
        var y1 = Attr(el, "y1") * scale;
        var x2 = Attr(el, "x2") * scale;
        var y2 = Attr(el, "y2") * scale;

        yield return Rapid(0, 0, x1, y1);
        yield return Burn(x1, y1, x2, y2);
    }

    private static IEnumerable<LaserSegment> ParsePolyline(XElement el, float scale)
    {
        var pts = ParsePointsList(el.Attribute("points")?.Value ?? "", scale);
        return PointsToSegments(pts);
    }

    private static IEnumerable<LaserSegment> ParseRect(XElement el, float scale)
    {
        var x = Attr(el, "x") * scale;
        var y = Attr(el, "y") * scale;
        var w = Attr(el, "width") * scale;
        var h = Attr(el, "height") * scale;

        var corners = new[]
        {
            new PointF2D(x,     y),
            new PointF2D(x + w, y),
            new PointF2D(x + w, y + h),
            new PointF2D(x,     y + h),
            new PointF2D(x,     y)     // cierra
        };

        return PointsToSegments(corners);
    }

    private static IEnumerable<LaserSegment> ParseCircle(XElement el, float scale)
    {
        var cx = Attr(el, "cx") * scale;
        var cy = Attr(el, "cy") * scale;
        var r  = Attr(el, "r")  * scale;

        // Aproximar círculo con 32 segmentos
        const int STEPS = 32;
        var pts = new PointF2D[STEPS + 1];
        for (int i = 0; i <= STEPS; i++)
        {
            var angle = 2 * Math.PI * i / STEPS;
            pts[i] = new PointF2D(cx + r * (float)Math.Cos(angle),
                                  cy + r * (float)Math.Sin(angle));
        }
        return PointsToSegments(pts);
    }

    // ─── Path parser (subconjunto SVG) ───────────────────────────────────

    private static IEnumerable<LaserSegment> ParsePath(XElement el, float scale)
    {
        var d = el.Attribute("d")?.Value ?? "";
        if (string.IsNullOrEmpty(d)) yield break;

        var segments = new List<LaserSegment>();
        float cx = 0, cy = 0, startX = 0, startY = 0;
        bool penDown = false;

        // Tokenizar: letras y números
        var tokens = Regex.Matches(d, @"[MmLlHhVvZzCcSsQqTtAa]|[-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?");
        var queue  = new Queue<string>(tokens.Cast<Match>().Select(m => m.Value));

        while (queue.Count > 0)
        {
            var cmd = queue.Dequeue();

            switch (cmd)
            {
                case "M":
                case "m":
                {
                    bool rel = cmd == "m";
                    var x = NextF(queue) * scale;
                    var y = NextF(queue) * scale;
                    cx = rel ? cx + x : x;
                    cy = rel ? cy + y : y;
                    startX = cx; startY = cy;
                    penDown = false;
                    break;
                }
                case "L":
                case "l":
                {
                    bool rel = cmd == "l";
                    while (queue.Count >= 2 && IsNumber(queue.Peek()))
                    {
                        var x = NextF(queue) * scale;
                        var y = NextF(queue) * scale;
                        var nx = rel ? cx + x : x;
                        var ny = rel ? cy + y : y;
                        segments.Add(penDown
                            ? Burn(cx, cy, nx, ny)
                            : Rapid(cx, cy, nx, ny));
                        cx = nx; cy = ny;
                        penDown = true;
                    }
                    break;
                }
                case "H":
                case "h":
                {
                    bool rel = cmd == "h";
                    var x  = NextF(queue) * scale;
                    var nx = rel ? cx + x : x;
                    segments.Add(Burn(cx, cy, nx, cy));
                    cx = nx; penDown = true;
                    break;
                }
                case "V":
                case "v":
                {
                    bool rel = cmd == "v";
                    var y  = NextF(queue) * scale;
                    var ny = rel ? cy + y : y;
                    segments.Add(Burn(cx, cy, cx, ny));
                    cy = ny; penDown = true;
                    break;
                }
                case "Z":
                case "z":
                    if (penDown)
                        segments.Add(Burn(cx, cy, startX, startY));
                    cx = startX; cy = startY;
                    penDown = false;
                    break;

                // Curvas cúbicas: aproximar con líneas (4 puntos de control)
                case "C":
                case "c":
                {
                    bool rel = cmd == "c";
                    while (queue.Count >= 6 && IsNumber(queue.Peek()))
                    {
                        float x1 = NextF(queue) * scale, y1 = NextF(queue) * scale;
                        float x2 = NextF(queue) * scale, y2 = NextF(queue) * scale;
                        float x  = NextF(queue) * scale, y  = NextF(queue) * scale;
                        if (rel) { x1+=cx; y1+=cy; x2+=cx; y2+=cy; x+=cx; y+=cy; }
                        var pts = BezierToPoints(
                            new PointF2D(cx, cy),
                            new PointF2D(x1, y1),
                            new PointF2D(x2, y2),
                            new PointF2D(x,  y), 12);
                        segments.AddRange(PointsToSegments(pts));
                        cx = x; cy = y; penDown = true;
                    }
                    break;
                }

                default:
                    // Número suelto después de M = L implícito
                    if (IsNumber(cmd) && queue.Count >= 1)
                    {
                        var x = float.Parse(cmd, CI) * scale;
                        var y = NextF(queue) * scale;
                        segments.Add(Burn(cx, cy, x, y));
                        cx = x; cy = y; penDown = true;
                    }
                    break;
            }
        }

        foreach (var s in segments) yield return s;
    }

    // ─── Bezier → puntos ────────────────────────────────────────────────

    private static PointF2D[] BezierToPoints(PointF2D p0, PointF2D p1, PointF2D p2, PointF2D p3, int steps)
    {
        var pts = new PointF2D[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            float t  = i / (float)steps;
            float t2 = t * t, t3 = t2 * t;
            float u  = 1 - t, u2 = u * u, u3 = u2 * u;
            pts[i] = new PointF2D(
                u3 * p0.X + 3 * u2 * t * p1.X + 3 * u * t2 * p2.X + t3 * p3.X,
                u3 * p0.Y + 3 * u2 * t * p1.Y + 3 * u * t2 * p2.Y + t3 * p3.Y);
        }
        return pts;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static LaserSegment Rapid(float x1, float y1, float x2, float y2) =>
        new() { Start = new(x1, y1), End = new(x2, y2), IsRapid = true };

    private static LaserSegment Burn(float x1, float y1, float x2, float y2) =>
        new() { Start = new(x1, y1), End = new(x2, y2), IsRapid = false };

    private static IEnumerable<LaserSegment> PointsToSegments(IEnumerable<PointF2D> pts)
    {
        var list = pts.ToList();
        if (list.Count < 2) yield break;
        yield return Rapid(0, 0, list[0].X, list[0].Y);
        for (int i = 0; i < list.Count - 1; i++)
            yield return Burn(list[i].X, list[i].Y, list[i+1].X, list[i+1].Y);
    }

    private static PointF2D[] ParsePointsList(string raw, float scale)
    {
        var nums = Regex.Matches(raw, @"[-+]?[0-9]*\.?[0-9]+")
                        .Select(m => float.Parse(m.Value, CI) * scale)
                        .ToArray();
        var pts = new PointF2D[nums.Length / 2];
        for (int i = 0; i < pts.Length; i++)
            pts[i] = new PointF2D(nums[i * 2], nums[i * 2 + 1]);
        return pts;
    }

    private static float Attr(XElement el, string name)
        => float.TryParse(el.Attribute(name)?.Value ?? "0",
            NumberStyles.Float, CI, out var v) ? v : 0;

    private static float NextF(Queue<string> q)
        => q.Count > 0 && float.TryParse(q.Dequeue(), NumberStyles.Float, CI, out var v) ? v : 0;

    private static bool IsNumber(string s)
        => s.Length > 0 && (char.IsDigit(s[0]) || s[0] == '-' || s[0] == '.');
}

public class SvgParseResult
{
    public LaserPath Path    { get; set; } = new();
    public bool      Success { get; set; }
    public string    Error   { get; set; } = string.Empty;
    public int       SegmentCount => Path.Segments.Count;
}
