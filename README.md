# LaserPCB — C# Control Software

## Estructura
```
LaserPCB/
├── Models/
│   ├── MachineState.cs      ← Estado GRBL (parse de respuestas)
│   ├── MachineSettings.cs   ← Config IP, potencia, feed rates
│   └── LaserPath.cs         ← Segmentos de trazado
├── Communication/
│   └── Esp32Client.cs       ← HTTP + WebSocket con GRBL_ESP32
└── Core/
    ├── SvgParser.cs         ← Lee SVG de KiCad → LaserPath
    ├── GcodeGenerator.cs    ← LaserPath → G-code GRBL
    └── LaserController.cs   ← Orquestador principal
```

## Setup ESP32
1. Flashear **GRBL_ESP32**: https://github.com/bdring/Grbl_Esp32
2. Configurar WiFi en `config/`
3. Verificar que responde en `http://192.168.1.100/`

## Uso básico desde WinForms

```csharp
// Program.cs / Form1.cs

var settings = new MachineSettings
{
    Esp32Ip        = "192.168.1.100",
    LaserPowerBurn = 800,
    FeedRateBurn   = 800,
    FeedRateRapid  = 3000
};

var controller = new LaserController(settings);

// Eventos
controller.Machine.StateUpdated      += state => UpdateStatusBar(state);
controller.Machine.ConnectionChanged += connected => UpdateUI(connected);
controller.Machine.ErrorOccurred     += err => ShowError(err);
controller.JobProgress               += (cur, tot) => progressBar.Value = cur * 100 / tot;

// Conectar
bool ok = await controller.Machine.ConnectAsync();

// Correr desde SVG
var svg = File.ReadAllText("board.svg");
var result = await controller.RunFromSvgAsync(svg);

if (!result.Success)
    MessageBox.Show(result.Message);

// Jog manual
await controller.Machine.JogAsync(x: 10, y: 0, feedRate: 500);

// Emergencia
await controller.EmergencyStopAsync();
```

## Flujo KiCad → PCB
1. KiCad → **File → Plot → SVG** (solo capa F.Cu o B.Cu)
2. Pintar placa con spray negro mate
3. Cargar SVG en la app
4. Ajustar potencia (800/1000) y velocidad (800 mm/min)
5. Hacer Home ($H) y setear origen de trabajo
6. Run → láser graba la pintura
7. Sumergir en FeCl3 (cloruro férrico) ~15-20 min
8. Limpiar con acetona

## Parámetros recomendados (diodo 5.5W)
| Material       | Potencia | Velocidad |
|----------------|----------|-----------|
| Spray negro    | 700-800  | 800 mm/min|
| Marcador negro | 500-600  | 1000 mm/min|
