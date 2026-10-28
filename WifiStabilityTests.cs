using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Xml.Serialization;

namespace NetworkRescue
{
    internal static class WifiStabilityTests
    {
        private static int count;
        private static readonly Guid Id = new Guid("1d7de567-4996-470a-87af-ce22b1d69c30");
        private static readonly DateTime Time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static void Check(bool condition, string message) { count++; if (!condition) throw new Exception(message); }
        private static void Invalid(WifiStabilitySample[] value, string message)
        {
            bool rejected = false;
            try { WifiStabilityReader.Validate(value); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, message);
        }
        private static WifiEvidence Link(uint state, bool attributes, uint signal)
        {
            return new WifiEvidence { Id = Id, State = state, AttributesRead = attributes, Signal = signal, RxKbps = 866700, TxKbps = 433300,
                Name = "PRIVATE-ADAPTER-NAME", Ssid = "PRIVATE-SSID-NAME" };
        }
        private static WifiInsight Insight(params WifiEvidence[] links)
        {
            return new WifiInsight { Readable = true, Total = links.Length, Interfaces = links.ToList() };
        }
        private static WifiStabilitySample Map(WifiInsight source)
        {
            WifiStabilitySample value = WifiStabilityReader.Map(source, Time);
            WifiStabilityReader.Validate(new[] { value });
            return value;
        }
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--native-readonly")
                {
                    WifiStabilitySample native = WifiStabilityReader.Capture();
                    WifiStabilityReader.Validate(new[] { native });
                    Console.WriteLine("WLAN current-connection read-only: state=" + native.StateCode + "; interfaces=" + native.Interfaces.Length + "; readable=" + native.Interfaces.Count(x => x.Readability == "Available") + "; error=" + native.ErrorCode + "; nativeError=" + native.NativeErrorCode);
                    return 0;
                }
                WifiStabilitySample sample = Map(Insight());
                Check(sample.StateCode == "NoWirelessInterface" && sample.Interfaces.Length == 0, "A successful empty enumeration means no WLAN interface");
                sample = Map(null);
                Check(sample.StateCode == "Unavailable" && sample.ErrorCode == "ReadUnavailable", "Null evidence remains unknown");
                sample = Map(new WifiInsight { Error = "5" });
                Check(sample.StateCode == "Unavailable" && sample.NativeErrorCode == 5 && sample.ErrorCode == "NativeError", "Access-denied native read is unavailable, not disconnected");
                sample = Map(new WifiInsight { Error = "1062" });
                Check(sample.NativeErrorCode == 1062, "Native service error is retained without starting the service");
                foreach (string code in new[] { "Timeout", "UnauthorizedAccessException", "SecurityException", "DllNotFoundException", "EntryPointNotFoundException", "BadImageFormatException", "InvalidDataException", "Arbitrary secret exception text" })
                {
                    sample = Map(new WifiInsight { Error = code });
                    Check(sample.StateCode == "Unavailable" && sample.ErrorCode != "Arbitrary secret exception text", "Errors use bounded safe codes");
                }
                sample = Map(Insight(Link(4, false, 0)));
                Check(sample.StateCode == "NotConnected" && sample.Interfaces[0].SignalPercent == null && sample.Interfaces[0].RxKbps == null, "Disconnected means absent metrics, not zero signal");
                sample = Map(Insight(Link(1, true, 0)));
                Check(sample.StateCode == "Available" && sample.Interfaces[0].SignalPercent == 0 && sample.Interfaces[0].RxKbps == 866700 && sample.Interfaces[0].InterfaceId == Id.ToString("D"), "A readable real zero is retained alongside canonical interface ID and link rates");
                sample = Map(Insight(Link(1, true, 100)));
                Check(sample.Interfaces[0].SignalPercent == 100, "Signal upper boundary is accepted");
                WifiEvidence denied = Link(1, false, 0); denied.QueryError = 5;
                sample = Map(Insight(denied));
                Check(sample.StateCode == "Unavailable" && sample.Interfaces[0].State == 1 && sample.Interfaces[0].QueryError == 5 && !sample.Interfaces[0].SignalPercent.HasValue, "Connected but denied attribute read is never a disconnect or zero signal");
                sample = Map(Insight(Link(1, true, 101)));
                Check(sample.StateCode == "Unavailable" && sample.ErrorCode == "InvalidSnapshot" && !sample.Interfaces[0].RxKbps.HasValue, "Invalid signal removes the whole attribute set");
                sample = Map(Insight(Link(1, false, 70)));
                Check(sample.StateCode == "Unavailable" && sample.ErrorCode == "AttributesUnavailable", "Unqueried attributes remain absent");
                WifiEvidence valid = Link(1, true, 82); valid.Id = Guid.NewGuid();
                sample = Map(Insight(denied, valid));
                Check(sample.StateCode == "Partial" && sample.Interfaces.Length == 2 && sample.Interfaces.Count(x => x.SignalPercent.HasValue) == 1, "Partial readability preserves independent good evidence");
                WifiEvidence down = Link(4, false, 0); down.Id = Guid.NewGuid();
                sample = Map(Insight(down, valid));
                Check(sample.StateCode == "Available" && sample.Interfaces.Length == 2, "An unused disconnected interface does not invalidate the connected interface");
                WifiInsight many = new WifiInsight { Readable = true, Total = 12 };
                for (int i = 0; i < 12; i++) { WifiEvidence entry = Link(1, true, (uint)i); entry.Id = Guid.NewGuid(); many.Interfaces.Add(entry); }
                sample = Map(many);
                Check(sample.StateCode == "Partial" && sample.ErrorCode == "InterfaceLimit" && sample.Interfaces.Length == 8, "Each snapshot is bounded to eight interfaces");
                sample = Map(new WifiInsight { Readable = true, Total = 1 });
                Check(sample.StateCode == "Unavailable", "Missing interface data does not become an empty successful enumeration");
                foreach (WifiEvidence malformed in new[] { null, Link(8, true, 50), new WifiEvidence { Id = Guid.Empty } })
                {
                    sample = Map(Insight(malformed));
                    Check(sample.StateCode == "Unavailable" && sample.ErrorCode == "InvalidSnapshot", "Malformed native rows cannot enter persistence");
                }
                sample = Map(Insight(valid, valid));
                Check(sample.StateCode == "Partial" && sample.Interfaces.Length == 1, "Duplicate native interface IDs are rejected");
                sample = Map(Insight(Link(1, true, 69)));
                WifiStabilitySample copy = WifiStabilityReader.Clone(sample);
                sample.Interfaces[0].SignalPercent = 1; sample.Interfaces[0].InterfaceId = "mutated"; sample.Interfaces = new WifiLinkSample[0];
                Check(copy.Interfaces.Length == 1 && copy.Interfaces[0].SignalPercent == 69 && copy.Interfaces[0].InterfaceId == Id.ToString("D"), "Clones share neither links nor arrays with the capture result");
                WifiStabilitySample[] copies = WifiStabilityReader.Clone(new[] { copy });
                copies[0].Interfaces[0].SignalPercent = 3;
                Check(copy.Interfaces[0].SignalPercent == 69, "History array cloning is deep");
                string xml;
                using (StringWriter writer = new StringWriter()) { new XmlSerializer(typeof(WifiStabilitySample)).Serialize(writer, copy); xml = writer.ToString(); }
                Check(!xml.Contains("PRIVATE-SSID-NAME") && !xml.Contains("PRIVATE-ADAPTER-NAME") && !xml.Contains("Bssid"), "Persistent DTO cannot leak SSID, BSSID or actual adapter names");
                foreach (Type type in new[] { typeof(WifiStabilitySample), typeof(WifiLinkSample) })
                    Check(!type.GetFields(BindingFlags.Instance | BindingFlags.Public).Any(x => x.Name.IndexOf("ssid", StringComparison.OrdinalIgnoreCase) >= 0 || x.Name.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0), "DTO surface has no network identity fields");
                using (StringReader reader = new StringReader(xml)) { WifiStabilitySample roundTrip = (WifiStabilitySample)new XmlSerializer(typeof(WifiStabilitySample)).Deserialize(reader); WifiStabilityReader.Validate(new[] { roundTrip }); Check(roundTrip.Interfaces[0].SignalPercent == 69, "XML history round trip preserves nullable metrics"); }
                Invalid(null, "Null history sample arrays are rejected");
                Invalid(new WifiStabilitySample[721], "History bounds reject more than 720 snapshots");
                Invalid(new WifiStabilitySample[] { null }, "Null records are rejected");
                sample = WifiStabilityReader.Clone(copy); sample.TimeUtc = DateTime.SpecifyKind(Time, DateTimeKind.Local); Invalid(new[] { sample }, "Non-UTC evidence is rejected");
                sample = WifiStabilityReader.Clone(copy); sample.TimeUtc = Time.AddSeconds(-1); Invalid(new[] { copy, sample }, "Out-of-order evidence is rejected");
                sample = WifiStabilityReader.Clone(copy); sample.StateCode = "Disconnected"; Invalid(new[] { sample }, "Unknown state codes are rejected");
                sample = WifiStabilityReader.Clone(copy); sample.ErrorCode = "private-error"; Invalid(new[] { sample }, "Arbitrary persisted text cannot hide in an error code");
                sample = WifiStabilityReader.Clone(copy); sample.Interfaces[0].SignalPercent = -1; Invalid(new[] { sample }, "Negative signal is rejected");
                sample = WifiStabilityReader.Clone(copy); sample.Interfaces[0].RxKbps = null; Invalid(new[] { sample }, "Available attributes cannot be half populated");
                sample = WifiStabilityReader.Clone(copy); sample.Interfaces[0].Readability = "NotConnected"; Invalid(new[] { sample }, "Status cannot mislabel a connected interface as disconnected");
                sample = WifiStabilityReader.Clone(copy); sample.Interfaces[0].InterfaceId = "PRIVATE-ADAPTER"; Invalid(new[] { sample }, "Only canonical Guid IDs may persist");
                sample = WifiStabilityReader.Clone(copy); sample.NativeErrorCode = 5; Invalid(new[] { sample }, "Native errors must have the typed error state");
                using (ManualResetEvent release = new ManualResetEvent(false))
                using (ManualResetEvent finished = new ManualResetEvent(false))
                {
                    int reads = 0;
                    Stopwatch watch = Stopwatch.StartNew();
                    sample = WifiStabilityReader.Capture(delegate { Interlocked.Increment(ref reads); release.WaitOne(); finished.Set(); return Insight(Link(1, true, 12)); }, 30);
                    Check(sample.StateCode == "Unavailable" && sample.ErrorCode == "Timeout" && watch.ElapsedMilliseconds < 2000, "A slow read returns bounded unavailable evidence");
                    WifiStabilityReader.Validate(new[] { sample });
                    sample = WifiStabilityReader.Capture(delegate { Interlocked.Increment(ref reads); return Insight(); }, 30);
                    Check(sample.ErrorCode == "Busy" && reads == 1, "Repeated captures cannot stack up more native workers after a timeout");
                    release.Set();
                    Check(finished.WaitOne(2000), "The old worker can finish independently");
                    // The completion signal precedes the capture's finally by a few instructions.
                    for (int i = 0; i < 100; i++)
                    {
                        sample = WifiStabilityReader.Capture(delegate { return Insight(Link(1, true, 88)); }, 500);
                        if (sample.ErrorCode != "Busy") break;
                        Thread.Sleep(10);
                    }
                    Check(sample.StateCode == "Available" && sample.Interfaces[0].SignalPercent == 88, "Late old results are discarded, never relabelled as a current sample");
                }
                sample = WifiStabilityReader.Capture(delegate { throw new InvalidOperationException("private exception content"); }, 500);
                Check(sample.StateCode == "Unavailable" && sample.ErrorCode == "ReadUnavailable", "Reader faults return safe bounded errors");
                Console.WriteLine("Wi-Fi stability checks passed: " + count);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
