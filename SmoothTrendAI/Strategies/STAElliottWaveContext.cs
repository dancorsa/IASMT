// ============================================================
// STAElliottWaveContext.cs — Conteo simplificado de ondas de Elliott
// Proyecto: SmoothTrendAI  |  Prefijo: STA
// Clase independiente. Aplica las 3 reglas duras de Elliott.
// Es deliberadamente conservador: devuelve "Undefined" antes de
// forzar un conteo ambiguo. Aporte probabilístico a la IA, no filtro absoluto.
// ============================================================
using System;
using System.Collections.Generic;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.Strategies
{
    // ─── Datos de un pivote de precio ─────────────────────────────────────
    public class STAPivotPoint
    {
        public double Price  { get; set; }
        public int    Bar    { get; set; }
        public bool   IsHigh { get; set; }

        public STAPivotPoint(double price, int bar, bool isHigh)
        {
            Price = price; Bar = bar; IsHigh = isHigh;
        }
    }

    /// <summary>
    /// Contador de ondas de Elliott basado en fractales y las 3 reglas duras.
    /// No reemplaza la nube ni el contexto diario — los complementa.
    /// </summary>
    public class STAElliottWaveContext
    {
        // ─── Parámetros ────────────────────────────────────────────────────
        public int    SwingLookbackBars   { get; set; } = 3;
        public int    MaxPivotsTracked    { get; set; } = 8;
        public double MinWaveConfidence   { get; set; } = 0.40;
        public bool   ShowFibonacciLevels { get; set; } = true;

        // ─── Estado actual ─────────────────────────────────────────────────
        public string CurrentWavePosition { get; private set; } = "Undefined";
        public string CurrentWaveDirection { get; private set; } = "Undefined";
        public double WaveConfidence      { get; private set; }
        public bool   IsFavorableForLong  { get; private set; }
        public bool   IsFavorableForShort { get; private set; }

        // ─── Niveles de Fibonacci ──────────────────────────────────────────
        public double Fib_382  { get; private set; }
        public double Fib_500  { get; private set; }
        public double Fib_618  { get; private set; }
        public double Fib_1272 { get; private set; }
        public double Fib_1618 { get; private set; }
        public double NearestFibSupport    { get; private set; }
        public double NearestFibResistance { get; private set; }

        // ─── Colección interna de pivotes ─────────────────────────────────
        private readonly List<STAPivotPoint> _pivots = new List<STAPivotPoint>();
        private ISeries<double> _highSeries;
        private ISeries<double> _lowSeries;
        private int _currentBar;

        public void Initialize(ISeries<double> highSeries, ISeries<double> lowSeries)
        {
            _highSeries = highSeries;
            _lowSeries  = lowSeries;
        }

        /// <summary>Llamar en cada OnBarUpdate (BarsInProgress == 0).</summary>
        public void Update(int currentBar, double currentPrice)
        {
            _currentBar = currentBar;
            if (currentBar < SwingLookbackBars * 2 + 1) return;

            DetectarPivote(SwingLookbackBars);

            if (_pivots.Count >= 4)
                ActualizarConteoOndas(currentPrice);
            else
            {
                CurrentWavePosition = "Undefined";
                WaveConfidence      = 0.0;
            }

            ActualizarFavorables();
        }

        // ─── Detección de pivote por fractal ──────────────────────────────
        private void DetectarPivote(int barsAgo)
        {
            if (_highSeries == null || _lowSeries == null) return;
            if (_currentBar < SwingLookbackBars * 2 + 1) return;

            double centroHigh = _highSeries[barsAgo];
            double centroLow  = _lowSeries[barsAgo];
            bool esSwingHigh  = true;
            bool esSwingLow   = true;

            for (int i = 1; i <= SwingLookbackBars; i++)
            {
                if (_highSeries[barsAgo - i] >= centroHigh ||
                    _highSeries[barsAgo + i] >= centroHigh)
                    esSwingHigh = false;

                if (_lowSeries[barsAgo - i] <= centroLow ||
                    _lowSeries[barsAgo + i] <= centroLow)
                    esSwingLow = false;
            }

            int barAbsoluta = _currentBar - barsAgo;
            foreach (var p in _pivots)
                if (p.Bar == barAbsoluta) return;  // ya registrado

            if (esSwingHigh)
            {
                // Alternancia: el último pivote debe ser Low
                if (_pivots.Count == 0 || !_pivots[_pivots.Count - 1].IsHigh)
                {
                    _pivots.Add(new STAPivotPoint(centroHigh, barAbsoluta, true));
                    LimitarPivotes();
                }
            }
            else if (esSwingLow)
            {
                // Alternancia: el último pivote debe ser High
                if (_pivots.Count == 0 || _pivots[_pivots.Count - 1].IsHigh)
                {
                    _pivots.Add(new STAPivotPoint(centroLow, barAbsoluta, false));
                    LimitarPivotes();
                }
            }
        }

        private void LimitarPivotes()
        {
            while (_pivots.Count > MaxPivotsTracked)
                _pivots.RemoveAt(0);
        }

        // ─── Conteo de ondas con las 3 reglas duras ───────────────────────
        private void ActualizarConteoOndas(double precioActual)
        {
            if (_pivots.Count < 4)
            {
                CurrentWavePosition = "Undefined";
                WaveConfidence      = 0.0;
                return;
            }

            int n    = Math.Min(_pivots.Count, 6);
            var pts  = _pivots.GetRange(_pivots.Count - n, n);

            var rAlcista = ClasificarImpulsoAlcista(pts, precioActual);
            if (rAlcista.Confidence >= MinWaveConfidence)
            {
                CurrentWavePosition = rAlcista.Position;
                CurrentWaveDirection = "LONG";
                WaveConfidence      = rAlcista.Confidence;
                ActualizarFibonacci(pts, esAlcista: true);
                return;
            }

            var rBajista = ClasificarImpulsoBajista(pts, precioActual);
            if (rBajista.Confidence >= MinWaveConfidence)
            {
                CurrentWavePosition = rBajista.Position;
                CurrentWaveDirection = "SHORT";
                WaveConfidence      = rBajista.Confidence;
                ActualizarFibonacci(pts, esAlcista: false);
                return;
            }

            var rCorreccion = ClasificarCorreccion(pts, precioActual);
            if (rCorreccion.Confidence >= MinWaveConfidence)
            {
                CurrentWavePosition = rCorreccion.Position;
                CurrentWaveDirection = "BOTH";
                WaveConfidence      = rCorreccion.Confidence;
                return;
            }

            CurrentWavePosition = "Undefined";
            CurrentWaveDirection = "Undefined";
            WaveConfidence      = 0.0;
        }

        private struct WaveResult { public string Position; public double Confidence; }

        // ── Impulso alcista: pts[0]=Low pts[1]=High pts[2]=Low pts[3]=High …
        private WaveResult ClasificarImpulsoAlcista(List<STAPivotPoint> pts, double precio)
        {
            var r = new WaveResult { Position = "Undefined", Confidence = 0.0 };
            if (pts.Count < 3 || pts[0].IsHigh) return r;

            double w1 = pts[1].Price - pts[0].Price;
            if (w1 <= 0) return r;

            // Regla 1: Onda 2 ≤ 100% de onda 1
            double retrace2 = pts[1].Price - pts[2].Price;
            double ratio2   = retrace2 / w1;
            if (ratio2 >= 1.0) return r;

            bool fib2Ok = ratio2 >= 0.382 && ratio2 <= 0.618;

            if (pts.Count == 3)
            {
                // Tenemos onda 1 y 2 completas
                r.Position   = fib2Ok ? "Wave_2_Pullback" : "Wave_1_Start";
                r.Confidence = fib2Ok ? 0.60 : 0.42;
                return r;
            }

            double w3 = pts[3].Price - pts[2].Price;

            // Regla 3: Onda 4 no entra en territorio de precio de onda 1
            if (pts.Count >= 5 && pts[4].Price <= pts[1].Price) return r;

            // Regla 2: Onda 3 no es la más corta (verificar con onda 5 si hay)
            if (pts.Count >= 6)
            {
                double w5 = pts[5].Price - pts[4].Price;
                if (w3 < w1 && w3 < w5) return r;
            }

            if (pts.Count == 4)
            {
                if (precio < pts[3].Price && precio > pts[1].Price)
                {
                    r.Position   = "Wave_4_Pullback";
                    r.Confidence = fib2Ok ? 0.58 : 0.45;
                }
                else if (precio >= pts[3].Price)
                {
                    r.Position   = "Wave_3_Extension";
                    r.Confidence = 0.52;
                }
                else
                {
                    r.Position   = "Wave_3_Start";
                    r.Confidence = fib2Ok ? 0.70 : 0.55;
                }
            }
            else if (pts.Count >= 5)
            {
                double retrace4 = pts[3].Price - pts[4].Price;
                double ratio4   = w3 > 0 ? retrace4 / w3 : 0;
                bool   fib4Ok   = ratio4 >= 0.236 && ratio4 <= 0.500;

                if (pts.Count == 5 && precio > pts[3].Price)
                {
                    r.Position   = "Wave_5_Start";
                    r.Confidence = fib4Ok ? 0.65 : 0.50;
                }
                else if (pts.Count >= 6)
                {
                    r.Position   = "Correction_AB";
                    r.Confidence = 0.55;
                }
                else
                {
                    r.Position   = "Wave_4_Pullback";
                    r.Confidence = fib4Ok ? 0.60 : 0.45;
                }
            }

            return r;
        }

        // ── Impulso bajista: espejo del alcista
        private WaveResult ClasificarImpulsoBajista(List<STAPivotPoint> pts, double precio)
        {
            var r = new WaveResult { Position = "Undefined", Confidence = 0.0 };
            if (pts.Count < 3 || !pts[0].IsHigh) return r;

            double w1 = pts[0].Price - pts[1].Price;
            if (w1 <= 0) return r;

            double retrace2 = pts[2].Price - pts[1].Price;
            double ratio2   = retrace2 / w1;
            if (ratio2 >= 1.0) return r;

            bool fib2Ok = ratio2 >= 0.382 && ratio2 <= 0.618;

            if (pts.Count == 3)
            {
                r.Position   = fib2Ok ? "Wave_2_Pullback" : "Wave_1_Start";
                r.Confidence = fib2Ok ? 0.60 : 0.42;
                return r;
            }

            double w3 = pts[2].Price - pts[3].Price;
            if (pts.Count >= 5 && pts[4].Price >= pts[1].Price) return r;

            if (pts.Count >= 6)
            {
                double w5 = pts[4].Price - pts[5].Price;
                if (w3 < w1 && w3 < w5) return r;
            }

            if (pts.Count == 4)
            {
                if (precio > pts[3].Price && precio < pts[1].Price)
                {
                    r.Position   = "Wave_4_Pullback";
                    r.Confidence = fib2Ok ? 0.58 : 0.45;
                }
                else if (precio <= pts[3].Price)
                {
                    r.Position   = "Wave_3_Extension";
                    r.Confidence = 0.52;
                }
                else
                {
                    r.Position   = "Wave_3_Start";
                    r.Confidence = fib2Ok ? 0.70 : 0.55;
                }
            }
            else if (pts.Count >= 5)
            {
                double retrace4 = pts[4].Price - pts[3].Price;
                double ratio4   = w3 > 0 ? retrace4 / w3 : 0;
                bool   fib4Ok   = ratio4 >= 0.236 && ratio4 <= 0.500;

                if (pts.Count == 5 && precio < pts[3].Price)
                {
                    r.Position   = "Wave_5_Start";
                    r.Confidence = fib4Ok ? 0.65 : 0.50;
                }
                else if (pts.Count >= 6)
                {
                    r.Position   = "Correction_AB";
                    r.Confidence = 0.55;
                }
                else
                {
                    r.Position   = "Wave_4_Pullback";
                    r.Confidence = fib4Ok ? 0.60 : 0.45;
                }
            }

            return r;
        }

        // ── Corrección A-B-C heurística
        private WaveResult ClasificarCorreccion(List<STAPivotPoint> pts, double precio)
        {
            var r = new WaveResult { Position = "Undefined", Confidence = 0.0 };
            if (pts.Count < 3) return r;

            double wA = Math.Abs(pts[1].Price - pts[0].Price);
            if (wA <= 0) return r;

            double wBRetrace = Math.Abs(pts[2].Price - pts[1].Price) / wA;

            if (wBRetrace >= 0.50 && wBRetrace <= 0.786)
            {
                if (pts.Count == 3)
                {
                    r.Position   = "Correction_AB";
                    r.Confidence = 0.52;
                }
                else if (pts.Count >= 4)
                {
                    double wC    = Math.Abs(pts[3].Price - pts[2].Price);
                    double ratCA = wC / wA;
                    r.Position   = (ratCA >= 0.786 && ratCA <= 1.272)
                                   ? "Correction_C_End"
                                   : "Correction_AB";
                    r.Confidence = r.Position == "Correction_C_End" ? 0.58 : 0.50;
                }
            }

            return r;
        }

        // ─── Fibonacci ────────────────────────────────────────────────────
        private void ActualizarFibonacci(List<STAPivotPoint> pts, bool esAlcista)
        {
            if (pts.Count < 2) return;

            int lastIdx = pts.Count - 1;
            // Tomar el último par de pivotes de impulso como referencia
            double inicio = pts[0].Price;
            double fin;

            if (esAlcista)
            {
                // Buscar el último High confirmado
                fin = pts[lastIdx].IsHigh ? pts[lastIdx].Price : pts[lastIdx - 1].Price;
                double rango = Math.Abs(fin - inicio);
                Fib_382  = fin - rango * 0.382;
                Fib_500  = fin - rango * 0.500;
                Fib_618  = fin - rango * 0.618;
                Fib_1272 = inicio + rango * 1.272;
                Fib_1618 = inicio + rango * 1.618;
                NearestFibSupport    = Fib_618;
                NearestFibResistance = Fib_1272;
            }
            else
            {
                fin = pts[lastIdx].IsHigh ? pts[lastIdx - 1].Price : pts[lastIdx].Price;
                double rango = Math.Abs(inicio - fin);
                Fib_382  = fin + rango * 0.382;
                Fib_500  = fin + rango * 0.500;
                Fib_618  = fin + rango * 0.618;
                Fib_1272 = inicio - rango * 1.272;
                Fib_1618 = inicio - rango * 1.618;
                NearestFibSupport    = Fib_1272;
                NearestFibResistance = Fib_618;
            }
        }

        private void ActualizarFavorables()
        {
            bool fasesImpulsivas =
                CurrentWavePosition == "Wave_3_Start"      ||
                CurrentWavePosition == "Wave_1_Start"      ||
                CurrentWavePosition == "Wave_5_Start"      ||
                CurrentWavePosition == "Correction_C_End";

            IsFavorableForLong  = fasesImpulsivas && WaveConfidence >= MinWaveConfidence
                                  && (CurrentWaveDirection == "LONG" || CurrentWaveDirection == "BOTH");
            IsFavorableForShort = fasesImpulsivas && WaveConfidence >= MinWaveConfidence
                                  && (CurrentWaveDirection == "SHORT" || CurrentWaveDirection == "BOTH");
        }

        public int PivotCount => _pivots.Count;

        public List<STAPivotPoint> GetPivots() => new List<STAPivotPoint>(_pivots);

        public override string ToString() =>
            $"Wave={CurrentWavePosition}, Conf={WaveConfidence:F2}, " +
            $"Dir={CurrentWaveDirection}, FavL={IsFavorableForLong}, FavS={IsFavorableForShort}, Pivots={_pivots.Count}";
    }
}
