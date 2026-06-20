# PROMPT INICIAL — SmoothTrendAI
> Archivo de referencia para continuar el desarrollo con cualquier modelo de IA.
> Última actualización: 2026-06-20

---

## Instrucciones de uso con otro modelo

1. Pegar este archivo completo al inicio de la conversación.
2. Indicar la tarea específica a resolver.
3. El modelo tendrá suficiente contexto para continuar sin releer el código fuente.

---

## CONTEXTO DEL PROYECTO

Proyecto: **SmoothTrendAI** — sistema de trading algorítmico para NinjaTrader 8.
Desarrollo **original e independiente**. No usa ni referencia código de terceros
(SiomTrading, AlgoLab ni ningún otro paquete comercial).
Los archivos son propios; prefijo `STA` en todas las clases helper para evitar conflictos.

### Plataforma
- NinjaTrader 8 / NinjaScript / .NET Framework 4.8 / C# 7.x
- `Calculate.OnBarClose` en indicador y estrategia
- Instrumento objetivo: NQ futuros (100 Range Bars)

---

## ARQUITECTURA — 8 archivos

```
IASMT/SmoothTrendAI/
├── Indicators/
│   └── SmoothTrendCloud.cs        ← Indicador principal (doble EMA + IA + dashboard)
└── Strategies/
    ├── STADailyContextFilter.cs   ← Filtro contexto diario (Kaufman)
    ├── STAElliottWaveContext.cs   ← Ondas de Elliott simplificadas
    ├── STASetupClassifier.cs      ← Orquestador de setups (Tipo 1 y 2)
    ├── STATradeJournal.cs         ← Logging CSV de trades y rechazos
    ├── STASignalValidator.cs      ← Llamadas HTTP a Claude / OpenAI (estrategia)
    ├── STARiskManager.cs          ← Gestión de riesgo y límites diarios
    └── SmoothTrendAI.cs           ← Estrategia principal
```

### Rutas de instalación en NT8
```
Documents\NinjaTrader 8\bin\Custom\Indicators\SmoothTrendCloud.cs
Documents\NinjaTrader 8\bin\Custom\Strategies\STA*.cs
Documents\NinjaTrader 8\bin\Custom\Strategies\SmoothTrendAI.cs
```

---

## MÓDULO 1 — SmoothTrendCloud.cs (Indicador)

### Lógica de la nube
```
TriggerLine     = EMA(Close, TriggerPeriod=12)
SmoothTrendLine = EMA(TriggerLine, SmoothPeriod=12)   ← EMA del EMA
```
La nube es alcista cuando `TriggerLine > SmoothTrendLine`.

### Señales expuestas (propiedades de solo lectura)
| Propiedad | Tipo | Descripción |
|---|---|---|
| `HasCrossUpSignal` | bool | Cruce alcista este bar (Tipo 1) |
| `HasCrossDownSignal` | bool | Cruce bajista este bar (Tipo 1) |
| `TouchedCloudFromAbove` | bool | Precio tocó nube sin cruzar en uptrend (Tipo 2 LONG) |
| `TouchedCloudFromBelow` | bool | Precio tocó nube sin cruzar en downtrend (Tipo 2 SHORT) |
| `BarsInCurrentColor` | int | Barras consecutivas en el mismo color |
| `CloudWidth` | double | Distancia entre TriggerLine y SmoothTrendLine |
| `CurrentCloudColor` | string | "Up" o "Down" |
| `TriggerValue` / `SmoothValue` | double | Valores actuales de cada línea |

### Visualización de la nube
El relleno entre las dos líneas EMA se dibuja con `Draw.Region` segmentado:
- Cada segmento de color se cierra con tag permanente `CS_{startBar}` al cambiar color
- El segmento actual se redibuja cada barra con tag `CS_Current`
- Segmentos > `SEGMENT_LOOKBACK (300)` barras se eliminan con `RemoveDrawObject`
- `BackBrushes[0]` tinta el fondo de cada barra con el color de la nube

### Propiedades configurables del indicador

**Grupo "Parámetros":**
| Propiedad | Default | Descripción |
|---|---|---|
| `TriggerPeriod` | 12 | Período del primer EMA |
| `SmoothPeriod` | 12 | Período del segundo EMA (EMA del EMA) |
| `MinTrendBarsForPullback` | 8 | Mínimo barras en color para validar Tipo 2 |

