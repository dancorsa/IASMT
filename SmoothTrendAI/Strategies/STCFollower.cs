// ============================================================
// STCFollower.cs — Estrategia réplica de señales SmoothTrendCloud
// Proyecto: SmoothTrendAI  |  Prefijo: STC
//
// Entra exactamente cuando el indicador SmoothTrendCloud marca flecha:
//   Tipo 1 (TrendStart)    → cruce de TriggerLine sobre SmoothTrendLine
//   Tipo 2 (CloudPullback) → toque de nube + N barras en tendencia
//
// Gestión: stop calculado + TP1 (2 ctrs 1:1) + TP2 (2 ctrs 2:1) + TP3 (2 ctrs 3:1).
// NT8 maneja todos los fills nativamente via brackets de 3 slots.
// ============================================================
#region Using declarations
using System;
using System.Collections.Generic;
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
        private double         _activeTarget3;
        private STATradeRecord _activeRecord;

        // ─── Breakeven ────────────────────────────────────────────────────────
        private bool _breakEvenTriggered;

        // ─── Auxiliares ───────────────────────────────────────────────────────
        private DateTime _lastDailyReset  = DateTime.MinValue;
        private DateTime _lastWeeklyReset = DateTime.MinValue;
        private int      _lastSetupBar    = -999;
        private double   _lastExitPrice   = 0;
        private ADX      _adx;

        // Tiempo en posición
        private int _entryBar = -1;

        // Prior day S/R
        private double _priorDayHigh = double.MaxValue;
        private double _priorDayLow  = double.MinValue;
        private double _currentDayHigh;
        private double _currentDayLow;

        // Tasa de barras (rolling 60 min)
        private readonly Queue<DateTime> _barTimes = new Queue<DateTime>();

        // Estado visible en el tablero — razón del último bloqueo o "✓ ENTRADA"
        private string _dashSignalStatus = "—  sin señal";

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
                StartBehavior                = StartBehavior.WaitUntilFlat;
                IsFillLimitOnTouch           = false;
                Slippage                     = 0;
                EntriesPerDirection          = 3;   // n1=TP1 + n2=TP2 + n3=TP3, tres slots en la misma dirección

                // Nube
                TriggerPeriod           = 12;
                SmoothPeriod            = 12;
                MinTrendBarsForPullback = 8;

                // Qué señales operar
                EnterOnTipo1 = true;
                EnterOnTipo2 = true;

                // Riesgo
                AccountCapital      = 50_000.0;
                RiskPctPerTrade     = 0.005;
                MaxContracts        = 6;
                StopCloudMultiplier = 1.5;
                StopBufferTicks     = 6;
                StopATRMultiplier   = 1.5;
                MinStopTicks        = 20;
                UseFixedContracts   = true;
                FixedContracts      = 6;
                ContratoTP1         = 2;
                ContratoTP2         = 2;
                ContratoTP3         = 2;
                TP1Ratio            = 1.0;
                TP2Ratio            = 2.0;
                TP3Ratio            = 3.0;

                // Breakeven
                UseBreakEven       = true;
                BreakEvenPoints    = 0;    // 0 = activar en TP1
                BreakEvenLockTicks = 4;

                // Límites diarios y semanales
                MaxDailyLossPct  = 0.018;
                MaxDailyTrades   = 10;
                MaxConsecLosses  = 3;
                MaxWeeklyTrades  = 30;
                MaxWeeklyLossPct = 0.05;

                // Entrada y calidad
                UseLimitEntry        = false;
                LimitOffsetTicks     = 2;
                UseMinRR             = true;
                MinRR                = 1.5;
                UseTimeExit          = true;
                MaxBarsInTrade       = 20;
                UseSRFilter          = true;
                SRDistanceTicks      = 10;
                UseBarRateFilter     = false;
                MinBarsPerHour       = 3;
                MaxBarsPerHour       = 60;
                UseAdaptiveSizing    = false;
                AdaptiveSizingThreshold = 40;

                // Sesión
                UseTimeFilter     = false;
                HoraInicio        = 1000;   // 10:00
                HoraFin           = 1530;   // 15:30
                SetupCooldownBars = 3;
                UseADXFilter      = true;
                ADXPeriod         = 14;
                ADXMinTrend       = 20;

                // Visualización
                ShowCloudInStrategy = true;
                ShowDashboard       = true;
                DashboardPosition   = TextPosition.TopRight;
            }
            else if (State == State.Configure)
            {
                AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Line, "TriggerLine");
                AddPlot(new Stroke(Brushes.Gold,        2), PlotStyle.Line, "SmoothTrendLine");

                _riskManager = new STARiskManager
                {
                    AccountCapital         = AccountCapital,
                    RiskPctPerTrade        = RiskPctPerTrade,
                    MaxContracts           = MaxContracts,
                    StopCloudMultiplier    = StopCloudMultiplier,
                    StopBufferTicks        = StopBufferTicks,
                    StopATRMultiplier      = StopATRMultiplier,
                    MinStopTicks           = MinStopTicks,
                    TrailAfterTP1          = false,
                    TrendStart_TP1Mult     = TP1Ratio,
                    TrendStart_TP2Mult     = TP2Ratio,
                    CloudPullback_TP1Mult  = TP1Ratio,
                    CloudPullback_TP2Mult  = TP2Ratio,
                    MaxDailyLossPct        = MaxDailyLossPct,
                    MaxDailyTrades         = MaxDailyTrades,
                    MaxConsecutiveLosses   = MaxConsecLosses,
                    MaxTrendStartPerDay    = MaxDailyTrades,
                    MaxCloudPullbackPerDay = MaxDailyTrades,
                    TrailingATRMultiplier  = 1.5,
                    MaxDailyProfitPct      = 1.0,
                    MaxWeeklyTrades        = MaxWeeklyTrades,
                    MaxWeeklyLossPct       = MaxWeeklyLossPct
                };

                _journal = new STATradeJournal();
            }
            else if (State == State.DataLoaded)
            {
                _cloud = SmoothTrendCloud(
                    TriggerPeriod, SmoothPeriod, MinTrendBarsForPullback,
                    10, Brushes.Cyan, Brushes.Orange,
                    false, false, false, "Claude", "", 0.60,
                    false, false, false, false, false, false,
                    3, 2.0, 4.0);

                _cloudUpBrush   = new SolidColorBrush(Color.FromArgb(28, 0, 180, 255));
                _cloudDownBrush = new SolidColorBrush(Color.FromArgb(28, 255, 120, 0));
                _cloudUpBrush.Freeze();
                _cloudDownBrush.Freeze();

                _journal.Open(Instrument.FullName);
                _adx = ADX(ADXPeriod);
            }
            else if (State == State.Realtime)
            {
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

            // ── Nube visual ───────────────────────────────────────────────────
            if (ShowCloudInStrategy && _cloud != null && _cloud.TriggerValue > 0)
            {
                Values[0][0]   = _cloud.TriggerValue;
                Values[1][0]   = _cloud.SmoothValue;
                BackBrushes[0] = _cloud.CurrentCloudColor == "Up" ? _cloudUpBrush : _cloudDownBrush;
            }

            // ── Tablero de condiciones ────────────────────────────────────────
            if (ShowDashboard && _cloud != null && _adx != null)
                DibujarTablero();

            // ── Detectar cierre por broker (stop o TP llenó) ─────────────────
            if (_positionOpen && Position.MarketPosition == MarketPosition.Flat)
            {
                FinalizarPosicion("BrokerExit");
                return;
            }
            if (_entrySubmitted && !_positionOpen) return;   // esperando fill

            // ── Reset semanal ─────────────────────────────────────────────────
            if (_lastWeeklyReset == DateTime.MinValue ||
                GetISOWeek(Time[0]) != GetISOWeek(_lastWeeklyReset))
            {
                _riskManager.ResetWeekly();
                _lastWeeklyReset = Time[0];
            }

            // ── Reset diario ──────────────────────────────────────────────────
            if (Time[0].Date != _lastDailyReset.Date)
            {
                _priorDayHigh   = _currentDayHigh > 0 ? _currentDayHigh : double.MaxValue;
                _priorDayLow    = _currentDayLow  > 0 ? _currentDayLow  : double.MinValue;
                _currentDayHigh = High[0];
                _currentDayLow  = Low[0];
                _riskManager.ResetDaily();
                _lastDailyReset = Time[0].Date;
            }
            else
            {
                _currentDayHigh = Math.Max(_currentDayHigh, High[0]);
                _currentDayLow  = Math.Min(_currentDayLow,  Low[0]);
            }

            // ── Tasa de barras (rolling 60 min) ───────────────────────────────
            _barTimes.Enqueue(Time[0]);
            while (_barTimes.Count > 0 && (Time[0] - _barTimes.Peek()).TotalMinutes > 60)
                _barTimes.Dequeue();

            // ── Posición abierta: solo salida por cruce opuesto ──────────────
            if (_positionOpen)
            {
                GestionarPosicionAbierta();
                return;
            }

            // ── Verificar límites diarios ─────────────────────────────────────
            if (!_riskManager.CanTrade())
            {
                _dashSignalStatus = $"⚠ RIESGO  trades={_riskManager.DailyTrades}/{MaxDailyTrades}" +
                                    $"  pérd={_riskManager.ConsecutiveLosses}/{MaxConsecLosses}" +
                                    $"  P&L=${_riskManager.DailyPnL:F0}";
                Print($"[STC-DIAG] Bar={CurrentBar} {Time[0]:HH:mm} BLOQUEADO: límites diarios " +
                      $"trades={_riskManager.DailyTrades}/{MaxDailyTrades} " +
                      $"pérdidas={_riskManager.ConsecutiveLosses}/{MaxConsecLosses} " +
                      $"PnL=${_riskManager.DailyPnL:F0} " +
                      $"pérdidaLím=${-(AccountCapital * MaxDailyLossPct):F0}");
                return;
            }

            // ── Diagnóstico ───────────────────────────────────────────────────
            Print($"[STC-DIAG] Bar={CurrentBar} {Time[0]:HH:mm} " +
                  $"CX↑={_cloud.HasCrossUpSignal} CX↓={_cloud.HasCrossDownSignal} " +
                  $"Touch↑={_cloud.TouchedCloudFromAbove} Touch↓={_cloud.TouchedCloudFromBelow} " +
                  $"BarsEnColor={_cloud.BarsInCurrentColor} Nube={_cloud.CurrentCloudColor}");

            // ── Detectar señal ────────────────────────────────────────────────
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

            if (direction == null)
            {
                _dashSignalStatus = "—  sin señal";
                return;
            }

            // Con señal detectada, asumir "pendiente de filtros" hasta que pasen todos
            _dashSignalStatus = $"↓ {direction}  {setupType}  evaluando filtros...";

            // ── Filtro horario ────────────────────────────────────────────────
            if (UseTimeFilter && !EsHorarioCalidad())
            {
                _dashSignalStatus = $"⚠ HORARIO  {Time[0]:HH:mm}  fuera de {HoraInicio/100:D2}:{HoraInicio%100:D2}–{HoraFin/100:D2}:{HoraFin%100:D2}";
                Print($"[STC-DIAG] BLOQUEADO TimeFilter: {direction} {setupType} {Time[0]:HH:mm}");
                return;
            }

            // ── Cooldown ──────────────────────────────────────────────────────
            if (SetupCooldownBars > 0 && CurrentBar - _lastSetupBar < SetupCooldownBars)
            {
                _dashSignalStatus = $"⚠ COOLDOWN  {CurrentBar - _lastSetupBar}/{SetupCooldownBars} barras desde último setup";
                Print($"[STC-DIAG] BLOQUEADO Cooldown: {direction} {setupType} " +
                      $"barrasDesdeUltima={CurrentBar - _lastSetupBar} min={SetupCooldownBars}");
                return;
            }

            // ── Filtro ADX (evitar zonas de indecisión) ───────────────────────
            if (UseADXFilter && _adx[0] < ADXMinTrend)
            {
                _dashSignalStatus = $"⚠ ADX={_adx[0]:F1}  <{ADXMinTrend}  (mercado sin tendencia)";
                Print($"[STC-DIAG] BLOQUEADO ADX: {direction} {setupType} " +
                      $"ADX={_adx[0]:F1} < {ADXMinTrend} (indecisión/neutro)");
                return;
            }

            // ── Filtro tasa de barras ─────────────────────────────────────────
            if (UseBarRateFilter)
            {
                int bph = _barTimes.Count;
                if (bph < MinBarsPerHour)
                {
                    _dashSignalStatus = $"⚠ BARRATE  {bph} barras/h  <{MinBarsPerHour}  (mercado muerto)";
                    Print($"[STC-DIAG] BLOQUEADO BarRate: {bph} barras/hora < {MinBarsPerHour} (mercado muerto)");
                    return;
                }
                if (bph > MaxBarsPerHour)
                {
                    _dashSignalStatus = $"⚠ BARRATE  {bph} barras/h  >{MaxBarsPerHour}  (mercado caótico)";
                    Print($"[STC-DIAG] BLOQUEADO BarRate: {bph} barras/hora > {MaxBarsPerHour} (mercado caótico)");
                    return;
                }
            }

            // ── Filtro distancia a S/R del día anterior ───────────────────────
            if (UseSRFilter && _priorDayHigh < double.MaxValue)
            {
                double distH = Math.Abs(Close[0] - _priorDayHigh);
                double distL = Math.Abs(Close[0] - _priorDayLow);
                double minDist = SRDistanceTicks * TickSize;
                if (distH < minDist || distL < minDist)
                {
                    _dashSignalStatus = $"⚠ S/R  PDH={_priorDayHigh:F2}  PDL={_priorDayLow:F2}  dist<{SRDistanceTicks}t";
                    Print($"[STC-DIAG] BLOQUEADO S/R: precio cerca de PDH={_priorDayHigh:F2} / PDL={_priorDayLow:F2} " +
                          $"(distancia mín={SRDistanceTicks}t)");
                    return;
                }
            }

            _lastSetupBar = CurrentBar;

            // ── Límite por tipo de setup ──────────────────────────────────────
            if (!_riskManager.CanTrade(setupType))
            {
                _dashSignalStatus = $"⚠ RIESGO  límite tipo {setupType}" +
                                    $"  (sem={_riskManager.WeeklyTrades}/{MaxWeeklyTrades})";
                Print($"[STC-DIAG] BLOQUEADO RiskManager por tipo: {setupType}");
                return;
            }

            // ── Calcular riesgo ────────────────────────────────────────────────
            double atr14   = ATR(14)[0];
            double tickVal = Instrument.MasterInstrument.PointValue * TickSize;

            // Sizing adaptativo
            if (UseAdaptiveSizing && _riskManager.DailyTrades >= 2)
                _riskManager.AdaptiveSizeFactor = _riskManager.DailyWinRate < AdaptiveSizingThreshold / 100.0
                    ? 0.5 : 1.0;

            var setupInfo = new STASetupResult
            {
                SetupType      = setupType,
                Direction      = direction,
                ElliottContext = ""
            };

            var risk = _riskManager.Calculate(
                setupInfo,
                Close[0],
                direction,
                _cloud.CloudWidth,
                atr14,
                Low[0],
                High[0],
                TickSize,
                tickVal,
                aiRiskAdjustment:  1.0,
                elliottMultiplier: 1.0,
                marketContext:     "Bullish");

            // ── Filtro R:R mínimo ─────────────────────────────────────────────
            if (UseMinRR)
            {
                bool   isLongCheck = direction == "LONG";
                double tp1Dist  = isLongCheck ? risk.Target1Price - Close[0] : Close[0] - risk.Target1Price;
                double stopDist = isLongCheck ? Close[0] - risk.StopPrice    : risk.StopPrice - Close[0];
                double rr       = stopDist > 0 ? tp1Dist / stopDist : 0;
                if (rr < MinRR)
                {
                    _dashSignalStatus = $"⚠ R:R={rr:F2}  <{MinRR}  (ratio insuficiente)";
                    Print($"[STC-DIAG] BLOQUEADO R:R: {direction} {setupType} R:R={rr:F2} < {MinRR}");
                    return;
                }
            }

            _dashSignalStatus = $"✓ {direction}  {setupType}  —  ENTRADA";
            EjecutarEntrada(direction, setupType, risk);
        }

        // ─── Entrada con brackets nativos NT8 ────────────────────────────────
        // Tres slots: TP1 (2 ctrs, 1:1) + TP2 (2 ctrs, 2:1) + TP3 (resto, 3:1).
        // SetStopLoss + SetProfitTarget se llaman ANTES de EnterLong/Short;
        // NT8 aplica los brackets automáticamente al fill de cada slot.
        private void EjecutarEntrada(string direction, string setupType, STARiskParameters risk)
        {
            bool isLong = direction == "LONG";
            int  tp1Qty, tp2Qty, tp3Qty;

            if (UseFixedContracts)
            {
                tp1Qty = Math.Max(1, ContratoTP1);
                tp2Qty = Math.Max(0, ContratoTP2);
                tp3Qty = Math.Max(0, ContratoTP3);
            }
            else
            {
                int total = risk.Contracts;
                if (total <= 1)
                {
                    tp1Qty = Math.Max(1, total); tp2Qty = 0; tp3Qty = 0;
                }
                else if (total == 2)
                {
                    tp1Qty = 1; tp2Qty = 1; tp3Qty = 0;
                }
                else
                {
                    int tercio = total / 3;
                    tp1Qty = tercio;
                    tp2Qty = tercio;
                    tp3Qty = total - 2 * tercio;
                }
            }
            string n1       = isLong ? "STC_TP1_Long"  : "STC_TP1_Short";
            string n2       = isLong ? "STC_TP2_Long"  : "STC_TP2_Short";
            string n3       = isLong ? "STC_TP3_Long"  : "STC_TP3_Short";

            // TP3 calculado localmente: misma distancia de stop × TP3Ratio
            double stopDist     = isLong ? (Close[0] - risk.StopPrice) : (risk.StopPrice - Close[0]);
            double target3Price = isLong
                ? Close[0] + stopDist * TP3Ratio
                : Close[0] - stopDist * TP3Ratio;
            target3Price = Math.Round(target3Price / TickSize) * TickSize;

            // Precio límite para CloudPullback (solo aplica cuando UseLimitEntry=true)
            bool   usarLimite = UseLimitEntry && setupType == "CloudPullback";
            double limitPrice = isLong
                ? Close[0] - LimitOffsetTicks * TickSize
                : Close[0] + LimitOffsetTicks * TickSize;

            // Slot TP1
            SetStopLoss   (n1, CalculationMode.Price, risk.StopPrice,    false);
            SetProfitTarget(n1, CalculationMode.Price, risk.Target1Price);
            if (usarLimite) { if (isLong) EnterLongLimit (tp1Qty, limitPrice, n1); else EnterShortLimit(tp1Qty, limitPrice, n1); }
            else            { if (isLong) EnterLong       (tp1Qty, n1);            else EnterShort      (tp1Qty, n1);            }

            // Slot TP2
            if (tp2Qty > 0)
            {
                SetStopLoss   (n2, CalculationMode.Price, risk.StopPrice,    false);
                SetProfitTarget(n2, CalculationMode.Price, risk.Target2Price);
                if (usarLimite) { if (isLong) EnterLongLimit (tp2Qty, limitPrice, n2); else EnterShortLimit(tp2Qty, limitPrice, n2); }
                else            { if (isLong) EnterLong       (tp2Qty, n2);            else EnterShort      (tp2Qty, n2);            }
            }

            // Slot TP3
            if (tp3Qty > 0)
            {
                SetStopLoss   (n3, CalculationMode.Price, risk.StopPrice,  false);
                SetProfitTarget(n3, CalculationMode.Price, target3Price);
                if (usarLimite) { if (isLong) EnterLongLimit (tp3Qty, limitPrice, n3); else EnterShortLimit(tp3Qty, limitPrice, n3); }
                else            { if (isLong) EnterLong       (tp3Qty, n3);            else EnterShort      (tp3Qty, n3);            }
            }

            _entrySubmitted     = true;
            _positionOpen       = false;
            _activeDirection    = direction;
            _activeSetupType    = setupType;
            _activeEntryPrice   = Close[0];
            _activeStopPrice    = risk.StopPrice;
            _activeTarget1      = risk.Target1Price;
            _activeTarget2      = risk.Target2Price;
            _activeTarget3      = target3Price;
            _breakEvenTriggered = false;
            _lastExitPrice      = 0;
            _entryBar           = CurrentBar;

            _activeRecord = new STATradeRecord
            {
                TimestampEntry           = Time[0],
                Instrument               = Instrument.FullName,
                BarType                  = BarsPeriod.ToString(),
                Direction                = direction,
                SetupType                = setupType,
                EntryPrice               = Close[0],
                Contracts                = tp1Qty + tp2Qty + tp3Qty,
                TriggerLineAtEntry       = _cloud.TriggerValue,
                SmoothLineAtEntry        = _cloud.SmoothValue,
                CloudWidthAtEntry        = _cloud.CloudWidth,
                BarsInCurrentColor       = _cloud.BarsInCurrentColor,
                StopTicks                = risk.StopTicks,
                ConsecutiveLossesAtEntry = _riskManager.ConsecutiveLosses
            };

            // Dibujar señal en chart
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
            Draw.HorizontalLine(this, "STC_TP3", target3Price,
                Brushes.Gold, DashStyleHelper.Dash, 1);
            Draw.Text(this, "STC_TP1_lbl", $"TP1 {risk.Target1Price:F2}",
                0, risk.Target1Price + (isLong ? TickSize * 2 : -TickSize * 2), Brushes.Lime);
            Draw.Text(this, "STC_TP2_lbl", $"TP2 {risk.Target2Price:F2}",
                0, risk.Target2Price + (isLong ? TickSize * 2 : -TickSize * 2), Brushes.DodgerBlue);
            Draw.Text(this, "STC_TP3_lbl", $"TP3 {target3Price:F2}",
                0, target3Price + (isLong ? TickSize * 2 : -TickSize * 2), Brushes.Gold);

            Print($"[STCFollower] {direction} {setupType} | " +
                  $"Entry={Close[0]:F2} Stop={risk.StopPrice:F2} " +
                  $"TP1={risk.Target1Price:F2} TP2={risk.Target2Price:F2} TP3={target3Price:F2} " +
                  $"Ctrs={tp1Qty + tp2Qty + tp3Qty} (TP1={tp1Qty} TP2={tp2Qty} TP3={tp3Qty})");
        }

        // ─── Gestión: cruce opuesto + breakeven ──────────────────────────────
        // Stops y TPs los gestiona NT8 nativamente via brackets.
        // SetStopLoss en esta función solo mueve el stop existente (una vez, al BE).
        private void GestionarPosicionAbierta()
        {
            bool   isLong = _activeDirection == "LONG";
            string n1     = isLong ? "STC_TP1_Long"  : "STC_TP1_Short";
            string n2     = isLong ? "STC_TP2_Long"  : "STC_TP2_Short";
            string n3     = isLong ? "STC_TP3_Long"  : "STC_TP3_Short";

            // Salida por tiempo (sin progreso en N barras)
            if (UseTimeExit && _entryBar >= 0 && CurrentBar - _entryBar >= MaxBarsInTrade)
            {
                Print($"[STCFollower] SALIDA por tiempo: {MaxBarsInTrade} barras sin resolver la posición");
                CerrarPosicion("TimeExit");
                return;
            }

            // Salida por cruce opuesto
            if (isLong  && _cloud.HasCrossDownSignal) { CerrarPosicion("CloudCruce_Opuesto"); return; }
            if (!isLong && _cloud.HasCrossUpSignal)   { CerrarPosicion("CloudCruce_Opuesto"); return; }

            // Breakeven: BreakEvenPoints=0 → activar en TP1; >0 → activar a X puntos de ganancia.
            // En modo TP1: n1 ya cierra ese bar, solo se mueve el stop de n2.
            // En modo puntos: n1 y n2 pueden estar aún abiertos, se mueven ambos.
            if (UseBreakEven && !_breakEvenTriggered)
            {
                bool beAlcanzado = BreakEvenPoints <= 0
                    ? (isLong ? High[0] >= _activeTarget1      : Low[0] <= _activeTarget1)
                    : (isLong ? High[0] >= _activeEntryPrice + BreakEvenPoints
                              : Low[0]  <= _activeEntryPrice - BreakEvenPoints);

                if (beAlcanzado)
                {
                    _breakEvenTriggered = true;
                    double bePrice = _activeEntryPrice
                        + (isLong ? 1 : -1) * BreakEvenLockTicks * TickSize;
                    bePrice = Math.Round(bePrice / TickSize) * TickSize;

                    bool mejora = isLong ? bePrice > _activeStopPrice
                                        : bePrice < _activeStopPrice;
                    if (mejora)
                    {
                        _activeStopPrice = bePrice;
                        if (BreakEvenPoints > 0)
                            SetStopLoss(n1, CalculationMode.Price, bePrice, false);
                        SetStopLoss(n2, CalculationMode.Price, bePrice, false);
                        SetStopLoss(n3, CalculationMode.Price, bePrice, false);
                        string modo = BreakEvenPoints <= 0 ? "TP1" : $"{BreakEvenPoints}pts";
                        Print($"[STCFollower] BE activado ({modo}) — stop→{bePrice:F2}");
                    }
                }
            }
        }

        // ─── Cerrar toda la posición (señal opuesta u otras salidas manuales) ─
        private void CerrarPosicion(string razon)
        {
            bool isLong = _activeDirection == "LONG";

            // ExitLong/Short con fromEntrySignal vacío = cerrar TODOS los slots
            if (isLong) ExitLong (Position.Quantity, "STC_Salida_" + razon, "");
            else        ExitShort(Position.Quantity, "STC_Salida_" + razon, "");

            FinalizarPosicion(razon);
        }

        private void FinalizarPosicion(string razon)
        {
            bool   isLong  = _activeDirection == "LONG";
            double tickVal = Instrument.MasterInstrument.PointValue * TickSize;

            double exitPrice = _lastExitPrice > 0 ? _lastExitPrice : Close[0];
            double pnlTicks  = isLong
                ? (exitPrice - _activeEntryPrice) / TickSize
                : (_activeEntryPrice - exitPrice) / TickSize;
            double pnlUsd = pnlTicks * tickVal * (_activeRecord?.Contracts ?? 1);
            _lastExitPrice = 0;

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
            RemoveDrawObject("STC_TP3");
            RemoveDrawObject("STC_TP1_lbl");
            RemoveDrawObject("STC_TP2_lbl");
            RemoveDrawObject("STC_TP3_lbl");

            Draw.Text(this, $"X_{CurrentBar}", $"[{razon}]", 0,
                isLong ? High[0] + TickSize * 6 : Low[0] - TickSize * 6,
                Brushes.Silver);

            Print($"[STCFollower] SALIDA {razon} | PnL={pnlTicks:F1}t (${pnlUsd:F2})");

            _positionOpen       = false;
            _entrySubmitted     = false;
            _activeRecord       = null;
            _activeDirection    = "";
            _breakEvenTriggered = false;
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
            if (name == "STC_TP1_Long" || name == "STC_TP1_Short" ||
                name == "STC_TP2_Long" || name == "STC_TP2_Short" ||
                name == "STC_TP3_Long" || name == "STC_TP3_Short")
            {
                // Solo registrar el trade al confirmar fill del broker
                if (!_positionOpen)
                    _riskManager.RegisterTrade(_activeSetupType ?? "CloudPullback");

                _positionOpen     = true;
                _entrySubmitted   = true;
                _activeEntryPrice = price;
                if (_activeRecord != null) _activeRecord.EntryPrice = price;
            }
            else
            {
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
            if (name != "STC_TP1_Long" && name != "STC_TP1_Short" &&
                name != "STC_TP2_Long" && name != "STC_TP2_Short" &&
                name != "STC_TP3_Long" && name != "STC_TP3_Short") return;

            if (orderState == OrderState.Rejected || orderState == OrderState.Cancelled)
            {
                _entrySubmitted = false;
                _positionOpen   = Position.MarketPosition != MarketPosition.Flat;
                Print($"[STCFollower] Orden {orderState}: {nativeError}");
            }
        }

        // ─── Tablero de condiciones ───────────────────────────────────────────
        private void DibujarTablero()
        {
            const string V  = "✓";
            const string X  = "✗";
            const string NA = "—";

            // Señal detectada esta barra
            string senal = "Sin señal";
            if      (EnterOnTipo2 && _cloud.TouchedCloudFromAbove) senal = "CloudPullback  LONG  ↑";
            else if (EnterOnTipo2 && _cloud.TouchedCloudFromBelow) senal = "CloudPullback  SHORT ↓";
            else if (EnterOnTipo1 && _cloud.HasCrossUpSignal)      senal = "TrendStart     LONG  ↑";
            else if (EnterOnTipo1 && _cloud.HasCrossDownSignal)    senal = "TrendStart     SHORT ↓";

            // Horario
            bool   timeOk  = !UseTimeFilter || EsHorarioCalidad();
            string timeStr = UseTimeFilter
                ? $"{(timeOk ? V : X)}  {Time[0]:HH:mm}  ({HoraInicio/100:D2}:{HoraInicio%100:D2}–{HoraFin/100:D2}:{HoraFin%100:D2})"
                : $"{NA}  desactivado";

            // ADX
            double adxVal = _adx[0];
            bool   adxOk  = !UseADXFilter || adxVal >= ADXMinTrend;
            string adxStr = UseADXFilter
                ? $"{(adxOk ? V : X)}  {adxVal:F1}  (mín {ADXMinTrend})"
                : $"{NA}  desactivado";

            // Cooldown
            int    barsDesdeLast = CurrentBar - _lastSetupBar;
            bool   coolOk        = SetupCooldownBars == 0 || barsDesdeLast >= SetupCooldownBars;
            string coolStr       = SetupCooldownBars > 0
                ? $"{(coolOk ? V : X)}  {barsDesdeLast} barras  (mín {SetupCooldownBars})"
                : $"{NA}  desactivado";

            // Riesgo diario y semanal
            bool   riskOk   = _riskManager.CanTrade();
            int    winRate   = _riskManager.DailyTrades > 0
                ? (int)Math.Round(_riskManager.DailyWinRate * 100) : 0;
            string riskStr  = $"{(riskOk ? V : X)}  {_riskManager.DailyTrades}/{MaxDailyTrades} trades  " +
                              $"WR={winRate}%  P&L=${_riskManager.DailyPnL:F0}  " +
                              $"sem={_riskManager.WeeklyTrades}/{MaxWeeklyTrades}";

            // Posición
            string posStr = _positionOpen
                ? $"{_activeDirection}  {Position.Quantity}c  entry={_activeEntryPrice:F2}"
                : "FLAT";

            string txt =
                "── STCFollower ──────────────────────────\n" +
                $" Señal   : {senal}\n"                       +
                $" Estado  : {_dashSignalStatus}\n"           +
                "─────────────────────────────────────────\n" +
                $" Horario : {timeStr}\n"                     +
                $" ADX     : {adxStr}\n"                      +
                $" Cooldown: {coolStr}\n"                     +
                $" Riesgo  : {riskStr}\n"                     +
                "─────────────────────────────────────────\n" +
                $" Posición: {posStr}\n";

            Draw.TextFixed(this, "STC_Dashboard", txt, DashboardPosition,
                Brushes.White,
                new NinjaTrader.Gui.Tools.SimpleFont("Courier New", 10),
                Brushes.Transparent, Brushes.Black, 80);
        }

        // ─── Helper horario ───────────────────────────────────────────────────
        private static int GetISOWeek(DateTime dt)
        {
            var day = System.Globalization.CultureInfo.InvariantCulture.Calendar
                .GetWeekOfYear(dt, System.Globalization.CalendarWeekRule.FirstFourDayWeek,
                               DayOfWeek.Monday);
            return dt.Year * 100 + day;
        }

        private TimeSpan HHMM(int hhmm) => new TimeSpan(hhmm / 100, hhmm % 100, 0);

        private bool EsHorarioCalidad()
        {
            var h = Time[0].TimeOfDay;
            return h >= HHMM(HoraInicio) && h <= HHMM(HoraFin);
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

        [NinjaScriptProperty, Range(1.0, 20.0)]
        [Display(Name = "Ratio TP3 (múltiplo de riesgo)", Order = 10, GroupName = "3. Riesgo")]
        public double TP3Ratio { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Usar contratos fijos (ignorar % riesgo)", Order = 11, GroupName = "3. Riesgo")]
        public bool UseFixedContracts { get; set; }

        [Range(1, 20)]
        [Display(Name = "Contratos fijos por entrada (obsoleto)", Order = 12, GroupName = "3. Riesgo")]
        public int FixedContracts { get; set; }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Contratos en TP1", Order = 13, GroupName = "3. Riesgo",
                 Description = "Contratos que salen en el primer objetivo. Solo aplica con 'Usar contratos fijos' activo.")]
        public int ContratoTP1 { get; set; }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Contratos en TP2", Order = 14, GroupName = "3. Riesgo",
                 Description = "Contratos que salen en el segundo objetivo.")]
        public int ContratoTP2 { get; set; }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Contratos en TP3", Order = 15, GroupName = "3. Riesgo",
                 Description = "Contratos que salen en el tercer objetivo.")]
        public int ContratoTP3 { get; set; }

        // 4. Breakeven
        [NinjaScriptProperty]
        [Display(Name = "Activar breakeven automático", Order = 1, GroupName = "4. Breakeven")]
        public bool UseBreakEven { get; set; }

        [NinjaScriptProperty, Range(0.0, 500.0)]
        [Display(Name = "Puntos para activar BE (0 = en TP1)", Order = 2, GroupName = "4. Breakeven",
                 Description = "0 = mover stop a BE cuando el precio alcanza TP1. Cualquier valor positivo = activar a esos puntos de ganancia desde la entrada.")]
        public double BreakEvenPoints { get; set; }

        [NinjaScriptProperty, Range(0, 50)]
        [Display(Name = "Ticks de lock-in en BE (0 = entrada exacta)", Order = 3, GroupName = "4. Breakeven",
                 Description = "Ticks adicionales sobre la entrada al mover el stop. 0 = breakeven puro.")]
        public int BreakEvenLockTicks { get; set; }

        // 5. Límites diarios
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

        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "Trades máximos por semana", Order = 4, GroupName = "5. Límites diarios")]
        public int MaxWeeklyTrades { get; set; }

        [NinjaScriptProperty, Range(0.01, 0.30)]
        [Display(Name = "Pérdida semanal máxima (%)", Order = 5, GroupName = "5. Límites diarios",
                 Description = "Detiene el trading cuando la pérdida semanal supera este % del capital.")]
        public double MaxWeeklyLossPct { get; set; }

        // 8. Entrada y calidad
        [NinjaScriptProperty]
        [Display(Name = "Entrada con orden límite en CloudPullback", Order = 1, GroupName = "8. Entrada y calidad",
                 Description = "Usa orden límite en lugar de mercado para CloudPullback. Busca mejor precio pero puede no llenar.")]
        public bool UseLimitEntry { get; set; }

        [NinjaScriptProperty, Range(0, 20)]
        [Display(Name = "Offset de la orden límite (ticks)", Order = 2, GroupName = "8. Entrada y calidad",
                 Description = "Ticks por debajo del cierre (LONG) o encima (SHORT) para el precio límite.")]
        public int LimitOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filtro R:R mínimo", Order = 3, GroupName = "8. Entrada y calidad")]
        public bool UseMinRR { get; set; }

        [NinjaScriptProperty, Range(0.5, 5.0)]
        [Display(Name = "R:R mínimo requerido (vs TP1)", Order = 4, GroupName = "8. Entrada y calidad",
                 Description = "Distancia al TP1 dividida entre distancia al stop. Ej: 1.5 = TP1 debe estar a 1.5× el riesgo.")]
        public double MinRR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Salida por tiempo", Order = 5, GroupName = "8. Entrada y calidad")]
        public bool UseTimeExit { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Barras máximas en posición", Order = 6, GroupName = "8. Entrada y calidad",
                 Description = "Cierra la posición si lleva este número de barras sin resolver.")]
        public int MaxBarsInTrade { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filtro distancia a S/R día anterior", Order = 7, GroupName = "8. Entrada y calidad",
                 Description = "Evita entrar cuando el precio está cerca del High o Low del día anterior.")]
        public bool UseSRFilter { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Distancia mínima a S/R (ticks)", Order = 8, GroupName = "8. Entrada y calidad")]
        public int SRDistanceTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filtro tasa de barras (mercado muerto/caótico)", Order = 9, GroupName = "8. Entrada y calidad")]
        public bool UseBarRateFilter { get; set; }

        [NinjaScriptProperty, Range(1, 30)]
        [Display(Name = "Barras/hora mínimas", Order = 10, GroupName = "8. Entrada y calidad",
                 Description = "Por debajo = mercado muerto. No opera.")]
        public int MinBarsPerHour { get; set; }

        [NinjaScriptProperty, Range(10, 200)]
        [Display(Name = "Barras/hora máximas", Order = 11, GroupName = "8. Entrada y calidad",
                 Description = "Por encima = mercado caótico. No opera.")]
        public int MaxBarsPerHour { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sizing adaptativo (reduce contratos si WR < umbral)", Order = 12, GroupName = "8. Entrada y calidad")]
        public bool UseAdaptiveSizing { get; set; }

        [NinjaScriptProperty, Range(10, 80)]
        [Display(Name = "Umbral de win rate para reducir tamaño (%)", Order = 13, GroupName = "8. Entrada y calidad",
                 Description = "Si el win rate del día cae bajo este %, los contratos se reducen a la mitad.")]
        public int AdaptiveSizingThreshold { get; set; }

        // 6. Sesión
        [NinjaScriptProperty]
        [Display(Name = "Filtro horario — solo operar en ventana definida",
                 Order = 1, GroupName = "6. Sesión")]
        public bool UseTimeFilter { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Hora inicio (HHMM, ej: 1000 = 10:00)",
                 Order = 2, GroupName = "6. Sesión",
                 Description = "Inicio de la ventana horaria permitida. Formato HHMM.")]
        public int HoraInicio { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Hora fin (HHMM, ej: 1530 = 15:30)",
                 Order = 3, GroupName = "6. Sesión",
                 Description = "Fin de la ventana horaria permitida. Formato HHMM.")]
        public int HoraFin { get; set; }

        [NinjaScriptProperty, Range(0, 20)]
        [Display(Name = "Cooldown barras tras señal (0 = desactivado)",
                 Order = 4, GroupName = "6. Sesión")]
        public int SetupCooldownBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filtro ADX — evitar zonas de indecisión",
                 Order = 5, GroupName = "6. Sesión",
                 Description = "Si está activo, solo opera cuando el ADX supera el mínimo configurado.")]
        public bool UseADXFilter { get; set; }

        [NinjaScriptProperty, Range(2, 100)]
        [Display(Name = "ADX — período de cálculo",
                 Order = 6, GroupName = "6. Sesión")]
        public int ADXPeriod { get; set; }

        [NinjaScriptProperty, Range(5, 60)]
        [Display(Name = "ADX — valor mínimo para operar",
                 Order = 7, GroupName = "6. Sesión",
                 Description = "ADX < este valor = mercado sin tendencia definida. Recomendado: 20–25.")]
        public int ADXMinTrend { get; set; }

        // 7. Visualización
        [NinjaScriptProperty]
        [Display(Name = "Mostrar nube en chart", Order = 1, GroupName = "7. Visualización")]
        public bool ShowCloudInStrategy { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Mostrar tablero de condiciones", Order = 2, GroupName = "7. Visualización")]
        public bool ShowDashboard { get; set; }

        [Display(Name = "Posición del tablero", Order = 3, GroupName = "7. Visualización")]
        public TextPosition DashboardPosition { get; set; }
    }
}
