// ============================================================
// STASetupClassifier.cs — Orquestador de los 3 tipos de setup
// Proyecto: SmoothTrendAI  |  Prefijo: STA
// Combina SmoothTrendCloud + STADailyContextFilter + STAElliottWaveContext.
// Tipo 2 (CloudPullback) tiene PRIORIDAD sobre Tipo 1 (TrendStart).
// ============================================================
using System;
using NinjaTrader.NinjaScript.Indicators;

namespace NinjaTrader.NinjaScript.Strategies
{
    // ─── Resultado de la clasificación de setup ───────────────────────────
    public class STASetupResult
    {
        public bool   IsValidSetup      { get; set; }
        public string SetupType         { get; set; } = "None";   // "TrendStart" | "CloudPullback"
        public string Direction         { get; set; } = "";        // "LONG" | "SHORT"
        public double BaseConfidence    { get; set; }
        public string ElliottContext    { get; set; } = "";
        public double ElliottMultiplier { get; set; } = 1.0;
        public string RejectionCandle   { get; set; }              // null si Tipo 1
    }

    /// <summary>
    /// Evalúa el setup de cada barra combinando los tres módulos de análisis.
    /// Instanciar en State.DataLoaded con inyección de dependencias.
    /// </summary>
    public class STASetupClassifier
    {
        private readonly SmoothTrendCloud      _cloud;
        private readonly STADailyContextFilter _daily;
        private readonly STAElliottWaveContext _elliott;

        // ─── Datos de la barra actual ─────────────────────────────────────
        private double _open0, _high0, _low0, _close0;
        private double _open1, _high1, _low1, _close1;
        private double _volume0, _avgVolume20;

        public bool RequireVolumeConfirmation { get; set; } = true;

        public STASetupClassifier(SmoothTrendCloud cloud,
                                   STADailyContextFilter daily,
                                   STAElliottWaveContext elliott)
        {
            _cloud   = cloud;
            _daily   = daily;
            _elliott = elliott;
        }

        /// <summary>
        /// Actualizar con los datos de la barra actual antes de llamar Evaluate().
        /// </summary>
        public void UpdateBarData(double open0,  double high0,  double low0,  double close0,
                                  double open1,  double high1,  double low1,  double close1,
                                  double volume0, double avgVolume20)
        {
            _open0 = open0; _high0 = high0; _low0 = low0; _close0 = close0;
            _open1 = open1; _high1 = high1; _low1 = low1; _close1 = close1;
            _volume0    = volume0;
            _avgVolume20 = avgVolume20;
        }

        /// <summary>
        /// Evaluar el setup de la barra actual.
        /// Tipo 2 tiene prioridad cuando ambos podrían aplicar.
        /// </summary>
        public STASetupResult Evaluate()
        {
            var tipo2 = EvaluarTipo2();
            if (tipo2.IsValidSetup) return tipo2;

            var tipo1 = EvaluarTipo1();
            if (tipo1.IsValidSetup) return tipo1;

            return new STASetupResult { IsValidSetup = false, SetupType = "None" };
        }

        // ─── TIPO 1: Cruce de la nube ─────────────────────────────────────
        private STASetupResult EvaluarTipo1()
        {
            var r = new STASetupResult { SetupType = "TrendStart" };

            if (_cloud.HasCrossUpSignal && _daily.IsDirectionAllowed("LONG"))
            {
                r.IsValidSetup  = true;
                r.Direction     = "LONG";
                r.BaseConfidence = 0.55;
                AplicarElliott(r, "LONG");
            }
            else if (_cloud.HasCrossDownSignal && _daily.IsDirectionAllowed("SHORT"))
            {
                r.IsValidSetup  = true;
                r.Direction     = "SHORT";
                r.BaseConfidence = 0.55;
                AplicarElliott(r, "SHORT");
            }

            return r;
        }