**Grupo "Visualización":**
| Propiedad | Default | Descripción |
|---|---|---|
| `RegionOpacity` | 10 | Opacidad del relleno (1–100) |
| `UpTrendColor` | Cyan | Color de nube alcista |
| `DownTrendColor` | Orange | Color de nube bajista |
| `ShowSignalArrows` | true | Flechas en cruces Tipo 1 |

**Grupo "3. Validación IA":**
| Propiedad | Default | Descripción |
|---|---|---|
| `ShowType2Arrows` | true | Flechas para señales Tipo 2 (CloudPullback) |
| `EnableAIValidation` | false | Llamar IA para clasificar cada señal Tipo 2 |
| `AIProvider` | "Claude" | Selector dropdown: "Claude" u "OpenAI" |
| `AIApiKey` | "" | Clave de API del proveedor |
| `AIMinConfidence` | 0.60 | Confianza mínima para mostrar flecha aprobada |
| `EnableAlerts` | true | Beep + alerta NT8 al detectar señales |
| `ShowDashboard` | true | Panel de estado en esquina superior derecha |
| `UseM15Confluence` | false | Filtro: M15 EMA-20 debe estar alineada con la señal |
| `UseTimeFilter` | false | Solo señales en 10:00–11:30 y 14:00–15:30 hora del chart |
| `LogRejectedSignals` | true | CSV con cada señal bloqueada (horario / M15 / IA) |

**Grupo "4. Niveles de Entrada":**
| Propiedad | Default | Descripción |
|---|---|---|
| `ShowEntryLevels` | true | Dibuja líneas Entrada/Stop/TP1/TP2 al detectar señal |
| `LevelStopBufferTicks` | 3 | Ticks de buffer bajo el mínimo (LONG) / sobre el máximo (SHORT) |
| `LevelTP1Ratio` | 2.0 | TP1 = distancia_stop × ratio (2.0 = 2R) |
| `LevelTP2Ratio` | 4.0 | TP2 = distancia_stop × ratio (4.0 = 4R) |

**Orden de declaración en el archivo (CRÍTICO para factory call):**
```
1:TriggerPeriod  2:SmoothPeriod  3:MinTrendBarsForPullback
4:RegionOpacity  5:UpTrendColor  6:DownTrendColor  7:ShowSignalArrows
8:ShowType2Arrows  9:EnableAIValidation  10:AIProvider  11:AIApiKey
12:AIMinConfidence  13:EnableAlerts  14:ShowDashboard  15:UseM15Confluence
16:UseTimeFilter  17:LogRejectedSignals
18:ShowEntryLevels  19:LevelStopBufferTicks  20:LevelTP1Ratio  21:LevelTP2Ratio
```

### Niveles de entrada visuales (ShowEntryLevels)

Al detectar señal (solo en tiempo real) dibuja 4 líneas horizontales de 5 barras de ancho:
- `NLE_{bar}` — ENTRADA (blanco) con precio
- `NLS_{bar}` — STOP (naranja-rojo) con precio y ticks de distancia
- `NLT1_{bar}` — TP1 (lima) con precio, ticks y "1R"
- `NLT2_{bar}` — TP2 (verde claro) con precio, ticks y "2R"

**Cálculo del stop:**
```
distVela = entry - (Low[0] - LevelStopBufferTicks * TickSize)    # LONG
stopDist = Max(distVela, Max(ATR(14) * 1.3, 6 * TickSize))       # piso mínimo
stop     = entry - stopDist
TP1      = entry + stopDist * LevelTP1Ratio
TP2      = entry + stopDist * LevelTP2Ratio
```

**Limpieza automática:** `Queue<int> _oldLevelBars` elimina niveles con más de
`LEVEL_LOOKBACK = 100` barras de antigüedad en cada `OnBarUpdate`.

### Log de señales rechazadas (LogRejectedSignals)

Archivo: `Documentos\NinjaTrader 8\STC_rechazadas_YYYYMMDD.csv`
Solo escribe en tiempo real (`_isRealtime`).

Columnas: `Timestamp, Instrumento, Direccion, Tipo, Razon, BarrasEnColor, NubeTicks, RSI, Sesion, Detalle`

Valores de `Razon`:
- `Horario` — señal fuera de ventanas de calidad
- `M15` — confluencia M15 no alineada
- `IA` — modelo rechazó con detalle `conf=74% razón`

