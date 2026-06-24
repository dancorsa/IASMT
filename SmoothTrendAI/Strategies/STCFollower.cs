// ============================================================
// STCFollower.cs — Estrategia réplica de señales SmoothTrendCloud
// Proyecto: SmoothTrendAI  |  Prefijo: STC
//
// Entra exactamente cuando el indicador SmoothTrendCloud marca flecha:
//   Tipo 1 (TrendStart)    → cruce de TriggerLine sobre SmoothTrendLine
//   Tipo 2 (CloudPullback) → toque de nube + N barras en tendencia
//
// Sin filtros adicionales (no Kaufman, no Elliott, no RSI/VWAP, no IA).
// Solo stop/TP/sizing por riesgo % y gestión de posición.
//
// Reutiliza: SmoothTrendCloud, STARiskManager, STATradeJournal
// ============================================================
#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class STCFollower : Strategy
    {
        // ─── Módulos ──────────────────────────────────────────────────────────
        private SmoothTrendCloud _cloud;
        private STARiskManager   _riskManager;
        private STATradeJournal  _journal;

        // ─── Estado de posición ────────────────────────────────────────────────
        private bool           _positionOpen;
        private bool           _entrySubmitted;
        private string         _activeDirection;
        private string         _activeSetupType;
        private double         _activeEntryPrice;
        private double         _activeStopPrice;
        private double         _activeTarget1;
        private double         _activeTarget2;
        private bool           _tp1Hit;
        private double         _trailingStopPrice;
        private bool           _trailingActive;
        private bool           _breakEvenTriggered;
        private STATradeRecord _activeRecord;

        // ─── Auxiliares ───────────────────────────────────────────────────────
        private DateTime _lastDailyReset  = DateTime.MinValue;
        private int      _lastSetupBar    = -999;
        private double   _lastExitPrice   = 0;   // precio real del último fill de salida

        // ─── Nube visual ──────────────────────────────────────────────────────
        private SolidColorBrush _cloudUpBrush;
        private SolidColorBrush _cloudDownBrush;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "STCFollower — Réplica exacta de señales SmoothTrendCloud sin filtros adicionales";
                Name        = "STCFollower";
                Calculate   = Calculate.OnBarClose;
                IsOverlay   = true;
                BarsRequiredToTrade          = 30;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;

                // Nube
                TriggerPeriod           = 12;
                SmoothPeriod            = 12;
                MinTrendBarsForPullback = 8;

                // Qué señales operar
                EnterOnTipo1 = true;
                EnterOnTipo2 = true;

                // Riesgo — optimizado para NQ full / challenge de fondeo
                AccountCapital      = 50_000.0;
                RiskPctPerTrade     = 0.005;   // 0.5% = $250/trade en MNQ con 5 contratos
                MaxContracts        = 5;        // MNQ: 5 ctrs × 25pts × $2 = $250 riesgo típico
                StopCloudMultiplier = 1.5;      // nube de NQ ya es amplia
                StopBufferTicks     = 6;        // más ruido intra-barra en NQ
                StopATRMultiplier   = 1.5;
                MinStopTicks        = 20;       // mínimo 5 puntos en NQ
                TP1Ratio            = 2.0;
                TP2Ratio            = 3.0;      // NQ puede revertir rápido
                TrailAfterTP1       = true;

                // Trailing stop — ATR×1.5 para dar holgura en NQ
                TrailAfterBreakEven    = true;
                UseFixedTrail          = false;
                TrailingPoints         = 30.0;
                TrailingATRMultiplier  = 1.5;

                // Límites diarios
                MaxDailyLossPct  = 0.018;  // 1.8% = $900 — buffer antes del límite real de $1000/día
                MaxDailyTrades   = 6;
                MaxConsecLosses  = 3;

                // Breakeven — 15 puntos alcanzables en 5-min NQ
                UseBreakEven       = true;
                BreakEvenPoints    = 15.0;
                BreakEvenLockTicks = 4;    // ~$80 para cubrir comisiones

                // Sesión (opcionales, espejo del indicador)
                UseTimeFilter     = false;
                SetupCooldownBars = 3;

                // Visualización
                ShowCloudInStrategy = true;
            }
            else if (State == State.Configure)
            {
                AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Line, "TriggerLine");
                AddPlot(new Stroke(Brushes.Gold,        2), PlotStyle.Line, "SmoothTrendLine");

                _riskManager = new STARiskManager
                {
                    AccountCapital        = AccountCapital,
                    RiskPctPerTrade       = RiskPctPerTrade,
                    MaxContracts          = MaxContracts,
                    StopCloudMultiplier   = StopCloudMultiplier,
                    StopBufferTicks       = StopBufferTicks,
                    StopATRMultiplier     = StopATRMultiplier,
                    MinStopTicks          = MinStopTicks,
                    TrailAfterTP1         = TrailAfterTP1,
                    // Ambos tipos usan los mismos ratios configurados por el usuario
                    TrendStart_TP1Mult    = TP1Ratio,
                    TrendStart_TP2Mult    = TP2Ratio,
                    CloudPullback_TP1Mult = TP1Ratio,
                    CloudPullback_TP2Mult = TP2Ratio,
                    // Límites diarios configurables por parámetro
                    MaxDailyLossPct        = MaxDailyLossPct,
                    MaxDailyTrades         = MaxDailyTrades,
                    MaxConsecutiveLosses   = MaxConsecLosses,
                    // Sin límite por tipo — el tope general MaxDailyTrades controla todo
                    MaxTrendStartPerDay    = MaxDailyTrades,
                    MaxCloudPullbackPerDay = MaxDailyTrades,
                    TrailingATRMultiplier  = TrailingATRMultiplier,
                    MaxDailyProfitPct      = 1.0   // sin límite de ganancia diaria
                };

                _journal = new STATradeJournal();
            }
            else if (State == State.DataLoaded)
            {
                // Instanciar el indicador con los parámetros de la nube.
                // Las features visuales/IA/alertas del indicador se desactivan:
                // la estrategia gestiona sus propias entradas y visualizaciones.
                _cloud = SmoothTrendCloud(
                    TriggerPeriod, SmoothPeriod, MinTrendBarsForPullback,
                    10,             // RegionOpacity
                    Brushes.Cyan,   // UpTrendColor
                    Brushes.Orange, // DownTrendColor
                    false,          // ShowSignalArrows   — estrategia dibuja sus propios marcadores
                    false,          // ShowType2Arrows
                    false,          // EnableAIValidation
                    "Claude",       // AIProvider
                    "",             // AIApiKey
                    0.60,           // AIMinConfidence
                    false,          // EnableAlerts
                    false,          // ShowDashboard
                    false,          // UseM15Confluence
                    false,          // UseTimeFilter      — manejado aquí
                    false,          // LogRejectedSignals
                    false,          // ShowEntryLevels
                    3,              // LevelStopBufferTicks
                    2.0,            // LevelTP1Ratio
                    4.0);           // LevelTP2Ratio

                _cloudUpBrush   = new SolidColorBrush(Color.FromArgb(28, 0, 180, 255));
                _cloudDownBrush = new SolidColorBrush(Color.FromArgb(28, 255, 120, 0));
                _cloudUpBrush.Freeze();
                _cloudDownBrush.Freeze();

                _journal.Open(Instrument.FullName);
            }
            else if (State == State.Realtime)
            {
                // Resetear contadores al entrar en tiempo real para ignorar
                // los trades procesados sobre barras históricas de hoy.
                _riskManager.ResetDaily();
                _lastDailyReset = DateTime.Today;
            }
            else if (State == State.Terminated)
            {
                _journal?.Dispose();
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            if (CurrentBar < BarsRequiredToTrade) return;

            // ── Nube visual: líneas + fondo de barra ─────────────────────────
            if (ShowCloudInStrategy && _cloud != null && _cloud.TriggerValue > 0)
            {
                Values[0][0]   = _cloud.TriggerValue;
                Values[1][0]   = _cloud.SmoothValue;
                BackBrushes[0] = _cloud.CurrentCloudColor == "Up" ? _cloudUpBrush : _cloudDownBrush;
            }

            // ── Detectar cierre de posición por el broker ─────────────────────
            if (_positionOpen && Position.MarketPosition == MarketPosition.Flat)
            {
                FinalizarPosicion("BrokerExit");
                return;
            }
            if (_entrySubmitted && !_positionOpen) return;

            // ── Reset diario ──────────────────────────────────────────────────
            if (Time[0].Date != _lastDailyReset.Date)
            {
                _riskManager.ResetDaily();
                _lastDailyReset = Time[0].Date;
            }

            // ── Gestión de posición abierta (máxima prioridad) ───────────────
            if (_positionOpen)
            {
                GestionarPosicionAbierta();
                return;
            }

            // ── Verificar límites diarios ─────────────────────────────────────
            if (!_riskManager.CanTrade())
            {
                Print($"[STC-DIAG] Bar={CurrentBar} {Time[0]:HH:mm} BLOQUEADO: límites diarios " +
                      $"trades={_riskManager.DailyTrades}/{MaxDailyTrades} " +
                      $"pérdidas={_riskManager.ConsecutiveLosses}/{MaxConsecLosses} " +
                      $"PnL=${_riskManager.DailyPnL:F0} " +
                      $"pérdidaLím=${-(AccountCapital * MaxDailyLossPct):F0}");
                return;
            }

            // ── Diagnóstico: estado de señales del cloud en cada barra ────────
            Print($"[STC-DIAG] Bar={CurrentBar} {Time[0]:HH:mm} " +
                  $"CX↑={_cloud.HasCrossUpSignal} CX↓={_cloud.HasCrossDownSignal} " +
                  $"Touch↑={_cloud.TouchedCloudFromAbove} Touch↓={_cloud.TouchedCloudFromBelow} " +
                  $"BarsEnColor={_cloud.BarsInCurrentColor} Nube={_cloud.CurrentCloudColor}");

            // ── Detectar señal (réplica exacta del indicador) ─────────────────
            // Tipo 2 tiene prioridad sobre Tipo 1 (igual que en STASetupClassifier)
            string direction = null;
            string setupType = null;

            if (EnterOnTipo2)
            {
                if      (_cloud.TouchedCloudFromAbove) { direction = "LONG";  setupType = "CloudPullback"; }
                else if (_cloud.TouchedCloudFromBelow) { direction = "SHORT"; setupType = "CloudPullback"; }
            }
            if (direction == null && EnterOnTipo1)
            {
                if      (_cloud.HasCrossUpSignal)   { direction = "LONG";  setupType = "TrendStart"; }
                else if (_cloud.HasCrossDownSignal) { direction = "SHORT"; setupType = "TrendStart"; }
            }

            if (direction == null) return;

            // ── Filtro horario (opcional, espejo del indicador) ───────────────
            if (UseTimeFilter && !EsHorarioCalidad())
            {
                Print($"[STC-DIAG] BLOQUEADO TimeFilter: {direction} {setupType} {Time[0]:HH:mm}");
                return;
            }

            // ── Cooldown anti-señales repetidas ───────────────────────────────
            if (SetupCooldownBars > 0 && CurrentBar - _lastSetupBar < SetupCooldownBars)
            {
                Print($"[STC-DIAG] BLOQUEADO Cooldown: {direction} {setupType} " +
                      $"barrasDesdeUltima={CurrentBar - _lastSetupBar} min={SetupCooldownBars}");
                return;
            }
            _lastSetupBar = CurrentBar;

            // ── Límite por tipo de setup ──────────────────────────────────────
            if (!_riskManager.CanTrade(setupType))
            {
                Print($"[STC-DIAG] BLOQUEADO RiskManager por tipo: {setupType}");
                return;
            }

            // ── Calcular parámetros de riesgo ─────────────────────────────────
            double atr14   = ATR(14)[0];
            double tickVal = Instrument.MasterInstrument.PointValue * TickSize;

            // STASetupResult mínimo — solo SetupType es relevante para STARiskManager
            var setupInfo = new STASetupResult
            {
                SetupType      = setupType,
                Direction      = direction,
                ElliottContext = ""   // sin Elliott, el multiplier TP2 no se expande
            };

            var risk = _riskManager.Calculate(
                setupInfo,
                Close[0],
                direction,
                _cloud.CloudWidth,
                atr14,
                Low[0],            // rejCandleLow  — extremo de la vela actual (Tipo 2)
                High[0],           // rejCandleHigh
                TickSize,
                tickVal,
                aiRiskAdjustment:  1.0,    // sin IA, sin ajuste de tamaño
                elliottMultiplier: 1.0,    // sin Elliott
                marketContext:     "Bullish"); // evita penalización por contexto neutral

            EjecutarEntrada(direction, setupType, risk);
        }

        // ─── Entrada ──────────────────────────────────────────────────────────
        private void EjecutarEntrada(string direction, string setupType, STARiskParameters risk)
        {
            bool   isLong    = direction == "LONG";
            string entryName = isLong ? "STC_Long" : "STC_Short";

            SetStopLoss(entryName, CalculationMode.Price, risk.StopPrice, false);

            if (isLong) EnterLong (risk.Contracts, entryName);
            else        EnterShort(risk.Contracts, entryName);

            _entrySubmitted    = true;
            _positionOpen      = false;
            _activeDirection   = direction;
            _activeSetupType   = setupType;
            _activeEntryPrice  = Close[0];
            _activeStopPrice   = risk.StopPrice;
            _activeTarget1     = risk.Target1Price;
            _activeTarget2     = risk.Target2Price;
            _tp1Hit             = false;
            _trailingActive     = false;
            _trailingStopPrice  = 0;
            _lastExitPrice      = 0;
            _breakEvenTriggered = false;

            _riskManager.RegisterTrade(setupType);

            _activeRecord = new STATradeRecord
            {
                TimestampEntry           = Time[0],
                Instrument               = Instrument.FullName,
                BarType                  = BarsPeriod.ToString(),
                Direction                = direction,
                SetupType                = setupType,
                EntryPrice               = Close[0],
                Contracts                = risk.Contracts,
                TriggerLineAtEntry       = _cloud.TriggerValue,
                SmoothLineAtEntry        = _cloud.SmoothValue,
                CloudWidthAtEntry        = _cloud.CloudWidth,
                BarsInCurrentColor       = _cloud.BarsInCurrentColor,
                StopTicks                = risk.StopTicks,
                ConsecutiveLossesAtEntry = _riskManager.ConsecutiveLosses
            };

            // ── Dibujar señal en chart ─────────────────────────────────────────
            string tag = $"E_{CurrentBar}";
            if (setupType == "TrendStart")
            {
                if (isLong) Draw.TriangleUp  (this, tag, true, 0, Low[0]  - TickSize * 8, Brushes.Lime);
                else        Draw.TriangleDown(this, tag, true, 0, High[0] + TickSize * 8, Brushes.Red);
            }
            else
            {
                if (isLong) Draw.Diamond(this, tag, true, 0, Low[0]  - TickSize * 8, Brushes.Cyan);
                else        Draw.Diamond(this, tag, true, 0, High[0] + TickSize * 8, Brushes.OrangeRed);
            }

            Draw.HorizontalLine(this, "STC_TP1", risk.Target1Price,
                Brushes.Lime, DashStyleHelper.Dash, 2);
            Draw.HorizontalLine(this, "STC_TP2", risk.Target2Price,
                Brushes.DodgerBlue, DashStyleHelper.Dash, 1);
            Draw.Text(this, "STC_TP1_lbl", $"TP1 {risk.Target1Price:F2}",
                0, risk.Target1Price + (isLong ? TickSize * 2 : -TickSize * 2), Brushes.Lime);
            Draw.Text(this, "STC_TP2_lbl", $"TP2 {risk.Target2Price:F2}",
                0, risk.Target2Price + (isLong ? TickSize * 2 : -TickSize * 2), Brushes.DodgerBlue);

            Print($"[STCFollower] {direction} {setupType} | " +
                  $"Entry={Close[0]:F2} Stop={risk.StopPrice:F2} " +
                  $"TP1={risk.Target1Price:F2} TP2={risk.Target2Price:F2} Ctrs={risk.Contracts}");
        }

        // ─── Gestión de posición abierta ──────────────────────────────────────
        private void GestionarPosicionAbierta()
        {
            bool   isLong    = _activeDirection == "LONG";
            string entryName = isLong ? "STC_Long" : "STC_Short";

            // Capturar estado del trailing ANTES de que Prioridad 3/4 lo activen.
            // Si activamos trailing en este bar, NO checkeamos salida en el mismo bar
            // (el stop ya queda puesto como orden real para el siguiente bar).
            bool trailingYaActivo = _trailingActive;
            bool tp1EsteBar      = false;   // evita SetStopLoss en el mismo bar que ExitLong/Short parcial

            // Prioridad 1: cruce opuesto de la nube → salida inmediata
            if (isLong  && _cloud.HasCrossDownSignal) { CerrarPosicion("CloudCruce_Opuesto"); return; }
            if (!isLong && _cloud.HasCrossUpSignal)   { CerrarPosicion("CloudCruce_Opuesto"); return; }

            // Prioridad 2: stop loss
            if (isLong  && Low[0]  <= _activeStopPrice) { CerrarPosicion("StopLoss"); return; }
            if (!isLong && High[0] >= _activeStopPrice) { CerrarPosicion("StopLoss"); return; }

            // Prioridad 3: Breakeven automático tras X puntos de ganancia
            if (UseBreakEven && !_breakEvenTriggered)
            {
                bool beAlcanzado = isLong
                    ? High[0] >= _activeEntryPrice + BreakEvenPoints
                    : Low[0]  <= _activeEntryPrice - BreakEvenPoints;

                if (beAlcanzado)
                {
                    _breakEvenTriggered = true;
                    double bePrice = _activeEntryPrice
                        + (isLong ? 1 : -1) * BreakEvenLockTicks * TickSize;

                    bool mejora = isLong ? bePrice > _activeStopPrice
                                        : bePrice < _activeStopPrice;
                    if (mejora)
                    {
                        _activeStopPrice = bePrice;   // SetStopLoss se llama al final del método
                        Print($"[STCFollower] BE activado +{BreakEvenPoints}pts — stop→{_activeStopPrice:F2}");
                    }

                    if (TrailAfterBreakEven && !_trailingActive)
                    {
                        _trailingActive    = true;
                        _trailingStopPrice = _activeStopPrice;
                    }
                }
            }

            // Prioridad 4: TP1 parcial (50%) → mover stop a breakeven (si BE no lo hizo antes)
            if (!_tp1Hit)
            {
                bool tp1Alcanzado = isLong ? High[0] >= _activeTarget1 : Low[0] <= _activeTarget1;
                if (tp1Alcanzado)
                {
                    _tp1Hit    = true;
                    tp1EsteBar = true;
                    int mitad  = Math.Max(1, Position.Quantity / 2);

                    if (isLong) ExitLong (mitad, "STC_TP1", entryName);
                    else        ExitShort(mitad, "STC_TP1", entryName);

                    // Solo mover stop a entry si BE no lo puso ya más cerca
                    bool beYaMejorStop = isLong
                        ? _activeStopPrice >= _activeEntryPrice
                        : _activeStopPrice <= _activeEntryPrice;
                    if (!beYaMejorStop)
                        _activeStopPrice = _activeEntryPrice;  // SetStopLoss se llama al final

                    if (TrailAfterTP1)
                    {
                        _trailingActive    = true;
                        _trailingStopPrice = _activeStopPrice;
                    }

                    if (_activeRecord != null) _activeRecord.Tp1Hit = true;
                    Print($"[STCFollower] TP1 alcanzado — stop→{_activeStopPrice:F2}");
                }
            }

            // Prioridad 5: trailing stop (activo tras TP1 o BE)
            if (_trailingActive)
            {
                double nuevoTrail;
                if (UseFixedTrail)
                {
                    nuevoTrail = isLong
                        ? Close[0] - TrailingPoints
                        : Close[0] + TrailingPoints;
                }
                else
                {
                    nuevoTrail = _riskManager.CalculateTrailingStop(Close[0], _activeDirection, ATR(14)[0]);
                }

                if (isLong  && nuevoTrail > _trailingStopPrice) _trailingStopPrice = nuevoTrail;
                if (!isLong && nuevoTrail < _trailingStopPrice) _trailingStopPrice = nuevoTrail;

                _activeStopPrice = _trailingStopPrice;  // SetStopLoss se llama al final

                // Solo chequear salida si el trailing ya estaba activo desde una barra anterior
                if (trailingYaActivo)
                {
                    if (isLong  && Low[0]  <= _trailingStopPrice) { CerrarPosicion("TrailingStop"); return; }
                    if (!isLong && High[0] >= _trailingStopPrice) { CerrarPosicion("TrailingStop"); return; }
                }
            }

            // Prioridad 6: TP2 final → cerrar posición restante
            bool tp2Alcanzado = isLong ? High[0] >= _activeTarget2 : Low[0] <= _activeTarget2;
            if (tp2Alcanzado)
            {
                if (_activeRecord != null) _activeRecord.Tp2Hit = true;
                CerrarPosicion("TP2_Final");
                return;
            }

            // UNA sola llamada a SetStopLoss por bar — evita "Unable to change order".
            // Excepción: no modificar el bracket en el mismo bar que se ejecuta TP1 parcial
            // (ExitLong/Short ya está pendiente; NT8 rechaza modificaciones simultáneas).
            if (!tp1EsteBar)
                SetStopLoss(entryName, CalculationMode.Price, _activeStopPrice, false);
        }

        // ─── Cerrar posición ──────────────────────────────────────────────────
        private void CerrarPosicion(string razon)
        {
            bool   isLong    = _activeDirection == "LONG";
            string entryName = isLong ? "STC_Long" : "STC_Short";

            if (isLong) ExitLong ("STC_Salida_" + razon, entryName);
            else        ExitShort("STC_Salida_" + razon, entryName);

            FinalizarPosicion(razon);
        }

        private void FinalizarPosicion(string razon)
        {
            bool   isLong  = _activeDirection == "LONG";
            double tickVal = Instrument.MasterInstrument.PointValue * TickSize;

            // Usar el precio real del fill de salida; si no se capturó, usar Close[0]
            double exitPrice = _lastExitPrice > 0 ? _lastExitPrice : Close[0];
            double pnlTicks = isLong
                ? (exitPrice - _activeEntryPrice) / TickSize
                : (_activeEntryPrice - exitPrice) / TickSize;
            double pnlUsd = pnlTicks * tickVal * (_activeRecord?.Contracts ?? 1);
            _lastExitPrice = 0; // resetear para el próximo trade

            _riskManager.RegisterTradeResult(pnlUsd);

            if (_activeRecord != null)
            {
                _activeRecord.TimestampExit = Time[0];
                _activeRecord.ExitPrice     = Close[0];
                _activeRecord.PnlTicks      = pnlTicks;
                _activeRecord.PnlUsd        = pnlUsd;
                _activeRecord.ExitReason    = razon;
                _journal.LogComplete(_activeRecord);
            }

            RemoveDrawObject("STC_TP1");
            RemoveDrawObject("STC_TP2");
            RemoveDrawObject("STC_TP1_lbl");
            RemoveDrawObject("STC_TP2_lbl");

            Draw.Text(this, $"X_{CurrentBar}", $"[{razon}]", 0,
                isLong ? High[0] + TickSize * 6 : Low[0] - TickSize * 6,
                Brushes.Silver);

            Print($"[STCFollower] SALIDA {razon} | PnL={pnlTicks:F1}t (${pnlUsd:F2})");

            _positionOpen      = false;
            _entrySubmitted    = false;
            _activeRecord      = null;
            _trailingActive    = false;
            _trailingStopPrice = 0;
        }

        // ─── Callbacks de órdenes ─────────────────────────────────────────────
        protected override void OnExecutionUpdate(Execution execution, string executionId,
                                                  double price, int quantity,
                                                  MarketPosition marketPosition,
                                                  string orderId, DateTime time)
        {
            if (execution?.Order == null ||
                (execution.Order.OrderState != OrderState.Filled &&
                 execution.Order.OrderState != OrderState.PartFilled)) return;

            string name = execution.Order.Name ?? "";
            if (name == "STC_Long" || name == "STC_Short")
            {
                _positionOpen     = true;
                _entrySubmitted   = true;
                _activeEntryPrice = price;
                if (_activeRecord != null) _activeRecord.EntryPrice = price;
            }
            else
            {
                // Cualquier fill de salida (stop, TP1, TP2, salidas manuales):
                // guardar el precio real para usarlo en el cálculo de P&L.
                _lastExitPrice = price;
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice,
                                              double stopPrice, int quantity,
                                              int filled, double averageFillPrice,
                                              OrderState orderState, DateTime time,
                                              ErrorCode error, string nativeError)
        {
            if (order == null) return;
            string name = order.Name ?? "";
            if (name != "STC_Long" && name != "STC_Short") return;

            if (orderState == OrderState.Rejected || orderState == OrderState.Cancelled)
            {
                _entrySubmitted = false;
                _positionOpen   = Position.MarketPosition != MarketPosition.Flat;
                Print($"[STCFollower] Orden {orderState}: {nativeError}");
            }
        }

        // ─── Helper horario ───────────────────────────────────────────────────
        private bool EsHorarioCalidad()
        {
            var h = Time[0].TimeOfDay;
            return (h >= new TimeSpan(10, 0, 0) && h <= new TimeSpan(11, 30, 0)) ||
                   (h >= new TimeSpan(14, 0, 0) && h <= new TimeSpan(15, 30, 0));
        }

        // ─── Parámetros editables ─────────────────────────────────────────────

        // 1. Nube
        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "Período TriggerLine", Order = 1, GroupName = "1. Nube")]
        public int TriggerPeriod { get; set; }

        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "Período SmoothTrendLine", Order = 2, GroupName = "1. Nube")]
        public int SmoothPeriod { get; set; }

        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Barras mínimas en tendencia (Tipo 2)", Order = 3, GroupName = "1. Nube")]
        public int MinTrendBarsForPullback { get; set; }

        // 2. Señales
        [NinjaScriptProperty]
        [Display(Name = "Operar cruces (Tipo 1 — TrendStart)", Order = 1, GroupName = "2. Señales")]
        public bool EnterOnTipo1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Operar toques de nube (Tipo 2 — CloudPullback)", Order = 2, GroupName = "2. Señales")]
        public bool EnterOnTipo2 { get; set; }

        // 3. Riesgo
        [NinjaScriptProperty, Range(1000, 10_000_000)]
        [Display(Name = "Capital de cuenta ($)", Order = 1, GroupName = "3. Riesgo")]
        public double AccountCapital { get; set; }

        [NinjaScriptProperty, Range(0.001, 0.05)]
        [Display(Name = "% riesgo por trade", Order = 2, GroupName = "3. Riesgo")]
        public double RiskPctPerTrade { get; set; }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Máximo de contratos", Order = 3, GroupName = "3. Riesgo")]
        public int MaxContracts { get; set; }

        [NinjaScriptProperty, Range(0.5, 5.0)]
        [Display(Name = "Multiplicador stop × ancho de nube (Tipo 1)", Order = 4, GroupName = "3. Riesgo")]
        public double StopCloudMultiplier { get; set; }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Buffer stop en ticks (Tipo 2)", Order = 5, GroupName = "3. Riesgo")]
        public int StopBufferTicks { get; set; }

        [NinjaScriptProperty, Range(0.5, 5.0)]
        [Display(Name = "Multiplicador ATR (piso de stop)", Order = 6, GroupName = "3. Riesgo")]
        public double StopATRMultiplier { get; set; }

        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Mínimo de ticks de stop", Order = 7, GroupName = "3. Riesgo")]
        public int MinStopTicks { get; set; }

        [NinjaScriptProperty, Range(1.0, 10.0)]
        [Display(Name = "Ratio TP1 (múltiplo de riesgo)", Order = 8, GroupName = "3. Riesgo")]
        public double TP1Ratio { get; set; }

        [NinjaScriptProperty, Range(1.0, 20.0)]
        [Display(Name = "Ratio TP2 (múltiplo de riesgo)", Order = 9, GroupName = "3. Riesgo")]
        public double TP2Ratio { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trailing stop activo tras TP1", Order = 10, GroupName = "3. Riesgo")]
        public bool TrailAfterTP1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trailing activo tras Breakeven", Order = 11, GroupName = "3. Riesgo",
                 Description = "Activa trailing stop automáticamente cuando se alcanza el breakeven.")]
        public bool TrailAfterBreakEven { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Usar trailing por puntos fijos (no ATR)", Order = 12, GroupName = "3. Riesgo",
                 Description = "Si activado: trailing fijo en puntos. Si desactivado: trailing por ATR × multiplicador.")]
        public bool UseFixedTrail { get; set; }

        [NinjaScriptProperty, Range(1.0, 500.0)]
        [Display(Name = "Trailing fijo (puntos)", Order = 13, GroupName = "3. Riesgo",
                 Description = "Distancia en puntos del trailing fijo. Sólo aplica si 'Trailing por puntos fijos' está activado.")]
        public double TrailingPoints { get; set; }

        [NinjaScriptProperty, Range(0.1, 10.0)]
        [Display(Name = "Multiplicador ATR para trailing", Order = 14, GroupName = "3. Riesgo",
                 Description = "Distancia del trailing = ATR(14) × este valor. Sólo aplica si el trailing es por ATR.")]
        public double TrailingATRMultiplier { get; set; }

        // 4. Breakeven
        [NinjaScriptProperty]
        [Display(Name = "Activar breakeven automático", Order = 1, GroupName = "4. Breakeven")]
        public bool UseBreakEven { get; set; }

        [NinjaScriptProperty, Range(1.0, 500.0)]
        [Display(Name = "Puntos de ganancia para activar BE",
                 Description = "Cuando el precio avanza este número de puntos desde la entrada, el stop se mueve a BE",
                 Order = 2, GroupName = "4. Breakeven")]
        public double BreakEvenPoints { get; set; }

        [NinjaScriptProperty, Range(0, 50)]
        [Display(Name = "Ticks de lock-in en BE (0 = entrada exacta)",
                 Description = "Ticks adicionales sobre la entrada al mover el stop. 0 = breakeven puro.",
                 Order = 3, GroupName = "4. Breakeven")]
        public int BreakEvenLockTicks { get; set; }

        // 5. Límites diarios (antes grupo 4)
        [NinjaScriptProperty, Range(0.001, 0.20)]
        [Display(Name = "Pérdida diaria máxima (%)", Order = 1, GroupName = "5. Límites diarios",
                 Description = "Detiene el trading cuando la pérdida del día supera este % del capital. Ej: 0.05 = 5%")]
        public double MaxDailyLossPct { get; set; }

        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Trades máximos por día", Order = 2, GroupName = "5. Límites diarios")]
        public int MaxDailyTrades { get; set; }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Pérdidas consecutivas máximas", Order = 3, GroupName = "5. Límites diarios")]
        public int MaxConsecLosses { get; set; }

        // 6. Sesión
        [NinjaScriptProperty]
        [Display(Name = "Filtro horario de calidad (10:00–11:30 / 14:00–15:30 ET)",
                 Order = 1, GroupName = "6. Sesión")]
        public bool UseTimeFilter { get; set; }

        [NinjaScriptProperty, Range(0, 20)]
        [Display(Name = "Cooldown barras tras señal (0 = desactivado)",
                 Order = 2, GroupName = "6. Sesión")]
        public int SetupCooldownBars { get; set; }

        // 7. Visualización
        [NinjaScriptProperty]
        [Display(Name = "Mostrar nube en chart", Order = 1, GroupName = "7. Visualización")]
        public bool ShowCloudInStrategy { get; set; }
    }
}
