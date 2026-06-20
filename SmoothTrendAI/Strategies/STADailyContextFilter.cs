// ============================================================
// STADailyContextFilter.cs — Filtro de contexto diario estilo Kaufman
// Proyecto: SmoothTrendAI  |  Prefijo: STA
// Clase independiente, sin dependencias de otras estrategias.
// ============================================================
namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Clasifica el contexto del mercado comparando el precio actual contra
    /// Prior High / Prior Close / Prior Low del día anterior (Kaufman).
    /// Determina la dirección permitida para las entradas del día.
    /// Actualizar en cada OnBarUpdate con los datos de la serie diaria.
    /// </summary>
    public class STADailyContextFilter
    {
        // ─── Valores del día anterior ──────────────────────────────────────
        public double PriorHigh  { get; private set; }
        public double PriorLow   { get; private set; }
        public double PriorOpen  { get; private set; }
        public double PriorClose { get; private set; }
        public bool   PriorCandleWasBullish { get; private set; }

        // ─── Clasificación del contexto ────────────────────────────────────
        // "Continuation_Up" | "Pullback_Down" | "Pullback_Up" |
        // "Continuation_Down" | "Neutral"
        public string MarketContext    { get; private set; } = "Neutral";

        // "LONG" | "SHORT" | "BOTH" | "NONE"
        public string AllowedDirection { get; private set; } = "BOTH";

        // ─── Sesgo estadístico ─────────────────────────────────────────────
        public int  ConsecutiveDirectionDays { get; private set; }
        public bool HasStrongDirectionalBias { get; private set; }

        private double _closeD0, _closeD1, _closeD2, _closeD3;
        private bool   _initialized;

        /// <summary>
        /// Actualizar con datos de la barra diaria.
        /// Llamar cuando CurrentBars[idxDaily] >= 3 y BarsInProgress == 0.
        /// </summary>
        public void Update(double currentPrice,
                           double dailyHigh1,  double dailyLow1,
                           double dailyOpen1,  double dailyClose1,
                           double dailyClose2, double dailyClose3)
        {
            PriorHigh  = dailyHigh1;
            PriorLow   = dailyLow1;
            PriorOpen  = dailyOpen1;
            PriorClose = dailyClose1;
            PriorCandleWasBullish = dailyClose1 > dailyOpen1;

            _closeD0 = currentPrice;
            _closeD1 = dailyClose1;
            _closeD2 = dailyClose2;
            _closeD3 = dailyClose3;
            _initialized = true;

            ClasificarContexto(currentPrice);
            ContarDiasConsecutivos();
        }

        private void ClasificarContexto(double precio)
        {
            if (!_initialized) return;

            if (PriorCandleWasBullish)
            {
                // ── Vela anterior ALCISTA ──────────────────────────────────
                if (precio > PriorHigh)
                {
                    MarketContext    = "Continuation_Up";
                    AllowedDirection = "LONG";
                }
                else if (precio < PriorClose)
                {
                    MarketContext    = "Pullback_Down";
                    AllowedDirection = "SHORT";
                }
                else
                {
                    MarketContext    = "Neutral";
                    AllowedDirection = "BOTH";
                }
            }
            else
            {
                // ── Vela anterior BAJISTA ──────────────────────────────────
                if (precio > PriorClose)
                {
                    MarketContext    = "Pullback_Up";
                    AllowedDirection = "LONG";
                }
                else if (precio < PriorLow)
                {
                    MarketContext    = "Continuation_Down";
                    AllowedDirection = "SHORT";
                }
                else
                {
                    MarketContext    = "Neutral";
                    AllowedDirection = "BOTH";
                }
            }
        }

        private void ContarDiasConsecutivos()
        {
            if (!_initialized) return;

            bool d0_vs_d1 = _closeD0 > _closeD1;
            bool d1_vs_d2 = _closeD1 > _closeD2;
            bool d2_vs_d3 = _closeD2 > _closeD3;

            int count = 1;
            if (d0_vs_d1 == d1_vs_d2)
            {
                count = 2;
                if (d1_vs_d2 == d2_vs_d3) count = 3;
            }

            ConsecutiveDirectionDays = count;
            HasStrongDirectionalBias = count >= 2;
        }

        /// <summary>Verifica si la dirección dada está permitida hoy.</summary>
        public bool IsDirectionAllowed(string direction)
        {
            if (!_initialized)       return false;
            if (AllowedDirection == "NONE") return false;
            if (AllowedDirection == "BOTH") return true;
            return AllowedDirection == direction;
        }

        public override string ToString() =>
            $"Context={MarketContext}, Dir={AllowedDirection}, " +
            $"ConsecDays={ConsecutiveDirectionDays}, StrongBias={HasStrongDirectionalBias}, " +
            $"PriorH={PriorHigh:F2} PriorC={PriorClose:F2} PriorL={PriorLow:F2}";
    }
}
