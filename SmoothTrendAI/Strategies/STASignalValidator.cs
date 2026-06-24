// ============================================================
// STASignalValidator.cs — Validación de señales con IA (Claude / GPT-4o)
// Proyecto: SmoothTrendAI  |  Prefijo: STA
// HttpClient instanciado una sola vez. Timeout 3 s.
// En modo histórico devuelve simulación local para no generar
// miles de llamadas HTTP durante el backtest.
// ============================================================
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum STAAIProvider { Claude, OpenAI }

    // ─── Resultado de la validación ───────────────────────────────────────
    public class STAAIValidationResult
    {
        public bool   Approve        { get; set; }
        public double Confidence     { get; set; }
        public string Reason         { get; set; } = "";
        public double RiskAdjustment { get; set; } = 1.0;
        public string SetupQuality   { get; set; } = "medium";
        public bool   IsTimeout      { get; set; }
    }

    // ─── Payload de señal ─────────────────────────────────────────────────
    public class STASignalPayload
    {
        public string Instrument        { get; set; }
        public string BarType           { get; set; }
        public string Timestamp         { get; set; }
        public string SignalDirection   { get; set; }
        public string SetupType         { get; set; }

        public double TriggerLine       { get; set; }
        public double SmoothTrendLine   { get; set; }
        public double CloudWidthTicks   { get; set; }
        public int    BarsInCurrentColor{ get; set; }
        public string RejectionCandle   { get; set; }

        public string DailyContext             { get; set; }
        public string AllowedDirection         { get; set; }
        public double PriorHigh               { get; set; }
        public double PriorClose              { get; set; }
        public double PriorLow                { get; set; }
        public int    ConsecutiveDirectionDays { get; set; }
        public bool   HasStrongDirectionalBias { get; set; }

        public string ElliottWavePosition   { get; set; }
        public double ElliottWaveConfidence { get; set; }
        public bool   ElliottFavorable      { get; set; }
        public double NearestFibSupport     { get; set; }
        public double NearestFibResistance  { get; set; }

        public double BaseConfidencePreAI   { get; set; }
        public double RsiM15                { get; set; }
        public double VolumeRatio           { get; set; }
        public double CurrentPrice          { get; set; }

        public double ProposedEntry         { get; set; }
        public double ProposedStop          { get; set; }
        public double ProposedTarget1       { get; set; }
        public double ProposedTarget2       { get; set; }
        public double RiskRewardRatio       { get; set; }
    }

    /// <summary>
    /// Envía el payload de señal a Claude o GPT-4o y devuelve la validación.
    /// </summary>
    public class STASignalValidator : IDisposable
    {
        private HttpClient _http;
        private bool       _disposed;

        public STAAIProvider Provider      { get; set; } = STAAIProvider.Claude;
        public string        ApiKey        { get; set; } = "";
        public double        MinConfidence { get; set; } = 0.62;
        public bool          EnableAI      { get; set; } = true;

        private const string SYSTEM_PROMPT =
            "Eres un analista cuantitativo especializado en estrategias de cruce y " +
            "retroceso de medias móviles (nube de doble EMA), con contexto Kaufman y " +
            "ondas de Elliott, operando futuros del CME. " +
            "Responde ÚNICAMENTE con JSON válido sin texto adicional:\n" +
            "{\"approve\":true/false,\"confidence\":0.0-1.0," +
            "\"reason\":\"string max 120 chars\"," +
            "\"risk_adjustment\":0.5-1.0,\"setup_quality\":\"high/medium/low\"}\n\n" +
            "CRITERIOS POR setup_type:\n" +
            "TrendStart (cruce, mayor riesgo de fakeout):\n" +
            "  - allowed_direction == signal_direction\n" +
            "  - volume_ratio >= 1.15\n" +
            "  - RSI no extremo opuesto (no >70 para LONG, no <30 para SHORT)\n" +
            "  - risk_reward_ratio >= 1.6\n" +
            "  - SÉ MÁS EXIGENTE: confirmar todos los factores.\n\n" +
            "CloudPullback (retroceso + rechazo, mejor R:R):\n" +
            "  - allowed_direction == signal_direction\n" +
            "  - rejection_candle presente\n" +
            "  - bars_in_current_color >= 8\n" +
            "  - risk_reward_ratio >= 1.4\n" +
            "  - SÉ MENOS EXIGENTE en volumen, MÁS en tendencia previa clara.\n\n" +
            "REFUERZO ELLIOTT:\n" +
            "  - Wave_3_Start con confidence >= 0.65: +0.10 a +0.15 en confidence\n" +
            "  - Correction_AB: penalizar -0.20\n" +
            "  - Undefined o confidence < 0.40: ignorar (neutro)\n\n" +
            "risk_adjustment: high=0.90-1.0 | medium=0.65-0.85 | low=0.50-0.60\n\n" +
            "RECHAZO GENERAL:\n" +
            "  - daily_context=Neutral AND has_strong_directional_bias=false\n" +
            "  - risk_reward_ratio < 1.3\n" +
            "  - elliott_wave_position=Correction_AB + otros factores débiles";

        public void Initialize()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }

        /// <summary>
        /// Validar señal. Pasa isHistorical=true durante backtest para evitar
        /// llamadas HTTP masivas — usa heurística local en su lugar.
        /// </summary>
        public async Task<STAAIValidationResult> ValidateAsync(STASignalPayload payload,
                                                                bool isHistorical = false)
        {
            if (isHistorical || !EnableAI)
                return SimularRespuesta(payload);

            if (string.IsNullOrEmpty(ApiKey))
                return Fallback("API Key no configurada", payload.BaseConfidencePreAI * 0.7);

            try
            {
                string jsonPayload  = SerializarPayload(payload);
                string requestBody  = Provider == STAAIProvider.Claude
                                      ? BuildClaudeBody(jsonPayload)
                                      : BuildOpenAIBody(jsonPayload);

                var req = BuildRequest(requestBody);
                var res = await _http.SendAsync(req).ConfigureAwait(false);
                res.EnsureSuccessStatusCode();
                string raw = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParsearRespuesta(raw, payload);
            }
            catch (TaskCanceledException)
            {
                // Timeout: fallback conservador con 50% del tamaño
                return new STAAIValidationResult
                {
                    Approve        = payload.BaseConfidencePreAI >= MinConfidence,
                    Confidence     = payload.BaseConfidencePreAI * 0.80,
                    Reason         = "Timeout IA — confianza base reducida",
                    RiskAdjustment = 0.50,
                    SetupQuality   = "medium",
                    IsTimeout      = true
                };
            }
            catch (Exception ex)
            {
                NinjaTrader.Code.Output.Process(
                    $"[STASignalValidator] Error: {ex.Message}",
                    NinjaTrader.NinjaScript.PrintTo.OutputTab1);
                return Fallback($"Error: {ex.Message.Substring(0, Math.Min(60, ex.Message.Length))}", 0);
            }
        }

        // ─── Serialización ─────────────────────────────────────────────────
        private string SerializarPayload(STASignalPayload p)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"strategy_type\":\"smooth_trend_cloud_v2\",");
            sb.Append("\"instrument\":"               + JES(p.Instrument)          + ",");
            sb.Append("\"bar_type\":"                 + JES(p.BarType)             + ",");
            sb.Append("\"timestamp\":"                + JES(p.Timestamp)           + ",");
            sb.Append("\"signal_direction\":"         + JES(p.SignalDirection)     + ",");
            sb.Append("\"setup_type\":"               + JES(p.SetupType)           + ",");
            sb.Append("\"trigger_line\":"             + N(p.TriggerLine)           + ",");
            sb.Append("\"smooth_trend_line\":"        + N(p.SmoothTrendLine)       + ",");
            sb.Append("\"cloud_width_ticks\":"        + N(p.CloudWidthTicks)       + ",");
            sb.Append("\"bars_in_current_color\":"    + p.BarsInCurrentColor       + ",");
            sb.Append("\"rejection_candle\":"         + JES(p.RejectionCandle)     + ",");
            sb.Append("\"daily_context\":"            + JES(p.DailyContext)        + ",");
            sb.Append("\"allowed_direction\":"        + JES(p.AllowedDirection)    + ",");
            sb.Append("\"prior_high\":"               + N(p.PriorHigh)             + ",");
            sb.Append("\"prior_close\":"              + N(p.PriorClose)            + ",");
            sb.Append("\"prior_low\":"                + N(p.PriorLow)              + ",");
            sb.Append("\"consecutive_direction_days\":" + p.ConsecutiveDirectionDays + ",");
            sb.Append("\"has_strong_directional_bias\":" + B(p.HasStrongDirectionalBias) + ",");
            sb.Append("\"elliott_wave_position\":"    + JES(p.ElliottWavePosition) + ",");
            sb.Append("\"elliott_wave_confidence\":"  + N(p.ElliottWaveConfidence) + ",");
            sb.Append("\"elliott_favorable\":"        + B(p.ElliottFavorable)      + ",");
            sb.Append("\"nearest_fib_support\":"      + N(p.NearestFibSupport)     + ",");
            sb.Append("\"nearest_fib_resistance\":"   + N(p.NearestFibResistance)  + ",");
            sb.Append("\"base_confidence_pre_ai\":"   + N(p.BaseConfidencePreAI)   + ",");
            sb.Append("\"rsi_m15\":"                  + N(p.RsiM15)                + ",");
            sb.Append("\"volume_ratio\":"             + N(p.VolumeRatio)           + ",");
            sb.Append("\"current_price\":"            + N(p.CurrentPrice)          + ",");
            sb.Append("\"proposed_entry\":"           + N(p.ProposedEntry)         + ",");
            sb.Append("\"proposed_stop\":"            + N(p.ProposedStop)          + ",");
            sb.Append("\"proposed_target_1\":"        + N(p.ProposedTarget1)       + ",");
            sb.Append("\"proposed_target_2\":"        + N(p.ProposedTarget2)       + ",");
            sb.Append("\"risk_reward_ratio\":"        + N(p.RiskRewardRatio));
            sb.Append('}');
            return sb.ToString();
        }

        private string BuildClaudeBody(string userContent) =>
            "{\"model\":\"claude-sonnet-4-6\",\"max_tokens\":256," +
            "\"system\":" + JES(SYSTEM_PROMPT) + "," +
            "\"messages\":[{\"role\":\"user\",\"content\":" + JES(userContent) + "}]}";

        private string BuildOpenAIBody(string userContent) =>
            "{\"model\":\"gpt-4o-mini\",\"max_tokens\":256,\"temperature\":0.1," +
            "\"messages\":[" +
            "{\"role\":\"system\",\"content\":" + JES(SYSTEM_PROMPT) + "}," +
            "{\"role\":\"user\",\"content\":" + JES(userContent) + "}]}";

        private HttpRequestMessage BuildRequest(string body)
        {
            var req = new HttpRequestMessage(HttpMethod.Post,
                Provider == STAAIProvider.Claude
                    ? "https://api.anthropic.com/v1/messages"
                    : "https://api.openai.com/v1/chat/completions");

            if (Provider == STAAIProvider.Claude)
            {
                req.Headers.Add("x-api-key", ApiKey);
                req.Headers.Add("anthropic-version", "2023-06-01");
            }
            else
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            }

            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return req;
        }

        // ─── Parseo de respuesta ─────────────────────────────────────────────
        private STAAIValidationResult ParsearRespuesta(string raw, STASignalPayload p)
        {
            try
            {
                string texto = Provider == STAAIProvider.Claude
                    ? JGetStr(raw, "text")
                    : JGetStr(raw, "content");

                string respJson = ExtraerJson(texto);
                string sq       = JGetStr(respJson, "setup_quality");

                return new STAAIValidationResult
                {
                    Approve        = JGetBool(respJson, "approve"),
                    Confidence     = JGetNum(respJson,  "confidence"),
                    Reason         = JGetStr(respJson,  "reason"),
                    RiskAdjustment = JGetNum(respJson,  "risk_adjustment"),
                    SetupQuality   = sq.Length > 0 ? sq : "medium"
                };
            }
            catch (Exception ex)
            {
                NinjaTrader.Code.Output.Process(
                    $"[STASignalValidator] Error parseando: {ex.Message}",
                    NinjaTrader.NinjaScript.PrintTo.OutputTab1);

                return new STAAIValidationResult
                {
                    Approve        = p.BaseConfidencePreAI >= MinConfidence,
                    Confidence     = p.BaseConfidencePreAI * 0.85,
                    Reason         = "Error parse respuesta IA — confianza base",
                    RiskAdjustment = 0.65,
                    SetupQuality   = "medium"
                };
            }
        }

        private static string ExtraerJson(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return "{}";
            int start = texto.IndexOf('{');
            int end   = texto.LastIndexOf('}');
            return start >= 0 && end > start
                ? texto.Substring(start, end - start + 1)
                : texto.Trim();
        }

        // ─── Simulación local para backtest ───────────────────────────────
        private STAAIValidationResult SimularRespuesta(STASignalPayload p)
        {
            double conf = p.BaseConfidencePreAI;

            bool dirOk = p.AllowedDirection == p.SignalDirection || p.AllowedDirection == "BOTH";
            bool rrOk  = p.SetupType == "TrendStart" ? p.RiskRewardRatio >= 1.6
                                                      : p.RiskRewardRatio >= 1.4;
            bool volOk = p.SetupType == "TrendStart" ? p.VolumeRatio >= 1.15
                                                      : p.VolumeRatio >= 1.0;

            if (p.ElliottWavePosition == "Wave_3_Start" && p.ElliottWaveConfidence >= 0.65)
                conf += 0.12;
            if (p.ElliottWavePosition == "Correction_AB")
                conf -= 0.20;

            conf = Math.Max(0.0, Math.Min(1.0, conf));

            bool approve = dirOk && rrOk && volOk && conf >= MinConfidence
                           && p.DailyContext != "Neutral";

            double riskAdj = approve
                ? (conf >= 0.80 ? 0.95 : conf >= 0.65 ? 0.75 : 0.55)
                : 0.50;

            return new STAAIValidationResult
            {
                Approve        = approve,
                Confidence     = conf,
                Reason         = approve ? "Simulación backtest aprobado" : "Simulación backtest rechazado",
                RiskAdjustment = riskAdj,
                SetupQuality   = conf >= 0.80 ? "high" : conf >= 0.65 ? "medium" : "low"
            };
        }

        private STAAIValidationResult Fallback(string razon, double conf) =>
            new STAAIValidationResult
            {
                Approve        = false,
                Confidence     = conf,
                Reason         = razon,
                RiskAdjustment = 0.50,
                SetupQuality   = "low"
            };

        // ─── Helpers JSON sin dependencias externas ──────────────────────────
        private static string JES(string s)
        {
            if (s == null) return "null";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                            .Replace("\n", "\\n").Replace("\r", "").Replace("\t", "\\t") + "\"";
        }

        private static string N(double d) =>
            d.ToString("G", CultureInfo.InvariantCulture);

        private static string B(bool b) => b ? "true" : "false";

        private static string JGetStr(string json, string key)
        {
            string marker = "\"" + key + "\":\"";
            int i = json.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return "";
            i += marker.Length;
            var sb = new StringBuilder();
            while (i < json.Length && json[i] != '"')
            {
                if (json[i] == '\\' && i + 1 < json.Length)
                {
                    char next = json[++i];
                    sb.Append(next == 'n' ? '\n' : next == 'r' ? '\r' : next == 't' ? '\t' : next);
                }
                else sb.Append(json[i]);
                i++;
            }
            return sb.ToString();
        }

        private static double JGetNum(string json, string key)
        {
            string marker = "\"" + key + "\":";
            int i = json.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += marker.Length;
            while (i < json.Length && json[i] == ' ') i++;
            int s = i;
            while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '.' ||
                   json[i] == '-' || json[i] == 'e' || json[i] == 'E' || json[i] == '+')) i++;
            double v;
            return double.TryParse(json.Substring(s, i - s),
                NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static bool JGetBool(string json, string key)
        {
            string marker = "\"" + key + "\":";
            int i = json.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return false;
            i += marker.Length;
            while (i < json.Length && json[i] == ' ') i++;
            return i + 4 <= json.Length && string.CompareOrdinal(json, i, "true", 0, 4) == 0;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _http?.Dispose();
                _disposed = true;
            }
        }
    }
}
