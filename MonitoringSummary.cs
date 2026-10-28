using System;
using System.Collections.Generic;
using System.IO;

namespace NetworkRescue
{
    public sealed class MonitoringSessionSummary
    {
        public MonitoringTargetSummary[] Targets = new MonitoringTargetSummary[0];
        public int TotalIntervals, GapCount;
        // The builder retains all intervals. Presentation layers may impose their own explicit limit.
        public bool Truncated, EventsTruncated, WifiTruncated;
    }

    public sealed class MonitoringTargetSummary
    {
        public string TargetId, Host, Method;
        public int SampleCount, ResponseCount;
        public MonitoringNoResponseInterval[] Intervals = new MonitoringNoResponseInterval[0];
        public MonitoringSamplingGap[] Gaps = new MonitoringSamplingGap[0];
    }

    public sealed class MonitoringNoResponseInterval
    {
        public DateTime StartUtc, LastNoResponseUtc;
        public DateTime? RecoveredUtc;
        // Observed endpoints only: LastNoResponseUtc - StartUtc. This is not an outage estimate.
        public long ObservedDurationMs;
        public bool OpenEnded;
        public int FirstSequence, LastSequence;
        public int? EventSequence, RecoveryEventSequence;
        // Temporal proximity is evidence context, never a statement of causation.
        public int NearbyConfigurationCount, NearbyWifiCount;
    }

    public sealed class MonitoringSamplingGap
    {
        public DateTime StartUtc, EndUtc;
        public string Reason;
        public int? PreviousSequence, NextSequence;
    }

