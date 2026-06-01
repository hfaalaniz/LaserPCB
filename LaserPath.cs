namespace LaserPCB.Models;

public record struct PointF2D(float X, float Y);

public class LaserSegment
{
    public PointF2D Start { get; set; }
    public PointF2D End   { get; set; }
    public bool IsRapid   { get; set; }  // true = G0 (sin láser), false = G1 (con láser)
}

public class LaserPath
{
    public List<LaserSegment> Segments { get; set; } = new();
    public float MinX { get; private set; }
    public float MinY { get; private set; }
    public float MaxX { get; private set; }
    public float MaxY { get; private set; }
    public float Width  => MaxX - MinX;
    public float Height => MaxY - MinY;

    public void RecalcBounds()
    {
        if (Segments.Count == 0) return;
        MinX = float.MaxValue; MinY = float.MaxValue;
        MaxX = float.MinValue; MaxY = float.MinValue;

        foreach (var seg in Segments)
        {
            foreach (var p in new[] { seg.Start, seg.End })
            {
                if (p.X < MinX) MinX = p.X;
                if (p.Y < MinY) MinY = p.Y;
                if (p.X > MaxX) MaxX = p.X;
                if (p.Y > MaxY) MaxY = p.Y;
            }
        }
    }
}
