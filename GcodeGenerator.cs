using System.Globalization;
using System.Text;
using LaserPCB.Models;

namespace LaserPCB.Core;

/// <summary>
/// Convierte LaserPath a G-code compatible con GRBL 1.1 + laser mode ($32=1).
/// </summary>
public class GcodeGenerator
{
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    public GcodeGeneratorOptions Options { get; set; } = new();

    public string Generate(LaserPath path, MachineSettings settings)
    {
        var sb = new StringBuilder();
        var f  = settings.FeedRateBurn;
        var fr = settings.FeedRateRapid;
        var s  = settings.LaserPowerBurn;

        // ── Header ─────────────────────────────────────────────────────────
        sb.AppendLine("; LaserPCB - G-code generado automáticamente");
        sb.AppendLine($"; Segmentos: {path.Segments.Count}");
        sb.AppendLine($"; Área: {path.Width:F2} x {path.Height:F2} mm");
        sb.AppendLine($"; Feed burn: {f} mm/min | Potencia: {s}/1000");
        sb.AppendLine();

        // ── Inicialización ──────────────────────────────────────────────────
        sb.AppendLine("G21        ; mm");
        sb.AppendLine("G90        ; coordenadas absolutas");
        sb.AppendLine("G94        ; feed por minuto");
        sb.AppendLine("$32=1      ; laser mode ON");
        sb.AppendLine("M5         ; laser OFF al inicio");
        sb.AppendLine($"G0 F{fr:F0}");
        sb.AppendLine("G0 X0 Y0   ; ir al origen");
        sb.AppendLine();

        // ── Cuerpo ──────────────────────────────────────────────────────────
        bool laserOn   = false;
        bool firstBurn = true;

        foreach (var seg in path.Segments)
        {
            if (seg.IsRapid)
            {
                // Apagar láser antes de moverse en rápido
                if (laserOn)
                {
                    sb.AppendLine("M5");
                    laserOn = false;
                }
                sb.AppendLine($"G0 X{F(seg.End.X)} Y{F(seg.End.Y)}");
            }
            else
            {
                // Encender láser antes del primer burn
                if (!laserOn)
                {
                    if (firstBurn)
                    {
                        sb.AppendLine($"G1 F{f:F0}");
                        firstBurn = false;
                    }
                    sb.AppendLine($"M3 S{s}");
                    laserOn = true;
                }
                sb.AppendLine($"G1 X{F(seg.End.X)} Y{F(seg.End.Y)}");
            }
        }

        // ── Footer ──────────────────────────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine("M5         ; laser OFF");
        sb.AppendLine("G0 X0 Y0   ; volver al origen");
        sb.AppendLine("M30        ; fin de programa");

        return sb.ToString();
    }

    /// <summary>
    /// Optimización simple: reordena segmentos para minimizar desplazamientos en vacío.
    /// Algoritmo greedy nearest-neighbor.
    /// </summary>
    public LaserPath OptimizePath(LaserPath original)
    {
        // Agrupar segmentos burn consecutivos (chains)
        var chains = ExtractChains(original.Segments);
        var ordered = GreedyOrderChains(chains);

        var optimized = new LaserPath();
        float cx = 0, cy = 0;

        foreach (var chain in ordered)
        {
            // Rapid al inicio del chain
            optimized.Segments.Add(new LaserSegment
            {
                Start   = new(cx, cy),
                End     = chain[0].Start,
                IsRapid = true
            });

            optimized.Segments.AddRange(chain);
            cx = chain[^1].End.X;
            cy = chain[^1].End.Y;
        }

        optimized.RecalcBounds();
        return optimized;
    }

    // ─── Helpers privados ────────────────────────────────────────────────

    private static List<List<LaserSegment>> ExtractChains(List<LaserSegment> segments)
    {
        var chains  = new List<List<LaserSegment>>();
        var current = new List<LaserSegment>();

        foreach (var seg in segments)
        {
            if (seg.IsRapid)
            {
                if (current.Count > 0) { chains.Add(current); current = new(); }
            }
            else
            {
                current.Add(seg);
            }
        }
        if (current.Count > 0) chains.Add(current);
        return chains;
    }

    private static List<List<LaserSegment>> GreedyOrderChains(List<List<LaserSegment>> chains)
    {
        var remaining = new List<List<LaserSegment>>(chains);
        var ordered   = new List<List<LaserSegment>>();
        float cx = 0, cy = 0;

        while (remaining.Count > 0)
        {
            // Buscar chain cuyo inicio esté más cerca de la posición actual
            int   bestIdx  = 0;
            float bestDist = float.MaxValue;
            bool  bestFlip = false;

            for (int i = 0; i < remaining.Count; i++)
            {
                var chain  = remaining[i];
                var dStart = Dist(cx, cy, chain[0].Start.X, chain[0].Start.Y);
                var dEnd   = Dist(cx, cy, chain[^1].End.X, chain[^1].End.Y);

                if (dStart < bestDist) { bestDist = dStart; bestIdx = i; bestFlip = false; }
                if (dEnd   < bestDist) { bestDist = dEnd;   bestIdx = i; bestFlip = true;  }
            }

            var best = remaining[bestIdx];
            if (bestFlip) best = FlipChain(best);

            ordered.Add(best);
            cx = best[^1].End.X;
            cy = best[^1].End.Y;
            remaining.RemoveAt(bestIdx);
        }

        return ordered;
    }

    private static List<LaserSegment> FlipChain(List<LaserSegment> chain) =>
        chain.Select(s => new LaserSegment
        {
            Start   = s.End,
            End     = s.Start,
            IsRapid = s.IsRapid
        }).Reverse().ToList();

    private static float Dist(float x1, float y1, float x2, float y2)
    {
        var dx = x2 - x1; var dy = y2 - y1;
        return dx * dx + dy * dy;  // no necesitamos sqrt para comparar
    }

    private static string F(float v) => v.ToString("F4", CI);
}

public class GcodeGeneratorOptions
{
    public bool OptimizePaths { get; set; } = true;
    public bool AddComments   { get; set; } = true;
}
