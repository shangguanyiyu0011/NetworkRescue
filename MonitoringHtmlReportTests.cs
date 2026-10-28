using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Web.Script.Serialization;

namespace NetworkRescue
{
    internal static class MonitoringHtmlReportTests
    {
        private static int checks;
        private static readonly DateTime Now = new DateTime(2026, 10, 28, 1, 0, 0, DateTimeKind.Utc);
        private const string Secret = "private-host-782.test";
        private static void Assert(bool condition, string name) { checks++; if (!condition) throw new Exception("Failed: " + name); }
        private static int Count(string value, string part) { return (value.Length - value.Replace(part, "").Length) / part.Length; }
        private static MonitoringHistoryRecord Record(int requested, int targets)
        {
            return new MonitoringHistoryRecord { Id = "963284a29a9e4cc09183962682347218", State = "Completed", StartedUtc = Now, EndedUtc = Now.AddSeconds(requested), IntervalMs = 1000, RequestedCount = requested,
                Targets = Enumerable.Range(1, targets).Select(delegate(int index) { return new MonitorTarget { Id = "secret-id-" + index, Host = Secret, Method = "icmp" }; }).ToArray() };
        }
        private static MonitorSample Sample(MonitoringHistoryRecord record, int target, int sequence, bool response)
        {
            MonitorTarget t = record.Targets[target];
            return new MonitorSample { TargetId = t.Id, TargetHost = t.Host, Sequence = sequence, TimeUtc = Now.AddSeconds(sequence), Method = "ICMP", Route = "Network", Status = response ? "Success" : "TimedOut",
                Success = response, RttMs = response ? (long?)(sequence * 3) : null, ResolvedAddress = "192.0.2.187" };
        }
        private static MonitoringHistoryRecord Fixture()
        {
            MonitoringHistoryRecord record = Record(8, 2);
            record.Samples = new[] { Sample(record, 0, 1, false), Sample(record, 0, 2, false), Sample(record, 0, 3, true), Sample(record, 0, 5, false), Sample(record, 0, 6, true), Sample(record, 0, 7, false), Sample(record, 0, 8, true) };
            record.Samples[5].Status = "Cancelled";
            record.Events = new[] { new NetworkChangeEvent { Sequence = 1, TimeUtc = Now, Kind = "MonitoringStarted" }, new NetworkChangeEvent { Sequence = 2, TimeUtc = Now.AddSeconds(2), Kind = "SettingChanged", Setting = "Dns", Before = "192.0.2.189", After = "198.51.100.188", AdapterName = @"C:\Users\secret-owner\adapter" }, new NetworkChangeEvent { Sequence = 3, TimeUtc = Now.AddSeconds(8), Kind = "MonitoringCompleted" } }; record.TotalEvents = 3;
            record.WifiCollectionEnabled = true; record.WifiTotalSamples = 4;
            record.WifiSamples = Enumerable.Range(1, 4).Select(delegate(int i) { return new WifiStabilitySample { TimeUtc = Now.AddSeconds(i * 2), StateCode = i == 3 ? "Unavailable" : "Available", ErrorCode = Secret,
                Interfaces = i == 3 ? new WifiLinkSample[0] : new[] { new WifiLinkSample { InterfaceId = "11111111-2222-3333-4444-555555555555", Readability = "Available", State = 1, SignalPercent = 60 + i * 5, RxKbps = 144000, TxKbps = 72000 } } }; }).ToArray();
            return record;
        }
        private static XmlDocument Markup(string html)
        {
            XmlDocument doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(html.Substring(html.IndexOf("<html", StringComparison.Ordinal))); return doc;
        }
        private static void PrivacyAndMarkup()
        {
            MonitoringHistoryRecord record = Fixture();
            record.Targets[0].Host = "<img src='https://" + Secret + "/exfil'/>";
            foreach (MonitorSample sample in record.Samples) { sample.TargetHost = record.Targets[0].Host; sample.ErrorCode = Secret; }
            record.Events[1].TargetHost = Secret; record.Events[1].TargetId = Secret;
            HtmlReportDocument report = MonitoringHtmlReport.Build(record, null);
            string[] secrets = { Secret, "192.0.2.187", "192.0.2.189", "198.51.100.188", "secret-id", "secret-owner", "963284a29a9e4cc09183962682347218", "11111111-2222-3333-4444-555555555555", "exfil" };
            foreach (string secret in secrets) Assert(!report.Html.Contains(secret) && !report.PreviewText.Contains(secret), "typed projection excludes " + secret);
            Assert(report.Html.Contains("Target1") && report.Html.Contains("Target2") && report.Html.Contains("Link1"), "deterministic session aliases");
            XmlDocument doc = Markup(report.Html);
            Assert(doc.GetElementsByTagName("svg").Count == 4, "RTT and two Wi-Fi SVGs are well formed");
            Assert(doc.SelectNodes("//*[local-name()='div' and @class='chart-block'][*[local-name()='div' and @class='target-heading'] and *[local-name()='svg']]").Count == 2, "target title and RTT plot share a print grouping");
            Assert(doc.SelectNodes("//*[local-name()='div' and @class='chart-block'][*[local-name()='p' and @class='chart-label'] and *[local-name()='svg']]").Count == 2, "Wi-Fi chart captions and plots share print grouping");
            Assert(doc.GetElementsByTagName("script").Count == 0 && doc.GetElementsByTagName("img").Count == 0 && doc.GetElementsByTagName("iframe").Count == 0, "no active or external content elements");
            Assert(doc.SelectNodes("//@src | //@href | //@*[starts-with(name(),'on')]").Count == 0, "no external references or handlers in any attribute");
            Assert(report.Html.Contains("default-src 'none'") && report.Html.Contains("style-src 'unsafe-inline'"), "offline restrictive content policy");
            Assert(!report.Html.Contains("url(") && !report.Html.Contains("@import") && !report.Html.Contains("data:"), "no CSS or embedded external resource route");
            Assert(report.PreviewText.Contains("7/8") && report.PreviewText.Contains("Target2 · ICMP: 0/8") && report.PreviewText.Contains("Missing observation boundary") && report.PreviewText.Contains("Recovery response observed"), "preview shows identical counts and real boundaries");
            Assert(!report.PreviewText.Contains("<html") && !report.PreviewText.Contains("<svg"), "preview is readable plain text");
            HtmlReportDocument repeated = MonitoringHtmlReport.Build(record, null);
            Assert(report.Html == repeated.Html && report.PreviewText == repeated.PreviewText, "same evidence has deterministic output");
            HtmlReportDocument hostileText = MonitoringHtmlReport.Build(record, delegate(string key) { return key == "monitorReportTitle" ? "<script a='1'>&\"Attack\"</script>" : key; });
            Assert(!hostileText.Html.Contains("<script") && hostileText.Html.Contains("&lt;script a=&#39;1&#39;&gt;&amp;&quot;Attack&quot;"), "all dynamic localization text is escaped");
            Markup(hostileText.Html);
        }
        private static void SamplingBoundaries()
        {
            MonitoringHistoryRecord record = Record(5, 1);
            record.Samples = new[] { Sample(record, 0, 1, true), Sample(record, 0, 2, true), Sample(record, 0, 4, true), Sample(record, 0, 5, true) };
            HtmlReportDocument report = MonitoringHtmlReport.Build(record, null);
            Assert(Count(report.Html, "<path class='response-line'") == 2, "missing sequence disconnects SVG response line");
            Assert(report.Html.Contains("<rect class='missing'") && Count(report.Html, "<circle class='response-dot'") == 4, "real sample points only and missing region is shaded");
            record.Samples[2].Status = "Stopped";
            report = MonitoringHtmlReport.Build(record, null);
            Assert(Count(report.Html, "<circle class='response-dot'") == 3 && Count(report.Html, "<path class='no-response'") == 0, "unmeasured stop creates no synthetic success or failure");
            record = Record(4, 1); report = MonitoringHtmlReport.Build(record, null);
            Assert(report.Html.Contains("No samples were recorded") && !report.Html.Contains("<circle class='response-dot'") && !report.Html.Contains("<path class='no-response'"), "no samples remains no evidence");
            Assert(report.Html.Contains("this history predates Wi-Fi support") && report.PreviewText.Contains("this history predates Wi-Fi support"), "legacy or disabled Wi-Fi has explicit unsupported boundary");
            record.State = "Interrupted"; report = MonitoringHtmlReport.Build(record, null);
            Assert(report.Html.Contains("Interrupted") && report.PreviewText.Contains("Session state: Interrupted"), "loaded interrupted history retains its actual session state");
            record = Fixture(); record.WifiTotalSamples = 10; record.TotalEvents = 8;
            report = MonitoringHtmlReport.Build(record, null);
            Assert(report.Html.Contains("earlier observations are missing") && report.PreviewText.Contains("earlier observations are missing"), "Wi-Fi retention boundary in HTML and preview");
            Assert(report.Html.Contains("earlier changes cannot be reconstructed") && report.PreviewText.Contains("earlier changes cannot be reconstructed"), "event retention boundary in HTML and preview");
        }
        private static void AttemptedTransportFailures()
        {
            MonitoringHistoryRecord record = Record(6, 1); record.Targets[0].Method = "https";
            record.Samples = Enumerable.Range(1, 6).Select(delegate(int sequence)
            {
                bool response = sequence == 3 || sequence == 5;
                MonitorSample sample = Sample(record, 0, sequence, response);
                sample.Method = "HTTPS"; sample.Route = "Direct"; sample.ResponseReceived = response;
                sample.Status = response ? "HttpResponse" : "HttpNoResponse";
                sample.HttpStatusCode = response ? (int?)200 : null; sample.HttpSuccess = response;
                return sample;
            }).ToArray();
            record.Samples[0].ErrorCode = "HttpConnectionClosed";
            record.Samples[1].ErrorCode = "HttpRequestCanceled";
            record.Samples[3].Status = "HttpConnectionClosed";
            record.Samples[5].Status = "NotMeasured";
            MonitoringSessionSummary summary = MonitoringSummaryBuilder.Build(record);
            HtmlReportDocument report = MonitoringHtmlReport.Build(record, null);
            Assert(summary.TotalIntervals == 2 && summary.Targets[0].SampleCount == 5, "attempted transport errors remain measured in shared semantic classifier");
            Assert(Count(report.Html, "<path class='no-response'") == 3 && Count(report.Html, "<circle class='response-dot'") == 2, "HTML plots real attempted failures and excludes only explicit unmeasured marker");
            Assert(report.PreviewText.Contains("No-response intervals: 2") && report.PreviewText.Contains("Measured observations: 5") && report.PreviewText.Contains("Responses: 2"), "preview counts attempted failures consistently with summary and graph");
            Assert(!report.Html.Contains("HttpConnectionClosed") && !report.Html.Contains("HttpRequestCanceled"), "transport tokens are classified without copying source tokens into report");
            Markup(report.Html);
        }
        private static void FullCapacity()
        {
            MonitoringHistoryRecord record = Record(3600, 4);
            record.Samples = Enumerable.Range(0, 4).SelectMany(delegate(int target) { return Enumerable.Range(1, 3600).Select(delegate(int sequence) { return Sample(record, target, sequence, sequence % 2 == 0); }); }).ToArray();
            record.Events = Enumerable.Range(1, 500).Select(delegate(int i) { return new NetworkChangeEvent { Sequence = i, TimeUtc = Now.AddSeconds(i), Kind = "SettingChanged", Setting = "ProxyServer", Before = Secret, After = Secret, AdapterName = Secret }; }).ToArray(); record.TotalEvents = 500;
            record.WifiCollectionEnabled = true; record.WifiTotalSamples = 720;
            record.WifiSamples = Enumerable.Range(1, 720).Select(delegate(int i) { return new WifiStabilitySample { TimeUtc = Now.AddSeconds(i * 5), StateCode = "Available", Interfaces = Enumerable.Range(1, 8).Select(delegate(int link) { return new WifiLinkSample { InterfaceId = "secret-link-" + link, Readability = "Available", State = 1, SignalPercent = 50 + i % 50, RxKbps = 144000, TxKbps = 72000 }; }).ToArray() }; }).ToArray();
            HtmlReportDocument report = MonitoringHtmlReport.Build(record, null);
            Assert(Encoding.UTF8.GetByteCount(report.Html) <= MonitoringHtmlReport.MaximumReportBytes, "full 14400 / 500 / 720x8 evidence fits report byte bound");
            Assert(Count(report.Html, "<path class='no-response'") == 7200 && Count(report.Html, "<circle class='response-dot'") == 7200 + 720 * 8 * 2, "full capacity retains every RTT sample and Wi-Fi signal/receive point");
            Assert(report.PreviewText.Contains("14400") && report.Html.Contains("14400"), "preview has the same full stored count");
            Assert(!report.Html.Contains(Secret) && !report.Html.Contains("secret-link"), "capacity fixture still redacted");
            Markup(report.Html);
            string localeRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "locales");
            if (!Directory.Exists(localeRoot)) localeRoot = Path.Combine(Environment.CurrentDirectory, "ui", "locales");
            int largest = 0; string largestLanguage = null;
            foreach (string language in new[] { "zh-CN", "zh-TW", "en", "ja", "ko", "de", "fr", "es", "pt", "ru" })
            {
                string path = Path.Combine(localeRoot, language + ".json");
                Dictionary<string, string> values = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8));
                HtmlReportDocument translated = MonitoringHtmlReport.Build(record, delegate(string key) { string value; return values.TryGetValue(key, out value) ? value : key; });
                int bytes = Encoding.UTF8.GetByteCount(translated.Html);
                Assert(bytes <= MonitoringHtmlReport.MaximumReportBytes, "full stored evidence fits byte limit in " + language);
                if (bytes > largest) { largest = bytes; largestLanguage = language; }
                Console.WriteLine("MonitoringHtmlReport capacity " + language + ": " + bytes + " UTF-8 bytes.");
            }
            Console.WriteLine("MonitoringHtmlReport largest locale: " + largestLanguage + " / " + largest + " UTF-8 bytes.");
        }
        public static int Main(string[] args)
        {
            try
            {
                PrivacyAndMarkup(); SamplingBoundaries(); AttemptedTransportFailures(); FullCapacity();
                if (args.Length > 0) File.WriteAllText(args[0], MonitoringHtmlReport.Build(Fixture(), null).Html, new UTF8Encoding(false));
                Console.WriteLine("MonitoringHtmlReport: " + checks + " checks passed."); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