### Filtro de horario (UseTimeFilter)

Método `EsHorarioCalidad()` — devuelve true si `Time[0]` cae en:
- 10:00–11:30 (apertura madura NY)
- 14:00–15:30 (tarde NY, momentum claro)

Usa hora local del chart. Si el PC está en Colombia (UTC-5 = EST en invierno), coincide directamente con ET.

### Validación IA en el indicador (trading manual)

El indicador tiene **su propio cliente HTTP** (`static HttpClient _http`) independiente de la estrategia.

**Flujo al detectar Tipo 2:**
1. Si `EnableAIValidation = false` o barra histórica → flecha inmediata (lima/magenta)
2. Si `EnableAIValidation = true` y es barra en tiempo real:
   - Dibuja punto gris ("esperando...")
   - Lanza `Task.Run` en hilo de fondo
   - `ValidarConIAAsync()` llama a Claude Haiku o GPT-4o-mini
   - Al recibir respuesta, actualiza en UI thread con `ChartControl.Dispatcher.InvokeAsync()`
   - Aprobado: flecha verde + texto `"IA 74%\nrazón"` + niveles de entrada
   - Rechazado: texto gris `"✗ razón"` + entrada en CSV de rechazadas

**CRÍTICO — threading:** capturar TODOS los datos de la barra como variables locales
ANTES del `Task.Run()`. Dentro del lambda solo usar variables capturadas, nunca `Close[0]`.
Esto incluye: `closeP`, `highP`, `lowP`, `rsi`, `atrTks`, `hora`, `atrPrice`, `tickSz`,
`tp1Ratio`, `tp2Ratio`, `stopBuff`, `tsLog`, `sesLog`, `logRej`, `logPath`.

**Modelos usados:**
- Claude: `claude-haiku-4-5-20251001` (indicador — rápido, bajo costo)
- OpenAI: `gpt-4o-mini`

**Payload:**
```json
{
  "direction": "LONG", "sesion": "apertura-NY", "calidad_vela": "rechazo-fuerte",
  "cloud_width_ticks": 8.5, "bars_in_color": 14, "rsi_14": 54.2,
  "atr_ticks": 18.0, "volume_ratio": 1.15,
  "close": 21460.00, "high": 21465.00, "low": 21443.00
}
```

**Respuesta esperada:** `{"approved": true, "confidence": 0.78, "reason": "rebote limpio en soporte"}`

### Dashboard (ShowDashboard)
Panel `Draw.TextFixed` en `TextPosition.TopRight`:
```
── SmoothTrendCloud ──
Nube : ▲ Alcista (14 barras) | M15: ▲
Señal: 10:23
IA   : ✓ LONG 78%
Filtros: TF:✓  M15:—  LOG:✓
```
`TF` = UseTimeFilter | `M15` = UseM15Confluence | `LOG` = LogRejectedSignals

### Confluencia M15 (UseM15Confluence)
- En `State.Configure`: `AddDataSeries(BarsPeriodType.Minute, 15)` → `_idxM15 = 1`
- En `OnBarUpdate` cuando `BarsInProgress == _idxM15`: calcula `_m15CloudIsUp = Close > EMA(BarsArray[1], 20)`
- En señales Tipo 2: filtra si M15 no está alineado con la dirección

### Alertas (EnableAlerts)
- Tipo 1 cruce LONG → `Alert(..., "Alert2.wav")`
- Tipo 1 cruce SHORT → `Alert(..., "Alert3.wav")`
- Tipo 2 CloudPullback (sin IA o IA aprobada) → `Alert(..., "Alert1.wav")`

### Converter para dropdown del proveedor IA
```csharp
public class STCIAProviderConverter : System.ComponentModel.TypeConverter
{
    public override bool GetStandardValuesSupported(...) => true;
    public override bool GetStandardValuesExclusive(...) => true;
    public override StandardValuesCollection GetStandardValues(...)
        => new StandardValuesCollection(new[] { "Claude", "OpenAI" });
}
[TypeConverter(typeof(STCIAProviderConverter))]
public string AIProvider { get; set; }
```
**IMPORTANTE:** NO usar `enum` para propiedades con dropdown en indicadores NT8.
El enum definido dentro de `namespace NinjaTrader.NinjaScript.Indicators` no es visible
desde el código auto-generado de NT8 (genera CS0246). Usar `string` + `TypeConverter`.

