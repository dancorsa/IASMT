# SmoothTrendAI — NinjaTrader 8 Strategy

Sistema de trading algorítmico original para NinjaTrader 8 que combina una nube de
doble EMA, filtro de contexto diario Kaufman, conteo de ondas de Elliott simplificado
y validación de señales mediante IA (Claude claude-sonnet-4-6 o GPT-4o) antes de ejecutar
cualquier entrada. **Código 100% original, sin dependencias de paquetes de terceros.**

---

## Arquitectura — 8 archivos

| Archivo | Tipo | Descripción |
|---|---|---|
| `SmoothTrendCloud.cs` | Indicator | Nube visual de doble EMA. Señales Tipo 1 y Tipo 2 |
| `STADailyContextFilter.cs` | Helper | Filtro Kaufman — Prior High / Close / Low |
| `STAElliottWaveContext.cs` | Helper | Conteo simplificado de ondas (3 reglas duras) |
| `STASetupClassifier.cs` | Helper | Orquestador: combina los 3 módulos de análisis |
| `STATradeJournal.cs` | Helper | Logging CSV diario de todos los trades |
| `STASignalValidator.cs` | Helper | Llamadas HTTP a Claude / GPT-4o |
| `STARiskManager.cs` | Helper | Stops, targets y tamaño de posición por setup |
| `SmoothTrendAI.cs` | Strategy | Estrategia principal — integra todo |

Todos los helpers usan el prefijo **STA** en nombres de clase para evitar conflictos
con otras estrategias instaladas en NinjaTrader.

---

## Principio de operación

### Capa 1 — Nube de doble EMA
```
TriggerLine     = EMA(Close, 12)
SmoothTrendLine = EMA(TriggerLine, 12)   ← EMA del EMA
```
La nube cambia de color (Cyan = alcista / Naranja = bajista) con el cruce de ambas líneas.

### Capa 2 — Contexto diario Kaufman
Clasifica el día usando Prior High / Prior Close / Prior Low:

| Vela anterior | Precio actual | Contexto | Dirección permitida |
|---|---|---|---|
| Alcista | > Prior High | Continuation_Up | LONG |
| Alcista | < Prior Close | Pullback_Down | SHORT |
| Bajista | > Prior Close | Pullback_Up | LONG |
| Bajista | < Prior Low | Continuation_Down | SHORT |
| Cualquiera | Entre niveles | Neutral | BOTH |

### Capa 3 — Ondas de Elliott (heurística conservadora)
Detecta pivotes por fractales y aplica las 3 reglas duras de Elliott.
Devuelve `Undefined` ante cualquier ambigüedad — nunca fuerza un conteo.

### Los 3 tipos de setup

| Tipo | Señal | Prioridad | Confianza base | R:R mínimo IA |
|---|---|---|---|---|
| **Tipo 1 — TrendStart** | Cruce de nube | Baja (puede ser fakeout) | 0.55 | 1.6 |
| **Tipo 2 — CloudPullback** | Toque + vela de rechazo | **Alta (siempre prioritario)** | 0.68 | 1.4 |
| **Tipo 3 — Elliott refuerzo** | Fase Wave_3_Start, etc. | Multiplicador sobre Tipo 1/2 | +0.12–0.15 | — |

El **Tipo 2 siempre tiene prioridad** cuando ambos coinciden en la misma barra.

---

## Instalación

