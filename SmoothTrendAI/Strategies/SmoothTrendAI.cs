// ============================================================
// SmoothTrendAI.cs — Estrategia principal
// Proyecto: SmoothTrendAI  |  Prefijo: STA
//
// Integra:
//   SmoothTrendCloud       → indicador visual + señales Tipo 1 y Tipo 2
//   STADailyContextFilter  → filtro Kaufman (Prior H/C/L)
//   STAElliottWaveContext  → conteo simplificado de ondas
//   STASetupClassifier     → orquestador de los 3 tipos de setup
//   STASignalValidator     → validación con IA (Claude / GPT-4o)
//   STARiskManager         → gestión de riesgo diferenciada
//   STATradeJournal        → logging CSV
//
// Series de datos:
//   BarsInProgress 0 = barra principal (Range o intraday)
//   BarsInProgress 1 = Daily (contexto Kaufman)
//   BarsInProgress 2 = M15 (RSI auxiliar, si el principal no es M15)
// ============================================================
#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum STADireccionPermitida { Ambas, SoloLong, SoloShort }

    public class SmoothTrendAI : Strategy
    {
        // ─── Índices de series de datos ────────────────────────────────────
        private const int IDX_MAIN  = 0;
        private const int IDX_DAILY = 1;
        private const int IDX_M15   = 2;

        // ─── Módulos principales ───────────────────────────────────────────
        private SmoothTrendCloud      _cloud;
        private STADailyContextFilter _dailyFilter;
        private STAElliottWaveContext  _elliottWave;
        private STASetupClassifier    _setupClassifier;
        private STASignalValidator    _validator;
        private STARiskManager        _riskManager;
        private STATradeJournal       _journal;

        // ─── Estado de la posición activa ─────────────────────────────────
        private bool          _positionOpen;
        private bool          _entrySubmitted;
        private string        _activeDirection;
        private string        _activeSetupType;
        private double        _activeEntryPrice;
        private double        _activeStopPrice;
        private double        _activeTarget1;
        private double        _activeTarget2;
        private bool          _tp1Hit;
        private double        _trailingStopPrice;
        private bool          _trailingActive;
        private STATradeRecord _activeRecord;

        // ─── Auxiliares ────────────────────────────────────────────────────
        private double   _avgVolume20;
        private DateTime _lastDailyReset = DateTime.MinValue;
        private bool     _usesM15Secondary;

        // ─── Estado del tablero de filtros ─────────────────────────────────
        private string _dashLastAiResult  = "—  (sin señal aún)";
        private string _dashSignalStatus  = "—  sin señal";

        // ─── VWAP acumulado (calculado manualmente, sin dependencia de terceros) ──
        private double   _vwapSumPV;
        private double   _vwapSumV;
        private DateTime _vwapDate    = DateTime.MinValue;
        private double   _currentVWAP;

        // ─── Cooldown anti-señales-duplicadas ──────────────────────────────
        private int _lastSetupBar = -999;

        // ─── Estado de scale-in ────────────────────────────────────────────
        private bool   _scaleInPending;
        private double _scaleInTriggerPrice;
        private int    _scaleInContracts;
        private int    _mainEntryContracts;

        // ─── Nuevas mejoras ────────────────────────────────────────────────
        private DateTime _lastWeeklyReset = DateTime.MinValue;
        private int      _entryBar        = -1;
        private double   _priorDayHigh    = double.MaxValue;
        private double   _priorDayLow     = double.MinValue;
        private double   _currentDayHigh;
        private double   _currentDayLow;
        private readonly System.Collections.Generic.Queue<DateTime> _barTimes
            = new System.Collections.Generic.Queue<DateTime>();

        // ─── Nube visual (plots) ────────────────────────────────────────────
        private SolidColorBrush _cloudUpBrush;
        private SolidColorBrush _cloudDownBrush;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "SmoothTrendAI — Nube doble EMA + Kaufman + Elliott + validación IA";
                Name        = "SmoothTrendAI";
                Calculate   = Calculate.OnBarClose;
                IsOverlay   = true;
                BarsRequiredToTrade = 30;
                IsExitOnSessionCloseStrategy  = true;
                ExitOnSessionCloseSeconds     = 30;

                // Nube
                TriggerPeriod           = 12;
                SmoothPeriod            = 12;
                MinTrendBarsForPullback = 8;

                // IA
                AIProviderParam     = STAAIProvider.Claude;
                AIApiKey            = "";
                AIMinConfidence     = 0.62;
                EnableAIValidation  = true;

                // Riesgo
                AccountCapital      = 50_000.0;
                RiskPctPerTrade     = 0.01;
                MaxContracts        = 4;
                StopCloudMultiplier = 2.0;
                StopBufferTicks     = 3;
                StopATRMultiplier   = 1.3;
                MinStopTicks        = 6;
                TrailAfterTP1       = true;
                PorcentajeTP1       = 50;
                TP1RatioTrendStart      = 1.5;
                TP2RatioTrendStart      = 3.0;
                TP1RatioCloudPullback   = 2.0;
                TP2RatioCloudPullback   = 4.0;

                // Sesión
                RestrictToRTH             = false;
                RequireVolumeConfirmation = true;
                UseVWAPFilter             = true;
                UseQualityTimeFilter      = false;
                HoraInicio                = 1000;   // 10:00
                HoraFin                   = 1530;   // 15:30
                DireccionPermitida        = STADireccionPermitida.Ambas;
                LogRejectedSignals        = true;
                SetupCooldownBars         = 5;
                MaxDailyTrades            = 5;
                MaxConsecLosses           = 3;
                MaxWeeklyTrades           = 20;
                MaxWeeklyLossPct          = 0.05;
                UseLimitEntry             = false;
                LimitOffsetTicks          = 2;
                UseMinRR                  = true;
                MinRR                     = 1.5;
                UseTimeExit               = true;
                MaxBarsInTrade            = 25;
                UseSRFilter               = true;
                SRDistanceTicks           = 10;
                UseBarRateFilter          = false;
                MinBarsPerHour            = 3;
                MaxBarsPerHour            = 60;
                UseAdaptiveSizing         = false;
                AdaptiveSizingThreshold   = 40;
                UseMultiTimeframeFilter   = false;
                UseAdxTrailBoost          = true;
                AdxBoostThreshold         = 35;
                AdxPeriod                 = 14;

                // Visualización adicional
                ShowCloudInStrategy    = true;
                ShowFibLevels          = true;
                ShowElliottPivots      = false;
                ShowStrategyDashboard  = true;
                DashboardPosition      = TextPosition.TopRight;

                // Scale-in
                EnableScaleIn = false;
                ScaleInTicks  = 8;
            }
            else if (State == State.Configure)
            {
                // Líneas de la nube visibles en el chart de la estrategia
                AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Line, "TriggerLine");
                AddPlot(new Stroke(Brushes.Gold,        2), PlotStyle.Line, "SmoothTrendLine");

                // Serie diaria para contexto Kaufman
                AddDataSeries(BarsPeriodType.Day, 1);

                // M15 auxiliar solo si el gráfico principal no es M15
                _usesM15Secondary = !(BarsPeriod.BarsPeriodType == BarsPeriodType.Minute
                                      && BarsPeriod.Value == 15);
                if (_usesM15Secondary)
                    AddDataSeries(BarsPeriodType.Minute, 15);

                // Inicializar validador de IA (HttpClient creado aquí)
                _validator = new STASignalValidator
                {
                    Provider      = AIProviderParam,
                    ApiKey        = AIApiKey,
                    MinConfidence = AIMinConfidence,
                    EnableAI      = EnableAIValidation
                };
                _validator.Initialize();

                // Gestor de riesgo
                _riskManager = new STARiskManager
                {
                    AccountCapital         = AccountCapital,
                    RiskPctPerTrade        = RiskPctPerTrade,
                    MaxContracts           = MaxContracts,
                    StopCloudMultiplier    = StopCloudMultiplier,
                    StopBufferTicks        = StopBufferTicks,
                    StopATRMultiplier      = StopATRMultiplier,
                    MinStopTicks           = MinStopTicks,
                    TrailAfterTP1          = TrailAfterTP1,
                    MaxDailyProfitPct      = 1.0,   // sin límite de ganancia diaria
                    MaxDailyTrades         = MaxDailyTrades,
                    MaxTrendStartPerDay    = MaxDailyTrades,
                    MaxCloudPullbackPerDay = MaxDailyTrades,
                    MaxConsecutiveLosses   = MaxConsecLosses,
                    TrendStart_TP1Mult     = TP1RatioTrendStart,
                    TrendStart_TP2Mult     = TP2RatioTrendStart,
                    CloudPullback_TP1Mult  = TP1RatioCloudPullback,
                    CloudPullback_TP2Mult  = TP2RatioCloudPullback,
                    MaxWeeklyTrades        = MaxWeeklyTrades,
                    MaxWeeklyLossPct       = MaxWeeklyLossPct
                };

                // Journal
                _journal = new STATradeJournal();
            }
            else if (State == State.DataLoaded)
            {
                // Indicador de nube — pasar los 7 parámetros que NT8 auto-genera
                // (regionOpacity, upColor, downColor, showArrows usan valores por defecto del indicador)
                _cloud = SmoothTrendCloud(TriggerPeriod, SmoothPeriod, MinTrendBarsForPullback,
                                          10,                                        // RegionOpacity
                                          System.Windows.Media.Brushes.Cyan,        // UpTrendColor
                                          System.Windows.Media.Brushes.Orange,      // DownTrendColor
                                          true,                                      // ShowSignalArrows
                                          false,                                     // ShowType2Arrows
                                          false,                                     // EnableAIValidation
                                          "Claude",                                  // AIProvider
                                          "",                                        // AIApiKey
                                          0.60,                                      // AIMinConfidence
                                          false,                                     // EnableAlerts
                                          false,                                     // ShowDashboard
                                          false,                                     // UseM15Confluence
                                          false,                                     // UseTimeFilter
                                          false,                                     // LogRejectedSignals
                                          false,                                     // ShowEntryLevels
                                          3,                                         // LevelStopBufferTicks
                                          2.0,                                       // LevelTP1Ratio
                                          4.0);                                      // LevelTP2Ratio

                // Filtros
                _dailyFilter = new STADailyContextFilter();
                _elliottWave = new STAElliottWaveContext
                {
                    SwingLookbackBars   = 3,
                    MaxPivotsTracked    = 8,
                    MinWaveConfidence   = 0.40,
                    ShowFibonacciLevels = true
                };
                // Pasar las series High/Low de la barra principal (BarsInProgress 0)
                _elliottWave.Initialize(High, Low);

                // Clasificador con inyección de dependencias
                _setupClassifier = new STASetupClassifier(_cloud, _dailyFilter, _elliottWave)
                {
                    RequireVolumeConfirmation = RequireVolumeConfirmation
                };

                // Brushes para el fondo de barra de la nube
                _cloudUpBrush   = new SolidColorBrush(Color.FromArgb(28, 0, 180, 255));
                _cloudDownBrush = new SolidColorBrush(Color.FromArgb(28, 255, 120, 0));
                _cloudUpBrush.Freeze();
                _cloudDownBrush.Freeze();

                _journal.Open(Instrument.FullName);
                _journal.OpenRejectedLog();
            }
            else if (State == State.Realtime)
            {
                _riskManager.ResetDaily();
                _lastDailyReset = DateTime.Today;
                Print("[STA] LIVE: contadores reiniciados al entrar en tiempo real");
            }
            else if (State == State.Terminated)
            {
                _validator?.Dispose();
                _journal?.Dispose();
            }
        }

        protected override void OnBarUpdate()
        {
            // Solo procesar la barra principal
            if (BarsInProgress != IDX_MAIN) return;
            if (CurrentBar < BarsRequiredToTrade) return;

            // ── Nube visual: líneas + fondo de barra ─────────────────────────
            if (ShowCloudInStrategy && _cloud != null && _cloud.TriggerValue > 0)
            {
                Values[0][0]   = _cloud.TriggerValue;
                Values[1][0]   = _cloud.SmoothValue;
                BackBrushes[0] = _cloud.CurrentCloudColor == "Up" ? _cloudUpBrush : _cloudDownBrush;
            }

            if (_positionOpen && Position.MarketPosition == MarketPosition.Flat)
            {
                FinalizarPosicion("BrokerExit");
                return;
            }
            if (_entrySubmitted && !_positionOpen)
                return;

            // ── Reset semanal ─────────────────────────────────────────────
            if (_lastWeeklyReset == DateTime.MinValue ||
                GetISOWeek(Time[0]) != GetISOWeek(_lastWeeklyReset))
            {
                _riskManager.ResetWeekly();
                _lastWeeklyReset = Time[0];
            }

            // ── Reset diario ──────────────────────────────────────────────
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

            // ── Tasa de barras ────────────────────────────────────────────
            _barTimes.Enqueue(Time[0]);
            while (_barTimes.Count > 0 && (Time[0] - _barTimes.Peek()).TotalMinutes > 60)
                _barTimes.Dequeue();

            // ── Actualizar contexto diario (necesita >= 3 barras diarias) ─
            if (CurrentBars[IDX_DAILY] >= 3)
            {
                _dailyFilter.Update(
                    currentPrice: Close[0],
                    dailyHigh1:   Highs[IDX_DAILY][1],
                    dailyLow1:    Lows[IDX_DAILY][1],
                    dailyOpen1:   Opens[IDX_DAILY][1],
                    dailyClose1:  Closes[IDX_DAILY][1],
                    dailyClose2:  Closes[IDX_DAILY][2],
                    dailyClose3:  Closes[IDX_DAILY][3]
                );
            }

            // ── Actualizar conteo de Elliott ──────────────────────────────
            _elliottWave.Update(CurrentBar, Close[0]);

            // ── VWAP acumulado (se resetea al inicio de cada día) ─────────
            ActualizarVWAP();

            // ── Visualización: niveles Fibonacci ─────────────────────────
            if (ShowFibLevels && _elliottWave.WaveConfidence >= 0.50)
            {
                Draw.HorizontalLine(this, "Fib_382",  _elliottWave.Fib_382,  Brushes.Gold);
                Draw.HorizontalLine(this, "Fib_618",  _elliottWave.Fib_618,  Brushes.Gold);
                Draw.HorizontalLine(this, "Fib_1272", _elliottWave.Fib_1272, Brushes.GreenYellow);
                Draw.HorizontalLine(this, "Fib_1618", _elliottWave.Fib_1618, Brushes.GreenYellow);
            }
            else if (ShowFibLevels)
            {
                RemoveDrawObject("Fib_382");
                RemoveDrawObject("Fib_618");
                RemoveDrawObject("Fib_1272");
                RemoveDrawObject("Fib_1618");
            }

            // ── Visualización: pivotes Elliott ────────────────────────────
            if (ShowElliottPivots)
            {
                foreach (var p in _elliottWave.GetPivots())
                {
                    int barsAgo = CurrentBar - p.Bar;
                    if (barsAgo < 0 || barsAgo > 100) continue;
                    if (p.IsHigh)
                        Draw.Dot(this, $"Pvt_{p.Bar}", false, barsAgo,
                                 p.Price + TickSize * 4, Brushes.Magenta);
                    else
                        Draw.Dot(this, $"Pvt_{p.Bar}", false, barsAgo,
                                 p.Price - TickSize * 4, Brushes.Lime);
                }
            }

            // ── Volumen promedio 20 barras ─────────────────────────────────
            // SMA sobre Volume evita recalcular en cada barra manualmente
            _avgVolume20 = SMA(Volume, 20)[0];

            // ── Actualizar clasificador con datos de la barra actual ───────
            _setupClassifier.UpdateBarData(
                Open[0], High[0], Low[0], Close[0],
                Open[1], High[1], Low[1], Close[1],
                Volume[0], _avgVolume20
            );

            // ── Clasificar setup (antes de filtros para poder mostrarlo en el tablero)
            var setup = _setupClassifier.Evaluate();

            // ── Tablero de filtros (se dibuja siempre, con o sin señal) ───
            DibujarDashboard(setup);

            // ── Gestión de posición abierta (máxima prioridad) ────────────
            if (_positionOpen)
            {
                GestionarPosicionAbierta();
                return;
            }

            // ── Verificar restricciones de sesión y límites diarios ────────
            if (RestrictToRTH && !EsHorarioRTH()) return;
            if (!_riskManager.CanTrade()) return;

            if (!setup.IsValidSetup)
            {
                _dashSignalStatus = "—  sin señal";
                // Diagnóstico: imprime estado cada 100 barras para verificar que las señales llegan
                if (CurrentBar % 100 == 0)
                    Print($"[STA-DIAG] Bar={CurrentBar} CX↑={_cloud.HasCrossUpSignal} CX↓={_cloud.HasCrossDownSignal} Touch↑={_cloud.TouchedCloudFromAbove} Touch↓={_cloud.TouchedCloudFromBelow} DirDiaria={_dailyFilter.AllowedDirection}");
                return;
            }

            // Señal detectada — marcar como pendiente de filtros
            _dashSignalStatus = $"↓ {setup.Direction}  {setup.SetupType}  evaluando filtros...";

            Print($"[STA] Setup VÁLIDO: {setup.SetupType} {setup.Direction} conf={setup.BaseConfidence:F2} {Time[0]:HH:mm}");

            // Cooldown: ignorar señales repetidas mientras el precio roza la nube
            if (SetupCooldownBars > 0 && CurrentBar - _lastSetupBar < SetupCooldownBars)
            {
                _dashSignalStatus = $"⚠ COOLDOWN  {CurrentBar - _lastSetupBar}/{SetupCooldownBars} barras desde último setup";
                return;
            }
            _lastSetupBar = CurrentBar;

            // Verificar límite específico del tipo de setup
            if (!_riskManager.CanTrade(setup.SetupType))
            {
                _dashSignalStatus = $"⚠ RIESGO  límite tipo {setup.SetupType}" +
                                    $"  trades={_riskManager.DailyTrades}/{MaxDailyTrades}" +
                                    $"  sem={_riskManager.WeeklyTrades}/{MaxWeeklyTrades}";
                return;
            }

            // ── Filtro de dirección permitida ─────────────────────────────
            if (DireccionPermitida == STADireccionPermitida.SoloLong  && setup.Direction == "SHORT")
            {
                _dashSignalStatus = "⚠ DIRECCIÓN  solo LONG permitido";
                LogRechazo(setup, "DireccionFiltro", 0, "Solo LONG permitido", 0);
                return;
            }
            if (DireccionPermitida == STADireccionPermitida.SoloShort && setup.Direction == "LONG")
            {
                _dashSignalStatus = "⚠ DIRECCIÓN  solo SHORT permitido";
                LogRechazo(setup, "DireccionFiltro", 0, "Solo SHORT permitido", 0);
                return;
            }

            // ── Filtro RSI: no entrar en zona extrema opuesta ─────────────
            double rsiM15 = ObtenerRsiM15();
            if (setup.Direction == "LONG"  && rsiM15 > 70)
            {
                _dashSignalStatus = $"⚠ RSI={rsiM15:F1}  >70  (sobrecompra — no LONG)";
                LogRechazo(setup, "RSI_Extremo", rsiM15, $"RSI={rsiM15:F1}>70", 0);
                return;
            }
            if (setup.Direction == "SHORT" && rsiM15 < 30)
            {
                _dashSignalStatus = $"⚠ RSI={rsiM15:F1}  <30  (sobreventa — no SHORT)";
                LogRechazo(setup, "RSI_Extremo", rsiM15, $"RSI={rsiM15:F1}<30", 0);
                return;
            }

            // ── Filtro de horario de calidad ──────────────────────────────
            if (UseQualityTimeFilter && !EsHorarioCalidad())
            {
                _dashSignalStatus = $"⚠ HORARIO  {Time[0]:HH:mm}  fuera de {HoraInicio/100:D2}:{HoraInicio%100:D2}–{HoraFin/100:D2}:{HoraFin%100:D2}";
                LogRechazo(setup, "TimeFilter", rsiM15, "Fuera de ventana 10:00-11:30 / 14:00-15:30", 0);
                return;
            }

            // ── Filtro VWAP ───────────────────────────────────────────────
            if (UseVWAPFilter && _currentVWAP > 0)
            {
                bool vwapOk = (setup.Direction == "LONG"  && Close[0] >= _currentVWAP) ||
                              (setup.Direction == "SHORT" && Close[0] <= _currentVWAP);
                if (!vwapOk)
                {
                    _dashSignalStatus = $"⚠ VWAP  precio={Close[0]:F2}  vwap={_currentVWAP:F2}" +
                                        $"  (necesita {(setup.Direction == "LONG" ? "precio≥VWAP" : "precio≤VWAP")})";
                    LogRechazo(setup, "VWAP", rsiM15, $"Precio={Close[0]:F2} VWAP={_currentVWAP:F2}", 0);
                    return;
                }
            }

            // ── Filtro multi-timeframe (tendencia diaria) ─────────────────
            if (UseMultiTimeframeFilter && CurrentBars[IDX_DAILY] >= 2)
            {
                bool dailyBullish = Closes[IDX_DAILY][0] > Closes[IDX_DAILY][1];
                if (setup.Direction == "LONG"  && !dailyBullish)
                {
                    _dashSignalStatus = "⚠ MTF  tendencia diaria bajista — no LONG";
                    LogRechazo(setup, "MTF", rsiM15, "Tendencia diaria bajista", 0);
                    return;
                }
                if (setup.Direction == "SHORT" && dailyBullish)
                {
                    _dashSignalStatus = "⚠ MTF  tendencia diaria alcista — no SHORT";
                    LogRechazo(setup, "MTF", rsiM15, "Tendencia diaria alcista", 0);
                    return;
                }
            }

            // ── Filtro tasa de barras ─────────────────────────────────────
            if (UseBarRateFilter)
            {
                int bph = _barTimes.Count;
                if (bph < MinBarsPerHour)
                {
                    _dashSignalStatus = $"⚠ BARRATE  {bph} barras/h  <{MinBarsPerHour}  (mercado muerto)";
                    LogRechazo(setup, "BarRate", rsiM15, $"{bph} barras/hora < {MinBarsPerHour} (mercado muerto)", 0);
                    return;
                }
                if (bph > MaxBarsPerHour)
                {
                    _dashSignalStatus = $"⚠ BARRATE  {bph} barras/h  >{MaxBarsPerHour}  (mercado caótico)";
                    LogRechazo(setup, "BarRate", rsiM15, $"{bph} barras/hora > {MaxBarsPerHour} (mercado caótico)", 0);
                    return;
                }
            }

            // ── Filtro distancia a S/R día anterior ───────────────────────
            if (UseSRFilter && _priorDayHigh < double.MaxValue)
            {
                double distH = Math.Abs(Close[0] - _priorDayHigh);
                double distL = Math.Abs(Close[0] - _priorDayLow);
                double minDist = SRDistanceTicks * TickSize;
                if (distH < minDist || distL < minDist)
                {
                    _dashSignalStatus = $"⚠ S/R  PDH={_priorDayHigh:F2}  PDL={_priorDayLow:F2}  dist<{SRDistanceTicks}t";
                    LogRechazo(setup, "SRFilter", rsiM15,
                        $"Cerca de PDH={_priorDayHigh:F2}/PDL={_priorDayLow:F2} mín={SRDistanceTicks}t", 0);
                    return;
                }
            }

            // Sizing adaptativo
            if (UseAdaptiveSizing && _riskManager.DailyTrades >= 2)
                _riskManager.AdaptiveSizeFactor = _riskManager.DailyWinRate < AdaptiveSizingThreshold / 100.0
                    ? 0.5 : 1.0;

            // ── Calcular riesgo preliminar para el payload de IA ──────────
            double atr14     = ATR(14)[0];
            double tickVal   = Instrument.MasterInstrument.PointValue * TickSize;
            double rejLow    = setup.RejectionCandle != null ? Low[0]  : Close[0] - atr14;
            double rejHigh   = setup.RejectionCandle != null ? High[0] : Close[0] + atr14;

            var riskPrelim = _riskManager.Calculate(
                setup, Close[0], setup.Direction,
                _cloud.CloudWidth, atr14,
                rejLow, rejHigh,
                TickSize, tickVal,
                aiRiskAdjustment: 1.0,
                elliottMultiplier: setup.ElliottMultiplier,
                marketContext: _dailyFilter.MarketContext
            );

            // R:R basado en TP1 (primera salida parcial)
            double rrRatio = riskPrelim.StopTicks > 0
                ? Math.Abs(riskPrelim.Target1Price - Close[0]) /
                  Math.Abs(Close[0] - riskPrelim.StopPrice)
                : 0;

            // ── Filtro R:R mínimo ─────────────────────────────────────────
            if (UseMinRR && rrRatio < MinRR)
            {
                _dashSignalStatus = $"⚠ R:R={rrRatio:F2}  <{MinRR}  (ratio insuficiente)";
                LogRechazo(setup, "MinRR", rsiM15, $"R:R={rrRatio:F2} < {MinRR}", rrRatio);
                return;
            }

            _dashSignalStatus = $"↓ {setup.Direction}  {setup.SetupType}  R:R={rrRatio:F2}  →  consultando IA...";

            // ── Construir payload para la IA ───────────────────────────────
            var payload = new STASignalPayload
            {
                Instrument               = Instrument.FullName,
                BarType                  = BarsPeriod.ToString(),
                Timestamp                = Time[0].ToString("yyyy-MM-ddTHH:mm:ss"),
                SignalDirection          = setup.Direction,
                SetupType                = setup.SetupType,
                TriggerLine              = _cloud.TriggerValue,
                SmoothTrendLine          = _cloud.SmoothValue,
                CloudWidthTicks          = TickSize > 0 ? _cloud.CloudWidth / TickSize : 0,
                BarsInCurrentColor       = _cloud.BarsInCurrentColor,
                RejectionCandle          = setup.RejectionCandle,
                DailyContext             = _dailyFilter.MarketContext,
                AllowedDirection         = _dailyFilter.AllowedDirection,
                PriorHigh                = _dailyFilter.PriorHigh,
                PriorClose               = _dailyFilter.PriorClose,
                PriorLow                 = _dailyFilter.PriorLow,
                ConsecutiveDirectionDays = _dailyFilter.ConsecutiveDirectionDays,
                HasStrongDirectionalBias = _dailyFilter.HasStrongDirectionalBias,
                ElliottWavePosition      = _elliottWave.CurrentWavePosition,
                ElliottWaveConfidence    = _elliottWave.WaveConfidence,
                ElliottFavorable         = setup.Direction == "LONG"
                                           ? _elliottWave.IsFavorableForLong
                                           : _elliottWave.IsFavorableForShort,
                NearestFibSupport        = _elliottWave.NearestFibSupport,
                NearestFibResistance     = _elliottWave.NearestFibResistance,
                BaseConfidencePreAI      = setup.BaseConfidence,
                RsiM15                   = rsiM15,
                VolumeRatio              = _avgVolume20 > 0 ? Volume[0] / _avgVolume20 : 1.0,
                CurrentPrice             = Close[0],
                ProposedEntry            = Close[0],
                ProposedStop             = riskPrelim.StopPrice,
                ProposedTarget1          = riskPrelim.Target1Price,
                ProposedTarget2          = riskPrelim.Target2Price,
                RiskRewardRatio          = Math.Round(rrRatio, 2)
            };

            // ── Llamada a IA (síncrona dentro de la estrategia) ───────────
            bool isHistorical = State == State.Historical;
            STAAIValidationResult aiResult;
            try
            {
                var aiTask = Task.Run(() =>
                    _validator.ValidateAsync(payload, isHistorical));
                // Esperar máximo 12s; si no llega → fallback conservador
                if (!aiTask.Wait(TimeSpan.FromSeconds(12)))
                {
                    aiResult = new STAAIValidationResult
                    {
                        Approve        = false,
                        Confidence     = 0,
                        Reason         = "Timeout estrategia — señal descartada",
                        RiskAdjustment = 0.50,
                        SetupQuality   = "low",
                        IsTimeout      = true
                    };
                }
                else
                {
                    aiResult = aiTask.Result;
                }
            }
            catch
            {
                return; // Error irrecuperable → skip esta barra
            }

            // ── Actualizar tablero con resultado de IA ────────────────────
            _dashLastAiResult = aiResult.Approve && aiResult.Confidence >= AIMinConfidence
                ? $"✓  {setup.Direction}  {aiResult.Confidence:P0}  {aiResult.SetupQuality}"
                : $"✗  {aiResult.Confidence:P0}  {aiResult.Reason?.Substring(0, Math.Min(40, aiResult.Reason?.Length ?? 0))}";

            // ── Dibujar X si la IA rechaza ────────────────────────────────
            if (!aiResult.Approve || aiResult.Confidence < AIMinConfidence)
            {
                _dashSignalStatus = $"⚠ IA  conf={aiResult.Confidence:P0}  {aiResult.Reason?.Substring(0, Math.Min(35, aiResult.Reason?.Length ?? 0))}";
                Draw.Text(this, $"Rej_{CurrentBar}", "✕", 0,
                    setup.Direction == "LONG"
                        ? Low[0]  - TickSize * 6
                        : High[0] + TickSize * 6,
                    Brushes.Gray);
                LogRechazo(setup, "AI", rsiM15, aiResult.Reason, rrRatio, aiResult.Confidence);
                return;
            }

            // ── Calcular riesgo final con ajuste de IA ─────────────────────
            var riskFinal = _riskManager.Calculate(
                setup, Close[0], setup.Direction,
                _cloud.CloudWidth, atr14,
                rejLow, rejHigh,
                TickSize, tickVal,
                aiResult.RiskAdjustment,
                setup.ElliottMultiplier,
                _dailyFilter.MarketContext
            );

            // ── Ejecutar entrada ───────────────────────────────────────────
            _dashSignalStatus = $"✓ {setup.Direction}  {setup.SetupType}  R:R={rrRatio:F2}  IA={aiResult.Confidence:P0}  —  ENTRADA";
            EjecutarEntrada(setup, riskFinal, aiResult, rsiM15, payload);
        }

        // ─── Ejecutar orden de entrada ──────────────────────────────────────
        private void EjecutarEntrada(STASetupResult setup,
                                      STARiskParameters risk,
                                      STAAIValidationResult aiResult,
                                      double rsiM15,
                                      STASignalPayload payload)
        {
            bool isLongEntry = setup.Direction == "LONG";
            _scaleInPending   = false;
            _scaleInContracts = 0;

            if (EnableScaleIn && risk.Contracts >= 2)
            {
                int initCtrs      = _riskManager.CalculateInitialContracts(risk.Contracts);
                _mainEntryContracts = initCtrs;
                _scaleInContracts = risk.Contracts - initCtrs;
                _scaleInTriggerPrice = isLongEntry
                    ? Close[0] + ScaleInTicks * TickSize
                    : Close[0] - ScaleInTicks * TickSize;

                SetStopLoss(isLongEntry ? "STA_Long" : "STA_Short",
                            CalculationMode.Price, risk.StopPrice, false);
                SetProfitTarget(isLongEntry ? "STA_Long" : "STA_Short",
                                CalculationMode.Price, risk.Target1Price);
                if (_scaleInContracts > 0)
                {
                    SetStopLoss("STA_ScaleIn", CalculationMode.Price, risk.StopPrice, false);
                    SetProfitTarget("STA_ScaleIn", CalculationMode.Price, risk.Target2Price);
                }

                if (isLongEntry) EnterLong (initCtrs, "STA_Long");
                else             EnterShort(initCtrs, "STA_Short");

                _scaleInPending = true;
                Print($"[STA] ENTRADA inicial {initCtrs}/{risk.Contracts} ctrs, " +
                      $"ScaleIn en {_scaleInTriggerPrice:F2}");
            }
            else
            {
                _mainEntryContracts = risk.Contracts;
                SetStopLoss(isLongEntry ? "STA_Long" : "STA_Short",
                            CalculationMode.Price, risk.StopPrice, false);
                SetProfitTarget(isLongEntry ? "STA_Long" : "STA_Short",
                                CalculationMode.Price, risk.Target1Price);

                bool usarLimite = UseLimitEntry && setup.SetupType == "CloudPullback";
                if (usarLimite)
                {
                    double lp = isLongEntry
                        ? Close[0] - LimitOffsetTicks * TickSize
                        : Close[0] + LimitOffsetTicks * TickSize;
                    if (isLongEntry) EnterLongLimit (risk.Contracts, lp, "STA_Long");
                    else             EnterShortLimit(risk.Contracts, lp, "STA_Short");
                }
                else
                {
                    if (isLongEntry) EnterLong (risk.Contracts, "STA_Long");
                    else             EnterShort(risk.Contracts, "STA_Short");
                }
            }

            _positionOpen      = false;
            _entrySubmitted    = true;
            _entryBar          = CurrentBar;
            _activeDirection   = setup.Direction;
            _activeSetupType   = setup.SetupType;
            _activeEntryPrice  = Close[0];
            _activeStopPrice   = risk.StopPrice;
            _activeTarget1     = risk.Target1Price;
            _activeTarget2     = risk.Target2Price;
            _tp1Hit            = false;
            _trailingActive    = false;
            _trailingStopPrice = 0;

            // ── Preparar registro de journal ───────────────────────────────
            _activeRecord = new STATradeRecord
            {
                TimestampEntry           = Time[0],
                Instrument               = Instrument.FullName,
                BarType                  = BarsPeriod.ToString(),
                Direction                = setup.Direction,
                SetupType                = setup.SetupType,
                RejectionCandle          = setup.RejectionCandle,
                EntryPrice               = Close[0],
                Contracts                = risk.Contracts,
                TriggerLineAtEntry       = _cloud.TriggerValue,
                SmoothLineAtEntry        = _cloud.SmoothValue,
                CloudWidthAtEntry        = _cloud.CloudWidth,
                BarsInCurrentColor       = _cloud.BarsInCurrentColor,
                DailyContext             = _dailyFilter.MarketContext,
                AllowedDirection         = _dailyFilter.AllowedDirection,
                PriorHigh                = _dailyFilter.PriorHigh,
                PriorClose               = _dailyFilter.PriorClose,
                PriorLow                 = _dailyFilter.PriorLow,
                ConsecutiveDirectionDays = _dailyFilter.ConsecutiveDirectionDays,
                ElliottWavePosition      = _elliottWave.CurrentWavePosition,
                ElliottWaveConfidence    = _elliottWave.WaveConfidence,
                ElliottMultiplierApplied = setup.ElliottMultiplier,
                RsiM15                   = rsiM15,
                VolumeRatio              = payload.VolumeRatio,
                AiConfidence             = aiResult.Confidence,
                AiRiskAdjustment         = aiResult.RiskAdjustment,
                AiSetupQuality           = aiResult.SetupQuality,
                AiReason                 = aiResult.Reason,
                StopTicks                = risk.StopTicks,
                ConsecutiveLossesAtEntry = _riskManager.ConsecutiveLosses
            };

            // ── Dibujar en chart ───────────────────────────────────────────
            string tag    = $"E_{CurrentBar}";
            bool   isLong = setup.Direction == "LONG";

            // Tipo 1: triángulo sólido | Tipo 2: diamante
            if (setup.SetupType == "TrendStart")
            {
                if (isLong) Draw.TriangleUp  (this, tag, true, 0, Low[0]  - TickSize * 8, Brushes.Lime);
                else        Draw.TriangleDown(this, tag, true, 0, High[0] + TickSize * 8, Brushes.Red);
            }
            else
            {
                if (isLong) Draw.Diamond(this, tag, true, 0, Low[0]  - TickSize * 8, Brushes.Cyan);
                else        Draw.Diamond(this, tag, true, 0, High[0] + TickSize * 8, Brushes.OrangeRed);
            }

            // Etiqueta informativa
            string lbl = $"{setup.SetupType} | {_elliottWave.CurrentWavePosition} " +
                         $"({_elliottWave.WaveConfidence:F2}) | {_dailyFilter.MarketContext} | " +
                         $"Cloud:{_cloud.CurrentCloudColor},{_cloud.BarsInCurrentColor}b";
            Draw.Text(this, tag + "_lbl", lbl, 0,
                isLong ? Low[0] - TickSize * 16 : High[0] + TickSize * 16,
                Brushes.White);

            // Etiqueta de vela de rechazo (Tipo 2)
            if (setup.RejectionCandle != null)
                Draw.Text(this, tag + "_cv", setup.RejectionCandle, 0,
                    isLong ? Low[0] - TickSize * 24 : High[0] + TickSize * 24,
                    Brushes.Yellow);

            // Niveles TP1 y TP2 visibles en el chart
            Draw.HorizontalLine(this, "STA_TP1", risk.Target1Price,
                Brushes.Lime, DashStyleHelper.Dash, 2);
            Draw.HorizontalLine(this, "STA_TP2", risk.Target2Price,
                Brushes.DodgerBlue, DashStyleHelper.Dash, 1);
            Draw.Text(this, "STA_TP1_lbl", $"TP1 {risk.Target1Price:F2}",
                0, risk.Target1Price + (isLong ? TickSize * 2 : -TickSize * 2), Brushes.Lime);
            Draw.Text(this, "STA_TP2_lbl", $"TP2 {risk.Target2Price:F2}",
                0, risk.Target2Price + (isLong ? TickSize * 2 : -TickSize * 2), Brushes.DodgerBlue);

            Print($"[STA] ENTRADA {setup.Direction} | {setup.SetupType} | " +
                  $"Entry={Close[0]:F2} Stop={risk.StopPrice:F2} " +
                  $"TP1={risk.Target1Price:F2} TP2={risk.Target2Price:F2} " +
                  $"Ctrs={risk.Contracts} | AI conf={aiResult.Confidence:F2} " +
                  $"q={aiResult.SetupQuality} | {aiResult.Reason}");
        }

        // ─── Gestión de posición abierta ────────────────────────────────────
        private void GestionarPosicionAbierta()
        {
            bool isLong = _activeDirection == "LONG";

            // ── PRIORIDAD 0: Scale-in pendiente ─────────────────────────
            if (_scaleInPending && _scaleInContracts > 0)
            {
                bool scaleTriggered = isLong
                    ? High[0] >= _scaleInTriggerPrice
                    : Low[0]  <= _scaleInTriggerPrice;
                if (scaleTriggered)
                {
                    SetStopLoss("STA_ScaleIn", CalculationMode.Price, _activeStopPrice, false);
                    if (isLong) EnterLong (_scaleInContracts, "STA_ScaleIn");
                    else        EnterShort(_scaleInContracts, "STA_ScaleIn");
                    _scaleInPending   = false;
                    Print($"[STA] SCALE-IN +{_scaleInContracts} ctrs @ {_scaleInTriggerPrice:F2}");
                }
            }

            // ── PRIORIDAD 0b: Salida por tiempo máximo en trade ────────────
            if (UseTimeExit && _entryBar >= 0 && CurrentBar - _entryBar >= MaxBarsInTrade)
            {
                CerrarPosicion("TimeExit");
                return;
            }

            // ── PRIORIDAD 1: Cruce opuesto de la nube → salida inmediata ──
            if (isLong  && _cloud.HasCrossDownSignal) { CerrarPosicion("CloudCruce_Opuesto"); return; }
            if (!isLong && _cloud.HasCrossUpSignal)   { CerrarPosicion("CloudCruce_Opuesto"); return; }

            // ── PRIORIDAD 2: Stop Loss ────────────────────────────────────
            if (isLong  && Low[0]  <= _activeStopPrice) { CerrarPosicion("StopLoss"); return; }
            if (!isLong && High[0] >= _activeStopPrice) { CerrarPosicion("StopLoss"); return; }

            // ── PRIORIDAD 3: TP1 parcial (50%) ────────────────────────────
            if (!_tp1Hit)
            {
                bool tp1 = isLong ? High[0] >= _activeTarget1 : Low[0] <= _activeTarget1;
                if (tp1)
                {
                    _tp1Hit = true;
                    int mitad = Math.Max(1, (int)Math.Round(Position.Quantity * PorcentajeTP1 / 100.0));
                    int mainExitQty = Math.Min(mitad, Math.Max(0, _mainEntryContracts));
                    int scaleExitQty = mitad - mainExitQty;

                    if (mainExitQty > 0)
                    {
                        if (isLong) ExitLong (mainExitQty, "STA_TP1", "STA_Long");
                        else        ExitShort(mainExitQty, "STA_TP1", "STA_Short");
                    }
                    if (scaleExitQty > 0)
                    {
                        if (isLong) ExitLong (scaleExitQty, "STA_TP1_Scale", "STA_ScaleIn");
                        else        ExitShort(scaleExitQty, "STA_TP1_Scale", "STA_ScaleIn");
                    }

                    // Mover stop a breakeven
                    _activeStopPrice = _activeEntryPrice;
                    ActualizarStopProtector(_activeStopPrice);

                    // Activar trailing
                    if (TrailAfterTP1)
                    {
                        _trailingActive    = true;
                        _trailingStopPrice = _activeStopPrice;
                    }

                    if (_activeRecord != null) _activeRecord.Tp1Hit = true;

                    Print($"[STA] TP1 — posición al 50%, stop→BE {_activeEntryPrice:F2}");
                }
            }

            // ── PRIORIDAD 4: Trailing stop (activo tras TP1) ──────────────
            if (_trailingActive)
            {
                double atr14 = ATR(14)[0];
                double trailMult = (UseAdxTrailBoost && ADX(AdxPeriod)[0] > AdxBoostThreshold)
                    ? _riskManager.TrailingATRMultiplier * 0.6
                    : 0;
                double nuevoTrail = _riskManager.CalculateTrailingStop(Close[0], _activeDirection, atr14, trailMult);

                if (isLong  && nuevoTrail > _trailingStopPrice) _trailingStopPrice = nuevoTrail;
                if (!isLong && nuevoTrail < _trailingStopPrice) _trailingStopPrice = nuevoTrail;

                _activeStopPrice = _trailingStopPrice;
                ActualizarStopProtector(_activeStopPrice);

                if (isLong  && Low[0]  <= _trailingStopPrice) { CerrarPosicion("TrailingStop"); return; }
                if (!isLong && High[0] >= _trailingStopPrice) { CerrarPosicion("TrailingStop"); return; }
            }

            // ── PRIORIDAD 5: TP2 final ────────────────────────────────────
            bool tp2 = isLong ? High[0] >= _activeTarget2 : Low[0] <= _activeTarget2;
            if (tp2)
            {
                if (_activeRecord != null) _activeRecord.Tp2Hit = true;
                CerrarPosicion("TP2_Final");
            }
        }

        // ─── Cerrar posición y registrar en journal ─────────────────────────
        private void CerrarPosicion(string razon)
        {
            bool isLong = _activeDirection == "LONG";

            if (isLong) ExitLong ("STA_Salida_" + razon, "STA_Long");
            else        ExitShort("STA_Salida_" + razon, "STA_Short");

            if (!_scaleInPending)
            {
                if (isLong) ExitLong ("STA_SalidaScale_" + razon, "STA_ScaleIn");
                else        ExitShort("STA_SalidaScale_" + razon, "STA_ScaleIn");
            }

            FinalizarPosicion(razon);
        }

        private void FinalizarPosicion(string razon)
        {
            bool isLong = _activeDirection == "LONG";

            // Calcular P&L
            double pnlTicks = isLong
                ? (Close[0] - _activeEntryPrice) / TickSize
                : (_activeEntryPrice - Close[0]) / TickSize;

            double tickVal = Instrument.MasterInstrument.PointValue * TickSize;
            double pnlUsd  = pnlTicks * tickVal * (_activeRecord?.Contracts ?? 1);

            _riskManager.RegisterTradeResult(pnlUsd);

            // Completar y guardar el registro
            if (_activeRecord != null)
            {
                _activeRecord.TimestampExit = Time[0];
                _activeRecord.ExitPrice     = Close[0];
                _activeRecord.PnlTicks      = pnlTicks;
                _activeRecord.PnlUsd        = pnlUsd;
                _activeRecord.ExitReason    = razon;
                _journal.LogComplete(_activeRecord);
            }

            Draw.Text(this, $"X_{CurrentBar}", $"[{razon}]", 0,
                isLong ? High[0] + TickSize * 6 : Low[0] - TickSize * 6,
                Brushes.Silver);

            // Limpiar líneas de TP al cerrar
            RemoveDrawObject("STA_TP1");
            RemoveDrawObject("STA_TP2");
            RemoveDrawObject("STA_TP1_lbl");
            RemoveDrawObject("STA_TP2_lbl");

            Print($"[STA] SALIDA {razon} | PnL={pnlTicks:F1}t (${pnlUsd:F2})");

            _positionOpen     = false;
            _entrySubmitted    = false;
            _activeRecord     = null;
            _trailingActive   = false;
            _scaleInPending   = false;
            _scaleInContracts = 0;
            _mainEntryContracts = 0;
        }

        private void ActualizarStopProtector(double stopPrice)
        {
            if (_activeDirection == "LONG")
                SetStopLoss("STA_Long", CalculationMode.Price, stopPrice, false);
            else
                SetStopLoss("STA_Short", CalculationMode.Price, stopPrice, false);

            if (!_scaleInPending)
                SetStopLoss("STA_ScaleIn", CalculationMode.Price, stopPrice, false);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
                                                  double price, int quantity,
                                                  MarketPosition marketPosition,
                                                  string orderId, DateTime time)
        {
            if (execution?.Order == null ||
                (execution.Order.OrderState != OrderState.Filled &&
                 execution.Order.OrderState != OrderState.PartFilled))
                return;

            string name = execution.Order.Name ?? "";
            if (name == "STA_Long" || name == "STA_Short")
            {
                // Solo registrar en risk manager al confirmar fill del broker
                if (!_positionOpen)
                    _riskManager.RegisterTrade(_activeSetupType ?? "CloudPullback");

                _positionOpen = true;
                _entrySubmitted = true;
                _activeEntryPrice = price;
                if (_activeRecord != null)
                    _activeRecord.EntryPrice = price;
            }
            else if (name == "STA_ScaleIn")
            {
                _positionOpen = true;
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
            bool isEntry = name == "STA_Long" || name == "STA_Short";
            if (!isEntry) return;

            if (orderState == OrderState.Rejected ||
                orderState == OrderState.Cancelled)
            {
                _entrySubmitted = false;
                _positionOpen = Position.MarketPosition != MarketPosition.Flat;
                Print($"[STA] Orden de entrada {orderState}: {nativeError}");
            }
        }

        // ─── Helpers ────────────────────────────────────────────────────────

        private double ObtenerRsiM15()
        {
            try
            {
                if (_usesM15Secondary && CurrentBars[IDX_M15] >= 14)
                    return RSI(BarsArray[IDX_M15], 14, 3)[0];

                // Si el chart principal ES M15
                if (!_usesM15Secondary)
                    return RSI(14, 3)[0];
            }
            catch { }
            return 50.0; // valor neutral si no disponible
        }

        private bool EsHorarioRTH()
        {
            var h       = Time[0].TimeOfDay;
            var inicio  = new TimeSpan(9, 30, 0);
            var fin     = new TimeSpan(15, 55, 0);
            return h >= inicio && h <= fin;
        }

        private static int GetISOWeek(DateTime dt)
        {
            var day = System.Globalization.CultureInfo.InvariantCulture.Calendar
                .GetWeekOfYear(dt, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
            return dt.Year * 100 + day;
        }

        private TimeSpan HHMM(int hhmm) => new TimeSpan(hhmm / 100, hhmm % 100, 0);

        private bool EsHorarioCalidad()
        {
            var h = Time[0].TimeOfDay;
            return h >= HHMM(HoraInicio) && h <= HHMM(HoraFin);
        }

        private void ActualizarVWAP()
        {
            DateTime today = Time[0].Date;
            if (today != _vwapDate)
            {
                _vwapSumPV = 0;
                _vwapSumV  = 0;
                _vwapDate  = today;
            }
            double tp   = (High[0] + Low[0] + Close[0]) / 3.0;
            _vwapSumPV += tp * Volume[0];
            _vwapSumV  += Volume[0];
            _currentVWAP = _vwapSumV > 0 ? _vwapSumPV / _vwapSumV : Close[0];
        }

        private void LogRechazo(STASetupResult setup, string motivo, double rsi,
                                  string detalle, double rr, double aiConf = 0)
        {
            if (!LogRejectedSignals || _journal == null) return;
            _journal.LogRejected(new STARejectedSignalRecord
            {
                Timestamp      = Time[0],
                Instrument     = Instrument.FullName,
                Direction      = setup.Direction,
                SetupType      = setup.SetupType,
                RejectReason   = motivo,
                BaseConfidence = setup.BaseConfidence,
                AiConfidence   = aiConf,
                AiReason       = detalle,
                ElliottWave    = _elliottWave.CurrentWavePosition,
                DailyContext   = _dailyFilter.MarketContext,
                RiskReward     = rr
            });
        }

        // ─── Parámetros editables ────────────────────────────────────────────

        // ── Nube ──────────────────────────────────────────────────────────
        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "Período TriggerLine", Order = 1, GroupName = "1. Nube")]
        public int TriggerPeriod { get; set; }

        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "Período SmoothTrendLine", Order = 2, GroupName = "1. Nube")]
        public int SmoothPeriod { get; set; }

        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Barras mínimas en tendencia (pullback Tipo 2)", Order = 3, GroupName = "1. Nube")]
        public int MinTrendBarsForPullback { get; set; }

        // ── IA ────────────────────────────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Proveedor de IA", Order = 1, GroupName = "2. Validación IA")]
        public STAAIProvider AIProviderParam { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "API Key", Order = 2, GroupName = "2. Validación IA")]
        [PasswordPropertyText(true)]
        public string AIApiKey { get; set; }

        [NinjaScriptProperty, Range(0.0, 1.0)]
        [Display(Name = "Confianza mínima IA (0–1)", Order = 3, GroupName = "2. Validación IA")]
        public double AIMinConfidence { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Habilitar validación IA", Order = 4, GroupName = "2. Validación IA")]
        public bool EnableAIValidation { get; set; }

        // ── Riesgo ────────────────────────────────────────────────────────
        [NinjaScriptProperty, Range(1000, 10_000_000)]
        [Display(Name = "Capital de cuenta ($)", Order = 1, GroupName = "3. Riesgo")]
        public double AccountCapital { get; set; }

        [NinjaScriptProperty, Range(0.001, 0.05)]
        [Display(Name = "% de riesgo por trade", Order = 2, GroupName = "3. Riesgo")]
        public double RiskPctPerTrade { get; set; }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Máximo de contratos", Order = 3, GroupName = "3. Riesgo")]
        public int MaxContracts { get; set; }

        [NinjaScriptProperty, Range(0.5, 5.0)]
        [Display(Name = "Multiplicador stop (nube) — Tipo 1", Order = 4, GroupName = "3. Riesgo")]
        public double StopCloudMultiplier { get; set; }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Buffer de stop en ticks (vela rechazo) — Tipo 2", Order = 5, GroupName = "3. Riesgo")]
        public int StopBufferTicks { get; set; }

        [NinjaScriptProperty, Range(0.5, 5.0)]
        [Display(Name = "Multiplicador stop ATR (piso)", Order = 6, GroupName = "3. Riesgo")]
        public double StopATRMultiplier { get; set; }

        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Mínimo de ticks en el stop", Order = 7, GroupName = "3. Riesgo")]
        public int MinStopTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trailing stop activo tras TP1", Order = 8, GroupName = "3. Riesgo")]
        public bool TrailAfterTP1 { get; set; }

        [NinjaScriptProperty, Range(10, 90)]
        [Display(Name = "% de posición que sale en TP1", Order = 9, GroupName = "3. Riesgo",
                 Description = "Porcentaje de contratos que se cierran al alcanzar TP1. El resto sigue hasta TP2 o trailing. Ej: 50 = mitad sale en TP1.")]
        public int PorcentajeTP1 { get; set; }

        [NinjaScriptProperty, Range(0.5, 10.0)]
        [Display(Name = "Ratio TP1 — TrendStart (cruce)", Order = 10, GroupName = "3. Riesgo",
                 Description = "Múltiplo de riesgo para el primer objetivo en setups de cruce (Tipo 1). Ej: 1.5 = TP1 a 1.5× el riesgo.")]
        public double TP1RatioTrendStart { get; set; }

        [NinjaScriptProperty, Range(0.5, 20.0)]
        [Display(Name = "Ratio TP2 — TrendStart (cruce)", Order = 11, GroupName = "3. Riesgo",
                 Description = "Múltiplo de riesgo para el segundo objetivo en setups de cruce (Tipo 1).")]
        public double TP2RatioTrendStart { get; set; }

        [NinjaScriptProperty, Range(0.5, 10.0)]
        [Display(Name = "Ratio TP1 — CloudPullback (toque de nube)", Order = 12, GroupName = "3. Riesgo",
                 Description = "Múltiplo de riesgo para el primer objetivo en pullbacks a la nube (Tipo 2).")]
        public double TP1RatioCloudPullback { get; set; }

        [NinjaScriptProperty, Range(0.5, 20.0)]
        [Display(Name = "Ratio TP2 — CloudPullback (toque de nube)", Order = 13, GroupName = "3. Riesgo",
                 Description = "Múltiplo de riesgo para el segundo objetivo en pullbacks a la nube (Tipo 2).")]
        public double TP2RatioCloudPullback { get; set; }

        // ── Sesión ────────────────────────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Dirección permitida", Order = 0, GroupName = "4. Sesión",
                 Description = "Ambas = LONG y SHORT. SoloLong = solo compras. SoloShort = solo ventas en corto.")]
        public STADireccionPermitida DireccionPermitida { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Restringir a horario RTH (09:30–15:55 ET)", Order = 1, GroupName = "4. Sesión")]
        public bool RestrictToRTH { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Requerir confirmación de volumen en vela de rechazo", Order = 2, GroupName = "4. Sesión")]
        public bool RequireVolumeConfirmation { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filtro VWAP (solo operar a favor del VWAP)", Order = 3, GroupName = "4. Sesión")]
        public bool UseVWAPFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filtro horario — solo operar en ventana definida", Order = 4, GroupName = "4. Sesión")]
        public bool UseQualityTimeFilter { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Hora inicio (HHMM, ej: 1000 = 10:00)", Order = 5, GroupName = "4. Sesión",
                 Description = "Inicio de la ventana horaria permitida. Formato HHMM.")]
        public int HoraInicio { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Hora fin (HHMM, ej: 1530 = 15:30)", Order = 6, GroupName = "4. Sesión",
                 Description = "Fin de la ventana horaria permitida. Formato HHMM.")]
        public int HoraFin { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Registrar señales rechazadas en CSV", Order = 7, GroupName = "4. Sesión")]
        public bool LogRejectedSignals { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Cooldown barras tras setup (0=desactivado)", Order = 8, GroupName = "4. Sesión")]
        public int SetupCooldownBars { get; set; }

        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Entradas máximas por día", Order = 9, GroupName = "4. Sesión",
                 Description = "Detiene el trading al alcanzar este número de entradas en el día.")]
        public int MaxDailyTrades { get; set; }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Pérdidas consecutivas máximas", Order = 10, GroupName = "4. Sesión",
                 Description = "Detiene el trading al alcanzar N pérdidas seguidas.")]
        public int MaxConsecLosses { get; set; }

        // ── Límites semanales ─────────────────────────────────────────────
        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Entradas máximas por semana", Order = 11, GroupName = "4. Sesión")]
        public int MaxWeeklyTrades { get; set; }

        [NinjaScriptProperty, Range(0.01, 0.20)]
        [Display(Name = "Pérdida semanal máxima (%)", Order = 12, GroupName = "4. Sesión",
                 Description = "Porcentaje del capital. Ej: 0.05 = 5%.")]
        public double MaxWeeklyLossPct { get; set; }

        // ── Entrada y calidad de setup ────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Entrada límite (CloudPullback)", Order = 1, GroupName = "8. Entrada y calidad")]
        public bool UseLimitEntry { get; set; }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Offset límite (ticks)", Order = 2, GroupName = "8. Entrada y calidad")]
        public int LimitOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filtro R:R mínimo", Order = 3, GroupName = "8. Entrada y calidad")]
        public bool UseMinRR { get; set; }

        [NinjaScriptProperty, Range(0.5, 10.0)]
        [Display(Name = "R:R mínimo requerido", Order = 4, GroupName = "8. Entrada y calidad")]
        public double MinRR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Salida por tiempo", Order = 5, GroupName = "8. Entrada y calidad")]
        public bool UseTimeExit { get; set; }

        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "Barras máximas en trade", Order = 6, GroupName = "8. Entrada y calidad")]
        public int MaxBarsInTrade { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filtro S/R día anterior", Order = 7, GroupName = "8. Entrada y calidad")]
        public bool UseSRFilter { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Distancia mínima a PDH/PDL (ticks)", Order = 8, GroupName = "8. Entrada y calidad")]
        public int SRDistanceTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filtro tasa de barras", Order = 9, GroupName = "8. Entrada y calidad")]
        public bool UseBarRateFilter { get; set; }

        [NinjaScriptProperty, Range(1, 1000)]
        [Display(Name = "Barras/hora mínimas", Order = 10, GroupName = "8. Entrada y calidad")]
        public int MinBarsPerHour { get; set; }

        [NinjaScriptProperty, Range(1, 1000)]
        [Display(Name = "Barras/hora máximas", Order = 11, GroupName = "8. Entrada y calidad")]
        public int MaxBarsPerHour { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sizing adaptativo (win rate)", Order = 12, GroupName = "8. Entrada y calidad")]
        public bool UseAdaptiveSizing { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Umbral win rate para sizing (%, ej: 40)", Order = 13, GroupName = "8. Entrada y calidad")]
        public int AdaptiveSizingThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filtro multi-timeframe (tendencia diaria)", Order = 14, GroupName = "8. Entrada y calidad")]
        public bool UseMultiTimeframeFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ADX trail boost (aprieta trailing en tendencia fuerte)", Order = 15, GroupName = "8. Entrada y calidad")]
        public bool UseAdxTrailBoost { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Umbral ADX para trail boost", Order = 16, GroupName = "8. Entrada y calidad")]
        public int AdxBoostThreshold { get; set; }

        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Período ADX", Order = 17, GroupName = "8. Entrada y calidad")]
        public int AdxPeriod { get; set; }

        // ── Visualización adicional ───────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Mostrar nube en chart", Order = 0, GroupName = "5. Visualización")]
        public bool ShowCloudInStrategy { get; set; }

        [Display(Name = "Mostrar niveles Fibonacci en chart", Order = 1, GroupName = "5. Visualización")]
        public bool ShowFibLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Mostrar pivotes Elliott en chart", Order = 2, GroupName = "5. Visualización")]
        public bool ShowElliottPivots { get; set; }

        [Display(Name = "Mostrar tablero de filtros", Order = 3, GroupName = "5. Visualización")]
        public bool ShowStrategyDashboard { get; set; }

        [Display(Name = "Posición del tablero", Order = 4, GroupName = "5. Visualización")]
        public TextPosition DashboardPosition { get; set; }

        // ── Scale-in ─────────────────────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Entrada escalonada (60% + 40%)", Order = 9, GroupName = "3. Riesgo")]
        public bool EnableScaleIn { get; set; }

        [NinjaScriptProperty, Range(2, 30)]
        [Display(Name = "Ticks de avance para scale-in", Order = 10, GroupName = "3. Riesgo")]
        public int ScaleInTicks { get; set; }

        // ─── Tablero de filtros en tiempo real ────────────────────────────
        private void DibujarDashboard(STASetupResult setup)
        {
            if (!ShowStrategyDashboard) return;

            string V = "✓"; string X = "✗"; string NA = "—";

            // Señal actual
            string senal = setup.IsValidSetup
                ? $"{setup.SetupType}  {setup.Direction}"
                : "Ninguna";

            // Volumen
            double volRatio = _avgVolume20 > 0 ? Volume[0] / _avgVolume20 : 0;
            bool   volOk    = !RequireVolumeConfirmation || volRatio >= 1.10;
            string volStr   = RequireVolumeConfirmation
                ? $"{(volOk ? V : X)}  {volRatio:F2}x  (mín 1.10x)"
                : $"{NA}  desactivado";

            // RSI
            double rsi    = ObtenerRsiM15();
            bool   rsiOkL = rsi <= 70;
            bool   rsiOkS = rsi >= 30;
            string rsiStr = $"LONG {(rsiOkL ? V : X)}  SHORT {(rsiOkS ? V : X)}  [{rsi:F1}]";

            // Horario
            bool   timeOk  = !UseQualityTimeFilter || EsHorarioCalidad();
            string timeStr = UseQualityTimeFilter
                ? $"{(timeOk ? V : X)}  {Time[0]:HH:mm}  ({HoraInicio/100:D2}:{HoraInicio%100:D2}–{HoraFin/100:D2}:{HoraFin%100:D2})"
                : $"{NA}  desactivado";

            // VWAP
            bool   vwapOkL = !UseVWAPFilter || _currentVWAP <= 0 || Close[0] >= _currentVWAP;
            bool   vwapOkS = !UseVWAPFilter || _currentVWAP <= 0 || Close[0] <= _currentVWAP;
            string vwapStr = UseVWAPFilter && _currentVWAP > 0
                ? $"LONG {(vwapOkL ? V : X)}  SHORT {(vwapOkS ? V : X)}  VWAP={_currentVWAP:F2}"
                : $"{NA}  desactivado";

            // Contexto diario
            string ctxStr;
            if (!_dailyFilter.IsInitialized)
            {
                ctxStr = $"— cargando... (barras diarias disponibles: {CurrentBars[IDX_DAILY]})";
            }
            else
            {
                bool dirOkL = _dailyFilter.IsDirectionAllowed("LONG");
                bool dirOkS = _dailyFilter.IsDirectionAllowed("SHORT");
                ctxStr = $"LONG {(dirOkL ? V : X)}  SHORT {(dirOkS ? V : X)}  [{_dailyFilter.MarketContext}]";
            }

            // Riesgo
            bool   riskOk  = _riskManager.CanTrade();
            int    winRate  = _riskManager.DailyTrades > 0
                ? (int)Math.Round(_riskManager.DailyWinRate * 100) : 0;
            string riskStr  = $"{(riskOk ? V : X)}  {_riskManager.DailyTrades}/{MaxDailyTrades} trades" +
                              $"  WR={winRate}%  P&L=${_riskManager.DailyPnL:F0}" +
                              $"  sem={_riskManager.WeeklyTrades}/{MaxWeeklyTrades}";

            // IA
            string iaStr = !EnableAIValidation
                ? $"{NA}  desactivada"
                : _dashLastAiResult;

            // Posición
            string posStr = _positionOpen
                ? $"{_activeDirection}  {Position.Quantity}c  entry={_activeEntryPrice:F2}"
                : "FLAT";

            string dirStr = DireccionPermitida == STADireccionPermitida.Ambas    ? "LONG ✓  SHORT ✓"
                         : DireccionPermitida == STADireccionPermitida.SoloLong  ? "LONG ✓  SHORT ✗"
                         :                                                          "LONG ✗  SHORT ✓";

            string txt =
                "── SmoothTrendAI ────────────────────────\n"  +
                $" Señal   : {senal}\n"                         +
                $" Estado  : {_dashSignalStatus}\n"             +
                $" Dirección: {dirStr}\n"                       +
                "─────────────────────────────────────────\n"  +
                $" Volumen : {volStr}\n"                        +
                $" RSI     : {rsiStr}\n"                        +
                $" Horario : {timeStr}\n"                       +
                $" VWAP    : {vwapStr}\n"                       +
                $" Contexto: {ctxStr}\n"                        +
                $" Riesgo  : {riskStr}\n"                       +
                $" IA      : {iaStr}\n"                         +
                "─────────────────────────────────────────\n"  +
                $" Posición: {posStr}\n";

            Draw.TextFixed(this, "STA_Dashboard", txt, DashboardPosition,
                Brushes.White,
                new NinjaTrader.Gui.Tools.SimpleFont("Courier New", 10),
                Brushes.Transparent, Brushes.Black, 80);
        }
    }
}
