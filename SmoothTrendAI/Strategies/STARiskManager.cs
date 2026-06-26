// ============================================================
// STARiskManager.cs — Gestión de riesgo diferenciada por tipo de setup
// Proyecto: SmoothTrendAI  |  Prefijo: STA
// TrendStart   → stop basado en ancho de nube (más amplio)
// CloudPullback → stop basado en vela de rechazo (más ajustado, mejor R:R)
// ============================================================
using System;

namespace NinjaTrader.NinjaScript.Strategies
{
    // ─── Resultado del cálculo de riesgo ─────────────────────────────────
    public class STARiskParameters
    {
        public double StopPrice    { get; set; }
        public double Target1Price { get; set; }
        public double Target2Price { get; set; }
        public double StopTicks    { get; set; }
        public int    Contracts    { get; set; }
        public string Description  { get; set; }
    }

    /// <summary>
    /// Calcula stops, targets y tamaño de posición.
    /// El cálculo varía según el SetupType del STASetupResult.
    /// Controla también los límites diarios y semanales de operación.
    /// </summary>
    public class STARiskManager
    {
        // ─── Parámetros configurables ──────────────────────────────────────
        public double AccountCapital          { get; set; } = 50_000.0;
        public double RiskPctPerTrade         { get; set; } = 0.01;
        public double StopCloudMultiplier     { get; set; } = 2.0;
        public int    StopBufferTicks         { get; set; } = 3;
        public double StopATRMultiplier       { get; set; } = 1.3;
        public int    MinStopTicks            { get; set; } = 6;
        public int    MaxContracts            { get; set; } = 4;
        public bool   TrailAfterTP1           { get; set; } = true;
        public double TrailingATRMultiplier   { get; set; } = 1.0;

        // Multiplicadores de targets por tipo
        public double TrendStart_TP1Mult      { get; set; } = 1.5;
        public double TrendStart_TP2Mult      { get; set; } = 3.0;
        public double CloudPullback_TP1Mult   { get; set; } = 2.0;
        public double CloudPullback_TP2Mult   { get; set; } = 4.0;

        // Penalización contexto neutral
        public double NeutralContextMultiplier { get; set; } = 0.70;

        // Límites diarios
        public int    MaxDailyTrades          { get; set; } = 5;
        public int    MaxTrendStartPerDay     { get; set; } = 3;
        public int    MaxCloudPullbackPerDay  { get; set; } = 4;
        public int    MaxConsecutiveLosses    { get; set; } = 3;
        public double MaxDailyLossPct         { get; set; } = 0.018;
        public double MaxDailyProfitPct       { get; set; } = 0.030;

        // Límites semanales
        public int    MaxWeeklyTrades         { get; set; } = 30;
        public double MaxWeeklyLossPct        { get; set; } = 0.05;

        // Factor de sizing adaptativo (0.5–1.0, ajustado externamente)
        public double AdaptiveSizeFactor      { get; set; } = 1.0;

        // ─── Contadores diarios ────────────────────────────────────────────
        public int    DailyTrades              { get; private set; }
        public int    DailyTrendStartTrades    { get; private set; }
        public int    DailyCloudPullbackTrades { get; private set; }
        public double DailyPnL                 { get; private set; }
        public int    ConsecutiveLosses        { get; private set; }
        public int    DailyWins                { get; private set; }
        public int    DailyLosses             { get; private set; }

        // ─── Contadores semanales ─────────────────────────────────────────
        public int    WeeklyTrades             { get; private set; }
        public double WeeklyPnL               { get; private set; }

        // ─── Propiedades calculadas ───────────────────────────────────────
        public double DailyWinRate => DailyTrades > 0
            ? (double)DailyWins / DailyTrades
            : 0.0;