---

## MÓDULO 2 — STADailyContextFilter.cs

Compara precio actual vs. datos del día anterior. Devuelve:
- `MarketContext`: "Continuation_Up" | "Pullback_Down" | "Pullback_Up" | "Continuation_Down" | "Neutral"
- `AllowedDirection`: "LONG" | "SHORT" | "BOTH" | "NONE"
- `ConsecutiveDirectionDays`, `HasStrongDirectionalBias`
- `PriorHigh`, `PriorClose`, `PriorLow`

---

## MÓDULO 3 — STAElliottWaveContext.cs

Detecta pivotes por fractales (lookback configurable). Aplica las 3 reglas duras.
Conservador: devuelve `"Undefined"` ante ambigüedad.

Salidas principales:
- `CurrentWavePosition`: "Wave_1", "Wave_2", "Wave_3_Start", "Wave_3", "Wave_5", "Corrective_ABC", "Undefined"
- `WaveConfidence`: 0.0–1.0
- `IsFavorableForLong` / `IsFavorableForShort`
- `Fib_382`, `Fib_618`, `Fib_1272`, `Fib_1618` — niveles desde pivote base
- `CurrentWaveDirection`: "LONG" | "SHORT" | "BOTH" | "Undefined"

---

## MÓDULO 4 — STASetupClassifier.cs

Orquestador. En `Evaluate()` devuelve `STASetupResult`:
```csharp
public class STASetupResult {
    bool   IsValidSetup;
    string SetupType;       // "TrendStart" | "CloudPullback"
    string Direction;       // "LONG" | "SHORT"
    double BaseConfidence;  // 0.55 (Tipo1) | 0.68 (Tipo2)
    double ElliottMultiplier; // 1.0–1.30 según wave position
    string ElliottContext;
    string RejectionCandle; // "Pin Bar Alcista" | "Envolvente Alcista" | etc.
}
```

**Prioridad:** CloudPullback siempre tiene precedencia sobre TrendStart en la misma barra.

**`RequireVolumeConfirmation` (bool, default: true):**
Requiere volumen de la vela ≥ 110% del promedio de 20 barras.
Con Range Bars, muchas velas no superan este umbral → desmarcar en backtest si aparecen 0 trades.

---

## MÓDULO 5 — STASignalValidator.cs

Llama a la IA desde la **estrategia** (diferente al validador del indicador).
- Claude: `claude-sonnet-4-6` (estrategia) vs `claude-haiku-4-5-20251001` (indicador)
- Timeout 3 s. Fallback: `aiRiskAdjustment = 0.5`, `setupQuality = "low"`.
- En `State.Historical`: usa `SimularRespuesta()` (sin HTTP) para backtest.
- JSON: usa `Newtonsoft.Json.Linq` (JObject). `System.Text.Json` no soporta indexador en .NET 4.8.
- Helper `ExtraerJson()` extrae el JSON si la respuesta contiene texto antes/después.

---

## MÓDULO 6 — STARiskManager.cs

```
Tipo 1 — TrendStart:
  Stop = CloudWidth × StopCloudMultiplier(2.0)
  TP1 = Stop × 1.5  |  TP2 = Stop × 3.0

Tipo 2 — CloudPullback:
  Stop = |EntryPrice - Low_velaRechazo| + buffer(3 ticks)
  TP1 = Stop × 2.0  |  TP2 = Stop × 4.0

Si Wave_3_Start con conf >= 0.65: TP2 *= 1.30
Contratos = floor(Capital × RiskPct / (StopTicks × TickValue))
          × aiRiskAdjustment × elliottMultiplier
Clamp: [1, MaxContracts=4]
```

**Límites diarios:**
- MaxDailyLoss = 1.8% | MaxDailyProfit = 3.0%
- MaxDailyTrades = 5 | MaxConsecutiveLosses = 3
- MaxTrendStart/día = 3 | MaxCloudPullback/día = 4

---

## MÓDULO 7 — STATradeJournal.cs

**Archivos CSV generados (estrategia):**
- `Documents\NinjaTrader 8\logs\SmoothTrendAI_{yyyyMMdd}.csv` — trades ejecutados
- `Documents\NinjaTrader 8\logs\SmoothTrendAI_rejected_{yyyyMMdd}.csv` — señales rechazadas

