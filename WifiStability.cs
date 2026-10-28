using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkRescue
{
    // Persistence DTOs deliberately have no network name, BSSID or adapter description.
    // Capture and Clone always create a detached graph; callers own and synchronize that graph.
    public sealed class WifiStabilitySample
    {
        public DateTime TimeUtc;
        public string StateCode, ErrorCode;
        public uint? NativeErrorCode;
        public WifiLinkSample[] Interfaces = new WifiLinkSample[0];
    }

    public sealed class WifiLinkSample
    {
        public string InterfaceId, Readability;
        public uint State;
        public int? SignalPercent;
        // These are WLAN connection receive/transmit link rates, not measured throughput.
        public uint? RxKbps, TxKbps, QueryError;
    }

    public static class WifiStabilityReader
    {
        public const int MaximumInterfaces = 8, MaximumSamples = 720, CaptureTimeoutMs = 2500;
        private static int reading;

        private static WifiStabilitySample Empty(DateTime time, string error, uint? native)
        {
            return new WifiStabilitySample { TimeUtc = time, StateCode = "Unavailable", ErrorCode = error, NativeErrorCode = native };
        }

        /// <summary>Reads only existing WLAN current-connection attributes. Does not scan or change permissions.</summary>
        public static WifiStabilitySample Capture()
        {
            return Capture(DiagnosticInsights.ReadWifi, CaptureTimeoutMs);
        }

        internal static WifiStabilitySample Capture(Func<WifiInsight> reader, int timeoutMs)
        {
            // A timed out native call cannot be cancelled. Do not accumulate more workers behind it.
            if (Interlocked.CompareExchange(ref reading, 1, 0) != 0) return Empty(DateTime.UtcNow, "Busy", null);
            Task<WifiInsight> work;
            try
            {
                work = Task.Run(delegate
                {
                    try { return reader(); }
                    finally { Interlocked.Exchange(ref reading, 0); }
                });
            }
            catch (Exception) { Interlocked.Exchange(ref reading, 0); return Empty(DateTime.UtcNow, "ReadUnavailable", null); }
            try
            {
                if (!work.Wait(timeoutMs))
                {
                    work.ContinueWith(delegate(Task<WifiInsight> failed) { var ignored = failed.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    return Empty(DateTime.UtcNow, "Timeout", null);
                }
                return Map(work.Result, DateTime.UtcNow);
            }
            catch (Exception) { return Empty(DateTime.UtcNow, "ReadUnavailable", null); }
        }

        private static string Error(string value, out uint? native)
        {
            native = null;
            if (String.IsNullOrEmpty(value)) return null;
            uint number;
            if (UInt32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number != 0)
            {
                native = number;
                return "NativeError";
            }
            if (value == "Timeout") return "Timeout";
            if (value == "UnauthorizedAccessException" || value == "SecurityException") return "AccessDenied";
            if (value == "DllNotFoundException" || value == "EntryPointNotFoundException" || value == "BadImageFormatException") return "ApiUnavailable";
            if (value == "InvalidDataException") return "InvalidSnapshot";
            return "ReadUnavailable";
        }

        internal static WifiStabilitySample Map(WifiInsight source) { return Map(source, DateTime.UtcNow); }

        internal static WifiStabilitySample Map(WifiInsight source, DateTime timeUtc)
        {
            if (source == null) return Empty(timeUtc, "ReadUnavailable", null);
            uint? native;
            string error = Error(source.Error, out native);
            if (source.Total < 0 || source.Total > 32 || source.Interfaces == null || source.Interfaces.Count > source.Total)
                return Empty(timeUtc, "InvalidSnapshot", null);
            List<WifiLinkSample> links = new List<WifiLinkSample>();
            HashSet<Guid> ids = new HashSet<Guid>();
            bool incomplete = !source.Readable || error != null || source.Total > MaximumInterfaces || source.Interfaces.Count < Math.Min(source.Total, MaximumInterfaces);
            int available = 0, disconnected = 0;
            for (int i = 0; i < Math.Min(source.Interfaces.Count, MaximumInterfaces); i++)
            {
                WifiEvidence entry = source.Interfaces[i];
                if (entry == null || entry.Id == Guid.Empty || entry.State > 7 || !ids.Add(entry.Id))
                {
                    incomplete = true;
                    if (error == null) error = "InvalidSnapshot";
                    continue;
                }
                WifiLinkSample link = new WifiLinkSample { InterfaceId = entry.Id.ToString("D"), State = entry.State };
                if (entry.State != 1)
                {
                    link.Readability = "NotConnected";
                    disconnected++;
                }
                else if (entry.AttributesRead && entry.QueryError == 0 && entry.Signal <= 100)
                {
                    link.Readability = "Available";
                    link.SignalPercent = (int)entry.Signal;
                    link.RxKbps = entry.RxKbps;
                    link.TxKbps = entry.TxKbps;
                    available++;
                }
                else
                {
                    link.Readability = "Unavailable";
                    incomplete = true;
                    if (entry.QueryError != 0)
                    {
                        link.QueryError = entry.QueryError;
                        if (!native.HasValue) native = entry.QueryError;
                        error = "NativeError";
                    }
                    else if (error == null) error = entry.Signal > 100 ? "InvalidSnapshot" : "AttributesUnavailable";
                }
                links.Add(link);
            }
            string state;
            if (links.Count == 0) state = !incomplete && source.Total == 0 ? "NoWirelessInterface" : "Unavailable";
            else if (incomplete) state = available + disconnected > 0 ? "Partial" : "Unavailable";
            else state = available > 0 ? "Available" : "NotConnected";
            if (state == "Partial" && error == null) error = source.Total > MaximumInterfaces ? "InterfaceLimit" : "IncompleteSnapshot";
            if (state == "Unavailable" && error == null) error = "ReadUnavailable";
            return new WifiStabilitySample { TimeUtc = timeUtc, StateCode = state, ErrorCode = error, NativeErrorCode = native, Interfaces = links.ToArray() };
        }

        public static WifiStabilitySample Clone(WifiStabilitySample source)
        {
            if (source == null) return null;
            WifiStabilitySample copy = new WifiStabilitySample { TimeUtc = source.TimeUtc, StateCode = source.StateCode, ErrorCode = source.ErrorCode, NativeErrorCode = source.NativeErrorCode };
            if (source.Interfaces == null) { copy.Interfaces = null; return copy; }
            copy.Interfaces = new WifiLinkSample[source.Interfaces.Length];
            for (int i = 0; i < copy.Interfaces.Length; i++)
            {
                WifiLinkSample link = source.Interfaces[i];
                if (link != null) copy.Interfaces[i] = new WifiLinkSample { InterfaceId = link.InterfaceId, State = link.State, Readability = link.Readability,
                    SignalPercent = link.SignalPercent, RxKbps = link.RxKbps, TxKbps = link.TxKbps, QueryError = link.QueryError };
            }
            return copy;
        }

        public static WifiStabilitySample[] Clone(WifiStabilitySample[] source)
        {
            if (source == null) return null;
            WifiStabilitySample[] copy = new WifiStabilitySample[source.Length];
            for (int i = 0; i < copy.Length; i++) copy[i] = Clone(source[i]);
            return copy;
        }

        private static void Require(bool condition) { if (!condition) throw new InvalidDataException("InvalidWifiStabilitySample"); }
        private static bool OneOf(string value, params string[] allowed) { return Array.IndexOf(allowed, value) >= 0; }

        /// <summary>Validates persisted evidence without interpreting missing/denied reads as disconnections.</summary>
        public static void Validate(WifiStabilitySample[] samples)
        {
            Require(samples != null && samples.Length <= MaximumSamples);
            DateTime last = DateTime.MinValue;
            DateTime latest = DateTime.UtcNow.AddDays(1);
            foreach (WifiStabilitySample sample in samples)
            {
                Require(sample != null && sample.TimeUtc.Kind == DateTimeKind.Utc && sample.TimeUtc.Year >= 2000 && sample.TimeUtc <= latest && sample.TimeUtc >= last);
                last = sample.TimeUtc;
                Require(OneOf(sample.StateCode, "NoWirelessInterface", "NotConnected", "Unavailable", "Partial", "Available"));
                Require(sample.ErrorCode == null || OneOf(sample.ErrorCode, "Busy", "Timeout", "NativeError", "AccessDenied", "ApiUnavailable", "InvalidSnapshot", "ReadUnavailable", "AttributesUnavailable", "InterfaceLimit", "IncompleteSnapshot"));
                Require(!sample.NativeErrorCode.HasValue || sample.NativeErrorCode.Value > 0);
                Require((sample.ErrorCode == "NativeError") == sample.NativeErrorCode.HasValue);
                Require(sample.Interfaces != null && sample.Interfaces.Length <= MaximumInterfaces);
                int available = 0, disconnected = 0, unknown = 0;
                HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (WifiLinkSample link in sample.Interfaces)
                {
                    Guid id;
                    Require(link != null && Guid.TryParseExact(link.InterfaceId, "D", out id) && id != Guid.Empty && id.ToString("D") == link.InterfaceId && ids.Add(link.InterfaceId));
                    Require(link.State <= 7 && OneOf(link.Readability, "Available", "NotConnected", "Unavailable"));
                    if (link.Readability == "Available")
                    {
                        Require(link.State == 1 && link.SignalPercent.HasValue && link.SignalPercent.Value >= 0 && link.SignalPercent.Value <= 100 && link.RxKbps.HasValue && link.TxKbps.HasValue && !link.QueryError.HasValue);
                        available++;
                    }
                    else
                    {
                        Require(!link.SignalPercent.HasValue && !link.RxKbps.HasValue && !link.TxKbps.HasValue);
                        if (link.Readability == "NotConnected") { Require(link.State != 1 && !link.QueryError.HasValue); disconnected++; }
                        else { Require(link.State == 1 && (!link.QueryError.HasValue || link.QueryError.Value > 0)); unknown++; }
                    }
                }
                if (sample.StateCode == "NoWirelessInterface") Require(sample.Interfaces.Length == 0 && sample.ErrorCode == null);
                if (sample.StateCode == "NotConnected") Require(disconnected > 0 && available == 0 && unknown == 0 && sample.ErrorCode == null);
                if (sample.StateCode == "Available") Require(available > 0 && unknown == 0 && sample.ErrorCode == null);
                if (sample.StateCode == "Partial") Require(available + disconnected > 0 && sample.ErrorCode != null);
                if (sample.StateCode == "Unavailable") Require(available + disconnected == 0 && sample.ErrorCode != null);
            }
        }
    }
}
