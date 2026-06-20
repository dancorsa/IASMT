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
- Cada segmento de color se cierra con un tag permanente `CS_{startBar}` al cambiar color
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

### Validación IA en el indicador (trading manual)

El indicador tiene **su propio cliente HTTP** (`static HttpClient _http`) independiente de la estrategia. Esto permite usarlo para trading manual sin necesidad de cargar la estrategia.

**Flujo al detectar Tipo 2:**
1. Si `EnableAIValidation = false` o barra histórica → flecha inmediata (lima/magenta)
2. Si `EnableAIValidation = true` y es barra en tiempo real:
   - Dibuja punto gris ("esperando...")
   - Lanza `Task.Run` en hilo de fondo
   - `ValidarConIAAsync()` llama a Claude Haiku o GPT-4o-mini
   - Al recibir respuesta, actualiza en UI thread con `ChartControl.Dispatcher.InvokeAsync()`
   - Aprobado: flecha verde + texto `"IA 74%\nrazón"`
   - Rechazado: texto gris `"✗ razón"`

**Punto importante:** capturar TODOS los datos de la barra (`Time[0]`, `Close[0]`, etc.)
ANTES del `Task.Run()`. Acceder a `Close[0]` dentro del lambda causa error de threading.

**Prompt mejorado (separación reglas vs. juicio):**
```
"El CÓDIGO ya verificó: precio tocó el borde de la nube y cerró dentro de la tendencia,
 con mínimo de barras en color requeridas.
 TU JUICIO añade valor en: calidad real de la vela de rechazo,
 contexto de sesión (evitar señales en baja liquidez),
 RSI en zona neutra (35-65), volumen >= 0.8x promedio."
```

**Payload que envía el indicador:**
```json
{
  "instrument": "NQ 09-26",
  "direction": "LONG",
  "sesion": "apertura-NY (alta volatilidad)",
  "calidad_vela": "rechazo-fuerte",
  "cloud_width_ticks": 8.5,
  "bars_in_color": 14,
  "rsi_14": 54.2,
  "atr_ticks": 18.0,
  "volume_ratio": 1.15,
  "close": 21460.00,
  "high": 21465.00,
  "low": 21443.00
}
```

**Respuesta esperada:**
```json
{"approved": true, "confidence": 0.78, "reason": "rebote limpio en soporte"}
```

### Dashboard (ShowDashboard)
Panel `Draw.TextFixed` en `TextPosition.TopRight` que muestra:
```
── SmoothTrendCloud ──
Nube : ▲ Alcista (14 barras) | M15: ▲
Señal: 10:23
IA   : ✓ LONG 78%
```
Se actualiza cada cierre de barra.