        /// <summary>Calcular los parámetros de riesgo para un trade.</summary>
        public STARiskParameters Calculate(STASetupResult setup,
                                            double entryPrice,
                                            string direction,
                                            double cloudWidth,
                                            double atr14,
                                            double rejCandleLow,
                                            double rejCandleHigh,
                                            double tickSize,
                                            double tickValue,
                                            double aiRiskAdjustment,
                                            double elliottMultiplier,
                                            string marketContext)
        {
            bool isLong       = direction == "LONG";
            bool isTrendStart = setup.SetupType == "TrendStart";

            // ── 1. Stop Distance ──────────────────────────────────────────
            double stopDist;

            if (isTrendStart)
            {
                stopDist = cloudWidth * StopCloudMultiplier;
            }
            else
            {
                double distVela = isLong
                    ? entryPrice - rejCandleLow  + StopBufferTicks * tickSize
                    : rejCandleHigh - entryPrice + StopBufferTicks * tickSize;
                stopDist = distVela;
            }

            stopDist = Math.Max(stopDist, atr14 * StopATRMultiplier);
            stopDist = Math.Max(stopDist, MinStopTicks * tickSize);

            double stopPrice = isLong
                ? entryPrice - stopDist
                : entryPrice + stopDist;

            double stopTicks = tickSize > 0 ? stopDist / tickSize : stopDist;

            // ── 2. Targets ────────────────────────────────────────────────
            double tp1Mult = isTrendStart ? TrendStart_TP1Mult : CloudPullback_TP1Mult;
            double tp2Mult = isTrendStart ? TrendStart_TP2Mult : CloudPullback_TP2Mult;

            if (elliottMultiplier >= 1.25 &&
                (setup.ElliottContext?.Contains("Wave_3_Start") ?? false))
                tp2Mult *= 1.30;

            double tp1 = isLong
                ? entryPrice + stopDist * tp1Mult
                : entryPrice - stopDist * tp1Mult;

            double tp2 = isLong
                ? entryPrice + stopDist * tp2Mult
                : entryPrice - stopDist * tp2Mult;

            // ── 3. Contratos ──────────────────────────────────────────────
            double riskCapital     = AccountCapital * RiskPctPerTrade;
            double riskPerContract = stopTicks * tickValue;

            int contratos = riskPerContract > 0
                ? (int)Math.Floor(riskCapital / riskPerContract)
                : 1;

            contratos = (int)Math.Floor(contratos * aiRiskAdjustment);
            contratos = (int)Math.Floor(contratos * elliottMultiplier);

            if (!isTrendStart)
                contratos = (int)Math.Floor(contratos * 1.10);

            if (marketContext == "Neutral")
                contratos = (int)Math.Floor(contratos * NeutralContextMultiplier);

            // Sizing adaptativo (reduce contratos cuando win rate es bajo)
            if (AdaptiveSizeFactor < 1.0)
                contratos = (int)Math.Floor(contratos * AdaptiveSizeFactor);

            contratos = Math.Max(1, Math.Min(contratos, MaxContracts));

            return new STARiskParameters
            {
                StopPrice    = stopPrice,
                Target1Price = tp1,
                Target2Price = tp2,
                StopTicks    = stopTicks,
                Contracts    = contratos,
                Description  = $"Stop={stopPrice:F2} ({stopTicks:F0}t) " +
                               $"TP1={tp1:F2} TP2={tp2:F2} Ctrs={contratos}"
            };
        }

        // ─── Control de límites ────────────────────────────────────────────

        /// <summary>Verificar si se puede abrir un nuevo trade.</summary>
        public bool CanTrade(string setupType = "Any")
        {
            if (DailyTrades       >= MaxDailyTrades)       return false;
            if (WeeklyTrades      >= MaxWeeklyTrades)      return false;
            if (ConsecutiveLosses >= MaxConsecutiveLosses) return false;

            double maxLoss       = AccountCapital * MaxDailyLossPct;
            double maxProfit     = AccountCapital * MaxDailyProfitPct;
            double maxWeeklyLoss = AccountCapital * MaxWeeklyLossPct;

            if (DailyPnL  <= -maxLoss)       return false;
            if (DailyPnL  >=  maxProfit)     return false;
            if (WeeklyPnL <= -maxWeeklyLoss) return false;

            if (setupType == "TrendStart"    && DailyTrendStartTrades    >= MaxTrendStartPerDay)    return false;
            if (setupType == "CloudPullback" && DailyCloudPullbackTrades >= MaxCloudPullbackPerDay) return false;

            return true;
        }

        public void RegisterTrade(string setupType)
        {
            DailyTrades++;
            WeeklyTrades++;
            if (setupType == "TrendStart") DailyTrendStartTrades++;
            else                           DailyCloudPullbackTrades++;
        }

        public void RegisterTradeResult(double pnlUsd)
        {
            DailyPnL  += pnlUsd;
            WeeklyPnL += pnlUsd;
            if (pnlUsd < 0) { ConsecutiveLosses++; DailyLosses++; }
            else            { ConsecutiveLosses = 0; DailyWins++;  }
        }

        public void ResetDaily()
        {
            DailyTrades = DailyTrendStartTrades = DailyCloudPullbackTrades = 0;
            DailyPnL    = 0;
            DailyWins   = DailyLosses = 0;
            ConsecutiveLosses = 0;
        }

        public void ResetWeekly()
        {
            WeeklyTrades = 0;
            WeeklyPnL    = 0;
        }

        /// <summary>Calcular el precio del trailing stop después de que TP1 fue tocado.</summary>
        public double CalculateTrailingStop(double currentPrice, string direction,
                                             double atr14, double multiplierOverride = 0)
        {
            double mult = multiplierOverride > 0 ? multiplierOverride : TrailingATRMultiplier;
            double dist = atr14 * mult;
            return direction == "LONG"
                ? currentPrice - dist
                : currentPrice + dist;
        }

        /// <summary>Contratos para la entrada inicial en scale-in.</summary>
        public int CalculateInitialContracts(int totalContracts, double scaleInFactor = 0.6)
            => Math.Max(1, (int)Math.Ceiling(totalContracts * scaleInFactor));
    }
}