**Archivo CSV generado (indicador):**
- `Documents\NinjaTrader 8\STC_rechazadas_{yyyyMMdd}.csv` — rechazos del indicador (horario/M15/IA)

**DTOs:**
- `STATradeRecord` — trade completo (35+ campos incluyendo Elliott, IA, R:R)
- `STARejectedSignalRecord` — señal rechazada (motivo, confianza IA, contexto)

---

## MÓDULO 8 — SmoothTrendAI.cs (Estrategia principal)

### Series de datos
```
BarsInProgress 0 = IDX_MAIN  — principal (Range 100)
BarsInProgress 1 = IDX_DAILY — diario (contexto Kaufman)
BarsInProgress 2 = IDX_M15   — M15 (RSI auxiliar)
```

### Propiedades configurables

**Grupo 1. Nube:** `TriggerPeriod`, `SmoothPeriod`, `MinTrendBarsForPullback`

**Grupo 2. Validación IA:** `AIProviderParam`, `AIApiKey`, `AIMinConfidence`, `EnableAIValidation`

**Grupo 3. Riesgo:** `AccountCapital`, `RiskPctPerTrade`, `MaxContracts`, `StopCloudMultiplier`,
`StopBufferTicks`, `StopATRMultiplier`, `MinStopTicks`, `TrailAfterTP1`,
`EnableScaleIn`, `ScaleInTicks`

**Grupo 4. Sesión:** `RestrictToRTH`, `RequireVolumeConfirmation`,
`UseVWAPFilter`, `UseQualityTimeFilter`, `LogRejectedSignals`

### Llamada al factory del indicador (21 parámetros)
```csharp
_cloud = SmoothTrendCloud(
    TriggerPeriod, SmoothPeriod, MinTrendBarsForPullback,   // 1-3
    10,                     // 4  RegionOpacity
    Brushes.Cyan,           // 5  UpTrendColor
    Brushes.Orange,         // 6  DownTrendColor
    true,                   // 7  ShowSignalArrows
    false,                  // 8  ShowType2Arrows
    false,                  // 9  EnableAIValidation
    "Claude",               // 10 AIProvider
    "",                     // 11 AIApiKey
    0.60,                   // 12 AIMinConfidence
    false,                  // 13 EnableAlerts
    false,                  // 14 ShowDashboard
    false,                  // 15 UseM15Confluence
    false,                  // 16 UseTimeFilter
    false,                  // 17 LogRejectedSignals
    false,                  // 18 ShowEntryLevels
    3,                      // 19 LevelStopBufferTicks
    2.0,                    // 20 LevelTP1Ratio
    4.0);                   // 21 LevelTP2Ratio
```

**CRÍTICO:** El orden de los parámetros debe coincidir con el orden físico de declaración
de `[NinjaScriptProperty]` en el archivo del indicador, NO con el atributo `Order` del `[Display]`.
Error resultante si el orden es incorrecto: `CS1503 — cannot convert from 'X' to 'Y'`.
Error resultante si faltan parámetros: `CS1501 — No overload for method takes N arguments`.

---

## CONVENCIONES CRÍTICAS DE CÓDIGO

1. **Prefijo STA** en todas las clases helper para evitar conflictos con otros scripts en NT8.

2. **NO incluir** `#region NinjaScript generated code` manualmente — NT8 lo genera solo.

3. **PrintTo**: en helpers → `NinjaTrader.NinjaScript.PrintTo.OutputTab1`.
   En la estrategia → método nativo `Print("mensaje")`.

4. **JSON**: usar `Newtonsoft.Json.Linq` (JObject) para parsear respuestas.
   `System.Text.Json` no soporta el indexador `[0]` en .NET Framework 4.8.

5. **Threading en indicadores**: capturar `Close[0]`, `Time[0]`, `High[0]`, `Low[0]`, etc.
   como variables locales ANTES de entrar a un `Task.Run()`. Acceder a series de NT8
   desde un hilo de fondo causa excepciones de acceso concurrente.
   Los updates al chart desde background thread → `ChartControl.Dispatcher.InvokeAsync()`.

6. **Enums en indicadores**: NO usar tipos enum propios como `[NinjaScriptProperty]`.
   El código auto-generado de NT8 no los encuentra (CS0246).
   Usar `string` + `TypeConverter` para dropdowns.