### Confluencia M15 (UseM15Confluence)
- En `State.Configure`: `AddDataSeries(BarsPeriodType.Minute, 15)` → `_idxM15 = 1`
- En `OnBarUpdate` cuando `BarsInProgress == _idxM15`: calcula `_m15CloudIsUp = Close > EMA(BarsArray[1], 20)`
- En señales Tipo 2: filtra si M15 no está alineado con la dirección de la señal

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
// Uso en propiedad:
[TypeConverter(typeof(STCIAProviderConverter))]
public string AIProvider { get; set; }
```
**IMPORTANTE:** NO usar `enum` para propiedades con dropdown en indicadores NT8.
El enum definido dentro de `namespace NinjaTrader.NinjaScript.Indicators` no es visible
desde el código auto-generado de NT8 (genera CS0246). Solución: `string` + `TypeConverter`.

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
Requiere que el volumen de la vela sea ≥ 110% del promedio de 20 barras.
Con Range Bars, muchas velas no superan este umbral → desmarcar en backtest si aparecen 0 trades.

---

## MÓDULO 5 — STASignalValidator.cs

Llama a la IA desde la **estrategia** (diferente al validador del indicador).
- Claude: `claude-sonnet-4-6` (estrategia) vs `claude-haiku-4-5-20251001` (indicador — más rápido)
- Timeout 3 s. Fallback: `aiRiskAdjustment = 0.5`, `setupQuality = "low"`.
- En `State.Historical`: usa `SimularRespuesta()` (sin HTTP) para backtest.

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

**Scale-in (EnableScaleIn):**
- Entrada inicial = `ceil(totalContracts × 0.60)`
- Segunda entrada = contratos restantes cuando precio avanza `ScaleInTicks` ticks a favor

---

## MÓDULO 7 — STATradeJournal.cs

**Archivos CSV generados:**
- `Documents\NinjaTrader 8\logs\SmoothTrendAI_{yyyyMMdd}.csv` — trades ejecutados
- `Documents\NinjaTrader 8\logs\SmoothTrendAI_rejected_{yyyyMMdd}.csv` — señales rechazadas

**DTOs:**
- `STATradeRecord` — trade completo (35+ campos incluyendo Elliott, IA, R:R)
- `STARejectedSignalRecord` — señal rechazada (motivo, confianza IA, contexto)

Campos de rechazo (`RejectReason`): `"RSI_Extremo"` | `"VWAP"` | `"TimeFilter"` | `"AI"`

---

## MÓDULO 8 — SmoothTrendAI.cs (Estrategia principal)

### Series de datos
```
BarsInProgress 0 = IDX_MAIN  — principal (Range 100)
BarsInProgress 1 = IDX_DAILY — diario (contexto Kaufman)
BarsInProgress 2 = IDX_M15   — M15 (RSI auxiliar, si el principal no es M15)
```

### Propiedades configurables (NinjaScriptProperty)

**Grupo 1. Nube:**
`TriggerPeriod`, `SmoothPeriod`, `MinTrendBarsForPullback`

**Grupo 2. Validación IA:**
`AIProviderParam` (string), `AIApiKey` (string), `AIMinConfidence` (double), `EnableAIValidation` (bool)

**Grupo 3. Riesgo:**
`AccountCapital`, `RiskPctPerTrade`, `MaxContracts`, `StopCloudMultiplier`, `StopBufferTicks`,
`StopATRMultiplier`, `MinStopTicks`, `TrailAfterTP1`,
`EnableScaleIn` (bool), `ScaleInTicks` (int)

**Grupo 4. Sesión:**
`RestrictToRTH` (bool), `RequireVolumeConfirmation` (bool),
`UseVWAPFilter` (bool), `UseQualityTimeFilter` (bool), `LogRejectedSignals` (bool)

**Grupo 5. Visualización:**
`ShowFibLevels` (bool), `ShowElliottPivots` (bool)

### Flujo principal en OnBarUpdate
```
1. Si BarsInProgress != IDX_MAIN → return
2. Reset diario si cambió fecha
3. Actualizar STADailyContextFilter (necesita >= 3 barras diarias)
4. Actualizar STAElliottWaveContext
5. ActualizarVWAP() — cálculo manual diario Σ(TP×Vol)/Σ(Vol)
6. Dibujar niveles Fibonacci si confianza >= 0.50
7. Dibujar pivotes Elliott si ShowElliottPivots
8. Actualizar STASetupClassifier con datos de barra actual
9. Si hay posición abierta → GestionarPosicionAbierta() y return
10. Verificar RTH / límites diarios
11. setup = STASetupClassifier.Evaluate()
12. Filtro RSI M15 (rechaza si LONG y RSI > 70, o SHORT y RSI < 30)
13. Filtro horario de calidad (10:00–11:30 / 14:00–15:30 ET)
14. Filtro VWAP manual (_currentVWAP)
15. Calcular riesgo preliminar
16. Construir payload IA
17. ValidarConIA() → si rechaza, LogRechazo() y return
18. EjecutarEntrada() con scale-in si habilitado
```

### Llamada al factory del indicador (13 parámetros actuales)
```csharp
_cloud = SmoothTrendCloud(
    TriggerPeriod, SmoothPeriod, MinTrendBarsForPullback,
    10,                     // RegionOpacity
    Brushes.Cyan,           // UpTrendColor
    Brushes.Orange,         // DownTrendColor
    true,                   // ShowSignalArrows
    false,                  // ShowType2Arrows  ← estrategia gestiona sus propias entradas
    false,                  // EnableAIValidation ← estrategia tiene su propio validador
    "Claude",               // AIProvider
    "",                     // AIApiKey (vacío — estrategia usa su propia clave)
    0.60,                   // AIMinConfidence
    false,                  // EnableAlerts
    false,                  // ShowDashboard
    false);                 // UseM15Confluence
