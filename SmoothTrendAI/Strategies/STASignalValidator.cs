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
using Newtonsoft.Json.Linq;      // para parseo de respuestas (disponible en NT8)

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
        private string SerializarPayload(STASignalPayload p) => new JObject
        {
            ["strategy_type"]               = "smooth_trend_cloud_v2",
            ["instrument"]                  = p.Instrument,
            ["bar_type"]                    = p.BarType,
            ["timestamp"]                   = p.Timestamp,
            ["signal_direction"]            = p.SignalDirection,
            ["setup_type"]                  = p.SetupType,
            ["trigger_line"]                = p.TriggerLine,
            ["smooth_trend_line"]           = p.SmoothTrendLine,
            ["cloud_width_ticks"]           = p.CloudWidthTicks,
            ["bars_in_current_color"]       = p.BarsInCurrentColor,
            ["rejection_candle"]            = p.RejectionCandle,
            ["daily_context"]               = p.DailyContext,
            ["allowed_direction"]           = p.AllowedDirection,
            ["prior_high"]                  = p.PriorHigh,
            ["prior_close"]                 = p.PriorClose,
            ["prior_low"]                   = p.PriorLow,
            ["consecutive_direction_days"]  = p.ConsecutiveDirectionDays,
            ["has_strong_directional_bias"] = p.HasStrongDirectionalBias,
            ["elliott_wave_position"]       = p.ElliottWavePosition,
            ["elliott_wave_confidence"]     = p.ElliottWaveConfidence,
            ["elliott_favorable"]           = p.ElliottFavorable,
            ["nearest_fib_support"]         = p.NearestFibSupport,
            ["nearest_fib_resistance"]      = p.NearestFibResistance,
            ["base_confidence_pre_ai"]      = p.BaseConfidencePreAI,
            ["rsi_m15"]                     = p.RsiM15,
            ["volume_ratio"]                = p.VolumeRatio,
            ["current_price"]               = p.CurrentPrice,
            ["proposed_entry"]              = p.ProposedEntry,
            ["proposed_stop"]               = p.ProposedStop,
            ["proposed_target_1"]           = p.ProposedTarget1,
            ["proposed_target_2"]           = p.ProposedTarget2,
            ["risk_reward_ratio"]           = p.RiskRewardRatio
        }.ToString(Newtonsoft.Json.Formatting.None);

        private string BuildClaudeBody(string userContent) => new JObject
        {
            ["model"]      = "claude-sonnet-4-6",
            ["max_tokens"] = 256,
            ["system"]     = SYSTEM_PROMPT,
            ["messages"]   = new JArray(new JObject
            {
                ["role"] = "user",
                ["content"] = userContent
            })
        }.ToString(Newtonsoft.Json.Formatting.None);

        private string BuildOpenAIBody(string userContent) => new JObject
        {
            ["model"] = "gpt-4o-mini",
            ["messages"] = new JArray(
                new JObject
                {
                    ["role"] = "system",
                    ["content"] = SYSTEM_PROMPT
                },
                new JObject
                {
                    ["role"] = "user",
                    ["content"] = userContent
                }),
            ["max_tokens"] = 256,
            ["temperature"] = 0.1
        }.ToString(Newtonsoft.Json.Formatting.None);

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

        // ─── Parseo de respuesta (usa Newtonsoft.Json — disponible en NT8) ──
        private STAAIValidationResult ParsearRespuesta(string raw, STASignalPayload p)
        {
            try
            {
                // Extraer el texto de contenido según el proveedor
                var jOuter = JObject.Parse(raw);
                string texto;

                if (Provider == STAAIProvider.Claude)
                    texto = jOuter["content"][0]["text"].ToString();
                else
                    texto = jOuter["choices"][0]["message"]["content"].ToString();

                var jResp = JObject.Parse(ExtraerJson(texto));

                return new STAAIValidationResult
                {
                    Approve        = jResp["approve"].Value<bool>(),
                    Confidence     = jResp["confidence"].Value<double>(),
                    Reason         = jResp["reason"]?.Value<string>() ?? "",
                    RiskAdjustment = jResp["risk_adjustment"].Value<double>(),
                    SetupQuality   = jResp["setup_quality"]?.Value<string>() ?? "medium"
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