### Requisitos
- NinjaTrader 8.1+ con .NET Framework 4.8
- Cuenta en [Anthropic](https://console.anthropic.com/) o [OpenAI](https://platform.openai.com/) para la API key

### Pasos
1. Copiar `SmoothTrendCloud.cs` a:
   `Documents\NinjaTrader 8\bin\Custom\Indicators\`

2. Copiar los 7 archivos restantes a:
   `Documents\NinjaTrader 8\bin\Custom\Strategies\`

3. En NinjaTrader: **New → NinjaScript Editor → Compile All**
   No debe haber errores ni warnings críticos.

4. Agregar el indicador `SmoothTrendCloud` a un chart para validar visualmente
   que la nube cambia de color y aparecen flechas en los cruces.

5. Cargar la estrategia `SmoothTrendAI` en el mismo chart o en el Strategy Analyzer.

---

## Configuración de parámetros

### Configuración recomendada — NQ en Range Bars 100R

| Parámetro | Valor |
|---|---|
| TriggerPeriod | 12 |
| SmoothPeriod | 12 |
| MinTrendBarsForPullback | 8 |
| AIProvider | Claude |
| AIMinConfidence | 0.65 |
| AccountCapital | según cuenta |
| RiskPctPerTrade | 0.01 (1%) |
| MaxContracts | 2 (conservador al inicio) |
| StopCloudMultiplier | 2.0 |
| StopBufferTicks | 3 |
| StopATRMultiplier | 1.3 |
| TrailAfterTP1 | true |
| RestrictToRTH | false |
| RequireVolumeConfirmation | true |

### Configuración recomendada — ES en M15 estándar

| Parámetro | Valor |
|---|---|
| TriggerPeriod | 12 |
| SmoothPeriod | 12 |
| MinTrendBarsForPullback | 6 |
| AIMinConfidence | 0.62 |
| StopCloudMultiplier | 1.8 |
| MinStopTicks | 8 |
| MaxContracts | 2 |

---

## Cómo Elliott modifica el tamaño de posición

```
Contratos base = Floor( (Capital × RiskPct) / (StopTicks × ValorTick) )

× AI risk_adjustment     (0.50 – 1.00)
× ElliottMultiplier      (1.00 – 1.30)  ← solo si hay conteo favorable
× 1.10 si CloudPullback  (bonus por mejor setup)
× 0.70 si Neutral context

= Contratos finales  (mínimo 1, máximo MaxContracts)
```

| Fase Elliott | Multiplicador aplicado |
|---|---|
| `Wave_3_Start` con conf ≥ 0.65 | **1.30** (onda más extendida y confiable) |
| `Wave_1_Start`, `Wave_5_Start`, `Correction_C_End` | 1.20–1.25 |
| `Wave_4_Pullback`, `Wave_3_Extension` | 1.00 (neutro) |
| `Correction_AB` | IA penaliza -0.20 en confianza → reduce contratos |
| `Undefined` | 1.00 (sin efecto) |

---

## Logging CSV

Ruta: `Documents\NinjaTrader 8\logs\SmoothTrendAI_{yyyyMMdd}.csv`

Incluye todos los campos necesarios para análisis posterior:
- Performance por tipo de setup (TrendStart vs CloudPullback)
- Win rate con/sin refuerzo de Elliott
- Distribución de respuestas de la IA por `setup_quality`

---

## Checklist antes de pasar a live

- [ ] Compilar indicador y estrategia sin errores en NinjaScript Editor
- [ ] Verificar visualmente que la nube cambia de color en el chart
- [ ] Confirmar que `DailyContextFilter` clasifica correctamente usando el Output tab
- [ ] Probar `ElliottWaveContext` contra un movimiento impulsivo histórico claro
      (WaveConfidence debe ser alto solo cuando el conteo es inequívoco)
- [ ] Verificar que `SetupClassifier` detecta al menos un TrendStart y un CloudPullback
      en datos históricos del instrumento objetivo
- [ ] Probar el payload de la IA con una señal real y confirmar respuesta JSON correcta
- [ ] Backtest mínimo 6 meses — comparar win rate Tipo 1 vs Tipo 2 en el CSV
- [ ] Paper trading mínimo 2 semanas antes de conectar capital real
- [ ] Verificar que el trailing stop se activa correctamente tras TP1

---

## Estructura del repositorio

```
SmoothTrendAI/
├── Indicators/
│   └── SmoothTrendCloud.cs
├── Strategies/
│   ├── STADailyContextFilter.cs
│   ├── STAElliottWaveContext.cs
│   ├── STASetupClassifier.cs
│   ├── STATradeJournal.cs
│   ├── STASignalValidator.cs
│   ├── STARiskManager.cs
│   └── SmoothTrendAI.cs
├── README.md
└── PROMPT_INICIAL.md
```

---

## Notas técnicas

- **`BackBrushes[0]`**: el indicador tinta el fondo del panel de precio con el color de la nube.
  Para ver la nube llena *entre* las dos líneas, agregar el indicador en un panel separado
  (desactivar `IsOverlay` desde las propiedades del chart).
- **Llamada IA síncrona**: la estrategia usa `Task.Run(...).Result` para esperar la respuesta.
  El timeout de 3 s es manejado dentro de `STASignalValidator`. En backtest se usa simulación
  local para evitar miles de llamadas HTTP.
- **`#region NinjaScript generated code`**: NO se incluye manualmente. NinjaTrader lo genera
  automáticamente al compilar basándose en los atributos `[NinjaScriptProperty]`.
