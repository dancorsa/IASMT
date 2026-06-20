#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript.DrawingTools;
using Newtonsoft.Json.Linq;
using System.Xml.Serialization;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    // Converter que muestra "Claude" / "OpenAI" como dropdown en el panel de propiedades de NT8
    public class STCIAProviderConverter : System.ComponentModel.TypeConverter
    {
        public override bool GetStandardValuesSupported(
            System.ComponentModel.ITypeDescriptorContext ctx) => true;
        public override bool GetStandardValuesExclusive(
            System.ComponentModel.ITypeDescriptorContext ctx) => true;
        public override StandardValuesCollection GetStandardValues(
            System.ComponentModel.ITypeDescriptorContext ctx)
            => new StandardValuesCollection(new[] { "Claude", "OpenAI" });
    }

    /// <summary>
    /// SmoothTrendCloud — Indicador de nube de doble EMA independiente.
    /// TriggerLine    = EMA(Input, TriggerPeriod)
    /// SmoothTrendLine = EMA(TriggerLine, SmoothPeriod)  ← EMA de EMA
    ///
    /// El fondo de cada barra se pinta con el color de la nube activa
    /// (opacidad configurable). Para ver la nube llena entre las dos líneas,
    /// agregar el indicador en un panel separado (IsOverlay = false desde
    /// el panel de propiedades del chart). En el panel de precios, el color
    /// de fondo es el efecto visual disponible.
    ///
    /// Detecta señales:
    ///   Tipo 1 — Cruce (HasCrossUpSignal / HasCrossDownSignal)
    ///   Tipo 2 — Toque de nube sin cruce (TouchedCloudFromAbove / TouchedCloudFromBelow)
    /// </summary>
    public class SmoothTrendCloud : Indicator
    {
        // ─── Serie interna para EMA-de-EMA ────────────────────────────────
        private Series<double> _triggerSeries;
        private EMA            _emaOfTrigger;

        // ─── Estado de la nube ─────────────────────────────────────────────
        private int  _barsInCurrentColor;
        private bool _lastColorWasUp;

        // ─── Señales públicas ──────────────────────────────────────────────
        private bool _hasCrossUpSignal;
        private bool _hasCrossDownSignal;
        private bool _touchedCloudFromAbove;
        private bool _touchedCloudFromBelow;

        // ─── Brushes con alfa pre-calculados ──────────────────────────────
        private SolidColorBrush _upBrush;
        private SolidColorBrush _downBrush;

        // ─── Rastreo de segmentos de color para Draw.Region ───────────────
        private int        _segmentStartBar;
        private Queue<int> _oldSegmentBars;
        private const int  SEGMENT_LOOKBACK = 300;

        // ─── Validación IA ─────────────────────────────────────────────────
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private bool   _isRealtime;
        private string _lastAIDecision = "—";
        private string _lastSignalTime = "—";

        // ─── Confluencia M15 ───────────────────────────────────────────────
        private bool _m15CloudIsUp;
        private int  _idxM15 = -1;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description  = "Nube de doble EMA — SmoothTrendCloud. Señales Tipo 1 (cruce) y Tipo 2 (toque sin cruce).";
                Name         = "SmoothTrendCloud";
                Calculate    = Calculate.OnBarClose;
                IsOverlay    = true;
                DisplayInDataBox = true;
                ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive = true;

                TriggerPeriod           = 12;
                SmoothPeriod            = 12;
                RegionOpacity           = 10;
                UpTrendColor            = Brushes.Cyan;
                DownTrendColor          = Brushes.Orange;
                ShowSignalArrows        = true;
                MinTrendBarsForPullback = 8;
                ShowType2Arrows         = true;
                EnableAIValidation      = false;
                AIProvider              = "Claude";
                AIApiKey                = "";
                AIMinConfidence         = 0.60;
                EnableAlerts            = true;
                ShowDashboard           = true;
                UseM15Confluence        = false;

                // Plot 0: TriggerLine (EMA rápida)
                AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Line, "TriggerLine");
                // Plot 1: SmoothTrendLine (EMA de EMA)
                AddPlot(new Stroke(Brushes.Gold, 2),       PlotStyle.Line, "SmoothTrendLine");
            }
            else if (State == State.Configure)
            {
                _triggerSeries = new Series<double>(this);
                if (UseM15Confluence)
                {
                    AddDataSeries(BarsPeriodType.Minute, 15);
                    _idxM15 = 1;
                }
            }
            else if (State == State.DataLoaded)
            {
                _emaOfTrigger       = EMA(_triggerSeries, SmoothPeriod);
                _barsInCurrentColor = 0;
                _lastColorWasUp     = true;
                _segmentStartBar    = 0;
                _oldSegmentBars     = new Queue<int>();
                _isRealtime         = false;
                RebuildBrushes();
            }
            else if (State == State.Realtime)
            {
                _isRealtime = true;
            }
        }

        protected override void OnBarUpdate()
        {
            // ── Manejo de la serie M15 (confluencia multi-timeframe) ──────────
            if (BarsInProgress == _idxM15)
            {
                if (CurrentBars[_idxM15] >= 20)
                    _m15CloudIsUp = Closes[_idxM15][0] > EMA(BarsArray[_idxM15], 20)[0];
                return;
            }

            if (CurrentBar < Math.Max(TriggerPeriod, SmoothPeriod) + 1)
                return;

            // ── Calcular TriggerLine (primer EMA) ─────────────────────────
            double triggerVal = EMA(Input, TriggerPeriod)[0];
            _triggerSeries[0] = triggerVal;

            // ── Calcular SmoothTrendLine (EMA del primer EMA) ─────────────
            double smoothVal = _emaOfTrigger[0];

            // ── Asignar plots ─────────────────────────────────────────────
            TriggerLine[0]     = triggerVal;
            SmoothTrendLine[0] = smoothVal;

            // ── Color de la nube y conteo de barras consecutivas ──────────
            bool cloudIsUp = triggerVal > smoothVal;

            if (cloudIsUp != _lastColorWasUp)
            {
                // Cerrar el segmento anterior con Draw.Region permanente
                int prevLen = CurrentBar - _segmentStartBar;
                if (prevLen > 0)
                {
                    string prevTag = $"CS_{_segmentStartBar}";
                    Draw.Region(this, prevTag, prevLen, 1,
                                TriggerLine, SmoothTrendLine,
                                null, _lastColorWasUp ? UpTrendColor : DownTrendColor,
                                RegionOpacity);
                    _oldSegmentBars.Enqueue(_segmentStartBar);

                    // Eliminar segmentos más antiguos que SEGMENT_LOOKBACK barras
                    while (_oldSegmentBars.Count > 0 &&
                           CurrentBar - _oldSegmentBars.Peek() > SEGMENT_LOOKBACK)
                        RemoveDrawObject($"CS_{_oldSegmentBars.Dequeue()}");
                }
                _segmentStartBar    = CurrentBar;
                _barsInCurrentColor = 1;
                _lastColorWasUp     = cloudIsUp;
                RebuildBrushes();
            }
            else
            {
                _barsInCurrentColor++;
            }

            // ── Colorear el plot según la dirección ───────────────────────
            PlotBrushes[0][0] = cloudIsUp ? Brushes.DodgerBlue : Brushes.OrangeRed;
            PlotBrushes[1][0] = cloudIsUp ? Brushes.Cyan       : Brushes.Orange;

            // ── Fondo de barra (tinte de nube en el panel del chart) ──────
            BackBrushes[0] = cloudIsUp ? _upBrush : _downBrush;

            // ── Nube rellena entre las dos líneas (segmento actual) ───────
            int curLen = CurrentBar - _segmentStartBar;
            if (curLen >= 0)
                Draw.Region(this, "CS_Current", curLen, 0,
                            TriggerLine, SmoothTrendLine,
                            null, cloudIsUp ? UpTrendColor : DownTrendColor,
                            RegionOpacity);

            // ── Detección de cruces (Tipo 1) ──────────────────────────────
            _hasCrossUpSignal   = CrossAbove(TriggerLine, SmoothTrendLine, 1);
            _hasCrossDownSignal = CrossBelow(TriggerLine, SmoothTrendLine, 1);

            if (ShowSignalArrows)
            {
                if (_hasCrossUpSignal)
                {
                    Draw.ArrowUp(this, "CrUp_" + CurrentBar, true, 0,
                        Low[0] - TickSize * 4, Brushes.Lime);
                    if (EnableAlerts && _isRealtime)
                        Alert("STC_CrUp_" + CurrentBar, Priority.High,
                              $"SmoothTrendCloud — Cruce LONG {Instrument.FullName}",
                              NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav",
                              30, Brushes.LimeGreen, Brushes.Black);
                }
                if (_hasCrossDownSignal)
                {
                    Draw.ArrowDown(this, "CrDn_" + CurrentBar, true, 0,
                        High[0] + TickSize * 4, Brushes.Red);
                    if (EnableAlerts && _isRealtime)
                        Alert("STC_CrDn_" + CurrentBar, Priority.High,
                              $"SmoothTrendCloud — Cruce SHORT {Instrument.FullName}",
                              NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert3.wav",
                              30, Brushes.OrangeRed, Brushes.Black);
                }
            }

            // ── Detección de toque de nube sin cruce (Tipo 2) ─────────────
            // Solo válido cuando la nube lleva al menos MinTrendBarsForPullback barras
            // en el mismo color (tendencia establecida, no ruido inicial).
            _touchedCloudFromAbove = false;
            _touchedCloudFromBelow = false;

            if (_barsInCurrentColor >= MinTrendBarsForPullback)
            {
                double cloudTop    = Math.Max(triggerVal, smoothVal);
                double cloudBottom = Math.Min(triggerVal, smoothVal);

                if (cloudIsUp)
                {
                    // Tendencia alcista: mecha baja toca la nube pero cierra
                    // por encima del límite inferior → rechazo sin cruce
                    _touchedCloudFromAbove =
                        Low[0]   <= cloudTop    &&
                        Close[0] > cloudBottom;
                }
                else
                {
                    // Tendencia bajista: mecha alta toca la nube pero cierra
                    // por debajo del límite superior
                    _touchedCloudFromBelow =
                        High[0]  >= cloudBottom &&
                        Close[0] < cloudTop;
                }
            }

            // ── Señales visuales Tipo 2 (CloudPullback para trading manual) ──
            if (ShowType2Arrows)
            {
                if (_touchedCloudFromAbove)
                {
                    bool m15Ok = _idxM15 < 0 || !UseM15Confluence || _m15CloudIsUp;
                    if (m15Ok) EmitirSenalManual("LONG",  CurrentBar, Low[0]  - TickSize * 6);
                }
                if (_touchedCloudFromBelow)
                {
                    bool m15Ok = _idxM15 < 0 || !UseM15Confluence || !_m15CloudIsUp;
                    if (m15Ok) EmitirSenalManual("SHORT", CurrentBar, High[0] + TickSize * 6);
                }
            }

            // ── Dashboard de estado ──────────────────────────────────────────
            if (ShowDashboard) ActualizarDashboard();
        }

        // ─── Señal manual Tipo 2 con validación IA opcional ───────────────
        private void EmitirSenalManual(string direction, int barNum, double price)
        {
            bool isLong = direction == "LONG";
            string arrowTag = isLong ? $"T2_Up_{barNum}" : $"T2_Dn_{barNum}";
            string textTag  = $"T2_Txt_{barNum}";

            if (!EnableAIValidation || string.IsNullOrEmpty(AIApiKey) || !_isRealtime)
            {
                // Sin IA o barra histórica: flecha directa
                if (isLong)
                    Draw.ArrowUp(this,   arrowTag, false, 0, price, Brushes.Lime);
                else
                    Draw.ArrowDown(this, arrowTag, false, 0, price, Brushes.Magenta);

                if (EnableAlerts && _isRealtime)
                {
                    _lastSignalTime = Time[0].ToString("HH:mm");
                    Alert($"STC_T2_{direction}_{barNum}", Priority.High,
                          $"SmoothTrendCloud — CloudPullback {direction} {Instrument.FullName}",
                          NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                          30, isLong ? Brushes.LimeGreen : Brushes.DeepPink, Brushes.Black);
                }
                return;
            }

            _lastSignalTime  = Time[0].ToString("HH:mm");
            _lastAIDecision  = "consultando...";

            // Con IA en tiempo real: marcador de espera mientras llega respuesta
            string waitTag = $"T2_Wait_{barNum}";
            Draw.Dot(this, waitTag, false, 0, price, Brushes.Gray);

            // Capturar datos de la barra actual (Task.Run corre en hilo aparte)
            double rsi     = CurrentBar >= 20 ? RSI(Input, 14, 3)[0] : 50.0;
            double atrTks  = CurrentBar >= 20 && TickSize > 0 ? ATR(14)[0] / TickSize : 10.0;
            double smaVol  = SMA(Volume, 20)[0];
            double volRat  = smaVol > 0 ? Volume[0] / smaVol : 1.0;
            double cwTicks = TickSize > 0 ? CloudWidth / TickSize : 0;
            string instr   = Instrument?.FullName ?? "Unknown";
            double closeP  = Close[0];
            double highP   = High[0];
            double lowP    = Low[0];
            double trigP   = TriggerLine[0];
            double smthP   = SmoothTrendLine[0];
            int    barsCol = _barsInCurrentColor;
            double minConf = AIMinConfidence;
            string prov    = AIProvider;
            string apiKey  = AIApiKey;
            TimeSpan hora  = Time[0].TimeOfDay;

            Task.Run(async () =>
            {
                bool approved = false;
                double conf   = 0;
                string reason = "";
                try
                {
                    (approved, conf, reason) = await ValidarConIAAsync(
                        direction, instr, closeP, highP, lowP,
                        trigP, smthP, cwTicks, barsCol,
                        rsi, atrTks, volRat, minConf, prov, apiKey, hora);
                }
                catch (Exception ex)
                {
                    reason = "error";
                    NinjaTrader.Code.Output.Process(
                        $"[SmoothTrendCloud IA] {ex.Message}",
                        NinjaTrader.NinjaScript.PrintTo.OutputTab1);
                }

                ChartControl?.Dispatcher?.InvokeAsync(() =>
                {
                    RemoveDrawObject(waitTag);
                    if (approved)
                    {
                        _lastAIDecision = $"✓ {direction} {conf * 100:F0}%";
                        if (isLong)
                            Draw.ArrowUp(this,   arrowTag, false, 0, price, Brushes.SpringGreen);
                        else
                            Draw.ArrowDown(this, arrowTag, false, 0, price, Brushes.DeepPink);

                        Draw.Text(this, textTag,
                            $"IA {conf * 100:F0}%\n{reason}",
                            0, price + (isLong ? -TickSize * 12 : TickSize * 12),
                            isLong ? Brushes.SpringGreen : Brushes.DeepPink);

                        if (EnableAlerts)
                            Alert($"STC_AI_{direction}_{barNum}", Priority.High,
                                  $"IA aprueba — CloudPullback {direction} {conf*100:F0}%",
                                  NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                                  30, isLong ? Brushes.LimeGreen : Brushes.DeepPink, Brushes.Black);
                    }
                    else
                    {
                        _lastAIDecision = $"✗ {reason}";
                        Draw.Text(this, textTag,
                            $"✗ {reason}",
                            0, price + (isLong ? -TickSize * 12 : TickSize * 12),
                            Brushes.DimGray);
                    }
                });
            });
        }

        private static async Task<(bool Approved, double Confidence, string Reason)>
            ValidarConIAAsync(
                string direction, string instrument,
                double close, double high, double low,
                double triggerLine, double smoothLine,
                double cloudWidthTicks, int barsInColor,
                double rsi, double atrTicks, double volumeRatio,
                double minConfidence, string provider, string apiKey,
                TimeSpan timeOfDay)
        {
            // Clasificar sesión para dar contexto al AI
            double h = timeOfDay.TotalHours;
            string sesion = h >= 9.5  && h <= 11.5 ? "apertura-NY (alta volatilidad)"
                          : h >= 14.0 && h <= 15.5 ? "tarde-NY (momentum claro)"
                          : h >= 11.5 && h <= 14.0 ? "mediodia-bajo-volumen"
                          : "fuera-horario-principal";

            // Fuerza relativa de la vela de rechazo: rango vs cuerpo
            double rango  = high - low;
            double cuerpo = Math.Abs(close - (high + low + close) / 3.0) * 3;
            string calidadVela = rango > 0 && (cuerpo / rango) < 0.4
                ? "rechazo-fuerte" : "rechazo-debil";

            const string SYSTEM =
                "Eres un analista técnico experto en futuros US (NQ, ES, MNQ). " +
                "Recibes señales CloudPullback donde el CÓDIGO ya verificó: " +
                "precio tocó el borde de la nube EMA y cerró dentro de la tendencia, " +
                "con mínimo de barras en color requeridas. " +
                "TU JUICIO añade valor en: calidad real de la vela de rechazo, " +
                "contexto de sesión (evitar señales en baja liquidez), " +
                "RSI en zona neutra (35-65), volumen >= 0.8x promedio. " +
                "Rechaza solo si hay razón técnica clara. No repitas lo que el código ya filtró. " +
                "Responde ÚNICAMENTE con JSON exacto: " +
                "{\"approved\":true/false,\"confidence\":0.0-1.0,\"reason\":\"máx 6 palabras\"}";

            string payload = $"{{" +
                $"\"instrument\":\"{JEsc(instrument)}\"," +
                $"\"direction\":\"{direction}\"," +
                $"\"sesion\":\"{sesion}\"," +
                $"\"calidad_vela\":\"{calidadVela}\"," +
                $"\"cloud_width_ticks\":{cloudWidthTicks:F1}," +
                $"\"bars_in_color\":{barsInColor}," +
                $"\"rsi_14\":{rsi:F1}," +
                $"\"atr_ticks\":{atrTicks:F1}," +
                $"\"volume_ratio\":{volumeRatio:F2}," +
                $"\"close\":{close:F2}," +
                $"\"high\":{high:F2}," +
                $"\"low\":{low:F2}" +
                $"}}";

            string responseText;

            if (provider == "Claude")
            {
                string body = $"{{\"model\":\"claude-haiku-4-5-20251001\",\"max_tokens\":80," +
                              $"\"system\":\"{JEsc(SYSTEM)}\"," +
                              $"\"messages\":[{{\"role\":\"user\",\"content\":\"{JEsc(payload)}\"}}]}}";

                var req = new HttpRequestMessage(HttpMethod.Post,
                    "https://api.anthropic.com/v1/messages");
                req.Headers.Add("x-api-key", apiKey);
                req.Headers.Add("anthropic-version", "2023-06-01");
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                responseText = JObject.Parse(json)["content"]?[0]?["text"]?.ToString() ?? "";
            }
            else // OpenAI
            {
                string body = $"{{\"model\":\"gpt-4o-mini\",\"max_tokens\":80," +
                              $"\"messages\":[" +
                              $"{{\"role\":\"system\",\"content\":\"{JEsc(SYSTEM)}\"}}," +
                              $"{{\"role\":\"user\",\"content\":\"{JEsc(payload)}\"}}]}}";

                var req = new HttpRequestMessage(HttpMethod.Post,
                    "https://api.openai.com/v1/chat/completions");
                req.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                responseText = JObject.Parse(json)["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
            }

            // Extraer JSON de la respuesta
            int s = responseText.IndexOf('{');
            int e = responseText.LastIndexOf('}');
            if (s >= 0 && e > s)
                responseText = responseText.Substring(s, e - s + 1);

            var parsed  = JObject.Parse(responseText);
            bool ok     = parsed["approved"]?.Value<bool>() ?? false;
            double c    = parsed["confidence"]?.Value<double>() ?? 0;
            string rsn  = parsed["reason"]?.Value<string>() ?? "";
            return (ok && c >= minConfidence, c, rsn);
        }

        private static string JEsc(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"")
             .Replace("\n", "\\n").Replace("\r", "");

        // ─── Dashboard de estado en el chart ──────────────────────────────
        private void ActualizarDashboard()
        {
            string dir    = _lastColorWasUp ? "▲ Alcista" : "▼ Bajista";
            string m15    = _idxM15 < 0    ? "—"
                          : _m15CloudIsUp   ? "▲" : "▼";
            string conflu = _idxM15 >= 0 && UseM15Confluence
                          ? $" | M15: {m15}" : "";

            string text =
                $"── SmoothTrendCloud ──\n" +
                $"Nube : {dir} ({_barsInCurrentColor} barras){conflu}\n" +
                $"Señal: {_lastSignalTime}\n" +
                $"IA   : {_lastAIDecision}";

            Draw.TextFixed(this, "STC_Dashboard", text,
                           TextPosition.TopRight,
                           Brushes.WhiteSmoke,
                           new SimpleFont("Courier New", 10) { Bold = true },
                           Brushes.Transparent,
                           new SolidColorBrush(Color.FromArgb(170, 15, 15, 25)),
                           90);
        }

        // ─── Reconstruir brushes con la opacidad actual ────────────────────
        private void RebuildBrushes()
        {
            byte a = (byte)Math.Max(5, Math.Min(255, RegionOpacity * 255 / 100));

            Color cu = ((SolidColorBrush)UpTrendColor).Color;
            _upBrush = new SolidColorBrush(Color.FromArgb(a, cu.R, cu.G, cu.B));
            _upBrush.Freeze();

            Color cd = ((SolidColorBrush)DownTrendColor).Color;
            _downBrush = new SolidColorBrush(Color.FromArgb(a, cd.R, cd.G, cd.B));
            _downBrush.Freeze();
        }

        // ─── Plots públicos ────────────────────────────────────────────────
        [Browsable(false)]
        [XmlIgnore]
        public Series<double> TriggerLine     => Values[0];

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> SmoothTrendLine => Values[1];

        // ─── Propiedades de señal ──────────────────────────────────────────
        public bool   HasCrossUpSignal      => _hasCrossUpSignal;
        public bool   HasCrossDownSignal    => _hasCrossDownSignal;
        public bool   TouchedCloudFromAbove => _touchedCloudFromAbove;
        public bool   TouchedCloudFromBelow => _touchedCloudFromBelow;
        public double TriggerValue          => Values[0].IsValidDataPoint(0) ? Values[0][0] : 0;
        public double SmoothValue           => Values[1].IsValidDataPoint(0) ? Values[1][0] : 0;
        public double CloudWidth            => Math.Abs(TriggerValue - SmoothValue);
        public string CurrentCloudColor     => _lastColorWasUp ? "Up" : "Down";
        public int    BarsInCurrentColor    => _barsInCurrentColor;

        // ─── Parámetros editables ──────────────────────────────────────────
        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Período TriggerLine (EMA rápida)",
                 Description = "Período de la EMA rápida sobre el precio",
                 Order = 1, GroupName = "Parámetros")]
        public int TriggerPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Período SmoothTrendLine (EMA de EMA)",
                 Description = "Período del segundo EMA aplicado sobre TriggerLine",
                 Order = 2, GroupName = "Parámetros")]
        public int SmoothPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Barras mínimas en tendencia (pullback)",
                 Description = "La nube debe llevar al menos N barras en el mismo color antes de considerar un toque como retroceso Tipo 2",
                 Order = 3, GroupName = "Parámetros")]
        public int MinTrendBarsForPullback { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Opacidad del fondo (%)",
                 Description = "Intensidad del color de fondo de la nube (1=casi transparente, 100=sólido)",
                 Order = 4, GroupName = "Visualización")]
        public int RegionOpacity { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Color tendencia alcista",
                 Order = 5, GroupName = "Visualización")]
        public Brush UpTrendColor { get; set; }

        [Browsable(false)]
        public string UpTrendColorSerializable
        {
            get => Serialize.BrushToString(UpTrendColor);
            set => UpTrendColor = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Color tendencia bajista",
                 Order = 6, GroupName = "Visualización")]
        public Brush DownTrendColor { get; set; }

        [Browsable(false)]
        public string DownTrendColorSerializable
        {
            get => Serialize.BrushToString(DownTrendColor);
            set => DownTrendColor = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Display(Name = "Mostrar flechas en cruces",
                 Order = 7, GroupName = "Visualización")]
        public bool ShowSignalArrows { get; set; }

        // ─── Grupo 3. Validación IA ────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Mostrar flechas Tipo 2 (CloudPullback)",
                 Description = "Dibuja flechas lima/magenta cuando el precio toca la nube sin cruzar",
                 Order = 1, GroupName = "3. Validación IA")]
        public bool ShowType2Arrows { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Activar validación IA",
                 Description = "Llama a Claude/OpenAI para clasificar cada señal Tipo 2 antes de mostrar la flecha",
                 Order = 2, GroupName = "3. Validación IA")]
        public bool EnableAIValidation { get; set; }

        [NinjaScriptProperty]
        [System.ComponentModel.TypeConverter(typeof(STCIAProviderConverter))]
        [Display(Name = "Proveedor de IA",
                 Description = "Claude (Anthropic) u OpenAI",
                 Order = 3, GroupName = "3. Validación IA")]
        public string AIProvider { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "API Key",
                 Description = "Clave de API del proveedor seleccionado",
                 Order = 4, GroupName = "3. Validación IA")]
        public string AIApiKey { get; set; }

        [NinjaScriptProperty]
        [Range(0.30, 0.99)]
        [Display(Name = "Confianza mínima IA",
                 Description = "Umbral mínimo de confianza para mostrar la señal como aprobada (0.60 = 60%)",
                 Order = 5, GroupName = "3. Validación IA")]
        public double AIMinConfidence { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Activar alertas de sonido",
                 Description = "Emite un beep de NT8 al detectar señales Tipo 1 y Tipo 2",
                 Order = 6, GroupName = "3. Validación IA")]
        public bool EnableAlerts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Mostrar dashboard en chart",
                 Description = "Panel en esquina superior derecha con dirección de nube, última señal y decisión de IA",
                 Order = 7, GroupName = "3. Validación IA")]
        public bool ShowDashboard { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filtro confluencia M15",
                 Description = "Solo muestra señales Tipo 2 si la EMA de 20 períodos en M15 está alineada con la dirección",
                 Order = 8, GroupName = "3. Validación IA")]
        public bool UseM15Confluence { get; set; }
    }
}
