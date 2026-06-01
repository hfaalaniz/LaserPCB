using System.Net.WebSockets;
using System.Text;
using LaserPCB.Models;

namespace LaserPCB.Communication;

/// <summary>
/// Cliente para GRBL_ESP32 firmware.
/// Endpoints usados:
///   GET  /command?commandText=...   → envía G-code / comandos GRBL
///   GET  /status                    → estado actual (texto GRBL)
///   GET  /SD/upload (multipart)     → sube archivo .gcode
///   WS   ws://ip/ws                 → stream de respuestas en tiempo real
/// </summary>
public class Esp32Client : IDisposable
{
    private readonly HttpClient _http;
    private readonly MachineSettings _settings;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;

    public event Action<MachineState>? StateUpdated;
    public event Action<string>?       ResponseReceived;
    public event Action<string>?       ErrorOccurred;
    public event Action<bool>?         ConnectionChanged;

    public MachineState CurrentState { get; private set; } = new();

    public Esp32Client(MachineSettings settings)
    {
        _settings = settings;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(settings.CommandTimeoutMs)
        };
    }

    // ─── Conexión ────────────────────────────────────────────────────────────

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            // Test HTTP
            var resp = await _http.GetAsync($"{_settings.BaseUrl}/", ct);
            if (!resp.IsSuccessStatusCode) return false;

            // WebSocket para respuestas en tiempo real
            await StartWebSocketAsync(ct);

            CurrentState = new MachineState { IsConnected = true, Status = MachineStatus.Idle };
            ConnectionChanged?.Invoke(true);
            return true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Connect error: {ex.Message}");
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        _wsCts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", CancellationToken.None);

        CurrentState = new MachineState { IsConnected = false, Status = MachineStatus.Disconnected };
        ConnectionChanged?.Invoke(false);
    }

    // ─── WebSocket ───────────────────────────────────────────────────────────

    private async Task StartWebSocketAsync(CancellationToken ct)
    {
        _wsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ws    = new ClientWebSocket();

        var wsUrl = $"ws://{_settings.Esp32Ip}:{_settings.Esp32Port}/ws";
        await _ws.ConnectAsync(new Uri(wsUrl), _wsCts.Token);

        _ = Task.Run(() => WebSocketListenLoopAsync(_wsCts.Token), _wsCts.Token);
    }

    private async Task WebSocketListenLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var sb     = new StringBuilder();

        try
        {
            while (_ws!.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    ConnectionChanged?.Invoke(false);
                    break;
                }

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (!result.EndOfMessage) continue;

                var msg = sb.ToString().Trim();
                sb.Clear();

                ProcessIncomingMessage(msg);
            }
        }
        catch (OperationCanceledException) { /* normal disconnect */ }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"WebSocket error: {ex.Message}");
            ConnectionChanged?.Invoke(false);
        }
    }

    private void ProcessIncomingMessage(string msg)
    {
        ResponseReceived?.Invoke(msg);

        if (msg.StartsWith('<') && msg.EndsWith('>'))
        {
            CurrentState = MachineState.Parse(msg);
            StateUpdated?.Invoke(CurrentState);
        }
        else if (msg.StartsWith("ALARM:"))
        {
            CurrentState.Status = MachineStatus.Alarm;
            StateUpdated?.Invoke(CurrentState);
            ErrorOccurred?.Invoke(msg);
        }
    }

    // ─── Comandos GRBL ───────────────────────────────────────────────────────

    /// <summary>Envía un comando G-code y espera "ok" o "error".</summary>
    public async Task<bool> SendCommandAsync(string command, CancellationToken ct = default)
    {
        try
        {
            var encoded = Uri.EscapeDataString(command.Trim());
            var url     = $"{_settings.BaseUrl}/command?commandText={encoded}";
            var resp    = await _http.GetAsync(url, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Command error '{command}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Solicita status inmediato (?) al ESP32.</summary>
    public async Task<MachineState> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var url  = $"{_settings.BaseUrl}/command?commandText=%3F"; // '?'
            var resp = await _http.GetAsync(url, ct);
            // La respuesta llega por WebSocket; retornamos el estado actual
            return CurrentState;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Status error: {ex.Message}");
            return CurrentState;
        }
    }

    // ─── Upload G-code completo ───────────────────────────────────────────────

    /// <summary>Sube un archivo .gcode a la SD/SPIFFS del ESP32.</summary>
    public async Task<bool> UploadGcodeAsync(string gcode, string filename = "job.gcode",
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var bytes   = Encoding.UTF8.GetBytes(gcode);
            var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(bytes), "file", filename);

            // Progreso manual (no hay streaming nativo en este endpoint)
            progress?.Report(0);
            var url  = $"{_settings.BaseUrl}/SD/upload";
            var resp = await _http.PostAsync(url, content, ct);
            progress?.Report(100);

            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Upload error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Ordena al ESP32 ejecutar el archivo subido.</summary>
    public Task<bool> RunFileAsync(string filename = "job.gcode", CancellationToken ct = default)
        => SendCommandAsync($"[ESP220]/SD/{filename}", ct);

    // ─── Comandos de máquina ─────────────────────────────────────────────────

    public Task<bool> HomeAsync(CancellationToken ct = default)
        => SendCommandAsync("$H", ct);

    public Task<bool> UnlockAsync(CancellationToken ct = default)
        => SendCommandAsync("$X", ct);

    public Task<bool> SoftResetAsync(CancellationToken ct = default)
        => SendCommandAsync("\x18", ct);   // Ctrl+X

    public Task<bool> FeedHoldAsync(CancellationToken ct = default)
        => SendCommandAsync("!", ct);

    public Task<bool> ResumeAsync(CancellationToken ct = default)
        => SendCommandAsync("~", ct);

    /// <summary>Laser OFF inmediato (emergencia).</summary>
    public async Task EmergencyStopAsync()
    {
        await SendCommandAsync("\x18");    // soft reset
        await SendCommandAsync("M5");      // laser off
    }

    public Task<bool> SetWorkOriginAsync(CancellationToken ct = default)
        => SendCommandAsync("G92 X0 Y0", ct);

    // ─── Jog ────────────────────────────────────────────────────────────────

    public Task<bool> JogAsync(float x, float y, float feedRate, CancellationToken ct = default)
        => SendCommandAsync($"$J=G21 G91 X{x:F3} Y{y:F3} F{feedRate:F0}", ct);

    public Task<bool> JogAbsoluteAsync(float x, float y, float feedRate, CancellationToken ct = default)
        => SendCommandAsync($"$J=G21 G90 X{x:F3} Y{y:F3} F{feedRate:F0}", ct);

    public Task<bool> JogCancelAsync(CancellationToken ct = default)
        => SendCommandAsync("\x85", ct);   // 0x85 = jog cancel

    // ─── Laser manual ───────────────────────────────────────────────────────

    public Task<bool> LaserOnAsync(int power, CancellationToken ct = default)
        => SendCommandAsync($"M3 S{power}", ct);

    public Task<bool> LaserOffAsync(CancellationToken ct = default)
        => SendCommandAsync("M5", ct);

    // ─── Configuración GRBL ──────────────────────────────────────────────────

    public async Task<Dictionary<string, string>> GetGrblSettingsAsync(CancellationToken ct = default)
    {
        var settings = new Dictionary<string, string>();
        await SendCommandAsync("$$", ct);
        // Los settings llegan por WebSocket como $N=valor
        // El caller debe escuchar ResponseReceived para capturarlos
        return settings;
    }

    public Task<bool> SetGrblSettingAsync(int param, float value, CancellationToken ct = default)
        => SendCommandAsync($"${param}={value:F3}", ct);

    // ─── Streaming G-code línea a línea ─────────────────────────────────────

    /// <summary>
    /// Envía G-code línea a línea esperando "ok" entre cada una.
    /// Más lento que upload pero no requiere SD card.
    /// </summary>
    public async Task StreamGcodeAsync(IEnumerable<string> lines,
        IProgress<(int current, int total)>? progress = null,
        CancellationToken ct = default)
    {
        var lineList = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var total    = lineList.Count;

        // Habilitar modo láser dinámico
        await SendCommandAsync("$32=1", ct);

        for (int i = 0; i < total && !ct.IsCancellationRequested; i++)
        {
            var sent = await SendCommandAsync(lineList[i], ct);
            if (!sent) break;

            progress?.Report((i + 1, total));

            // Throttle mínimo para no saturar el buffer del ESP32
            await Task.Delay(10, ct);
        }
    }

    public void Dispose()
    {
        _wsCts?.Cancel();
        _ws?.Dispose();
        _http.Dispose();
    }
}
