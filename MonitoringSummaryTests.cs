using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NetworkRescue
{
    internal static class MonitoringSummaryTests
    {
        private static int checks;
        private static readonly DateTime Now = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
        private static void Assert(bool condition, string name) { checks++; if (!condition) throw new Exception("Failed: " + name); }
        private static void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, name);
        }
        private static MonitoringHistoryRecord Record(int requested)
        {
            return new MonitoringHistoryRecord { Id = Guid.NewGuid().ToString("N"), State = "Completed", StartedUtc = Now,
                EndedUtc = Now.AddSeconds(requested + 10), IntervalMs = 1000, RequestedCount = requested,
                Targets = new[] { new MonitorTarget { Id = "a", Host = "example.test", Method = "icmp" } } };
        }
        private static MonitorSample Sample(MonitoringHistoryRecord record, string id, int sequence, double seconds, bool response)
        {
            MonitorTarget target = record.Targets.Single(delegate(MonitorTarget t) { return t.Id == id; });
            return new MonitorSample { TargetId = id, TargetHost = target.Host, Sequence = sequence, Sent = sequence, TimeUtc = Now.AddSeconds(seconds),
                Method = target.Method.ToUpperInvariant(), Status = response ? "Success" : "TimedOut", Success = response,
                ResponseReceived = target.Method == "https" && response, RttMs = response ? (long?)5 : null,
                HttpStatusCode = target.Method == "https" && response ? (int?)200 : null };
        }
        private static NetworkChangeEvent Event(int sequence, string kind, double seconds, string id)
        {
            return new NetworkChangeEvent { Sequence = sequence, Kind = kind, TimeUtc = Now.AddSeconds(seconds), TargetId = id };
        }
        private static void Intermittent()
        {
            MonitoringHistoryRecord r = Record(7);
            r.Samples = new[] { Sample(r, "a", 1, 1, true), Sample(r, "a", 2, 2, false), Sample(r, "a", 3, 3, false),
                Sample(r, "a", 4, 4, true), Sample(r, "a", 5, 5, false), Sample(r, "a", 6, 6, true), Sample(r, "a", 7, 7, true) };
            MonitoringSessionSummary summary = MonitoringSummaryBuilder.Build(r); MonitoringTargetSummary target = summary.Targets[0];
            Assert(summary.TotalIntervals == 2 && summary.GapCount == 0 && !summary.Truncated, "intermittent failures remain separate with full data");
            Assert(target.SampleCount == 7 && target.ResponseCount == 4, "counts reflect real response samples");
            MonitoringNoResponseInterval first = target.Intervals[0], second = target.Intervals[1];
            Assert(first.StartUtc == Now.AddSeconds(2) && first.LastNoResponseUtc == Now.AddSeconds(3) && first.RecoveredUtc == Now.AddSeconds(4), "first failure last failure and recovery kept independently");
            Assert(first.ObservedDurationMs == 1000 && !first.OpenEnded && second.ObservedDurationMs == 0 && !second.OpenEnded, "duration uses failed observation endpoints only");
            Assert(first.EventSequence == null && first.RecoveryEventSequence == null, "no event links fabricated");
        }
        private static void HttpResponses()
        {
            MonitoringHistoryRecord r = Record(5); r.Targets[0].Method = "https";
            r.Samples = Enumerable.Range(1, 5).Select(delegate(int seq) { return Sample(r, "a", seq, seq, seq != 1); }).ToArray();
            int[] codes = { 403, 407, 500, 302 };
            for (int i = 1; i < r.Samples.Length; i++) { r.Samples[i].HttpStatusCode = codes[i - 1]; r.Samples[i].HttpSuccess = false; r.Samples[i].Success = false; }
            MonitoringTargetSummary target = MonitoringSummaryBuilder.Build(r).Targets[0];
            Assert(target.ResponseCount == 4 && target.Intervals.Length == 1 && target.Intervals[0].RecoveredUtc == Now.AddSeconds(2), "403 407 500 and redirects are responding HTTPS, independent of application success");
            r = Record(1); r.Samples = new[] { Sample(r, "a", 1, 1, false) }; r.Samples[0].ResponseReceived = true;
            Assert(MonitoringSummaryBuilder.Build(r).Targets[0].Intervals.Length == 1, "ICMP follows Success only");
        }
        private static void OpenEndedAndMissing()
        {
            MonitoringHistoryRecord r = Record(10); r.State = "Stopped"; r.EndedUtc = Now.AddHours(1);
            r.Samples = new[] { Sample(r, "a", 1, 1, false), Sample(r, "a", 2, 2, false) };
            MonitoringTargetSummary target = MonitoringSummaryBuilder.Build(r).Targets[0];
            Assert(target.Intervals[0].OpenEnded && !target.Intervals[0].RecoveredUtc.HasValue && target.Intervals[0].ObservedDurationMs == 1000, "stopped session never extends a failed observation to session end");
            Assert(target.Gaps.Length == 1 && target.Gaps[0].Reason == "TrailingSamplesMissing", "stopped remaining samples shown as unmeasured");
            r.Samples = new MonitorSample[0]; target = MonitoringSummaryBuilder.Build(r).Targets[0];
            Assert(target.SampleCount == 0 && target.Intervals.Length == 0 && target.Gaps.Length == 1 && target.Gaps[0].Reason == "NotMeasured", "no samples is not an outage");
        }
        private static void TransportErrorsAreMeasured()
        {
            foreach (string error in new[] { "HttpConnectionClosed", "HttpRequestCanceled", "HttpRequestCancelled", "Closed", "Canceled" })
            {
                MonitoringHistoryRecord r = Record(3); r.Targets[0].Method = "https";
                r.Samples = new[] { Sample(r, "a", 1, 1, false), Sample(r, "a", 2, 2, false), Sample(r, "a", 3, 3, true) };
                r.Samples[1].Status = "HttpNoResponse"; r.Samples[1].ErrorCode = error;
                MonitoringTargetSummary target = MonitoringSummaryBuilder.Build(r).Targets[0];
                Assert(target.SampleCount == 3 && target.ResponseCount == 1 && target.Gaps.Length == 0 && target.Intervals.Length == 1 &&
                    target.Intervals[0].LastNoResponseUtc == Now.AddSeconds(2) && target.Intervals[0].ObservedDurationMs == 1000 &&
                    target.Intervals[0].RecoveredUtc == Now.AddSeconds(3) && !target.Intervals[0].OpenEnded, "attempted transport error remains measured: " + error);
            }
            MonitoringHistoryRecord statusRecord = Record(2); statusRecord.Targets[0].Method = "https";
            statusRecord.Samples = new[] { Sample(statusRecord, "a", 1, 1, false), Sample(statusRecord, "a", 2, 2, true) };
            statusRecord.Samples[0].Status = "HttpConnectionClosed";
            Assert(MonitoringSummaryBuilder.Build(statusRecord).Targets[0].SampleCount == 2 && MonitoringSummaryBuilder.Build(statusRecord).TotalIntervals == 1,
                "status text is not classified by substring");
        }
        private static void SamplingGaps()
        {
            MonitoringHistoryRecord r = Record(5); r.EndedUtc = Now.AddMinutes(5);
            r.Samples = new[] { Sample(r, "a", 1, 1, false), Sample(r, "a", 3, 2, false), Sample(r, "a", 4, 100, false), Sample(r, "a", 5, 101, true) };
            MonitoringTargetSummary target = MonitoringSummaryBuilder.Build(r).Targets[0];
            Assert(target.Intervals.Length == 3 && target.Gaps.Length == 2, "sequence and timestamp discontinuities split failed intervals");
            Assert(target.Gaps[0].Reason == "SequenceGap" && target.Gaps[1].Reason == "TimestampGap", "gap types expose what was observed");
            Assert(target.Intervals[0].OpenEnded && target.Intervals[1].OpenEnded && !target.Intervals[2].OpenEnded && target.Intervals.All(delegate(MonitoringNoResponseInterval interval) { return interval.ObservedDurationMs == 0; }), "no merge or recovery inferred across gaps");
            r = Record(3); r.Targets[0].Method = "https";
            r.Samples = new[] { Sample(r, "a", 1, 1, false), Sample(r, "a", 2, 4.1, false), Sample(r, "a", 3, 7.2, true) };
            Assert(MonitoringSummaryBuilder.Build(r).Targets[0].Intervals.Length == 1 && MonitoringSummaryBuilder.Build(r).GapCount == 0, "normal three-second HTTPS timeout cadence is not a sleep gap");
            r = Record(3); r.Samples = new[] { Sample(r, "a", 1, 1, false), Sample(r, "a", 2, 2, false), Sample(r, "a", 3, 3, true) };
            r.Samples[1].Status = "Cancelled"; target = MonitoringSummaryBuilder.Build(r).Targets[0];
            Assert(target.SampleCount == 2 && target.ResponseCount == 1 && target.Intervals.Length == 1 && target.Intervals[0].OpenEnded && target.Gaps[0].Reason == "UnmeasuredSample", "cancelled record is not failed sampling and cannot imply recovery");
            foreach (string marker in new[] { "Cancel", "Canceled", "Cancelled", "Closed", "Stop", "Stopped", "Sleep", "Sleeping", "Suspend", "Suspended", "NotMeasured", "ObservationUnavailable" })
            {
                r.Samples[1].Status = marker;
                target = MonitoringSummaryBuilder.Build(r).Targets[0];
                Assert(target.SampleCount == 2 && target.Intervals[0].OpenEnded, "unmeasured marker excluded: " + marker);
            }
            r = Record(4); r.Samples = new[] { Sample(r, "a", 3, 3, false), Sample(r, "a", 4, 4, true) };
            Assert(MonitoringSummaryBuilder.Build(r).Targets[0].Gaps[0].Reason == "InitialSamplesMissing", "missing initial sequence is recorded");
            r = Record(2); r.Samples = new[] { Sample(r, "a", 1, 1, false), Sample(r, "a", 2, 2, true) };
            r.Events = new[] { Event(1, "MonitoringStopped", 1.5, null) }; r.TotalEvents = 1;
            Assert(MonitoringSummaryBuilder.Build(r).Targets[0].Intervals[0].OpenEnded && MonitoringSummaryBuilder.Build(r).GapCount == 1, "explicit stop boundary prevents cross-stop recovery");
        }
        private static WifiStabilitySample Wifi(double seconds, string state, string readability)
        {
            return new WifiStabilitySample { TimeUtc = Now.AddSeconds(seconds), StateCode = state,
                Interfaces = readability == null ? new WifiLinkSample[0] : new[] { new WifiLinkSample { State = readability == "NotConnected" ? 4u : 1u, Readability = readability, SignalPercent = readability == "Available" ? (int?)60 : null } } };
        }
        private static void NearbyEvidenceAndReplay()
        {
            MonitoringHistoryRecord r = Record(3); r.EndedUtc = Now.AddSeconds(90);
            r.Samples = new[] { Sample(r, "a", 1, 40, false), Sample(r, "a", 2, 41, false), Sample(r, "a", 3, 42, true) };
            r.Events = new[] { Event(10, "SettingChanged", 30, null), Event(11, "TargetNoResponse", 40, "a"), Event(12, "TargetRecovered", 42, "a"),
                Event(13, "SettingChanged", 51, null), Event(14, "SettingChanged", 51.001, null) }; r.TotalEvents = 14;
            r.WifiCollectionEnabled = true;
            r.WifiSamples = new[] { Wifi(29.9, "Available", "Available"), Wifi(30, "Available", "Available"), Wifi(40, "Unavailable", null),
                Wifi(41, "NotConnected", "NotConnected"), Wifi(51, "Partial", "Available"), Wifi(51.01, "Available", "Available") }; r.WifiTotalSamples = 7;
            MonitoringSessionSummary summary = MonitoringSummaryBuilder.Build(r); MonitoringNoResponseInterval interval = summary.Targets[0].Intervals[0];
            Assert(interval.EventSequence == 11 && interval.RecoveryEventSequence == 12, "links retain real event sequence for replay");
            Assert(interval.NearbyConfigurationCount == 2 && interval.NearbyWifiCount == 3, "ten-second nearby window counts only retained real readable evidence once per snapshot");
            Assert(summary.EventsTruncated && summary.WifiTruncated && !summary.Truncated, "source evidence truncation distinct from interval truncation");
            r.Events[1].TimeUtc = Now.AddSeconds(39.9); interval = MonitoringSummaryBuilder.Build(r).Targets[0].Intervals[0];
            Assert(interval.EventSequence == null, "nearby event never substitutes for a missing exact transition");
            r.WifiCollectionEnabled = false; r.WifiSamples = null; r.WifiTotalSamples = 0;
            Assert(MonitoringSummaryBuilder.Build(r).Targets[0].Intervals[0].NearbyWifiCount == 0, "legacy no-wifi schema does not synthesize wifi context");
        }
        private static void SeparateTargetsAndRetention()
        {
            MonitoringHistoryRecord r = Record(3);
            r.Targets = new[] { r.Targets[0], new MonitorTarget { Id = "b", Host = "other.test", Method = "https" } };
            r.Samples = new[] { Sample(r, "a", 1, 1, false), Sample(r, "b", 1, 1, true), Sample(r, "a", 2, 2, false),
                Sample(r, "b", 2, 2, false), Sample(r, "a", 3, 3, true), Sample(r, "b", 3, 3, false) };
            MonitoringSessionSummary summary = MonitoringSummaryBuilder.Build(r);
            Assert(summary.Targets.Length == 2 && summary.TotalIntervals == 2 && summary.Targets[0].ResponseCount == 1 && summary.Targets[1].ResponseCount == 1, "per-target counters separated despite interleaved samples");
            Assert(!summary.Targets[0].Intervals[0].OpenEnded && summary.Targets[1].Intervals[0].OpenEnded, "one target recovery never closes another target interval");
            r = Record(3600); r.EndedUtc = Now.AddSeconds(3600);
            r.Targets = Enumerable.Range(0, 4).Select(delegate(int i) { return new MonitorTarget { Id = "t" + i, Host = "example" + i + ".test", Method = "icmp" }; }).ToArray();
            List<MonitorSample> all = new List<MonitorSample>();
            foreach (MonitorTarget target in r.Targets) for (int i = 1; i <= 3600; i++) all.Add(Sample(r, target.Id, i, i, i % 2 == 0));
            r.Samples = all.ToArray(); summary = MonitoringSummaryBuilder.Build(r);
            Assert(r.Samples.Length == 14400 && summary.TotalIntervals == 7200 && summary.Targets.All(delegate(MonitoringTargetSummary target) { return target.Intervals.Length == 1800; }) && !summary.Truncated, "maximum fixture retains all 1800 intermittent intervals per target");
        }
        private static void InvalidInputs()
        {
            Reject(delegate { MonitoringSummaryBuilder.Build(null); }, "null record rejected");
            MonitoringHistoryRecord r = Record(1); r.IntervalMs = 0; Reject(delegate { MonitoringSummaryBuilder.Build(r); }, "missing interval bound rejected");
            r = Record(1); r.EndedUtc = Now.AddSeconds(-1); Reject(delegate { MonitoringSummaryBuilder.Build(r); }, "backwards session rejected");
            r = Record(1); r.Targets[0].Method = null; Reject(delegate { MonitoringSummaryBuilder.Build(r); }, "unknown method rejected");
            r = Record(2); r.Samples = new[] { Sample(r, "a", 1, 1, true), Sample(r, "a", 1, 2, false) }; Reject(delegate { MonitoringSummaryBuilder.Build(r); }, "duplicate sequence rejected");
            r.Samples[1].Sequence = 2; r.Samples[1].TimeUtc = Now; Reject(delegate { MonitoringSummaryBuilder.Build(r); }, "backward target sample time rejected");
            r = Record(1); r.Samples = new[] { Sample(r, "a", 1, 1, false) }; r.Samples[0].Status = null; Reject(delegate { MonitoringSummaryBuilder.Build(r); }, "missing status cannot silently become failure");
            r.Samples[0].Status = "TimedOut"; r.Samples[0].TimeUtc = DateTime.SpecifyKind(Now, DateTimeKind.Unspecified); Reject(delegate { MonitoringSummaryBuilder.Build(r); }, "non-UTC sample rejected");
            r = Record(1); r.Events = new[] { Event(1, "TargetNoResponse", 1, "missing") }; r.TotalEvents = 1; Reject(delegate { MonitoringSummaryBuilder.Build(r); }, "unbound target event rejected");
        }
        public static int Main(string[] args)
        {
            try
            {
                Intermittent(); HttpResponses(); OpenEndedAndMissing(); TransportErrorsAreMeasured(); SamplingGaps(); NearbyEvidenceAndReplay(); SeparateTargetsAndRetention(); InvalidInputs();
                Console.WriteLine("Monitoring summary checks passed: " + checks + " (synthetic fixtures only; no network, UI or system changes)"); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("After " + checks + " checks: " + ex); return 1; }
        }
    }
}