```

**CRÍTICO:** Si se agrega un nuevo `[NinjaScriptProperty]` al indicador, el factory
cambia de firma y esta llamada debe actualizarse con el parámetro adicional.
Error resultante: `CS1501 — No overload for method 'SmoothTrendCloud' takes N arguments`.

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

6. **Enums en indicadores**: NO usar tipos enum propios como `[NinjaScriptProperty]`.
   El código auto-generado de NT8 no los encuentra (CS0246).
   Usar `string` + `TypeConverter` para dropdowns.

7. **VWAP manual**: no depender del `VWAP()` built-in de NT8 (puede no estar disponible).
   Calcular como `Σ(TypicalPrice × Volume) / Σ(Volume)`, reseteando `_vwapDate` al cambiar de día.

8. **Multi-timeframe en indicador con M15**: agregar `AddDataSeries(BarsPeriodType.Minute, 15)`
   en `State.Configure`. Manejar `BarsInProgress == _idxM15` al inicio de `OnBarUpdate`
   para calcular datos M15 y hacer `return` inmediatamente.

---

## ERRORES RESUELTOS

| Error | Causa | Fix |
|---|---|---|
| CS0102 duplicado SmoothTrendCloud | `#region generated code` manual | Eliminar la sección; NT8 la genera sola |
| CS0246 XmlIgnore | Falta `using System.Xml.Serialization;` | Agregado al indicador |
| CS0234 PrintTo | `NinjaTrader.Code.PrintTo` incorrecto | `NinjaTrader.NinjaScript.PrintTo` |
| CS0021 / CS1061 indexer JsonElement | `System.Text.Json` incompleto en .NET 4.8 | `Newtonsoft.Json.Linq` (JObject) |
| CS7036 SmoothTrendCloud toma N args | Factory genera params para todos los `[NinjaScriptProperty]` | Pasar todos los parámetros en la llamada |
| CS0246 STCIAProvider not found | Enum en namespace Indicators no visible desde auto-generated | Cambiar a `string` + `TypeConverter` |
| CS1501 SmoothTrendCloud takes N args | Se agregaron propiedades nuevas al indicador | Actualizar llamada en estrategia con los parámetros nuevos |

---

## DIAGNÓSTICO: 0 TRADES EN BACKTEST

Si el Strategy Analyzer muestra 0 trades con fecha desde enero 2026:

1. **Desmarcar "Requerir confirm..."** (`RequireVolumeConfirmation`) — en Range Bars, el volumen
   varía mucho por barra; el filtro de 110% del promedio bloquea casi todos los CloudPullback.

2. **Desmarcar "Filtro VWAP"** temporalmente — verificar si las señales aparecen.

3. **Revisar CSV de rechazos** en `logs\SmoothTrendAI_rejected_*.csv` para ver el motivo exacto.

4. **El Strategy Analyzer agrega las series secundarias (Daily, M15) automáticamente**
   cuando la estrategia llama `AddDataSeries()` en el código — no hace falta añadirlas manualmente.

---

## USO DEL INDICADOR PARA TRADING MANUAL

El indicador `SmoothTrendCloud` puede usarse de forma **completamente independiente**
de la estrategia, directamente en cualquier chart.

**Configuración para trading manual:**
1. Cargar `SmoothTrendCloud` en el chart de NQ (Range 100)
2. Activar **"Mostrar flechas Tipo 2"** → señales CloudPullback visibles
3. Opcional — activar **"Activar validación IA"** + poner API Key → IA clasifica cada señal
4. Activar **"Filtro confluencia M15"** → reduce señales contra tendencia mayor
5. El **Dashboard** muestra estado en tiempo real en la esquina del chart
6. Las **alertas** suenan al detectar cualquier señal

**Señales:**
- Flecha verde ↑ lima = CloudPullback LONG (sin IA) | Verde brillante = IA aprobó
- Flecha magenta ↓ = CloudPullback SHORT (sin IA) | Rosa brillante = IA aprobó
- Texto gris `"✗ razón"` = IA rechazó

---

## TAREAS PENDIENTES

- [ ] Compilar y verificar que compila sin errores tras todos los cambios de esta sesión
- [ ] Probar en tiempo real el lunes (mercado cerrado fines de semana)
      — verificar que las flechas Tipo 2 aparecen y el dashboard funciona
- [ ] Probar backtest con `RequireVolumeConfirmation = false` y `UseVWAPFilter = false`
      para confirmar que se generan trades
- [ ] Analizar CSV de rechazos después de primera sesión en vivo
- [ ] Ajustar `AIMinConfidence` del indicador según resultados (inicio recomendado: 0.60)
- [ ] Paper trading mínimo 2 semanas antes de capital real
