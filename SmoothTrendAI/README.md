# SmoothTrendAI — NinjaTrader 8 Strategy Suite

Sistema de trading algorítmico original para NinjaTrader 8 que combina una nube de
doble EMA, filtro de contexto diario Kaufman, conteo de ondas de Elliott simplificado
y validación de señales mediante IA (Claude claude-sonnet-4-6 o GPT-4o) antes de ejecutar
cualquier entrada. **Código 100% original, sin dependencias de paquetes de terceros.**

---

## Arquitectura — 9 archivos

| Archivo | Tipo | Descripción |
|---|---|---|
| `SmoothTrendCloud.cs` | Indicator | Nube visual de doble EMA. Señales Tipo 1 y Tipo 2 |
| `STADailyContextFilter.cs` | Helper | Filtro Kaufman — Prior High / Close / Low |
| `STAElliottWaveContext.cs` | Helper | Conteo simplificado de ondas (3 reglas duras) |
| `STASetupClassifier.cs` | Helper | Orquestador: combina los 3 módulos de análisis |
| `STATradeJournal.cs` | Helper | Logging CSV diario de todos los trades |
| `STASignalValidator.cs` | Helper | Llamadas HTTP a Claude / GPT-4o |
| `STARiskManager.cs` | Helper | Stops, targets y tamaño de posición por setup |
| `SmoothTrendAI.cs` | Strategy | Estrategia principal — integra todo + filtro IA |
| `STCFollower.cs` | Strategy | Réplica simplificada — sigue señales del indicador sin IA |

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

## Estrategias disponibles

### SmoothTrendAI (estrategia principal)

Pipeline completo: nube + Kaufman + Elliott + VWAP + validación IA antes de cada entrada.
Salida partida en TP1 / TP2 con ratchet stop escalonado tras TP1.
Incluye tablero de diagnóstico en chart, logging CSV de señales rechazadas y scale-in opcional.

### STCFollower (réplica sin IA)

Sigue exactamente las señales del indicador `SmoothTrendCloud` sin filtros adicionales.
Útil para comparar win rate bruto del indicador contra el win rate filtrado de `SmoothTrendAI`.

Características:
- Brackets de 3 slots: TP1 (1:1) + TP2 (2:1) + TP3 (3:1) — NT8 gestiona todos los fills
- Breakeven automático al alcanzar TP1
- Modo contratos fijos (`UseFixedContracts`) o basados en riesgo porcentual
- Límites diarios: pérdida máxima (`MaxDailyLossPct`), trades máximos (`MaxDailyTrades`), pérdidas consecutivas (`MaxConsecLosses`)
- Sin llamadas HTTP ni validación IA — latencia mínima

---

## Instalación

