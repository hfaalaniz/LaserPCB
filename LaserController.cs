using LaserPCB.Communication;
using LaserPCB.Models;

namespace LaserPCB.Core;

/// <summary>
/// Orquestador principal. Une Parser → Generator → Esp32Client.
/// Usar desde UI (WinForms/MAUI).
/// </summary>
public class LaserController : IDisposable
{
    private readonly SvgParser       _parser    = new();
    private readonly GcodeGenerator  _generator = new();
    public  readonly Esp32Client     Machine;
    public  readonly MachineSettings Settings;

    public string? CurrentGcode     { get; private set; }
    public LaserPath? CurrentPath   { get; private set; }
    public bool IsRunning           { get; private set; }

    public event Action<string>?           GcodeGenerated;
    public event Action<(int cur, int tot)>? JobProgress;
    public event Action<bool>?             JobCompleted;

    public LaserController(MachineSettings settings)
    {
        Settings = settings;
        Machine  = new Esp32Client(settings);
    }

    // ─── Flujo completo ──────────────────────────────────────────────────

    /// <summary>
    /// 1. Parsea SVG
    /// 2. Genera G-code optimizado
    /// 3. Sube al ESP32
    /// 4. Ejecuta
    /// </summary>
    public async Task<JobResult> RunFromSvgAsync(string svgContent,
        CancellationToken ct = default)
    {
        // 1. Parse
        var parseResult = _parser.Parse(svgContent);
        if (!parseResult.Success)
            return JobResult.Fail($"Parse error: {parseResult.Error}");

        CurrentPath = parseResult.Path;

        // 2. Optimizar y generar G-code
        if (_generator.Options.OptimizePaths)
            CurrentPath = _generator.OptimizePath(CurrentPath);

        CurrentGcode = _generator.Generate(CurrentPath, Settings);
        GcodeGenerated?.Invoke(CurrentGcode);

        // 3. Upload + run
        return await RunGcodeAsync(CurrentGcode, ct);
    }

    /// <summary>Envía G-code crudo y lo ejecuta.</summary>
    public async Task<JobResult> RunGcodeAsync(string gcode, CancellationToken ct = default)
    {
        if (!Machine.CurrentState.IsConnected)
            return JobResult.Fail("No conectado al ESP32");

        IsRunning = true;
        try
        {
            var lines    = gcode.Split('\n');
            var progress = new Progress<(int, int)>(p => JobProgress?.Invoke(p));

            await Machine.StreamGcodeAsync(lines, progress, ct);

            JobCompleted?.Invoke(true);
            return JobResult.Ok();
        }
        catch (OperationCanceledException)
        {
            await Machine.FeedHoldAsync();
            JobCompleted?.Invoke(false);
            return JobResult.Fail("Trabajo cancelado");
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Pausa / reanuda el trabajo en curso.</summary>
    public Task TogglePauseAsync() =>
        Machine.CurrentState.Status == MachineStatus.Hold
            ? Machine.ResumeAsync()
            : Machine.FeedHoldAsync();

    /// <summary>Para todo inmediatamente.</summary>
    public async Task EmergencyStopAsync()
    {
        await Machine.EmergencyStopAsync();
        IsRunning = false;
        JobCompleted?.Invoke(false);
    }

    public void Dispose() => Machine.Dispose();
}

public record JobResult(bool Success, string Message)
{
    public static JobResult Ok()              => new(true,  string.Empty);
    public static JobResult Fail(string msg)  => new(false, msg);
}
