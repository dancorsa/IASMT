// ============================================================
// STATradeJournal.cs — Logging CSV de trades
// Proyecto: SmoothTrendAI  |  Prefijo: STA
// Ruta: Documents\NinjaTrader 8\logs\SmoothTrendAI_{yyyyMMdd}.csv
// ============================================================
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace NinjaTrader.NinjaScript.Strategies
{
    // ─── DTO de registro de trade ─────────────────────────────────────────
    public class STATradeRecord
    {
        public DateTime TimestampEntry  { get; set; }
        public DateTime TimestampExit   { get; set; }
        public string   Instrument      { get; set; }
        public string   BarType         { get; set; }
        public string   Direction       { get; set; }
        public string   SetupType       { get; set; }
        public string   RejectionCandle { get; set; }

        public double EntryPrice        { get; set; }
        public double ExitPrice         { get; set; }
        public int    Contracts         { get; set; }
        public double PnlTicks          { get; set; }
        public double PnlUsd            { get; set; }

        public double TriggerLineAtEntry  { get; set; }
        public double SmoothLineAtEntry   { get; set; }
        public double CloudWidthAtEntry   { get; set; }
        public int    BarsInCurrentColor  { get; set; }

        public string DailyContext             { get; set; }
        public string AllowedDirection         { get; set; }
        public double PriorHigh               { get; set; }
        public double PriorClose              { get; set; }
        public double PriorLow                { get; set; }
        public int    ConsecutiveDirectionDays { get; set; }

        public string ElliottWavePosition      { get; set; }
        public double ElliottWaveConfidence    { get; set; }
        public double ElliottMultiplierApplied { get; set; }

        public double RsiM15      { get; set; }
        public double VolumeRatio { get; set; }

        public double AiConfidence     { get; set; }
        public double AiRiskAdjustment { get; set; }
        public string AiSetupQuality   { get; set; }
        public string AiReason         { get; set; }

        public double StopTicks    { get; set; }
        public bool   Tp1Hit       { get; set; }
        public bool   Tp2Hit       { get; set; }
        public string ExitReason   { get; set; }
        public int    ConsecutiveLossesAtEntry { get; set; }
    }

    // ─── DTO de señal rechazada ───────────────────────────────────────────
    public class STARejectedSignalRecord
    {
        public DateTime Timestamp      { get; set; }
        public string   Instrument     { get; set; }
        public string   Direction      { get; set; }
        public string   SetupType      { get; set; }
        public string   RejectReason   { get; set; }  // "AI" | "RSI_Extremo" | "VWAP" | "TimeFilter"
        public double   BaseConfidence { get; set; }
        public double   AiConfidence   { get; set; }
        public string   AiReason       { get; set; }
        public string   ElliottWave    { get; set; }
        public string   DailyContext   { get; set; }
        public double   RiskReward     { get; set; }
    }

    /// <summary>
    /// Escribe un archivo CSV por día con todos los campos del trade.
    /// Estructura preparada para análisis posterior de performance
    /// por tipo de setup (TrendStart vs CloudPullback) y con/sin Elliott.
    /// También registra señales rechazadas en un CSV separado.
    /// </summary>
    public class STATradeJournal : IDisposable
    {
        private StreamWriter _writer;
        private StreamWriter _rejectedWriter;
        private bool         _disposed;

        private const string CSV_HEADER =
            "timestamp_entry,timestamp_exit,instrument,bar_type,direction," +
            "setup_type,rejection_candle," +
            "entry_price,exit_price,contracts,pnl_ticks,pnl_usd," +
            "trigger_line_at_entry,smooth_line_at_entry,cloud_width_at_entry," +
            "bars_in_current_color," +
            "daily_context,allowed_direction,prior_high,prior_close,prior_low," +
            "consecutive_direction_days," +
            "elliott_wave_position,elliott_wave_confidence,elliott_multiplier_applied," +
            "rsi_m15,volume_ratio," +
            "ai_confidence,ai_risk_adjustment,ai_setup_quality,ai_reason," +
            "stop_ticks,tp1_hit,tp2_hit,exit_reason," +
            "consecutive_losses_at_entry";

        public void Open(string instrument, string accountName = null)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8", "logs");

                Directory.CreateDirectory(logDir);
                string fecha  = DateTime.Now.ToString("yyyyMMdd");
                // Sufijo de cuenta evita que dos cuentas corriendo la misma estrategia
                // compitan por el mismo archivo (lock exclusivo) y mezclen/dupliquen trades.
                string suffix   = string.IsNullOrEmpty(accountName) ? "" : "_" + SanitizeForFileName(accountName);
                string filePath = Path.Combine(logDir, $"SmoothTrendAI_{fecha}{suffix}.csv");

                bool esNuevo = !File.Exists(filePath);
                _writer = new StreamWriter(filePath, append: true, encoding: Encoding.UTF8);

                if (esNuevo)
                {
                    _writer.WriteLine(CSV_HEADER);
                    _writer.Flush();
                }
            }
            catch (Exception ex)
            {
                NinjaTrader.Code.Output.Process(
                    $"[STATradeJournal] Error abriendo log: {ex.Message}",
                    NinjaTrader.NinjaScript.PrintTo.OutputTab1);
            }
        }

        public void OpenRejectedLog()
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8", "logs");

                Directory.CreateDirectory(logDir);
                string fecha    = DateTime.Now.ToString("yyyyMMdd");
                string filePath = Path.Combine(logDir, $"SmoothTrendAI_rejected_{fecha}.csv");

                bool esNuevo = !File.Exists(filePath);
                _rejectedWriter = new StreamWriter(filePath, append: true, encoding: Encoding.UTF8);
                if (esNuevo)
                {
                    _rejectedWriter.WriteLine(
                        "timestamp,instrument,direction,setup_type,reject_reason," +
                        "base_confidence,ai_confidence,ai_reason," +
                        "elliott_wave,daily_context,risk_reward");
                    _rejectedWriter.Flush();
                }
            }
            catch (Exception ex)
            {
                NinjaTrader.Code.Output.Process(
                    $"[STATradeJournal] Error abriendo log rechazados: {ex.Message}",
                    NinjaTrader.NinjaScript.PrintTo.OutputTab1);
            }
        }

        public void LogRejected(STARejectedSignalRecord r)
        {
            if (_rejectedWriter == null || r == null) return;
            try
            {
                _rejectedWriter.WriteLine(string.Join(",",
                    r.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss", Inv),
                    Esc(r.Instrument ?? ""),
                    r.Direction,
                    r.SetupType,
                    r.RejectReason,
                    r.BaseConfidence.ToString("F2", Inv),
                    r.AiConfidence.ToString("F2", Inv),
                    Esc(r.AiReason ?? ""),
                    r.ElliottWave,
                    r.DailyContext,
                    r.RiskReward.ToString("F2", Inv)
                ));
                _rejectedWriter.Flush();
            }
            catch (Exception ex)
            {
                NinjaTrader.Code.Output.Process(
                    $"[STATradeJournal] Error escribiendo rechazo: {ex.Message}",
                    NinjaTrader.NinjaScript.PrintTo.OutputTab1);
            }
        }

        /// <summary>Registrar un trade completo (entrada + salida en una sola línea).</summary>
        public void LogComplete(STATradeRecord r)
        {
            if (_writer == null || r == null) return;
            try
            {
                _writer.WriteLine(BuildLine(r));
                _writer.Flush();
            }
            catch (Exception ex)
            {
                NinjaTrader.Code.Output.Process(
                    $"[STATradeJournal] Error escribiendo trade: {ex.Message}",
                    NinjaTrader.NinjaScript.PrintTo.OutputTab1);
            }
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private string BuildLine(STATradeRecord r)
        {
            // CultureInfo.InvariantCulture es obligatorio en todo campo numérico:
            // si el proceso corre con una configuración regional de coma decimal,
            // ToString("F2") produce p.ej. "29870,25", y esa coma extra desalinea
            // las columnas del CSV (el separador de campos también es coma).
            return string.Join(",",
                r.TimestampEntry.ToString("yyyy-MM-ddTHH:mm:ss", Inv),
                r.TimestampExit.ToString("yyyy-MM-ddTHH:mm:ss", Inv),
                Esc(r.Instrument),
                Esc(r.BarType),
                r.Direction,
                r.SetupType,
                Esc(r.RejectionCandle ?? ""),
                r.EntryPrice.ToString("F2", Inv),
                r.ExitPrice.ToString("F2", Inv),
                r.Contracts.ToString(Inv),
                r.PnlTicks.ToString("F1", Inv),
                r.PnlUsd.ToString("F2", Inv),
                r.TriggerLineAtEntry.ToString("F4", Inv),
                r.SmoothLineAtEntry.ToString("F4", Inv),
                r.CloudWidthAtEntry.ToString("F4", Inv),
                r.BarsInCurrentColor.ToString(Inv),
                r.DailyContext,
                r.AllowedDirection,
                r.PriorHigh.ToString("F2", Inv),
                r.PriorClose.ToString("F2", Inv),
                r.PriorLow.ToString("F2", Inv),
                r.ConsecutiveDirectionDays.ToString(Inv),
                r.ElliottWavePosition,
                r.ElliottWaveConfidence.ToString("F2", Inv),
                r.ElliottMultiplierApplied.ToString("F2", Inv),
                r.RsiM15.ToString("F1", Inv),
                r.VolumeRatio.ToString("F2", Inv),
                r.AiConfidence.ToString("F2", Inv),
                r.AiRiskAdjustment.ToString("F2", Inv),
                Esc(r.AiSetupQuality ?? ""),
                Esc(r.AiReason ?? ""),
                r.StopTicks.ToString("F1", Inv),
                r.Tp1Hit ? "1" : "0",
                r.Tp2Hit ? "1" : "0",
                Esc(r.ExitReason ?? ""),
                r.ConsecutiveLossesAtEntry.ToString(Inv)
            );
        }

        private static string SanitizeForFileName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s;
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _writer?.Flush();
                _writer?.Close();
                _writer?.Dispose();
                _rejectedWriter?.Flush();
                _rejectedWriter?.Close();
                _rejectedWriter?.Dispose();
                _disposed = true;
            }
        }
    }
}