### Requisitos
- NinjaTrader 8.1+ con .NET Framework 4.8
- Cuenta en [Anthropic](https://console.anthropic.com/) o [OpenAI](https://platform.openai.com/) para la API key *(solo para SmoothTrendAI)*

### Pasos
1. Copiar `SmoothTrendCloud.cs` a:
   `Documents\NinjaTrader 8\bin\Custom\Indicators\`

2. Copiar los 8 archivos restantes a:
   `Documents\NinjaTrader 8\bin\Custom\Strategies\`

3. En NinjaTrader: **New → NinjaScript Editor → Compile All**
   No debe haber errores ni warnings críticos.

4. Agregar el indicador `SmoothTrendCloud` a un chart para validar visualmente
   que la nube cambia de color y aparecen flechas en los cruces.

5. Cargar `SmoothTrendAI` o `STCFollower` en el mismo chart o en el Strategy Analyzer.

---

## Configuración de parámetros

### SmoothTrendAI — MNQ conservador (challenge)

| Parámetro | Valor | Notas |
|---|---|---|
| TriggerPeriod | 12 | |
| SmoothPeriod | 12 | |
| MinTrendBarsForPullback | 8 | |
| AIProvider | Claude | |
| AIMinConfidence | 0.62 | umbral mínimo para aprobar señal |
| AccountCapital | según cuenta | |
| RiskPctPerTrade | 0.005 (0.5%) | conservador para challenge |
| MaxContracts | 5 | |
| StopCloudMultiplier | 2.0 | |
| StopBufferTicks | 3 | |
| StopATRMultiplier | 1.3 | |
| MinStopTicks | 6 | |
| TrailAfterTP1 | true | ratchet stop escalonado |
| UseVWAPFilter | true | filtrar entradas contra VWAP |
| RequireVolumeConfirmation | true | |
| SetupCooldownBars | 5 | evita señales duplicadas |
| RestrictToRTH | false | |
| EnableScaleIn | false | activar solo con datos suficientes |

### SmoothTrendAI — NQ en Range Bars 100R

| Parámetro | Valor |
|---|---|
| TriggerPeriod | 12 |
| SmoothPeriod | 12 |
| MinTrendBarsForPullback | 8 |
| AIMinConfidence | 0.65 |
| AccountCapital | según cuenta |
| RiskPctPerTrade | 0.01 (1%) |
| MaxContracts | 4 |
| StopCloudMultiplier | 2.0 |
| StopBufferTicks | 3 |
| MinStopTicks | 6 |
| TrailAfterTP1 | true |
| UseVWAPFilter | true |
| RequireVolumeConfirmation | true |

### STCFollower — MNQ (6 contratos fijos)

| Parámetro | Valor | Notas |
|---|---|---|
| EnterOnTipo1 | true | cruces de nube |
| EnterOnTipo2 | true | toques de nube |
| UseFixedContracts | true | |
| FixedContracts | 6 | 2 por cada slot TP1/TP2/TP3 |
| TP1Ratio | 1.0 | 1:1 |
| TP2Ratio | 2.0 | 1:2 |
| TP3Ratio | 3.0 | 1:3 |
| StopBufferTicks | 6 | |
| MinStopTicks | 20 | |
| UseBreakEven | true | |
| BreakEvenLockTicks | 4 | ticks de ganancia garantizados |
| MaxDailyLossPct | 0.018 (1.8%) | límite challenge ~$900 |
| MaxDailyTrades | 10 | |
| MaxConsecLosses | 3 | |

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

## Gestión de posición en SmoothTrendAI

### Salida partida TP1 / TP2
- Al fill de entrada se colocan automáticamente la orden de stop y dos límites: TP1 y TP2.
- Tras llenar TP1, el stop de los contratos restantes se mueve a breakeven.
- El límite de TP2 se publica como orden visible en el chart tras la salida parcial en TP1.

### Ratchet stop tras TP1
Una vez activado el trailing, el stop solo puede moverse en dirección favorable (nunca retrocede).
El paso del ratchet usa el ATR multiplicado por `StopATRMultiplier`.

### Scale-in opcional
Con `EnableScaleIn = true`, la estrategia agrega contratos (`ScaleInContracts`) a `ScaleInTicks`
puntos en dirección favorable de la entrada principal.

---

## Logging CSV

Ruta: `Documents\NinjaTrader 8\logs\SmoothTrendAI_{yyyyMMdd}.csv`

Incluye todos los campos necesarios para análisis posterior:
- Performance por tipo de setup (TrendStart vs CloudPullback)
- Win rate con/sin refuerzo de Elliott
- Distribución de respuestas de la IA por `setup_quality`
- Log separado de señales rechazadas (cuando `LogRejectedSignals = true`)

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
- [ ] Comparar resultados de `STCFollower` vs `SmoothTrendAI` sobre el mismo período
      para cuantificar el valor del filtro IA
- [ ] Paper trading mínimo 2 semanas antes de conectar capital real
- [ ] Verificar que el ratchet stop se activa correctamente tras TP1

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
│   ├── SmoothTrendAI.cs
│   └── STCFollower.cs
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
- **VWAP acumulado**: calculado manualmente barra a barra, sin dependencias externas.
  Se resetea al inicio de cada sesión según `VWAPSessionResetHour`.
- **Series de datos**: `BarsInProgress 0` = barra principal, `BarsInProgress 1` = Daily (Kaufman),
  `BarsInProgress 2` = M15 auxiliar (RSI, omitido si el gráfico principal ya es M15).
- **`#region NinjaScript generated code`**: NO se incluye manualmente. NinjaTrader lo genera
  automáticamente al compilar basándose en los atributos `[NinjaScriptProperty]`.
