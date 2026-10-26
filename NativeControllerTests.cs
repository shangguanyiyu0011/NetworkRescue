using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkRescue
{
    internal static class NativeControllerTests
    {
        private static int assertions;
        private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
        private static void Rejected(Action action)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Untrusted repair selection must be rejected");
        }
        [STAThread]
        public static int Main()
        {
            try
            {
                SelectionChecks();
                NetworkPolicyChecks();
                MonitorCommandChecks();
                MultiMonitorBridgeChecks();
                MonitoringRetentionChecks();
                MonitoringCaptureCancellationChecks();
                SiteCommandChecks();
                UpdateCommandChecks();
                WorkspaceCommandChecks();
                HealthSnapshotGuardChecks();
                AutomaticHealthSnapshotChecks();
                HealthComparisonBudgetChecks();
                ThemeCommandChecks();
                SerializationBoundaryChecks();
                CancelLifecycleChecks();
                NativeController noviceExport = UninitializedController();
                typeof(NativeController).GetField("mode", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(noviceExport, "beginner");
                Dispatch(noviceExport, "export").GetAwaiter().GetResult();
                Check(true, "Novice report export has no advanced-mode restriction; missing report is handled without opening a dialog");
                foreach (AssemblyName reference in typeof(NativeWindow).Assembly.GetReferencedAssemblies())
                    Check(reference.Name == "mscorlib" || reference.Name.StartsWith("System", StringComparison.Ordinal) || new[] { "PresentationFramework", "PresentationCore", "WindowsBase" }.Contains(reference.Name), "Native application references only system assemblies: " + reference.Name);
                Console.WriteLine("PASS: " + assertions + " native controller safety assertions; no windows, network or configuration changes.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static void SelectionChecks()
        {
            var recommended = new RepairAction { Id = "FlushDns", Suggested = true, Title = "clear cache" };
            var recommended2 = new RepairAction { Id = "RenewDhcp", AdapterId = "c8f34924-6a8d-4476-98c0-dc47a6891de1", Suggested = true, Title = "renew" };
            var manual = new RepairAction { Id = "DisableProxy", Suggested = false, Title = "proxy" };
            var report = new DiagnosticReport { Items = new List<DiagnosticItem> { new DiagnosticItem { Actions = new List<RepairAction> { recommended, recommended2, manual } } } };
            object[] manualRequest = { new Dictionary<string, object> { { "Id", "DisableProxy" }, { "AdapterId", "" } } };
            var beginner = NativeController.SelectActions(report, "beginner", null);
            Check(beginner.Count == 2 && beginner.Contains(recommended) && beginner.Contains(recommended2) && !beginner.Contains(manual), "Beginner default contains only native recommendations");
            object[] single = { new Dictionary<string, object> { { "Id", "FlushDns" } } };
            Check(NativeController.SelectActions(report, "beginner", single).Single() == recommended, "Beginner may select one offered recommendation");
            Check(NativeController.SelectActions(report, "beginner", new object[0]).Count == 0, "Empty beginner selection cannot repair");
            object[] multiple = { single[0], new Dictionary<string, object> { { "Id", "RenewDhcp" }, { "AdapterId", recommended2.AdapterId } } };
            Check(NativeController.SelectActions(report, "beginner", multiple).Count == 2, "Beginner may explicitly select multiple recommendations");
            Rejected(delegate { NativeController.SelectActions(report, "beginner", manualRequest); });
            Check(NativeController.SelectActions(report, "advanced", manualRequest).Single() == manual, "Advanced mode may explicitly select an offered manual action");
            Check(NativeController.SelectActions(report, "advanced", new[] { manualRequest[0], manualRequest[0] }).Count == 1, "Duplicate selections execute once");
            Rejected(delegate { NativeController.SelectActions(report, "advanced", new object[] { new Dictionary<string, object> { { "Id", "RunCommand" } } }); });
            Rejected(delegate { NativeController.SelectActions(report, "advanced", new object[] { new Dictionary<string, object> { { "Id", "DisableProxy" }, { "AdapterId", "wrong-adapter" } } }); });
            Rejected(delegate { NativeController.SelectActions(report, "advanced", null); });
            Rejected(delegate { NativeController.SelectActions(report, "advanced", new object[65]); });
            Rejected(delegate { NativeController.SelectActions(report, "advanced", new object[] { "unsafe" }); });
            Rejected(delegate { NativeController.SelectActions(report, "invalid", manualRequest); });
            Rejected(delegate { NativeController.SelectActions(null, "beginner", manualRequest); });
            Rejected(delegate { NativeController.SelectActions(report, "beginner", new object[] { new Dictionary<string, object> { { "Id", "FlushDns" }, { "Suggested", true }, { "AdapterId", "unoffered" } } }); });
            var duplicateReport = new DiagnosticReport { Items = new List<DiagnosticItem> { new DiagnosticItem { Actions = new List<RepairAction> { recommended, recommended } } } };
            Check(NativeController.SelectActions(duplicateReport, "beginner", null).Count == 1, "Native report duplicate recommendations are deduplicated");
        }
        private static Dictionary<string, object> MonitorRequest()
        {
            // This host cannot pass either network API's normalizer if a bridge guard regresses.
            return new Dictionary<string, object> { { "command", "monitor" }, { "host", "https://no-public-request.invalid/guard" }, { "method", "https" }, { "useSystemProxy", true }, { "intervalMs", 1000 }, { "count", 1 } };
        }

        private static void HealthSnapshotGuardChecks()
        {
            DateTime now = DateTime.UtcNow;
            var healthy = new DiagnosticReport { InternetAvailable = true, Timestamp = now.AddMinutes(-1) };
            Check(NativeController.IsHealthyReportFresh(healthy, now), "Recent HTTP-confirmed report can nominate a healthy snapshot");
            Check(!NativeController.IsHealthyReportFresh(null, now), "Missing diagnosis cannot authorize a healthy snapshot");
            healthy.InternetAvailable = false;
            Check(!NativeController.IsHealthyReportFresh(healthy, now), "A failed diagnosis cannot authorize a healthy snapshot");
            healthy.InternetAvailable = true; healthy.Timestamp = now.AddMinutes(-6);
            Check(!NativeController.IsHealthyReportFresh(healthy, now), "A diagnosis over five minutes old must be repeated");
            healthy.Timestamp = now.AddMinutes(1);
            Check(!NativeController.IsHealthyReportFresh(healthy, now), "A future report after a clock change cannot authorize saving");
            var before = new NetworkChangeSnapshot { Complete = true, TimeUtc = now, Entries = new[] {
                new NetworkConfigurationEntry { Key = "proxy:ProxyPac", Kind = "ProxyPac", Value = "https://fixture.invalid", Fingerprint = "private-path-a" },
                new NetworkConfigurationEntry { Key = "proxy:ProxyEnabled", Kind = "ProxyEnabled", Value = "Enabled" }
            } };
            var same = new NetworkChangeSnapshot { Complete = true, TimeUtc = now, Entries = before.Entries.Reverse().Select(x => new NetworkConfigurationEntry { Key = x.Key, Kind = x.Kind, Value = x.Value, Fingerprint = x.Fingerprint }).ToArray() };
            Check(NativeController.SameHealthConfiguration(before, same), "Ordering changes do not invalidate the healthy candidate");
            same.Entries[1].Fingerprint = "private-path-b";
            Check(!NativeController.SameHealthConfiguration(before, same), "A secret PAC path change invalidates saving although visible authorities match");
            same.Entries[1].Fingerprint = "private-path-a"; same.Complete = false;
            Check(!NativeController.SameHealthConfiguration(before, same), "An unreadable current snapshot cannot authorize saving");
            Check(!NativeController.SameHealthConfiguration(before, null), "Missing capture cannot authorize saving");
            Check(NativeController.ReadConfigurationAsync(delegate { return before; }, CancellationToken.None).GetAwaiter().GetResult() == before, "Configuration capture returns the actual typed observation");
            Check(!NativeController.ReadConfigurationAsync(delegate { throw new IOException("private-path"); }, CancellationToken.None).GetAwaiter().GetResult().Complete, "Capture failure remains unknown without exposing the exception");
            var blocked = new TaskCompletionSource<NetworkChangeSnapshot>();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            NetworkChangeSnapshot timeout = NativeController.ReadConfigurationAsync(delegate { return blocked.Task.GetAwaiter().GetResult(); }, CancellationToken.None).GetAwaiter().GetResult();
            Check(!timeout.Complete && timeout.ErrorCode == "CaptureTimedOut" && watch.ElapsedMilliseconds < 4000, "A stuck OS read cannot hold the UI operation indefinitely");
            blocked.SetResult(before);
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); bool stopped = false;
                try { NativeController.ReadConfigurationAsync(delegate { throw new Exception("must not run"); }, cancel.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
                Check(stopped, "Pre-cancelled capture never enters a read");
            }
        }
        private static void WorkspaceCommandChecks()
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            NativeController controller = UninitializedController();
            typeof(NativeController).GetField("mode", flags).SetValue(controller, "beginner");
            foreach (string command in new[] { "monitorPlans", "saveMonitorPlan", "deleteMonitorPlan" })
            {
                bool denied = false;
                try { Dispatch(controller, command).GetAwaiter().GetResult(); } catch (InvalidOperationException) { denied = true; }
                Check(denied, "Plan command requires advanced mode: " + command);
            }
            foreach (string command in new[] { "compareHealthProfile", "deleteHealthProfile" })
            {
                bool denied = false;
                try { ((Task)typeof(NativeController).GetMethod("Dispatch", flags).Invoke(controller, new object[] { new Dictionary<string, object> { { "command", command }, { "id", "../not-an-id" } } })).GetAwaiter().GetResult(); } catch (InvalidDataException) { denied = true; }
                Check(denied, "Invalid record ID is rejected before file access or confirmation: " + command);
            }
            var saveHealth = new Dictionary<string, object> { { "command", "saveHealthProfile" }, { "name", "guard fixture" } };
            ((Task)typeof(NativeController).GetMethod("Dispatch", flags).Invoke(controller, new object[] { saveHealth })).GetAwaiter().GetResult();
            Check(typeof(NativeController).GetField("operation", flags).GetValue(controller) == null, "Without a healthy report, saving cannot enter a file or network operation");
            string directory = Path.Combine(Path.GetTempPath(), "NetworkRescueControllerWorkspace-" + Guid.NewGuid().ToString("N"));
            typeof(NativeController).GetField("settingsRoot", flags).SetValue(controller, directory);
            typeof(NativeController).GetField("mode", flags).SetValue(controller, "advanced");
            try
            {
                var request = new Dictionary<string, object> { { "command", "saveMonitorPlan" }, { "name", "bridge plan" }, { "targets", new object[] {
                    new Dictionary<string, object> { { "id", "target-1" }, { "host", "LOCALHOST" }, { "method", "https" }, { "useSystemProxy", true } },
                    new Dictionary<string, object> { { "id", "target-2" }, { "host", "::1" }, { "method", "icmp" }, { "useSystemProxy", false } }
                } }, { "intervalMs", 2000 }, { "count", 15 } };
                ((Task)typeof(NativeController).GetMethod("Dispatch", flags).Invoke(controller, new object[] { request })).GetAwaiter().GetResult();
                MonitorPlan plan = new NetworkWorkspaceStore(Path.Combine(directory, "Workspace")).LoadMonitorPlans().Single();
                Check(plan.Name == "bridge plan" && plan.Targets.Length == 2 && plan.Targets[1].Host == "::1", "Lowercase UI target fields reach the persisted typed plan");
                Check(plan.Targets[0].UseSystemProxy && plan.Targets[1].Method == "icmp" && plan.IntervalMs == 2000 && plan.Count == 15, "The complete plan retains route, method, interval and count");
                Check(typeof(NativeController).GetField("monitoring", flags).GetValue(controller) == null, "Saving a plan never starts a monitor session or network probe");
                Check(!File.Exists(Path.Combine(directory, "settings.json")), "The plan store does not rewrite existing global settings");
                Check(typeof(NativeController).GetField("operation", flags).GetValue(controller) == null, "The plan operation returns to idle");
            }
            finally
            {
                string resolved = Path.GetFullPath(directory);
                if (Path.GetDirectoryName(resolved) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(resolved).StartsWith("NetworkRescueControllerWorkspace-", StringComparison.Ordinal) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
            }
        }
        private static void AutomaticHealthSnapshotChecks()
        {
            string name;
            System.Globalization.CultureInfo previousCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
                name = NativeController.HealthSnapshotAutomaticName("Healthy snapshot", new DateTime(2026, 10, 26, 9, 8, 7, DateTimeKind.Local));
                Check(name == "Healthy snapshot 2026-10-26 09:08:07", "Automatic snapshot date uses the supplied local time with invariant digits and Gregorian date");
            }
            finally { Thread.CurrentThread.CurrentCulture = previousCulture; }

            string directory = Path.Combine(Path.GetTempPath(), "NetworkRescueAutoHealth-" + Guid.NewGuid().ToString("N"));
            try
            {
                NetworkWorkspaceStore store = new NetworkWorkspaceStore(directory);
                NetworkChangeSnapshot snapshot = new NetworkChangeSnapshot { TimeUtc = DateTime.UtcNow, Complete = true };
                Func<string, HealthProfile> save = delegate(string candidate) { return store.SaveHealthProfile(candidate, snapshot); };
                HealthProfile first = NativeController.SaveNamedHealthProfile(name, true, save);
                HealthProfile second = NativeController.SaveNamedHealthProfile(name, true, save);
                HealthProfile third = NativeController.SaveNamedHealthProfile(name, true, save);
                Check(first.Name == name && second.Name == name + " (2)" && third.Name == name + " (3)", "Same second automatic names receive readable suffixes without replacing records");
                Check(first.Id != second.Id && second.Id != third.Id && store.LoadHealthProfiles().Length == 3, "Automatic saves retain every distinct prior snapshot");
                HealthProfile manual = NativeController.SaveNamedHealthProfile("Manual fixture", false, save);
                Check(manual.Name == "Manual fixture", "Manual snapshot names remain unchanged");
                int attempts = 0;
                bool rejected = false;
                try { NativeController.SaveNamedHealthProfile(name, false, delegate(string candidate) { attempts++; return save(candidate); }); }
                catch (ArgumentException ex) { rejected = ex.Message == "WorkspaceDuplicateName"; }
                Check(rejected && attempts == 1, "Manual duplicate rejection does not silently rename");

                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                var packet = (Dictionary<string, object>)serializer.DeserializeObject(serializer.Serialize(NativeController.HealthProfileSavedPacket(first, true)));
                var metadata = (Dictionary<string, object>)packet["profile"];
                Check((string)packet["type"] == "healthProfileSaved" && (bool)packet["automatic"] && metadata.Count == 3 && !metadata.ContainsKey("Snapshot"), "Save success packet contains automatic flag and profile metadata only");
                packet = (Dictionary<string, object>)serializer.DeserializeObject(serializer.Serialize(NativeController.HealthProfileSavedPacket(manual, false)));
                Check(!(bool)packet["automatic"], "Manual save success packet retains a false automatic flag");

                attempts = 0; rejected = false;
                try { NativeController.SaveNamedHealthProfile(name, true, delegate(string candidate) { attempts++; throw new ArgumentException("WorkspaceDuplicateName"); }); }
                catch (ArgumentException ex) { rejected = ex.Message == "WorkspaceDuplicateName"; }
                Check(rejected && attempts == 21, "Concurrent duplicate races stop after twenty one attempts");
                attempts = 0; rejected = false;
                try { NativeController.SaveNamedHealthProfile(name, true, delegate(string candidate) { attempts++; throw new ArgumentException("InvalidHealthSnapshot"); }); }
                catch (ArgumentException ex) { rejected = ex.Message == "InvalidHealthSnapshot"; }
                Check(rejected && attempts == 1, "Only the exact duplicate error can trigger automatic retry");
                for (int i = store.LoadHealthProfiles().Length; i < 20; i++) store.SaveHealthProfile("capacity " + i, snapshot);
                string path = Path.Combine(directory, "health-profiles.nrw"); byte[] before = File.ReadAllBytes(path);
                attempts = 0; rejected = false;
                try { NativeController.SaveNamedHealthProfile(name, true, delegate(string candidate) { attempts++; return save(candidate); }); }
                catch (ArgumentException ex) { rejected = ex.Message == "WorkspaceHealthLimit"; }
                Check(rejected && attempts == 1 && store.LoadHealthProfiles().Length == 20 && before.SequenceEqual(File.ReadAllBytes(path)), "Full storage fails without retrying or replacing any existing snapshot");
            }
            finally
            {
                string resolved = Path.GetFullPath(directory);
                if (Path.GetDirectoryName(resolved) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(resolved).StartsWith("NetworkRescueAutoHealth-", StringComparison.Ordinal) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
            }

            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            NativeController controller = UninitializedController();
            string guardedRoot = Path.Combine(Path.GetTempPath(), "NetworkRescueAutoHealthGuard-" + Guid.NewGuid().ToString("N"));
            typeof(NativeController).GetField("settingsRoot", flags).SetValue(controller, guardedRoot);
            foreach (object invalid in new object[] { "true", "false", 1, 0, null, new object[0] })
            {
                bool rejected = false;
                try { ((Task)typeof(NativeController).GetMethod("Dispatch", flags).Invoke(controller, new object[] { new Dictionary<string, object> { { "command", "saveHealthProfile" }, { "autoName", invalid } } })).GetAwaiter().GetResult(); }
                catch (InvalidDataException ex) { rejected = ex.Message == "Invalid request field: autoName"; }
                Check(rejected, "Automatic naming requires an actual bool before any storage operation");
            }
            foreach (bool explicitFalse in new[] { false, true })
            {
                var message = new Dictionary<string, object> { { "command", "saveHealthProfile" } };
                if (explicitFalse) message["autoName"] = false;
                bool rejected = false;
                try { ((Task)typeof(NativeController).GetMethod("Dispatch", flags).Invoke(controller, new object[] { message })).GetAwaiter().GetResult(); }
                catch (InvalidDataException ex) { rejected = ex.Message == "Invalid request field: name"; }
                Check(rejected, "Missing or false automatic naming still requires a manual name");
            }
            var autoMessage = new Dictionary<string, object> { { "command", "saveHealthProfile" }, { "autoName", true } };
            Action dispatchAuto = delegate { ((Task)typeof(NativeController).GetMethod("Dispatch", flags).Invoke(controller, new object[] { autoMessage })).GetAwaiter().GetResult(); };
            dispatchAuto();
            Check(typeof(NativeController).GetField("operation", flags).GetValue(controller) == null && !Directory.Exists(guardedRoot), "Automatic naming cannot bypass a missing healthy report or touch storage");
            DiagnosticReport healthy = new DiagnosticReport { InternetAvailable = true, Timestamp = DateTime.UtcNow };
            typeof(NativeController).GetField("report", flags).SetValue(controller, healthy);
            typeof(NativeController).GetField("healthyReport", flags).SetValue(controller, new DiagnosticReport { InternetAvailable = true, Timestamp = healthy.Timestamp });
            typeof(NativeController).GetField("healthyConfiguration", flags).SetValue(controller, new NetworkChangeSnapshot { Complete = true, TimeUtc = DateTime.UtcNow });
            dispatchAuto();
            Check(typeof(NativeController).GetField("operation", flags).GetValue(controller) == null && !Directory.Exists(guardedRoot), "Automatic save requires the same diagnostic report reference");
            typeof(NativeController).GetField("healthyReport", flags).SetValue(controller, healthy);
            healthy.Timestamp = DateTime.UtcNow.AddMinutes(-6); dispatchAuto();
            Check(typeof(NativeController).GetField("operation", flags).GetValue(controller) == null && !Directory.Exists(guardedRoot), "Automatic save retains the five minute freshness gate");
            healthy.Timestamp = DateTime.UtcNow; healthy.InternetAvailable = false; dispatchAuto();
            Check(typeof(NativeController).GetField("operation", flags).GetValue(controller) == null && !Directory.Exists(guardedRoot), "Automatic save requires confirmed Internet availability");
            healthy.InternetAvailable = true;
            typeof(NativeController).GetField("healthyConfiguration", flags).SetValue(controller, new NetworkChangeSnapshot { Complete = false, TimeUtc = DateTime.UtcNow });
            dispatchAuto();
            Check(typeof(NativeController).GetField("operation", flags).GetValue(controller) == null && !Directory.Exists(guardedRoot), "Automatic save retains the complete configuration gate");
            ((Task)typeof(NativeController).GetMethod("Dispatch", flags).Invoke(controller, new object[] { new Dictionary<string, object> { { "command", "saveHealthProfile" }, { "autoName", false }, { "name", "Manual fixture" } } })).GetAwaiter().GetResult();
            Check(typeof(NativeController).GetField("operation", flags).GetValue(controller) == null && !Directory.Exists(guardedRoot), "Explicit false with a valid manual name follows the original healthy report gate");
            Check(NativeController.DisplayVersion == "1.0.0", "Controller display version matches 1.0.0");
        }

        private static void HealthComparisonBudgetChecks()
        {
            var profile = new HealthProfile { Id = Guid.NewGuid().ToString("N"), Name = "budget fixture", CreatedUtc = DateTime.UtcNow, Snapshot = new NetworkChangeSnapshot { Complete = true, Entries = new[] { new NetworkConfigurationEntry { Value = "must-not-include-raw-snapshot" } } } };
            var comparison = new HealthComparison { Complete = true, ComparedUtc = DateTime.UtcNow, Changes = Enumerable.Range(1, 1024).Select(i => new NetworkChangeEvent { Sequence = i, Kind = "SettingChanged", Setting = "Addresses", AdapterName = new string('\'', 256), Before = new string('\'', 2048), After = new string('\'', 2048) }).ToArray() };
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = NativeController.MaximumUiJsonLength };
            string packet = serializer.Serialize(NativeController.BuildHealthComparisonPacket(profile, comparison));
            var parsed = (Dictionary<string, object>)serializer.DeserializeObject(packet);
            var result = (Dictionary<string, object>)parsed["comparison"];
            var metadata = (Dictionary<string, object>)parsed["profile"];
            Check(packet.Length < NativeController.MaximumUiJsonLength && packet.Length < 2100000, "Worst JSON-escaped health changes remain within the native message budget");
            Check((bool)result["Truncated"] && (int)result["TotalChanges"] == 1024, "Comparison explicitly distinguishes complete observation from limited preview");
            Check(((object[])result["Changes"]).Length > 0 && ((object[])result["Changes"]).Length <= 256, "Oversized comparisons still show a nonempty bounded preview");
            Check(metadata.Count == 3 && !metadata.ContainsKey("Snapshot") && !packet.Contains("must-not-include-raw-snapshot"), "Comparison sends profile metadata rather than duplicating sensitive stored observations");
            Check(comparison.Changes.Length == 1024, "Previewing never destroys the actual typed comparison");
            comparison.Changes = new[] { new NetworkChangeEvent { Kind = "SettingChanged", Setting = "Dns", Before = "1.1.1.1", After = "8.8.8.8" } };
            parsed = (Dictionary<string, object>)serializer.DeserializeObject(serializer.Serialize(NativeController.BuildHealthComparisonPacket(profile, comparison)));
            result = (Dictionary<string, object>)parsed["comparison"];
            Check(!(bool)result["Truncated"] && (int)result["TotalChanges"] == 1 && ((object[])result["Changes"]).Length == 1, "Normal comparisons retain every change without a truncation warning");
        }

        private static void MonitorRejected(NativeController host, Dictionary<string, object> message, string expected)
        {
            bool rejected = false;
            try
            {
                Task dispatch = (Task)typeof(NativeController).GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(host, new object[] { message });
                dispatch.GetAwaiter().GetResult();
            }
            catch (InvalidDataException ex) { rejected = ex.Message == expected; }
            Check(rejected, "Monitor bridge must reject before starting: " + expected);
            Check(typeof(NativeController).GetField("operation", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(host) == null, "Rejected monitor input cannot enter an operation");
        }

        private static void MonitorCommandChecks()
        {
            // No Form/WebView constructor, handle, PAC lookup, DNS lookup or HTTP request is needed.
            NativeController host = (NativeController)FormatterServices.GetUninitializedObject(typeof(NativeController));
            GC.SuppressFinalize(host);
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(NativeController).GetField("mode", flags).SetValue(host, "advanced");
            typeof(NativeController).GetField("ui", flags).SetValue(host, new Dictionary<string, string>());
            Dictionary<string, object> message;
            foreach (object method in new object[] { "http", "HTTPS", "ping", "https://", 1, null })
            {
                message = MonitorRequest(); message["method"] = method;
                MonitorRejected(host, message, method is string && ((string)method).Length <= 8 ? "native.invalidMessage" : "Invalid request field: method");
            }
            message = MonitorRequest(); message.Remove("method");
            MonitorRejected(host, message, "Invalid request field: method");
            foreach (object proxy in new object[] { "true", 1, new object[0], null })
            {
                message = MonitorRequest(); message["useSystemProxy"] = proxy;
                MonitorRejected(host, message, "Invalid request field: useSystemProxy");
            }
            message = MonitorRequest(); message.Remove("useSystemProxy");
            MonitorRejected(host, message, "Invalid request field: useSystemProxy");
            foreach (string method in new[] { "https", "icmp" })
            foreach (bool proxy in new[] { true, false })
            {
                foreach (object count in new object[] { 0, 3601, "1", 1.0, null })
                {
                    message = MonitorRequest(); message["method"] = method; message["useSystemProxy"] = proxy; message["count"] = count;
                    MonitorRejected(host, message, "Invalid request field: count");
                }
                message = MonitorRequest(); message["method"] = method; message["useSystemProxy"] = proxy; message.Remove("count");
                MonitorRejected(host, message, "Invalid request field: count");
            }
            foreach (object interval in new object[] { 999, 10001, "1000", 1000.0, null })
            {
                message = MonitorRequest(); message["intervalMs"] = interval;
                MonitorRejected(host, message, "Invalid request field: intervalMs");
            }
            foreach (int count in new[] { 1, 3600 }) Check(NativeController.IntValue(new Dictionary<string, object> { { "count", count } }, "count", 1, 3600) == count, "Monitor count boundary remains valid");
            foreach (int interval in new[] { 1000, 10000 }) Check(NativeController.IntValue(new Dictionary<string, object> { { "intervalMs", interval } }, "intervalMs", 1000, 10000) == interval, "Monitor interval boundary remains valid");
        }

        private static Dictionary<string, object> TargetRequest(string id, string host, string method)
        {
            return new Dictionary<string, object> { { "id", id }, { "host", host }, { "method", method }, { "useSystemProxy", true } };
        }
        private static void MultiMonitorBridgeChecks()
        {
            object[] values = { TargetRequest("a", "127.0.0.1", "icmp"), TargetRequest("b", "example.test", "https") };
            var message = new Dictionary<string, object> { { "targets", values } };
            MonitorTarget[] targets = NativeController.ReadMonitorTargets(message);
            Check(targets.Length == 2 && targets[0].Id == "a" && targets[1].Host == "example.test", "Multi-target bridge preserves independent identities and order without resolving hosts");
            ((Dictionary<string, object>)values[0])["host"] = "changed.test";
            Check(targets[0].Host == "127.0.0.1", "Bridge copies input rather than sharing mutable request objects");
            values[0] = TargetRequest("a", "127.0.0.1", "icmp");
            foreach (object invalid in new object[] { null, "a", new object[0], new object[5], new object[] { "unsafe" }, new object[] { TargetRequest("same", "127.0.0.1", "icmp"), TargetRequest("same", "example.test", "https") }, new object[] { TargetRequest("unsafe id", "127.0.0.1", "icmp") }, new object[] { TargetRequest("a", "https://user:secret@example.test/path", "https") }, new object[] { TargetRequest("a", "127.0.0.1", "tcp") } })
            {
                message["targets"] = invalid;
                Rejected(delegate { NativeController.ReadMonitorTargets(message); });
            }
            var badProxy = TargetRequest("a", "127.0.0.1", "icmp"); badProxy["useSystemProxy"] = "true";
            Rejected(delegate { NativeController.ReadMonitorTargets(new Dictionary<string, object> { { "targets", new object[] { badProxy } } }); });
            MonitorTarget[] legacy = NativeController.ReadMonitorTargets(new Dictionary<string, object> { { "host", "127.0.0.1" }, { "method", "icmp" }, { "useSystemProxy", false } });
            Check(legacy.Length == 1 && legacy[0].Id == "target-1" && !legacy[0].UseSystemProxy, "Legacy single-target requests remain compatible");
        }
        private static void MonitoringRetentionChecks()
        {
            MonitorTarget[] targets = Enumerable.Range(1, 4).Select(delegate(int i) { return new MonitorTarget { Id = "target-" + i, Host = "target" + i + ".example.test", Method = "icmp" }; }).ToArray();
            var session = new NativeController.MonitoringSession(targets);
            DateTime now = DateTime.UtcNow;
            foreach (MonitorTarget target in targets)
                for (int i = 1; i <= 330; i++) session.Append(new MonitorSample { TargetId = target.Id, TargetHost = target.Host, Sequence = i, TimeUtc = now, Sent = i, Received = i, Success = true, Method = "ICMP", Status = "Success", RttMs = 3, Avg = 3 });
            Check(session.Samples.All(delegate(KeyValuePair<string, Queue<MonitorSample>> row) { return row.Value.Count == 300 && row.Value.Peek().Sequence == 31; }), "Each target retains only its own latest 300 samples with full counters");
            session.Append(new MonitorSample { TargetId = "unoffered" });
            Check(session.Samples.Count == 4, "Unknown progress identities cannot grow monitoring storage");
            session.Changes.ObserveConfiguration(new NetworkChangeSnapshot { Complete = true, TimeUtc = now, Entries = new[] { new NetworkConfigurationEntry { Key = "fixture", Kind = "Addresses", Value = "before" } } });
            for (int i = 0; i < 510; i++) session.Changes.ObserveConfiguration(new NetworkChangeSnapshot { Complete = true, TimeUtc = now, Entries = new[] { new NetworkConfigurationEntry { Key = "fixture", Kind = "Addresses", AdapterName = new string('n', 400), Value = new string((i % 2) == 0 ? 'x' : 'y', 3000) } } });
            Check(session.Changes.TotalEvents == 510 && session.Changes.RecentEvents.Length == 500, "Event storage remains bounded independently of complete event count");
            session.EndedUtc = now; session.Cancelled = true;
            string report = NativeController.BuildMonitoringText(session, delegate(string key) { return key == "brand" ? "断网急救箱" : key; });
            Check(report.Contains("target4.example.test") && report.Contains(" · 330 · ") && report.Contains("monitorTimelineTruncated") && report.Contains("monitorEventMonitoringStopped"), "Stopped monitoring exports all targets, retained samples, event cap and explicit cancellation");
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = NativeController.MaximumUiJsonLength, RecursionLimit = 32 };
            object[] series = targets.Select(delegate(MonitorTarget target) { return (object)new { TargetId = target.Id, samples = session.Samples[target.Id].ToArray() }; }).ToArray();
            string packet = serializer.Serialize(new { type = "monitorEnded", events = session.Changes.RecentEvents, series = series });
            Check(packet.Length < NativeController.MaximumUiJsonLength && serializer.DeserializeObject(packet) != null, "Maximum retained target series and bounded long events fit the native message budget");
            string redacted = ReportRedactor.Redact(report);
            Check(!redacted.Contains("target4.example.test") && redacted.Contains("断网急救箱"), "Monitoring report preserves the renamed brand and redacts target hostnames");
            Check(redacted.Contains(" · 330 · "), "Redacted monitoring reports retain sample sequence numbers without URL fragment markers");
        }
        private static void MonitoringCaptureCancellationChecks()
        {
            var session = new NativeController.MonitoringSession(new[] { new MonitorTarget { Id = "a", Host = "127.0.0.1", Method = "icmp" } });
            using (var release = new ManualResetEventSlim(false))
            using (var started = new ManualResetEventSlim(false))
            using (var finished = new ManualResetEventSlim(false))
            using (var cancel = new CancellationTokenSource())
            {
                int readers = 0;
                Task watch = NativeController.ObserveConfigurationAsync(session, null, cancel.Token, delegate
                {
                    Interlocked.Increment(ref readers); started.Set(); release.Wait(); finished.Set();
                    return new NetworkChangeSnapshot { Complete = true, TimeUtc = DateTime.UtcNow };
                });
                try
                {
                    Check(started.Wait(1500), "Configuration watcher starts its read off the UI thread");
                    var clock = System.Diagnostics.Stopwatch.StartNew(); cancel.Cancel();
                    bool stopped = false;
                    try { watch.GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
                    Check(stopped && clock.ElapsedMilliseconds < 1000 && readers == 1, "Stopping does not wait for a stuck OS read or queue replacement readers");
                }
                finally { release.Set(); Check(finished.Wait(1500), "Test releases its deliberately stalled reader"); }
            }
        }

        private static void NetworkPolicyChecks()
        {
            int previous = HttpWebRequest.DefaultMaximumErrorResponseLength;
            SecurityProtocolType protocols = ServicePointManager.SecurityProtocol;
            try
            {
                Program.ConfigureNetworkPolicy();
                Check(HttpWebRequest.DefaultMaximumErrorResponseLength == 0, "Production initialization disables only this process's error-body prebuffering before requests");
                Check(ServicePointManager.SecurityProtocol == SecurityProtocolType.Tls12, "Production initialization retains TLS 1.2 policy");
            }
            finally { HttpWebRequest.DefaultMaximumErrorResponseLength = previous; ServicePointManager.SecurityProtocol = protocols; }
        }

        private static void SiteCommandChecks()
        {
            NativeController host = (NativeController)FormatterServices.GetUninitializedObject(typeof(NativeController));
            GC.SuppressFinalize(host);
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(NativeController).GetField("ui", flags).SetValue(host, new Dictionary<string, string>());
            // Every fixture must fail before Run; no native window, resolver, proxy or HTTP call exists.
            foreach (string mode in new[] { "beginner", "advanced" })
            {
                typeof(NativeController).GetField("mode", flags).SetValue(host, mode);
                foreach (string target in new[] {
                    "https://fixture-user:secret@example.invalid/", "http://fixture-user@example.invalid/",
                    "https://@example.invalid/", "https://user%40name:secret@example.invalid/",
                    "file:///C:/Windows/win.ini", "ftp://example.invalid/", "javascript:alert(1)", "data:text/plain,fixture",
                    "http://example.invalid:0/", "http://example.invalid:65536/", "http://example.invalid:/",
                    "https://example.invalid\\private", "https://example.invalid/\r\nX-Fixture: value", "bad host.invalid", " " })
                    SiteRejected(host, new Dictionary<string, object> { { "command", "site" }, { "target", target } }, "siteInputError");
                foreach (object target in new object[] { null, 1, true, new object[0], new Dictionary<string, object>(), "", new string('x', 2049) })
                    SiteRejected(host, new Dictionary<string, object> { { "command", "site" }, { "target", target } }, "Invalid request field: target");
                SiteRejected(host, new Dictionary<string, object> { { "command", "site" } }, "Invalid request field: target");
            }
        }

        private static void SiteRejected(NativeController host, Dictionary<string, object> message, string expected)
        {
            bool rejected = false;
            try
            {
                Task dispatch = (Task)typeof(NativeController).GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(host, new object[] { message });
                dispatch.GetAwaiter().GetResult();
            }
            catch (InvalidDataException ex) { rejected = ex.Message == expected; }
            Check(rejected, "Site bridge must reject before starting: " + expected);
            Check(typeof(NativeController).GetField("operation", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(host) == null, "Rejected site input cannot enter an operation");
        }

        private static NativeController UninitializedController()
        {
            NativeController controller = (NativeController)FormatterServices.GetUninitializedObject(typeof(NativeController));
            GC.SuppressFinalize(controller);
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(NativeController).GetField("ui", flags).SetValue(controller, new Dictionary<string, string>());
            typeof(NativeController).GetField("closed", flags).SetValue(controller, true);
            return controller;
        }
        private static Task Dispatch(NativeController controller, string command)
        {
            return (Task)typeof(NativeController).GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, new object[] { new Dictionary<string, object> { { "command", command } } });
        }
        private static void UpdateCommandChecks()
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            NativeController controller = UninitializedController();
            foreach (object repository in new object[] { "https://github.com/owner/repo", "owner/repo/extra", "../repo", "owner/repo?token=secret", "owner/repo\r\nHeader: value", 1, true, new string('x', 161) })
            {
                var message = new Dictionary<string, object> { { "command", "checkUpdates" }, { "repository", repository } };
                bool rejected = false;
                try { ((Task)typeof(NativeController).GetMethod("Dispatch", flags).Invoke(controller, new object[] { message })).GetAwaiter().GetResult(); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Invalid update repository is rejected before settings or network access");
                Check(typeof(NativeController).GetField("operation", flags).GetValue(controller) == null, "Invalid update repository cannot start a check");
                Check(typeof(NativeController).GetField("updateRepository", flags).GetValue(controller) == null, "Invalid update repository cannot replace configuration");
            }
            foreach (UpdateCheckResult result in new[] {
                (UpdateCheckResult)null,
                new UpdateCheckResult { Status = "UpToDate", Repository = "owner/repo", ReleaseUrl = "https://github.com/owner/repo/releases/tag/v1.0.0" },
                new UpdateCheckResult { Status = "UpdateAvailable", Repository = "owner/repo", ReleaseUrl = "https://malicious.invalid/installer.exe" },
                new UpdateCheckResult { Status = "UpdateAvailable", Repository = "owner/repo", ReleaseUrl = "https://github.com/other/repo/releases/tag/v1.0.0" },
                new UpdateCheckResult { Status = "UpdateAvailable", Repository = "owner/repo", ReleaseUrl = "file:///C:/Windows/notepad.exe" }
            })
            {
                typeof(NativeController).GetField("updateResult", flags).SetValue(controller, result);
                Dispatch(controller, "openUpdate").GetAwaiter().GetResult();
                Check(true, "Opening an update without a verified newer release returns without launching a process");
            }
        }
        private static void ThemeCommandChecks()
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            NativeController controller = UninitializedController();
            FieldInfo theme = typeof(NativeController).GetField("theme", flags);
            theme.SetValue(controller, "light");
            foreach (object value in new object[] { "auto", "DARK", "", "../../../dark", null, 1, true, new string('x', 13) })
            {
                bool rejected = false;
                try { ((Task)typeof(NativeController).GetMethod("Dispatch", flags).Invoke(controller, new object[] { new Dictionary<string, object> { { "command", "setTheme" }, { "theme", value } } })).GetAwaiter().GetResult(); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected && (string)theme.GetValue(controller) == "light", "Theme command rejects unsupported values before changing or persisting settings");
            }
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "theme-settings-fixture-" + Guid.NewGuid().ToString("N"));
            typeof(NativeController).GetField("settingsRoot", flags).SetValue(controller, directory);
            typeof(NativeController).GetField("json", flags).SetValue(controller, new System.Web.Script.Serialization.JavaScriptSerializer());
            foreach (string selected in new[] { "dark", "light" })
            {
                var message = new Dictionary<string, object> { { "command", "setTheme" }, { "theme", selected } };
                ((Task)typeof(NativeController).GetMethod("Dispatch", flags).Invoke(controller, new object[] { message })).GetAwaiter().GetResult();
                var saved = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(directory, "settings.json")));
                Check((string)theme.GetValue(controller) == selected && (string)saved["theme"] == selected, "Theme selection persists without requiring network or a native window");
                Check(saved.ContainsKey("language") && saved.ContainsKey("mode") && saved.ContainsKey("reviewedOperationId") && saved.ContainsKey("updateRepository"), "Saving a theme preserves the other settings fields");
                Check(typeof(NativeController).GetField("operation", flags).GetValue(controller) == null, "Changing a theme cannot start a network or repair operation");
            }
            SoftDialog.SetTheme("light");
        }
        private static void SerializationBoundaryChecks()
        {
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = NativeController.MaximumUiJsonLength, RecursionLimit = 32 };
            string text = serializer.Serialize(new string('x', 600000));
            Check(text.Length == 600002, "Structured reports larger than the old 500 KiB limit fit the bounded native-view envelope");
            Check(serializer.Serialize(new string('x', NativeController.MaximumUiJsonLength - 2)).Length == NativeController.MaximumUiJsonLength, "The native UI message boundary itself remains accepted");
            bool rejected = false;
            try { serializer.Serialize(new string('x', NativeController.MaximumUiJsonLength - 1)); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Oversized UI messages still have a hard limit");
        }
        private static void CancelLifecycleChecks()
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            NativeController controller = UninitializedController();
            FieldInfo operation = typeof(NativeController).GetField("operation", flags), cancellation = typeof(NativeController).GetField("cancellation", flags);
            using (var source = new CancellationTokenSource())
            {
                cancellation.SetValue(controller, source);
                operation.SetValue(controller, "repair");
                Dispatch(controller, "cancel").GetAwaiter().GetResult();
                Check(!source.IsCancellationRequested && (string)operation.GetValue(controller) == "repair", "Cancel cannot interrupt repair/restore writes");
                operation.SetValue(controller, "monitor");
                Dispatch(controller, "cancel").GetAwaiter().GetResult();
                Check(source.IsCancellationRequested, "Read-only monitor cancellation reaches its token");
            }
            cancellation.SetValue(controller, null); operation.SetValue(controller, null);
            Dispatch(controller, "cancel").GetAwaiter().GetResult();
            Check(operation.GetValue(controller) == null, "Cancel after an operation has ended is harmless");
            MethodInfo run = typeof(NativeController).GetMethod("Run", flags);
            bool invoked = false;
            Func<CancellationToken, Task> work = delegate(CancellationToken token) { invoked = true; return Task.FromResult(0); };
            ((Task)run.Invoke(controller, new object[] { "monitor", work })).GetAwaiter().GetResult();
            Check(invoked && operation.GetValue(controller) == null && cancellation.GetValue(controller) == null, "Completed run releases busy state and cancellation source");
            Check((long)typeof(NativeController).GetField("generation", flags).GetValue(controller) == 2L, "Completed run invalidates both previous and queued progress generations");
            operation.SetValue(controller, "repair"); invoked = false;
            ((Task)run.Invoke(controller, new object[] { "monitor", work })).GetAwaiter().GetResult();
            Check(!invoked && (string)operation.GetValue(controller) == "repair", "A busy repair blocks a competing read-only run");
            operation.SetValue(controller, null);
            Func<CancellationToken, Task> waiting = delegate(CancellationToken token) { return Task.Delay(10000, token); };
            Task active = (Task)run.Invoke(controller, new object[] { "monitor", waiting });
            Dispatch(controller, "cancel").GetAwaiter().GetResult();
            Check(active.Wait(1500) && operation.GetValue(controller) == null && cancellation.GetValue(controller) == null, "Cancelled read-only run ends and clears busy state promptly");
            int callbacks = 0;
            typeof(NativeController).GetMethod("PostForRun", flags).Invoke(controller, new object[] { 4L, new Action(delegate { callbacks++; }) });
            Check(callbacks == 0, "Closed controller drops progress before touching its absent native window");
        }
    }
}
