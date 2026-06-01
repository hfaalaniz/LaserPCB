namespace LaserPCB.Models;

public enum MachineStatus
{
    Idle, Run, Hold, Jog, Alarm,
    Door, Check, Home, Sleep, Unknown, Disconnected
}

public class MachinePosition
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public override string ToString() => $"X:{X:F3} Y:{Y:F3} Z:{Z:F3}";
}

public class MachineState
{
    public MachineStatus Status      { get; set; } = MachineStatus.Disconnected;
    public MachinePosition WorkPos   { get; set; } = new();
    public MachinePosition MachinePos{ get; set; } = new();
    public int   LaserPower          { get; set; }   // 0–1000 (S value GRBL)
    public float FeedRate            { get; set; }
    public bool  IsConnected         { get; set; }
    public string RawStatus          { get; set; } = string.Empty;
    public DateTime LastUpdate       { get; set; } = DateTime.Now;

    // Parsea: <Idle|MPos:0.000,0.000,0.000|FS:0,0|WCO:0.000,0.000,0.000>
    public static MachineState Parse(string raw)
    {
        var state = new MachineState { RawStatus = raw, IsConnected = true, LastUpdate = DateTime.Now };
        if (string.IsNullOrWhiteSpace(raw) || !raw.StartsWith('<')) return state;

        var clean = raw.Trim('<', '>');
        var parts = clean.Split('|');

        state.Status = parts[0].Split(':')[0].Trim() switch
        {
            "Idle"  => MachineStatus.Idle,
            "Run"   => MachineStatus.Run,
            "Hold"  => MachineStatus.Hold,
            "Jog"   => MachineStatus.Jog,
            "Alarm" => MachineStatus.Alarm,
            "Door"  => MachineStatus.Door,
            "Check" => MachineStatus.Check,
            "Home"  => MachineStatus.Home,
            "Sleep" => MachineStatus.Sleep,
            _       => MachineStatus.Unknown
        };

        foreach (var part in parts)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;

            if (part.StartsWith("MPos:"))
                state.MachinePos = ParsePos(part[5..]);
            else if (part.StartsWith("WPos:"))
                state.WorkPos = ParsePos(part[5..]);
            else if (part.StartsWith("FS:"))
            {
                var fs = part[3..].Split(',');
                if (fs.Length >= 1 && float.TryParse(fs[0], System.Globalization.NumberStyles.Float, ci, out var f))
                    state.FeedRate = f;
                if (fs.Length >= 2 && int.TryParse(fs[1], out var s))
                    state.LaserPower = s;
            }
        }
        return state;
    }

    private static MachinePosition ParsePos(string raw)
    {
        var c  = raw.Split(',');
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var p  = new MachinePosition();
        if (c.Length >= 1) float.TryParse(c[0], System.Globalization.NumberStyles.Float, ci, out p.X);
        if (c.Length >= 2) float.TryParse(c[1], System.Globalization.NumberStyles.Float, ci, out p.Y);
        if (c.Length >= 3) float.TryParse(c[2], System.Globalization.NumberStyles.Float, ci, out p.Z);
        return p;
    }
}
