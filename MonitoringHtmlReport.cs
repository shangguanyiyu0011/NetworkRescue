using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NetworkRescue
{
    public sealed class HtmlReportDocument
    {
        public string Html, PreviewText;
    }

    /// <summary>Offline report. A detached, typed allowlist projection is the only input to rendering.</summary>
    public static class MonitoringHtmlReport
    {
        public const int MaximumReportBytes = 8 * 1024 * 1024;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly string[] States = { "Running", "Completed", "Stopped", "Failed", "Interrupted" };
        private static readonly string[] Kinds = { "MonitoringStarted", "MonitoringCompleted", "MonitoringStopped", "ObservationUnavailable", "SettingChanged", "TargetRecovered", "TargetNoResponse" };
        private static readonly string[] Settings = { "ProxyEnabled", "ProxyServer", "ProxyPac", "AdapterStatus", "Addresses", "Dns", "Gateways" };
        private static readonly string[] WifiStates = { "Available", "Partial", "NotConnected", "Unavailable", "NoWirelessInterface" };
        private static string Allowed(string value, string[] allowed, string fallback) { return Array.IndexOf(allowed, value) >= 0 ? value : fallback; }
        private static string Escape(string value)
        {
            StringBuilder result = new StringBuilder();
            foreach (char c in value ?? "")
            {
                if (c == '&') result.Append("&amp;"); else if (c == '<') result.Append("&lt;"); else if (c == '>') result.Append("&gt;");
                else if (c == '"') result.Append("&quot;"); else if (c == '\'') result.Append("&#39;");
                else if (c == '\t' || c == '\r' || c == '\n' || c >= 32 && !Char.IsSurrogate(c) && c != '\ufffe' && c != '\uffff') result.Append(c);
            }
            return result.ToString();
        }
        private static void Require(bool condition) { if (!condition) throw new InvalidDataException("InvalidMonitoringReport"); }
        private static bool TimeValid(DateTime value) { return value.Kind == DateTimeKind.Utc && value.Year >= 2000; }

        // No raw host, interface identifier, exception text, configuration value or history ID leaves this method.
        private static MonitoringHistoryRecord Redact(MonitoringHistoryRecord input)
        {
            Require(input != null && input.Targets != null && input.Targets.Length >= 1 && input.Targets.Length <= 4 && input.Samples != null && input.Samples.Length <= 14400);
            Require(TimeValid(input.StartedUtc) && TimeValid(input.EndedUtc) && input.EndedUtc >= input.StartedUtc && input.IntervalMs >= 1000 && input.IntervalMs <= 10000 && input.RequestedCount >= 1 && input.RequestedCount <= 3600);
            Require(input.Events != null && input.Events.Length <= 500 && input.TotalEvents >= input.Events.Length && input.WifiSamples != null && input.WifiSamples.Length <= 720 && input.WifiTotalSamples >= input.WifiSamples.Length);
            MonitoringHistoryRecord safe = new MonitoringHistoryRecord { State = Allowed(input.State, States, "Failed"), StartedUtc = input.StartedUtc, EndedUtc = input.EndedUtc,
                IntervalMs = input.IntervalMs, RequestedCount = input.RequestedCount, TotalEvents = input.TotalEvents, WifiCollectionEnabled = input.WifiCollectionEnabled, WifiTotalSamples = input.WifiTotalSamples };
            Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            safe.Targets = new MonitorTarget[input.Targets.Length];
            for (int i = 0; i < safe.Targets.Length; i++)
            {
                MonitorTarget target = input.Targets[i]; Require(target != null && target.Id != null && !aliases.ContainsKey(target.Id) && (target.Method == "icmp" || target.Method == "https"));
                string alias = "Target" + (i + 1).ToString(Invariant); aliases.Add(target.Id, alias);
                safe.Targets[i] = new MonitorTarget { Id = alias, Host = alias, Method = target.Method == "https" ? "https" : "icmp", UseSystemProxy = target.UseSystemProxy };
            }
            List<MonitorSample> samples = new List<MonitorSample>();
            foreach (MonitorSample sample in input.Samples)
            {
                string alias; Require(sample != null && sample.TargetId != null && aliases.TryGetValue(sample.TargetId, out alias)); alias = aliases[sample.TargetId];
                Require(sample.Sequence >= 1 && sample.Sequence <= input.RequestedCount && TimeValid(sample.TimeUtc) && sample.TimeUtc >= safe.StartedUtc && sample.TimeUtc <= safe.EndedUtc);
                Require(!sample.RttMs.HasValue || sample.RttMs.Value >= 0 && sample.RttMs.Value <= 86400000);
                bool https = safe.Targets.First(delegate(MonitorTarget t) { return t.Id == alias; }).Method == "https";
                // Status is a typed observation category, never copied from an arbitrary token.
                bool measured = !MonitoringSummaryBuilder.IsUnmeasuredSample(sample);
                bool responded = measured && (https ? sample.ResponseReceived : sample.Success);
                Require(!responded || sample.RttMs.HasValue);
                samples.Add(new MonitorSample { Sequence = sample.Sequence, TimeUtc = sample.TimeUtc, TargetId = alias, TargetHost = alias,
                    Method = https ? "HTTPS" : "ICMP", Route = https ? safe.Targets.First(delegate(MonitorTarget t) { return t.Id == alias; }).UseSystemProxy ? "WindowsProxy" : "Direct" : "Network",
                    Status = measured ? responded ? "Success" : "TimedOut" : "NotMeasured", Success = responded, ResponseReceived = https && responded,
                    RttMs = responded ? sample.RttMs : null, HttpSuccess = https && responded && sample.HttpSuccess,
                    HttpStatusCode = https && responded && sample.HttpStatusCode.HasValue && sample.HttpStatusCode.Value >= 100 && sample.HttpStatusCode.Value <= 599 ? sample.HttpStatusCode : null });
            }
            safe.Samples = samples.OrderBy(delegate(MonitorSample s) { return s.TimeUtc; }).ThenBy(delegate(MonitorSample s) { return s.Sequence; }).ToArray();
            safe.Events = input.Events.Select(delegate(NetworkChangeEvent e)
            {
                Require(e != null && TimeValid(e.TimeUtc) && e.TimeUtc >= safe.StartedUtc && e.TimeUtc <= safe.EndedUtc);
                string alias; string target = e.TargetId != null && aliases.TryGetValue(e.TargetId, out alias) ? alias : null;
                return new NetworkChangeEvent { Sequence = e.Sequence, TimeUtc = e.TimeUtc, Kind = Allowed(e.Kind, Kinds, "ObservationUnavailable"),
                    Setting = Allowed(e.Setting, Settings, null), TargetId = target, TargetHost = target };
            }).ToArray();
            Dictionary<string, string> links = new Dictionary<string, string>(StringComparer.Ordinal);
            safe.WifiSamples = input.WifiSamples.Select(delegate(WifiStabilitySample w)
            {
                Require(w != null && TimeValid(w.TimeUtc) && w.TimeUtc >= safe.StartedUtc && w.TimeUtc <= safe.EndedUtc && w.Interfaces != null && w.Interfaces.Length <= 8);
                List<WifiLinkSample> projected = new List<WifiLinkSample>();
                foreach (WifiLinkSample link in w.Interfaces)
                {
                    Require(link != null && link.InterfaceId != null); string alias;
                    if (!links.TryGetValue(link.InterfaceId, out alias)) { alias = "Link" + (links.Count + 1).ToString(Invariant); links.Add(link.InterfaceId, alias); }
                    bool available = link.Readability == "Available" && link.SignalPercent.HasValue && link.SignalPercent.Value >= 0 && link.SignalPercent.Value <= 100;
                    projected.Add(new WifiLinkSample { InterfaceId = alias, Readability = available ? "Available" : link.Readability == "NotConnected" ? "NotConnected" : "Unavailable",
                        State = available ? 1U : link.State <= 7 ? link.State : 0U, SignalPercent = available ? link.SignalPercent : null,
                        RxKbps = available ? link.RxKbps : null, TxKbps = available ? link.TxKbps : null });
                }
                return new WifiStabilitySample { TimeUtc = w.TimeUtc, StateCode = Allowed(w.StateCode, WifiStates, "Unavailable"), Interfaces = projected.ToArray() };
            }).ToArray();
            return safe;
        }

        public static HtmlReportDocument Build(MonitoringHistoryRecord record, Func<string, string> text)
        {
            MonitoringHistoryRecord safe = Redact(record);
            MonitoringSessionSummary summary = MonitoringSummaryBuilder.Build(safe);
            ReportWriter writer = new ReportWriter(safe, summary, text);
            HtmlReportDocument result = writer.Build();
            if (Encoding.UTF8.GetByteCount(result.Html) > MaximumReportBytes) throw new InvalidDataException("MonitoringReportTooLarge");
            return result;
        }

        private sealed class ReportWriter
        {
            private readonly MonitoringHistoryRecord data;
            private readonly MonitoringSessionSummary summary;
            private readonly Func<string, string> text;
            private readonly StringBuilder html = new StringBuilder(), preview = new StringBuilder();
            internal ReportWriter(MonitoringHistoryRecord data, MonitoringSessionSummary summary, Func<string, string> text) { this.data = data; this.summary = summary; this.text = text; }
            private string T(string key, string fallback) { string value = text == null ? null : text(key); return String.IsNullOrEmpty(value) || value == key ? fallback : value; }
            private string H(string key, string fallback) { return Escape(T(key, fallback)); }
            private static string N(double number) { return number.ToString("0.##", Invariant); }
            private static string Utc(DateTime date) { return date.ToString("yyyy-MM-dd HH:mm:ss.fff", Invariant) + " UTC"; }
            private void P(string text) { preview.AppendLine(text); }
            private void Section(string number, string title) { html.Append("<section><header class='section-heading'><span>").Append(number).Append("</span><h2>").Append(Escape(title)).Append("</h2></header>"); }
            private string State(string value) { return T("monitorReportState" + value, value); }
            private string Kind(string value) { return T("monitorReportEvent" + value, value); }
            private string Setting(string value) { return T("monitorReportSetting" + value, value ?? ""); }
            private string Boundary(bool open) { return T(open ? "monitorReportOpenBoundary" : "monitorReportRecoveredBoundary", open ? "Open boundary; end not observed" : "Recovery response observed"); }
            internal HtmlReportDocument Build()
            {
                string title = T("monitorReportTitle", "Network observation report");
                html.Append("<!DOCTYPE html><html xmlns='http://www.w3.org/1999/xhtml'><head><meta charset='utf-8'/><meta name='viewport' content='width=device-width,initial-scale=1'/><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"/><title>").Append(Escape(title)).Append("</title><style>").Append(Css).Append("</style></head><body><main>");
                html.Append("<header class='masthead'><div class='edition'>NETWORKRESCUE / 1.3 <span>").Append(H("monitorReportEdition", "LOCAL OBSERVATION")).Append("</span></div><h1>").Append(Escape(title)).Append("</h1><p class='lede'>").Append(H("monitorReportSubtitle", "A measured record of one monitoring session. Missing observations remain visible.")).Append("</p><div class='session-meta'><div><b>").Append(H("monitorReportStart", "Start")).Append("</b><time>").Append(Utc(data.StartedUtc)).Append("</time></div><div><b>").Append(H("monitorReportEnd", "End")).Append("</b><time>").Append(Utc(data.EndedUtc)).Append("</time></div><div><b>").Append(H("monitorReportSessionState", "Session state")).Append("</b><span>").Append(Escape(State(data.State))).Append("</span></div></div></header>");
                P(title); P(Utc(data.StartedUtc) + " — " + Utc(data.EndedUtc)); P(T("monitorReportSessionState", "Session state") + ": " + State(data.State));
                string scope = T("monitorReportPrivacy", "Targets and interfaces use session aliases. Hosts, IP addresses, interface IDs, adapter names, network names, paths and configuration values are excluded. This file has no scripts, external assets or automatic upload.");
                html.Append("<aside class='privacy'><b>").Append(H("monitorReportSafeScope", "Sharing scope")).Append("</b><p>").Append(Escape(scope)).Append("</p></aside>"); P(scope);
                html.Append("<div class='metrics'><div><strong>").Append(data.Targets.Length).Append("</strong><span>").Append(H("monitorReportTargets", "Targets")).Append("</span></div><div><strong>").Append(data.Samples.Length).Append("</strong><span>").Append(H("monitorReportSamples", "Stored observations")).Append("</span></div><div><strong>").Append(summary.TotalIntervals).Append("</strong><span>").Append(H("monitorReportIntervals", "No-response intervals")).Append("</span></div><div><strong>").Append(summary.GapCount).Append("</strong><span>").Append(H("monitorReportGaps", "Sampling gaps")).Append("</span></div></div>");
                P(T("monitorReportTargets", "Targets") + ": " + data.Targets.Length + " · " + T("monitorReportSamples", "Stored observations") + ": " + data.Samples.Length + " · " + T("monitorReportIntervals", "No-response intervals") + ": " + summary.TotalIntervals + " · " + T("monitorReportGaps", "Sampling gaps") + ": " + summary.GapCount);
                string settings = T("monitorReportSampling", "Requested samples per target / interval") + ": " + data.RequestedCount + " / " + N(data.IntervalMs / 1000.0) + " s";
                html.Append("<p class='caption'>").Append(Escape(settings)).Append("</p>"); P(settings);
                Section("01", T("monitorReportTargetSummary", "Target observations"));
                html.Append("<p class='caption'>").Append(H("monitorReportEvidenceNote", "RTT is plotted only for measured responses. Red ticks indicate measured no-response observations; shaded regions indicate missing or unmeasured observations. Lines never bridge a sampling gap. Full stored samples are plotted without downsampling.")).Append("</p>");
                foreach (MonitoringTargetSummary target in summary.Targets) Target(target);
                CheckBudget();
                html.Append("</section>");
                Wifi(); Events();
                html.Append("<footer><b>NETWORKRESCUE 1.3</b><span>").Append(H("monitorReportFooter", "Offline report · all timestamps UTC · observations are not a diagnosis")).Append("</span></footer></main></body></html>");
                return new HtmlReportDocument { Html = html.ToString(), PreviewText = preview.ToString() };
            }
            private void CheckBudget() { if (html.Length > MaximumReportBytes) throw new InvalidDataException("MonitoringReportTooLarge"); }
            private void Target(MonitoringTargetSummary target)
            {
                MonitorTarget definition = data.Targets.First(delegate(MonitorTarget t) { return t.Id == target.TargetId; });
                MonitorSample[] samples = data.Samples.Where(delegate(MonitorSample s) { return s.TargetId == target.TargetId; }).OrderBy(delegate(MonitorSample s) { return s.Sequence; }).ToArray();
                string label = target.TargetId + " · " + definition.Method.ToUpperInvariant() + (definition.Method == "https" ? " / " + T(definition.UseSystemProxy ? "monitorReportSystemProxy" : "monitorReportDirect", definition.UseSystemProxy ? "Windows proxy" : "Direct") : "");
                html.Append("<article class='target'><div class='chart-block'><div class='target-heading'><h3>").Append(Escape(label)).Append("</h3><span>").Append(samples.Length).Append(" / ").Append(data.RequestedCount).Append(" ").Append(H("monitorReportSamples", "Stored observations")).Append("</span></div>");
                string stats = T("monitorReportMeasured", "Measured observations") + ": " + target.SampleCount + " · " + T("monitorReportResponses", "Responses") + ": " + target.ResponseCount + " · " + T("monitorReportIntervals", "No-response intervals") + ": " + target.Intervals.Length + " · " + T("monitorReportGaps", "Sampling gaps") + ": " + target.Gaps.Length;
                html.Append("<p class='caption'>").Append(Escape(stats)).Append("</p>"); P(label + ": " + samples.Length + "/" + data.RequestedCount + " " + T("monitorReportSamples", "Stored observations")); P(stats);
                html.Append(RttChart(samples, target.Gaps));
                html.Append("<p class='legend'><span class='legend-response'>").Append(H("monitorReportRttLegend", "Response RTT")).Append("</span><span class='legend-no-response'>").Append(H("monitorReportNoResponseLegend", "Measured no response")).Append("</span><span class='legend-missing'>").Append(H("monitorReportMissingLegend", "Missing / unmeasured")).Append("</span></p></div>");
                if (samples.Length == 0) { html.Append("<p class='empty'>").Append(H("monitorReportNoSamples", "No samples were recorded. No response or failure can be inferred.")).Append("</p>"); P(T("monitorReportNoSamples", "No samples were recorded. No response or failure can be inferred.")); }
                html.Append("<details open='open'><summary>").Append(H("monitorReportIntervalDetails", "Interval evidence and boundaries")).Append("</summary>");
                if (target.Intervals.Length == 0) html.Append("<p class='caption'>").Append(H("monitorReportNoIntervals", "No measured no-response interval was found in the available samples.")).Append("</p>");
                else
                {
                    html.Append("<div class='table-scroll'><table><thead><tr><th>").Append(H("monitorReportFirstLast", "First / last no response (UTC)")).Append("</th><th>").Append(H("monitorReportObservedDuration", "Observed span")).Append("</th><th>").Append(H("monitorReportBoundary", "Boundary / recovery (UTC)")).Append("</th><th>").Append(H("monitorReportNearby", "Nearby observations ±10 s")).Append("</th></tr></thead><tbody>");
                    foreach (MonitoringNoResponseInterval interval in target.Intervals)
                    {
                        string boundary = Boundary(interval.OpenEnded) + (interval.RecoveredUtc.HasValue ? " · " + Utc(interval.RecoveredUtc.Value) : "");
                        html.Append("<tr><td><time>").Append(Utc(interval.StartUtc)).Append("</time><br/><time>").Append(Utc(interval.LastNoResponseUtc)).Append("</time></td><td>").Append(N(interval.ObservedDurationMs / 1000.0)).Append(" s</td><td>").Append(Escape(boundary)).Append("</td><td>").Append(H("monitorReportConfiguration", "Configuration")).Append(": ").Append(interval.NearbyConfigurationCount).Append(" · Wi-Fi: ").Append(interval.NearbyWifiCount).Append("</td></tr>");
                        P(target.TargetId + " " + Utc(interval.StartUtc) + " / " + Utc(interval.LastNoResponseUtc) + " · " + N(interval.ObservedDurationMs / 1000.0) + " s · " + boundary);
                    }
                    html.Append("</tbody></table></div>");
                }
                foreach (MonitoringSamplingGap gap in target.Gaps)
                {
                    string line = T("monitorReportGap", "Missing observation boundary") + ": " + Utc(gap.StartUtc) + " — " + Utc(gap.EndUtc) + " · " + T("monitorReportGap" + Allowed(gap.Reason, new[] { "NotMeasured", "InitialSamplesMissing", "SequenceGap", "TimestampGap", "UnmeasuredSample", "TrailingSamplesMissing" }, "NotMeasured"), "Not measured");
                    html.Append("<p class='gap-note'>").Append(Escape(line)).Append("</p>"); P(target.TargetId + " " + line);
                }
                html.Append("<p class='caption'>").Append(H("monitorReportIntervalNote", "Observed span is the last minus first no-response timestamp, not an estimated outage duration. An open boundary may mean the session ended or sampling was interrupted. Nearby observations show timing only, not causation.")).Append("</p></details></article>");
            }
            private double X(DateTime time) { double span = (data.EndedUtc - data.StartedUtc).TotalMilliseconds; return 52 + (span <= 0 ? 0 : Math.Max(0, Math.Min(1, (time - data.StartedUtc).TotalMilliseconds / span))) * 900; }
            private static string Point(double x, double y) { return N(x) + "," + N(y); }
            private string SvgStart(string label, string scale)
            {
                return "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1000 230' role='img' aria-label='" + Escape(label) + "'><title>" + Escape(label) + "</title><rect class='plot-paper' x='52' y='22' width='900' height='158'/><path class='grid' d='M52 22H952 M52 101H952 M52 180H952'/><text x='6' y='27'>" + Escape(scale) + "</text><text x='24' y='184'>0</text><text x='52' y='218'>" + Escape(Utc(data.StartedUtc)) + "</text><text x='952' y='218' text-anchor='end'>" + Escape(Utc(data.EndedUtc)) + "</text>";
            }
            private string RttChart(MonitorSample[] samples, MonitoringSamplingGap[] gaps)
            {
                long maximum = Math.Max(1, samples.Where(delegate(MonitorSample s) { return s.Success && s.RttMs.HasValue; }).Select(delegate(MonitorSample s) { return s.RttMs.Value; }).DefaultIfEmpty(1).Max());
                StringBuilder chart = new StringBuilder(SvgStart(T("monitorReportRttChart", "RTT and no-response observations"), maximum + " ms"));
                foreach (MonitoringSamplingGap gap in gaps) chart.Append("<rect class='missing' x='").Append(N(X(gap.StartUtc))).Append("' y='22' width='").Append(N(Math.Max(2, X(gap.EndUtc) - X(gap.StartUtc)))).Append("' height='158'/>");
                MonitorSample previous = null;
                foreach (MonitorSample sample in samples)
                {
                    double x = X(sample.TimeUtc);
                    if (sample.Success && sample.RttMs.HasValue)
                    {
                        double y = 180 - 158.0 * sample.RttMs.Value / maximum;
                        if (previous != null && previous.Success && previous.RttMs.HasValue && sample.Sequence == previous.Sequence + 1 && (sample.TimeUtc - previous.TimeUtc).TotalMilliseconds <= data.IntervalMs * 2.5 && !gaps.Any(delegate(MonitoringSamplingGap g) { return g.StartUtc < sample.TimeUtc && g.EndUtc > previous.TimeUtc; }))
                            chart.Append("<path class='response-line' d='M").Append(Point(X(previous.TimeUtc), 180 - 158.0 * previous.RttMs.Value / maximum)).Append("L").Append(Point(x, y)).Append("'/>");
                        chart.Append("<circle class='response-dot' cx='").Append(N(x)).Append("' cy='").Append(N(y)).Append("' r='1.8'/>");
                    }
                    else if (sample.Status != "NotMeasured") chart.Append("<path class='no-response' d='M").Append(N(x)).Append(" 169v11'/>");
                    previous = sample;
                }
                return chart.Append("</svg>").ToString();
            }
            private void Wifi()
            {
                Section("02", T("monitorReportWifi", "Wi-Fi signal and link"));
                string note = T("monitorReportWifiNote", "Signal is the WLAN quality percentage. Receive/transmit link rates are negotiated link rates, not measured Internet throughput. Missing or unreadable samples are not disconnections.");
                html.Append("<p class='caption'>").Append(Escape(note)).Append("</p>"); P(note);
                if (!data.WifiCollectionEnabled) { string message = T("monitorReportWifiUnsupported", "Wi-Fi collection was disabled or this history predates Wi-Fi support; no Wi-Fi conclusion is available."); html.Append("<p class='empty'>").Append(Escape(message)).Append("</p></section>"); P(message); return; }
                P("Wi-Fi: " + data.WifiSamples.Length + "/" + data.WifiTotalSamples + " " + T("monitorReportSamples", "Stored observations"));
                html.Append("<p class='caption'>").Append(data.WifiSamples.Length).Append(" / ").Append(data.WifiTotalSamples).Append(" ").Append(H("monitorReportSamples", "Stored observations")).Append("</p>");
                if (summary.WifiTruncated) { string message = T("monitorReportWifiTruncated", "Only the latest stored Wi-Fi samples are available; earlier observations are missing."); html.Append("<p class='gap-note'>").Append(Escape(message)).Append("</p>"); P(message); }
                string[] links = data.WifiSamples.SelectMany(delegate(WifiStabilitySample w) { return w.Interfaces; }).Select(delegate(WifiLinkSample w) { return w.InterfaceId; }).Distinct().ToArray();
                if (links.Length == 0) { html.Append("<p class='empty'>").Append(H("monitorReportWifiNoSamples", "No readable Wi-Fi interface observations are available.")).Append("</p>"); P(T("monitorReportWifiNoSamples", "No readable Wi-Fi interface observations are available.")); }
                foreach (string link in links)
                {
                    html.Append("<article class='target'><div class='chart-block'><h3>").Append(Escape(link)).Append("</h3>");
                    int readable = data.WifiSamples.Count(delegate(WifiStabilitySample s) { return s.Interfaces.Any(delegate(WifiLinkSample l) { return l.InterfaceId == link && l.Readability == "Available"; }); });
                    int disconnected = data.WifiSamples.Count(delegate(WifiStabilitySample s) { return s.Interfaces.Any(delegate(WifiLinkSample l) { return l.InterfaceId == link && l.Readability == "NotConnected"; }); });
                    string coverage = T("monitorReportWifiReadable", "Readable") + ": " + readable + " · " + T("monitorReportWifiDisconnected", "Observed disconnected") + ": " + disconnected + " · " + T("monitorReportWifiUnknown", "Missing / unreadable") + ": " + (data.WifiSamples.Length - readable - disconnected);
                    html.Append("<p class='caption'>").Append(Escape(coverage)).Append("</p>"); P(link + " · " + coverage);
                    html.Append(WifiChart(link, 0)).Append("</div>").Append(WifiChart(link, 1));
                    html.Append("<p class='legend'><span class='legend-response'>").Append(H("monitorReportRxLegend", "Receive link rate")).Append("</span><span class='legend-transmit'>").Append(H("monitorReportTxLegend", "Transmit link rate (dashed)")).Append("</span><span class='legend-no-response'>").Append(H("monitorReportWifiDisconnected", "Observed disconnected")).Append("</span><span class='legend-missing'>").Append(H("monitorReportWifiUnknown", "Missing / unreadable")).Append("</span></p></article>");
                    CheckBudget();
                }
                html.Append("</section>");
            }
            private string WifiChart(string alias, int mode)
            {
                string label = mode == 0 ? T("monitorReportSignal", "Signal quality (%)") : T("monitorReportLinkRates", "Receive / transmit link rate (Mbps)");
                uint maximum = mode == 0 ? 100U : Math.Max(1U, data.WifiSamples.SelectMany(delegate(WifiStabilitySample w) { return w.Interfaces; }).Where(delegate(WifiLinkSample l) { return l.InterfaceId == alias; }).Select(delegate(WifiLinkSample l) { return Math.Max(l.RxKbps ?? 0, l.TxKbps ?? 0); }).DefaultIfEmpty(1U).Max());
                StringBuilder svg = new StringBuilder("<div class='chart-block'><p class='chart-label'>" + Escape(label) + "</p>" + SvgStart(alias + " · " + label, mode == 0 ? "100%" : N(maximum / 1000.0)));
                DateTime boundary = data.StartedUtc;
                foreach (WifiStabilitySample sample in data.WifiSamples)
                {
                    if ((sample.TimeUtc - boundary).TotalMilliseconds > 7500) svg.Append("<rect class='missing' x='").Append(N(X(boundary))).Append("' y='22' width='").Append(N(Math.Max(2, X(sample.TimeUtc) - X(boundary)))).Append("' height='158'/>");
                    boundary = sample.TimeUtc;
                }
                if ((data.EndedUtc - boundary).TotalMilliseconds > 7500) svg.Append("<rect class='missing' x='").Append(N(X(boundary))).Append("' y='22' width='").Append(N(Math.Max(2, X(data.EndedUtc) - X(boundary)))).Append("' height='158'/>");
                WifiStabilitySample previous = null; WifiLinkSample last = null;
                foreach (WifiStabilitySample sample in data.WifiSamples)
                {
                    WifiLinkSample current = sample.Interfaces.FirstOrDefault(delegate(WifiLinkSample l) { return l.InterfaceId == alias; });
                    bool available = current != null && current.Readability == "Available";
                    double x = X(sample.TimeUtc);
                    if (available)
                    {
                        for (int series = 0; series < (mode == 0 ? 1 : 2); series++)
                        {
                            double? value = mode == 0 ? (double?)current.SignalPercent : series == 0 ? current.RxKbps : current.TxKbps;
                            if (!value.HasValue) continue;
                            string cls = series == 0 ? "response" : "transmit"; double y = 180 - 158.0 * value.Value / maximum;
                            double? old = last == null ? null : mode == 0 ? (double?)last.SignalPercent : series == 0 ? last.RxKbps : last.TxKbps;
                            if (previous != null && last != null && last.Readability == "Available" && old.HasValue && (sample.TimeUtc - previous.TimeUtc).TotalMilliseconds <= 7500) svg.Append("<path class='").Append(cls).Append("-line' d='M").Append(Point(X(previous.TimeUtc), 180 - 158.0 * old.Value / maximum)).Append("L").Append(Point(x, y)).Append("'/>");
                            svg.Append("<circle class='").Append(cls).Append("-dot' cx='").Append(N(x)).Append("' cy='").Append(N(y)).Append("' r='2'/>");
                        }
                    }
                    else svg.Append("<path class='").Append(current != null && current.Readability == "NotConnected" ? "no-response" : "unknown-tick").Append("' d='M").Append(N(x)).Append(" 169v11'/>");
                    previous = sample; last = current;
                }
                return svg.Append("</svg></div>").ToString();
            }
            private void Events()
            {
                Section("03", T("monitorReportTimeline", "Session and configuration timeline"));
                string note = T("monitorReportTimelineNote", "Only event type, time and configuration category are shown. Configuration values and adapter identities are excluded.");
                html.Append("<p class='caption'>").Append(Escape(note)).Append("</p>"); P(note);
                int configurationCount = data.Events.Count(delegate(NetworkChangeEvent e) { return e.Kind == "SettingChanged"; });
                string totals = T("monitorReportConfiguration", "Configuration") + ": " + configurationCount + " · " + T("monitorReportStoredEvents", "Stored events / observed total") + ": " + data.Events.Length + "/" + data.TotalEvents;
                html.Append("<p class='caption'>").Append(Escape(totals)).Append("</p>"); P(totals);
                if (summary.EventsTruncated) { string message = T("monitorReportEventsTruncated", "The event timeline is truncated to the latest stored events; earlier changes cannot be reconstructed."); html.Append("<p class='gap-note'>").Append(Escape(message)).Append("</p>"); P(message); }
                html.Append("<ol class='timeline'>");
                foreach (NetworkChangeEvent e in data.Events)
                {
                    string label = Kind(e.Kind) + (e.Kind == "SettingChanged" ? " · " + Setting(e.Setting) : "") + (e.TargetId == null ? "" : " · " + e.TargetId);
                    html.Append("<li><time>").Append(Utc(e.TimeUtc)).Append("</time><span>").Append(Escape(label)).Append("</span></li>");
                }
                html.Append("</ol></section>");
            }
        }

        private const string Css = @"
:root{--ink:#152c2d;--muted:#526866;--paper:#faf8f1;--line:#cbd5cd;--teal:#087c71;--rust:#b54632;--missing:#e6d5a8;--blue:#41699c}*{box-sizing:border-box}body{margin:0;background:#e8e9df;color:var(--ink);font:15px/1.65 Georgia,'Noto Serif CJK SC','Microsoft YaHei',serif}main{max-width:1180px;margin:40px auto;background:var(--paper);padding:54px 62px;box-shadow:0 8px 50px #152c2d12}.edition{font:12px/1.4 Consolas,'Courier New',monospace;letter-spacing:.13em;border-top:5px solid var(--ink);padding-top:16px;display:flex;justify-content:space-between}.edition span{color:var(--teal)}h1{font-size:clamp(32px,5vw,56px);line-height:1.13;letter-spacing:-.045em;max-width:800px;margin:28px 0 18px}h2{font-size:27px;line-height:1.3;margin:0}h3{font-size:20px;line-height:1.4;margin:0}.lede{font-size:18px;max-width:760px;color:var(--muted)}.session-meta{display:grid;grid-template-columns:1fr 1fr .7fr;gap:24px;margin:30px 0;padding:20px 0;border-top:1px solid var(--line);border-bottom:1px solid var(--line)}.session-meta b{display:block;font-size:12px;color:var(--muted);letter-spacing:.05em}.session-meta time,.session-meta span{font:13px/1.8 Consolas,'Courier New',monospace}.privacy{border-left:4px solid var(--teal);padding:12px 20px;background:#eef2e9}.privacy p{margin:3px 0;font-size:13px}.metrics{display:flex;justify-content:space-between;margin:32px 0 8px;gap:24px}.metrics strong{display:block;font:44px/1.1 Georgia,serif;letter-spacing:-.06em}.metrics span{font-size:12px;color:var(--muted)}section{margin-top:46px}.section-heading{display:flex;align-items:baseline;gap:16px;border-bottom:2px solid var(--ink);padding-bottom:12px;margin-bottom:20px}.section-heading>span{color:var(--teal);font:15px Consolas,monospace}.caption{font-size:13px;color:var(--muted);margin:12px 0}.target{margin:24px 0 34px;padding-bottom:20px;border-bottom:1px solid var(--line)}.target-heading{display:flex;justify-content:space-between;gap:16px;align-items:baseline}.target-heading>span{font:12px Consolas,monospace;color:var(--muted)}svg{display:block;width:100%;height:auto;min-height:140px;margin:8px 0}svg text{fill:var(--muted);font:10px Consolas,'Courier New',monospace}.plot-paper{fill:#f1f3eb}.grid{fill:none;stroke:var(--line);stroke-width:1}.missing{fill:var(--missing);opacity:.6}.response-line{fill:none;stroke:var(--teal);stroke-width:1.6}.response-dot{fill:var(--teal)}.transmit-line{fill:none;stroke:var(--blue);stroke-width:1.6;stroke-dasharray:4 3}.transmit-dot{fill:var(--blue)}.no-response{stroke:var(--rust);stroke-width:2}.unknown-tick{stroke:#8b805b;stroke-width:2;stroke-dasharray:2 2}.legend{display:flex;flex-wrap:wrap;gap:16px;font-size:11px;color:var(--muted);margin:0 0 14px}.legend span:before{content:'';display:inline-block;width:16px;height:3px;margin-right:6px;vertical-align:middle}.legend-response:before{background:var(--teal)}.legend-transmit:before{background:var(--blue)}.legend-no-response:before{background:var(--rust)}.legend-missing:before{background:var(--missing)}.chart-label{font-size:13px;font-weight:bold;margin:16px 0 0}details summary{cursor:pointer;font-size:14px;font-weight:bold;color:var(--teal);padding:8px 0}.table-scroll{overflow-x:auto}table{border-collapse:collapse;width:100%;font-size:12px;line-height:1.6;margin-top:8px}th{text-align:left;color:var(--muted);font-weight:normal;border-bottom:2px solid var(--line)}th,td{padding:10px 12px 10px 0;vertical-align:top}td{border-bottom:1px solid var(--line)}td time{white-space:nowrap;font:11px/1.8 Consolas,monospace}.gap-note{background:#f1e9d2;padding:8px 12px;font-size:12px;border-left:3px solid #a88435}.empty{padding:20px;background:#f0eee5;color:var(--muted);font-size:14px}.timeline{list-style:none;padding:0;margin:20px 0}.timeline li{display:flex;gap:24px;padding:9px 0;border-bottom:1px solid var(--line);font-size:13px}.timeline time{flex:0 0 240px;font:12px/1.8 Consolas,monospace;color:var(--muted)}footer{border-top:3px solid var(--ink);margin-top:44px;padding-top:14px;display:flex;justify-content:space-between;gap:20px;font:11px/1.6 Consolas,monospace;color:var(--muted)}@media(max-width:700px){main{margin:0;padding:28px 20px;box-shadow:none}.edition{font-size:10px}.edition span{display:none}.session-meta{grid-template-columns:1fr;gap:12px}.metrics{flex-wrap:wrap;gap:20px}.metrics>div{flex:1 0 40%}.target-heading{display:block}.timeline li{display:block}.timeline time{display:block}.section-heading{gap:10px}footer{display:block}footer span{display:block;margin-top:8px}}@media print{@page{margin:15mm}body{background:white;font-size:11pt}main{max-width:none;margin:0;padding:0;box-shadow:none}h1{font-size:30pt}.privacy,.gap-note,.missing,.plot-paper{-webkit-print-color-adjust:exact;print-color-adjust:exact}.chart-block{break-inside:avoid;page-break-inside:avoid}.target-heading,.section-heading,summary,h1,h2,h3,.chart-label{break-after:avoid;page-break-after:avoid}.chart-block .caption{break-after:avoid;page-break-after:avoid}svg,tr,.timeline li{break-inside:avoid}.table-scroll{overflow:visible}section{margin-top:24px}footer{font-size:8pt}}
";
    }
}