    public static class MonitoringSummaryBuilder
    {
        public const int NearbyWindowSeconds = 10;
        private static InvalidDataException Invalid() { return new InvalidDataException("InvalidMonitoringSummaryRecord"); }
        private static bool ValidTime(DateTime time)
        {
            return time.Kind == DateTimeKind.Utc && time >= new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }
        private static bool InBounds(DateTime time, MonitoringHistoryRecord record)
        {
            return ValidTime(time) && time >= record.StartedUtc && time <= record.EndedUtc;
        }
        internal static bool IsUnmeasuredSample(MonitorSample sample)
        {
            // Lifecycle markers are explicit sample states. Transport error codes (for example
            // HttpConnectionClosed or HttpRequestCanceled) still describe an attempted probe.
            return sample != null && UnmeasuredToken(sample.Status);
        }
        private static bool UnmeasuredToken(string value)
        {
            if (String.IsNullOrEmpty(value)) return false;
            return value.Equals("Cancel", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Canceled", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Closed", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Stop", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Stopped", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Sleep", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Sleeping", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Suspend", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Suspended", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("NotMeasured", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("ObservationUnavailable", StringComparison.OrdinalIgnoreCase);
        }
        private static void Gap(List<MonitoringSamplingGap> gaps, DateTime start, DateTime end, string reason, int? previous, int? next)
        {
            gaps.Add(new MonitoringSamplingGap { StartUtc = start, EndUtc = end, Reason = reason, PreviousSequence = previous, NextSequence = next });
        }
        private static int? EventSequence(NetworkChangeEvent[] events, string kind, string target, DateTime time)
        {
            foreach (NetworkChangeEvent item in events)
                if (item.Kind == kind && item.TargetId == target && item.TimeUtc == time) return item.Sequence;
            return null;
        }
        private static bool HasLifecycleBoundary(NetworkChangeEvent[] events, DateTime previous, DateTime next)
        {
            foreach (NetworkChangeEvent item in events)
                if ((item.Kind == "MonitoringStopped" || item.Kind == "MonitoringCompleted") && item.TimeUtc >= previous && item.TimeUtc < next) return true;
            return false;
        }
        private static bool Nearby(DateTime time, MonitoringNoResponseInterval interval)
        {
            // Tick comparisons avoid overflowing DateTime near the supported bounds.
            long window = TimeSpan.TicksPerSecond * (long)NearbyWindowSeconds;
            return time.Ticks >= interval.StartUtc.Ticks - window && time.Ticks - interval.LastNoResponseUtc.Ticks <= window;
        }
        private static bool ReadableWifi(WifiStabilitySample wifi)
        {
            if (wifi.StateCode != "Available" && wifi.StateCode != "Partial" && wifi.StateCode != "NotConnected") return false;
            if (wifi.Interfaces == null) return false;
            foreach (WifiLinkSample link in wifi.Interfaces)
                if (link != null && (link.Readability == "Available" || link.Readability == "NotConnected")) return true;
            return false;
        }

        public static MonitoringSessionSummary Build(MonitoringHistoryRecord record)
        {
            if (record == null || !ValidTime(record.StartedUtc) || !ValidTime(record.EndedUtc) || record.EndedUtc < record.StartedUtc ||
                record.IntervalMs < 1000 || record.IntervalMs > 10000 || record.RequestedCount < 1 || record.RequestedCount > 3600 ||
                record.Targets == null || record.Targets.Length < 1 || record.Targets.Length > 4 || record.Samples == null ||
                record.Samples.Length > 14400 || record.Samples.Length > record.RequestedCount * record.Targets.Length ||
                record.Events == null || record.Events.Length > 500 || record.TotalEvents < record.Events.Length || record.WifiTotalSamples < 0) throw Invalid();
            WifiStabilitySample[] wifiSamples = record.WifiSamples ?? new WifiStabilitySample[0];
            if (wifiSamples.Length > 720 || record.WifiTotalSamples < wifiSamples.Length || (!record.WifiCollectionEnabled && (wifiSamples.Length > 0 || record.WifiTotalSamples > 0))) throw Invalid();
            Dictionary<string, List<MonitorSample>> samples = new Dictionary<string, List<MonitorSample>>(StringComparer.Ordinal);
            Dictionary<string, MonitorTarget> targets = new Dictionary<string, MonitorTarget>(StringComparer.Ordinal);
            foreach (MonitorTarget target in record.Targets)
            {
                if (target == null || String.IsNullOrWhiteSpace(target.Id) || target.Id.Length > 32 || String.IsNullOrWhiteSpace(target.Host) || target.Host.Length > 253 ||
                    (target.Method != "icmp" && target.Method != "https") || targets.ContainsKey(target.Id)) throw Invalid();
                targets.Add(target.Id, target); samples.Add(target.Id, new List<MonitorSample>());
            }
            foreach (MonitorSample sample in record.Samples)
            {
                MonitorTarget target;
                if (sample == null || sample.GetType() != typeof(MonitorSample) || sample.TargetId == null || !targets.TryGetValue(sample.TargetId, out target) ||
                    sample.TargetHost != target.Host || !String.Equals(sample.Method, target.Method, StringComparison.OrdinalIgnoreCase) ||
                    String.IsNullOrWhiteSpace(sample.Status) || !InBounds(sample.TimeUtc, record) || sample.Sequence < 1 || sample.Sequence > record.RequestedCount ||
                    (sample.HttpStatusCode.HasValue && (sample.HttpStatusCode.Value < 100 || sample.HttpStatusCode.Value > 599))) throw Invalid();
                List<MonitorSample> list = samples[sample.TargetId];
                if (list.Count > 0 && (sample.Sequence <= list[list.Count - 1].Sequence || sample.TimeUtc < list[list.Count - 1].TimeUtc)) throw Invalid();
                list.Add(sample);
            }
            int previousEvent = 0;
            foreach (NetworkChangeEvent item in record.Events)
            {
                if (item == null || item.Sequence <= previousEvent || item.Sequence > record.TotalEvents || !InBounds(item.TimeUtc, record) ||
                    (item.Kind != "MonitoringStarted" && item.Kind != "MonitoringStopped" && item.Kind != "MonitoringCompleted" && item.Kind != "ObservationUnavailable" &&
                    item.Kind != "SettingChanged" && item.Kind != "TargetRecovered" && item.Kind != "TargetNoResponse")) throw Invalid();
                if ((item.Kind == "TargetNoResponse" || item.Kind == "TargetRecovered") && (item.TargetId == null || !targets.ContainsKey(item.TargetId))) throw Invalid();
                previousEvent = item.Sequence;
            }
            DateTime previousWifi = record.StartedUtc;
            foreach (WifiStabilitySample wifi in wifiSamples)
            {
                if (wifi == null || !InBounds(wifi.TimeUtc, record) || wifi.TimeUtc < previousWifi || wifi.Interfaces == null || wifi.Interfaces.Length > 8 ||
                    (wifi.StateCode != "Available" && wifi.StateCode != "Partial" && wifi.StateCode != "NotConnected" && wifi.StateCode != "NoWirelessInterface" && wifi.StateCode != "Unavailable")) throw Invalid();
                foreach (WifiLinkSample link in wifi.Interfaces)
                    if (link == null || link.State > 7 || (link.SignalPercent.HasValue && (link.SignalPercent.Value < 0 || link.SignalPercent.Value > 100)) ||
                        (link.Readability != "Available" && link.Readability != "NotConnected" && link.Readability != "Unavailable")) throw Invalid();
                previousWifi = wifi.TimeUtc;
            }
            MonitoringSessionSummary summary = new MonitoringSessionSummary { EventsTruncated = record.TotalEvents > record.Events.Length, WifiTruncated = record.WifiTotalSamples > wifiSamples.Length };
            List<MonitoringTargetSummary> result = new List<MonitoringTargetSummary>();
            foreach (MonitorTarget target in record.Targets)
            {
                MonitoringTargetSummary targetSummary = new MonitoringTargetSummary { TargetId = target.Id, Host = target.Host, Method = target.Method };
                List<MonitoringNoResponseInterval> intervals = new List<MonitoringNoResponseInterval>();
                List<MonitoringSamplingGap> gaps = new List<MonitoringSamplingGap>();
                List<MonitorSample> targetSamples = samples[target.Id];
                MonitoringNoResponseInterval active = null;
                MonitorSample previous = null;
                long maximumSpacingMs = Math.Max(2L * record.IntervalMs, record.IntervalMs + (target.Method == "https" ? 3000L : 1200L));
                foreach (MonitorSample sample in targetSamples)
                {
                    string gapReason = null;
                    if (previous == null && sample.Sequence > 1) gapReason = "InitialSamplesMissing";
                    else if (previous != null && sample.Sequence != previous.Sequence + 1) gapReason = "SequenceGap";
                    else if ((sample.TimeUtc - (previous == null ? record.StartedUtc : previous.TimeUtc)).TotalMilliseconds > maximumSpacingMs ||
                        (previous != null && HasLifecycleBoundary(record.Events, previous.TimeUtc, sample.TimeUtc))) gapReason = "TimestampGap";
                    if (gapReason != null)
                    {
                        Gap(gaps, previous == null ? record.StartedUtc : previous.TimeUtc, sample.TimeUtc, gapReason, previous == null ? (int?)null : previous.Sequence, sample.Sequence);
                        active = null;
                    }
                    if (IsUnmeasuredSample(sample))
                    {
                        Gap(gaps, sample.TimeUtc, sample.TimeUtc, "UnmeasuredSample", previous == null ? (int?)null : previous.Sequence, sample.Sequence);
                        active = null; previous = sample; continue;
                    }
                    targetSummary.SampleCount++;
                    bool response = target.Method == "icmp" ? sample.Success : sample.ResponseReceived;
                    if (response)
                    {
                        targetSummary.ResponseCount++;
                        if (active != null)
                        {
                            active.RecoveredUtc = sample.TimeUtc; active.OpenEnded = false;
                            active.RecoveryEventSequence = EventSequence(record.Events, "TargetRecovered", target.Id, sample.TimeUtc);
                            active = null;
                        }
                    }
                    else
                    {
                        if (active == null)
                        {
                            active = new MonitoringNoResponseInterval { StartUtc = sample.TimeUtc, LastNoResponseUtc = sample.TimeUtc, FirstSequence = sample.Sequence,
                                LastSequence = sample.Sequence, OpenEnded = true, EventSequence = EventSequence(record.Events, "TargetNoResponse", target.Id, sample.TimeUtc) };
                            intervals.Add(active);
                        }
                        active.LastNoResponseUtc = sample.TimeUtc; active.LastSequence = sample.Sequence;
                        active.ObservedDurationMs = (sample.TimeUtc.Ticks - active.StartUtc.Ticks) / TimeSpan.TicksPerMillisecond;
                    }
                    previous = sample;
                }
                if (previous == null) Gap(gaps, record.StartedUtc, record.EndedUtc, "NotMeasured", null, null);
                else if (previous.Sequence < record.RequestedCount) Gap(gaps, previous.TimeUtc, record.EndedUtc, "TrailingSamplesMissing", previous.Sequence, null);
                foreach (MonitoringNoResponseInterval interval in intervals)
                {
                    foreach (NetworkChangeEvent item in record.Events) if (item.Kind == "SettingChanged" && Nearby(item.TimeUtc, interval)) interval.NearbyConfigurationCount++;
                    foreach (WifiStabilitySample wifi in wifiSamples) if (ReadableWifi(wifi) && Nearby(wifi.TimeUtc, interval)) interval.NearbyWifiCount++;
                }
                targetSummary.Intervals = intervals.ToArray(); targetSummary.Gaps = gaps.ToArray();
                summary.TotalIntervals += intervals.Count; summary.GapCount += gaps.Count; result.Add(targetSummary);
            }
            summary.Targets = result.ToArray();
            return summary;
        }
    }
}