        // ─── TIPO 2: Retroceso a la nube con vela de rechazo ─────────────
        private STASetupResult EvaluarTipo2()
        {
            var r = new STASetupResult { SetupType = "CloudPullback" };

            if (_cloud.TouchedCloudFromAbove && _daily.IsDirectionAllowed("LONG"))
            {
                string rechazo = DetectarVelaDeRechazoAlcista();
                if (rechazo != null)
                {
                    r.IsValidSetup   = true;
                    r.Direction      = "LONG";
                    r.BaseConfidence = 0.68;
                    r.RejectionCandle = rechazo;
                    AplicarElliott(r, "LONG");
                }
            }
            else if (_cloud.TouchedCloudFromBelow && _daily.IsDirectionAllowed("SHORT"))
            {
                string rechazo = DetectarVelaDeRechazoBajista();
                if (rechazo != null)
                {
                    r.IsValidSetup   = true;
                    r.Direction      = "SHORT";
                    r.BaseConfidence = 0.68;
                    r.RejectionCandle = rechazo;
                    AplicarElliott(r, "SHORT");
                }
            }

            return r;
        }

        // ─── Modificador de Elliott ────────────────────────────────────────
        private void AplicarElliott(STASetupResult r, string direction)
        {
            r.ElliottContext = $"{_elliott.CurrentWavePosition} (conf={_elliott.WaveConfidence:F2})";

            bool favorable = direction == "LONG"
                ? _elliott.IsFavorableForLong
                : _elliott.IsFavorableForShort;

            if (!favorable) return;

            if (r.SetupType == "TrendStart")
            {
                r.BaseConfidence    = Math.Min(1.0, r.BaseConfidence + 0.15);
                r.ElliottMultiplier = 1.20;
            }
            else  // CloudPullback
            {
                bool esOnda3Alta = _elliott.CurrentWavePosition == "Wave_3_Start"
                                   && _elliott.WaveConfidence >= 0.65;

                r.BaseConfidence    = Math.Min(1.0, r.BaseConfidence + 0.12);
                r.ElliottMultiplier = esOnda3Alta ? 1.30 : 1.25;
            }
        }

        // ─── Velas de rechazo ALCISTAS ────────────────────────────────────

        private string DetectarVelaDeRechazoAlcista()
        {
            double rango  = _high0 - _low0;
            if (rango <= 0) return null;

            double cuerpo        = Math.Abs(_close0 - _open0);
            double mechaInferior = Math.Min(_open0, _close0) - _low0;

            // Pin Bar / Martillo alcista
            if (mechaInferior >= cuerpo * 2.0          &&
                _close0 >= _low0 + rango * 0.65        &&
                cuerpo   <= rango * 0.35               &&
                ConfirmarVolumen())
                return "Pin Bar Alcista";

            // Envolvente alcista
            if (_close0 > _open0   &&
                _close1 < _open1   &&
                _open0  <= _close1 &&
                _close0 >= _open1  &&
                ConfirmarVolumen())
                return "Envolvente Alcista";

            // Vela de reversión interna (requiere volumen siempre)
            if (_open0  <= _low0  + rango * 0.30 &&
                _close0 >= _low0  + rango * 0.70 &&
                ConfirmarVolumenReforzado())
                return "Reversión Interna Alcista";

            return null;
        }

        // ─── Velas de rechazo BAJISTAS ────────────────────────────────────

        private string DetectarVelaDeRechazoBajista()
        {
            double rango  = _high0 - _low0;
            if (rango <= 0) return null;

            double cuerpo        = Math.Abs(_close0 - _open0);
            double mechaSuperior = _high0 - Math.Max(_open0, _close0);

            // Pin Bar / Estrella fugaz bajista
            if (mechaSuperior >= cuerpo * 2.0          &&
                _close0 <= _high0 - rango * 0.65       &&
                cuerpo   <= rango * 0.35               &&
                ConfirmarVolumen())
                return "Pin Bar Bajista";

            // Envolvente bajista
            if (_close0 < _open0   &&
                _close1 > _open1   &&
                _open0  >= _close1 &&
                _close0 <= _open1  &&
                ConfirmarVolumen())
                return "Envolvente Bajista";

            // Vela de reversión interna bajista
            if (_open0  >= _high0 - rango * 0.30 &&
                _close0 <= _high0 - rango * 0.70 &&
                ConfirmarVolumenReforzado())
                return "Reversión Interna Bajista";

            return null;
        }

        private bool ConfirmarVolumen()
        {
            if (!RequireVolumeConfirmation) return true;
            return _avgVolume20 > 0 && _volume0 >= _avgVolume20 * 1.10;
        }

        private bool ConfirmarVolumenReforzado()
        {
            return _avgVolume20 > 0 && _volume0 >= _avgVolume20 * 1.10;
        }
    }
}