7. **VWAP manual**: no depender del `VWAP()` built-in de NT8.
   Calcular como `Σ(TypicalPrice × Volume) / Σ(Volume)`, reseteando al cambiar de día.

8. **Multi-timeframe en indicador con M15**: agregar `AddDataSeries(BarsPeriodType.Minute, 15)`
   en `State.Configure`. Manejar `BarsInProgress == _idxM15` al inicio de `OnBarUpdate`
   para calcular datos M15 y hacer `return` inmediatamente.

9. **Factory del indicador**: el orden de parámetros = orden físico en el archivo, no el `Order` del `[Display]`.
   Agregar siempre propiedades nuevas al FINAL del archivo para que el orden sea predecible.

---

## ERRORES RESUELTOS

| Error | Causa | Fix |
|---|---|---|
| CS0102 duplicado SmoothTrendCloud | `#region generated code` manual | Eliminar la sección |
| CS0246 XmlIgnore | Falta `using System.Xml.Serialization;` | Agregado al indicador |
| CS0234 PrintTo | `NinjaTrader.Code.PrintTo` incorrecto | `NinjaTrader.NinjaScript.PrintTo` |
| CS0021 / CS1061 indexer JsonElement | `System.Text.Json` incompleto en .NET 4.8 | `Newtonsoft.Json.Linq` |
| CS0246 STCIAProvider not found | Enum en namespace Indicators no visible desde auto-generated | `string` + `TypeConverter` |
| CS1501 SmoothTrendCloud takes N args | Se agregaron `[NinjaScriptProperty]` nuevas | Actualizar llamada con parámetros adicionales |
| CS1503 cannot convert from X to Y | Parámetros del factory en orden incorrecto | Respetar orden físico de declaración en el archivo |
| gpt-5.4-mini no existe | Nombre de modelo inválido | Corregir a `gpt-4o-mini` |

---

## DIAGNÓSTICO: 0 TRADES EN BACKTEST

Si el Strategy Analyzer muestra 0 trades:

1. **Desmarcar `RequireVolumeConfirmation`** — con Range Bars el volumen varía; el filtro de 110% bloquea casi todo.
2. **Desmarcar `UseVWAPFilter`** temporalmente.
3. **Revisar CSV** `logs\SmoothTrendAI_rejected_*.csv` para ver el motivo exacto.
4. El Strategy Analyzer agrega series secundarias (Daily, M15) automáticamente.

---

## USO DEL INDICADOR PARA TRADING MANUAL

El indicador `SmoothTrendCloud` puede usarse de forma **completamente independiente**
de la estrategia, directamente en cualquier chart.

**Configuración recomendada:**
1. Cargar `SmoothTrendCloud` en chart NQ (Range 100)
2. `ShowType2Arrows = true` — señales CloudPullback visibles
3. `ShowEntryLevels = true` — líneas de Entrada/Stop/TP1/TP2 al detectar señal
4. `UseTimeFilter = true` — filtrar señales fuera de horario de calidad
5. `LogRejectedSignals = true` — CSV con todo lo que se filtró
6. Opcional: `EnableAIValidation = true` + API Key — IA clasifica cada señal
7. Opcional: `UseM15Confluence = true` — filtro de tendencia mayor

**Señales visuales:**
- Flecha ↑ lima = CloudPullback LONG (sin IA)
- Flecha ↑ verde brillante = CloudPullback LONG aprobado por IA
- Flecha ↓ magenta = CloudPullback SHORT (sin IA)
- Flecha ↓ rosa brillante = CloudPullback SHORT aprobado por IA
- Texto gris `"✗ razón"` = IA rechazó la señal
- Punto gris = esperando respuesta de la IA

**Dashboard:**
```
── SmoothTrendCloud ──
Nube : ▲ Alcista (14 barras) | M15: ▲
Señal: 10:23
IA   : ✓ LONG 78%
Filtros: TF:✓  M15:—  LOG:✓
```

---

## TAREAS PENDIENTES

- [ ] Probar en tiempo real el lunes — verificar flechas, dashboard y niveles de entrada
- [ ] Analizar CSV `STC_rechazadas_*.csv` después de la primera sesión para calibrar filtros
- [ ] Probar backtest con `RequireVolumeConfirmation = false` y `UseVWAPFilter = false`
- [ ] Ajustar `AIMinConfidence` según resultados (inicio recomendado: 0.60)
- [ ] Paper trading mínimo 2 semanas antes de capital real
