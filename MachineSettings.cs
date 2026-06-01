namespace LaserPCB.Models;

public class MachineSettings
{
    // Conexión
    public string Esp32Ip   { get; set; } = "192.168.1.100";
    public int    Esp32Port { get; set; } = 80;
    public int    PollIntervalMs { get; set; } = 500;
    public int    CommandTimeoutMs { get; set; } = 5000;

    // Parámetros láser
    public int   LaserPowerMax   { get; set; } = 1000;  // S max en GRBL
    public int   LaserPowerBurn  { get; set; } = 800;   // potencia grabado
    public float FeedRateBurn    { get; set; } = 800;   // mm/min grabado
    public float FeedRateRapid   { get; set; } = 3000;  // mm/min desplazamiento

    // Área de trabajo
    public float WorkAreaWidth  { get; set; } = 200;    // mm
    public float WorkAreaHeight { get; set; } = 200;    // mm

    // G-code
    public bool  UseRelativeMode { get; set; } = false;
    public float ZSafeHeight     { get; set; } = 0;     // sin eje Z en 2D

    public string BaseUrl => $"http://{Esp32Ip}:{Esp32Port}";
}
