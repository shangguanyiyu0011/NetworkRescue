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
                MonitoringHistoryBridgeChecks();
                MonitoringDisplayProjectionChecks();
                MonitoringReplayGapChecks();
                MonitoringSummaryPreviewChecks();
                TrayLifecycleChecks();
                MonitoringHtmlSelectionChecks();
                MonitoringPersistenceFailureChecks();
                WifiMonitoringBridgeChecks();
                MonitoringCaptureCancellationChecks();
                SiteCommandChecks();
                UpdateCommandChecks();
                WorkspaceCommandChecks();
                HealthSnapshotGuardChecks();
                AutomaticHealthSnapshotChecks();
                HealthComparisonBudgetChecks();
                ThemeCommandChecks();
                SerializationBoundaryChecks();
                MonitoringCombinedMessageBudgetChecks();
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
            Check(NativeController.DisplayVersion == "1.3.0", "Controller display version matches 1.3.0");
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
        private static void MonitoringHistoryBridgeChecks()
        {
            var target = new MonitorTarget { Id = "history-target", Host = "example.test", Method = "https", UseSystemProxy = false };
            var session = new NativeController.MonitoringSession(new[] { target }) { StartedUtc = DateTime.UtcNow.AddMinutes(-20), RequestedCount = 600 };
            session.Changes.Record("MonitoringStarted", session.StartedUtc);
            for (int i = 1; i <= 600; i++)
            {
                bool success = i != 5;
                var sample = new MonitorSample { TargetId = target.Id, TargetHost = target.Host, Sequence = i, Sent = i, Received = i - (i >= 5 ? 1 : 0),
                    TimeUtc = session.StartedUtc.AddSeconds(i), Success = success, ResponseReceived = success, HttpSuccess = success,
                    HttpStatusCode = success ? (int?)200 : null, Method = "HTTPS", Route = "Direct", Status = success ? "Success" : "TimedOut", RttMs = success ? (long?)3 : null };
                session.Append(sample); session.Changes.ObserveSample(sample);
            }
            MonitoringHistoryRecord record = NativeController.CaptureMonitoringRecord(session, "Running");
            Check(record.Samples.Length == 600 && session.Samples[target.Id].Count == 300, "Persistent archive preserves early samples beyond the live chart window");
            Check(record.Events.Length == record.TotalEvents && record.EndedUtc >= record.Samples.Last().TimeUtc, "Checkpoint atomically captures event total and a timestamp covering its samples");
            NetworkChangeEvent outage = record.Events.Single(delegate(NetworkChangeEvent change) { return change.Kind == "TargetNoResponse"; });
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            Dictionary<string, object> replay = serializer.Deserialize<Dictionary<string, object>>(serializer.Serialize(NativeController.BuildMonitoringReplay(record, outage.Sequence)));
            Dictionary<string, object> first = ((System.Collections.IEnumerable)replay["series"]).Cast<Dictionary<string, object>>().First();
            Dictionary<string, object>[] points = ((System.Collections.IEnumerable)first["samples"]).Cast<Dictionary<string, object>>().ToArray();
            Check(points.Length > 0 && points.Any(delegate(Dictionary<string, object> point) { return (int)point["Sequence"] == 5; }), "History replay retrieves an actual early outage point outside the live 300-point cache");
            Check((bool)replay["recovered"] && Convert.ToInt64(replay["durationMs"]) == 0, "A single failed probe has zero observed duration even when the following probe recovers");
            string exported = NativeController.BuildMonitoringRecordText(record, delegate(string key) { return key; });
            Check(exported.Contains(" · 5 · ") && exported.Contains(" · 600 · ") && exported.Contains("monitorEventTargetNoResponse"), "Historical export includes early and recent real samples with the event timeline");
            bool rejected = false;
            try { NativeController.BuildMonitoringReplay(record, Int32.MaxValue); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Replay rejects a missing event instead of manufacturing an observation");
        }

        private static Dictionary<string, object> Packet(object value)
        {
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = NativeController.MaximumUiJsonLength, RecursionLimit = 32 };
            return serializer.Deserialize<Dictionary<string, object>>(serializer.Serialize(value));
        }
        private static Dictionary<string, object>[] Rows(object value) { return ((System.Collections.IEnumerable)value).Cast<Dictionary<string, object>>().ToArray(); }
        private static MonitorSample DisplaySample(MonitorTarget target, DateTime start, int sequence, bool response)
        {
            return new MonitorSample { TargetId = target.Id, TargetHost = target.Host, Method = target.Method == "icmp" ? "ICMP" : "HTTPS", Route = target.Method == "icmp" ? "Network" : "Direct",
                Sequence = sequence, TimeUtc = start.AddSeconds(sequence), Sent = sequence, Received = sequence - 1, Success = response, ResponseReceived = target.Method == "https" && response,
                HttpSuccess = target.Method == "https" && response, HttpStatusCode = target.Method == "https" && response ? (int?)200 : null,
                Status = response ? "Success" : "TimedOut", RttMs = response ? (long?)(sequence * 10) : null, LossPercent = 100.0 / sequence, Min = 1, Max = 900, Avg = 400, JitterMs = 700 };
        }
        private static void MonitoringDisplayProjectionChecks()
        {
            DateTime start = DateTime.UtcNow.AddMinutes(-10); var target = new MonitorTarget { Id = "retained", Host = "retained.example.test", Method = "icmp" };
            MonitorTarget[] targets = { target }; MonitorSample[] raw = { DisplaySample(target, start, 1, true), DisplaySample(target, start, 3, true) };
            raw[0].Received = 1; raw[0].LossPercent = 0;
            MonitorSample[] shown = NativeController.BuildMonitoringDisplaySamples(raw, 1000);
            Check(shown.Length == 2 && shown[0].Sequence == 1 && shown[1].Sequence == 3 && shown[1].Sent == 2 && shown[1].Received == 2 && shown[1].LossPercent == 0, "Dropped stale attempt leaves retained sequence identities and a measured two of two denominator");
            Check(shown[1].Min == 10 && shown[1].Avg == 20 && shown[1].Max == 30 && shown[1].JitterMs == null, "Display RTT statistics are rebuilt and jitter never crosses a sequence gap");
            Check(!Object.ReferenceEquals(shown[0], raw[0]) && raw[1].Sent == 3 && raw[1].Received == 2 && raw[1].Avg == 400 && raw[1].JitterMs == 700, "Display projection is detached and preserves raw producer evidence");
            shown[0].Status = "changed-view";
            Check(raw[0].Status == "Success", "A display mutation cannot rewrite the archived sample");
            var record = new MonitoringHistoryRecord { Id = Guid.NewGuid().ToString("N"), State = "Completed", StartedUtc = start, EndedUtc = start.AddSeconds(4), IntervalMs = 1000, RequestedCount = 3, Targets = targets, Samples = raw };
            MonitoringTargetSummary summary = MonitoringSummaryBuilder.Build(record).Targets.Single();
            MonitorSummary final = NativeController.BuildMonitoringFinal(targets, raw, 1000, record.StartedUtc, record.EndedUtc).Targets.Single();
            Check(summary.SampleCount == 2 && summary.ResponseCount == 2 && final.Sent == summary.SampleCount && final.Received == summary.ResponseCount && final.LossPercent == 0 && final.Status == "Responding" && final.JitterMs == null, "Ended final counters and summary share retained observation denominator");
            Dictionary<string, object>[] series = Rows(Packet(new { series = NativeController.BuildMonitoringDisplaySeries(targets, raw, 1000) })["series"]);
            Dictionary<string, object> latest = Rows(series.Single()["samples"]).Last();
            Check((int)latest["Sequence"] == 3 && (int)latest["Sent"] == summary.SampleCount && (int)latest["Received"] == summary.ResponseCount && Convert.ToDouble(latest["LossPercent"]) == 0, "Historical and ended chart series share the same observed denominator");
            var session = new NativeController.MonitoringSession(targets) { StartedUtc = record.StartedUtc, EndedUtc = record.EndedUtc, RequestedCount = 3 };
            foreach (MonitorSample sample in raw) session.Append(sample);
            string report = NativeController.BuildMonitoringText(session, delegate(string key) { return key; });
            Check(report.Contains("monitorSent: 2/2") && !report.Contains("monitorSent: 2/3"), "Monitoring text never reintroduces the removed attempt into its denominator");
            string historyText = NativeController.BuildMonitoringRecordText(record, delegate(string key) { return key; });
            Check(historyText.Contains("monitorSent: 2/2") && !historyText.Contains("monitorSent: 2/3"), "Historical text uses the same observed counts as the historical summary");
            MonitorSample[] empty = new MonitorSample[0];
            final = NativeController.BuildMonitoringFinal(targets, empty, 1000, start, start.AddSeconds(4)).Targets.Single();
            Check(final.Status == "NotMeasured" && final.Sent == 0 && final.Received == 0 && !final.Success && !final.ResponseReceived && !final.HttpSuccess && final.RttMs == null && final.ErrorCode == null && final.LossPercent == 0, "An empty target is not measured with zero counts and no invented response RTT or error");
            var producer = new MultiMonitorSummary { Targets = new[] { new MonitorSummary { TargetId = target.Id, Host = target.Host, Sent = 0, ErrorCode = "ResolveHostNotFound", Status = "Unknown", Method = "ICMP", Route = "Network" } } };
            final = NativeController.BuildMonitoringFinal(targets, empty, 1000, start, start.AddSeconds(4), producer).Targets.Single();
            Check(final.Sent == 0 && final.Received == 0 && !final.Success && final.RttMs == null && final.ErrorCode == "ResolveHostNotFound", "DNS resolution failure before any producer attempt survives an empty retained sample set");
            Check(producer.Targets[0].ErrorCode == "ResolveHostNotFound" && producer.Targets[0].Sent == 0, "Final summary preserves producer evidence without mutating the producer graph");
            producer.Targets[0].Sent = 2; producer.Targets[0].ErrorCode = "PingTimeout"; producer.Targets[0].RttMs = 999;
            final = NativeController.BuildMonitoringFinal(targets, empty, 1000, start, start.AddSeconds(4), producer).Targets.Single();
            Check(final.Status == "NotMeasured" && final.Sent == 0 && final.Received == 0 && !final.Success && final.RttMs == null && final.ErrorCode == null, "Producer attempts discarded across a stale boundary cannot invent measured results");
            producer.Targets[0].Sent = 0; producer.Targets[0].ErrorCode = "PingUnavailable";
            final = NativeController.BuildMonitoringFinal(targets, empty, 1000, start, start.AddSeconds(4), producer).Targets.Single();
            Check(final.ErrorCode == null && final.Status == "NotMeasured", "Only explicit pre-attempt Resolve errors are preserved from an empty producer result");
            record.Samples = empty; summary = MonitoringSummaryBuilder.Build(record).Targets.Single();
            Check(summary.SampleCount == 0 && summary.ResponseCount == 0 && summary.Intervals.Length == 0 && summary.Gaps.Single().Reason == "NotMeasured", "Empty target summary remains unknown rather than manufacturing an outage");
            MonitorSample lifecycle = DisplaySample(target, start, 1, true); lifecycle.Status = "Suspended"; lifecycle.ErrorCode = "OldError";
            final = NativeController.BuildMonitoringFinal(targets, new[] { lifecycle }, 1000, start, start.AddSeconds(4)).Targets.Single();
            Check(final.Status == "NotMeasured" && final.Sent == 0 && final.RttMs == null && final.ErrorCode == null && !final.Success, "Unmeasured lifecycle markers cannot create successful or failed probes");
            MonitorSample[] many = Enumerable.Range(1, 301).Select(delegate(int i) { return DisplaySample(target, start, i, i != 1); }).ToArray();
            series = Rows(Packet(new { series = NativeController.BuildMonitoringDisplaySeries(targets, many, 1000) })["series"]);
            Dictionary<string, object>[] retained = Rows(series.Single()["samples"]);
            Check(retained.Length == 300 && (int)retained[0]["Sequence"] == 2 && (int)retained[0]["Sent"] == 2 && (int)retained[0]["Received"] == 1 && (int)retained.Last()["Sent"] == 301 && (int)retained.Last()["Received"] == 300, "Chart statistics use the full archive before taking the final 300 samples");
        }

        private static void MonitoringReplayGapChecks()
        {
            DateTime start = DateTime.UtcNow.AddMinutes(-5); var target = new MonitorTarget { Id = "gap-replay", Host = "gap.example.test", Method = "icmp" };
            var record = new MonitoringHistoryRecord { Id = Guid.NewGuid().ToString("N"), State = "Completed", StartedUtc = start, EndedUtc = start.AddSeconds(30), IntervalMs = 1000, RequestedCount = 3,
                Targets = new[] { target }, Samples = new[] { DisplaySample(target, start, 1, false), DisplaySample(target, start, 3, true) }, TotalEvents = 2,
                Events = new[] { new NetworkChangeEvent { Sequence = 1, TimeUtc = start.AddSeconds(1), Kind = "TargetNoResponse", TargetId = target.Id, TargetHost = target.Host }, new NetworkChangeEvent { Sequence = 2, TimeUtc = start.AddSeconds(3), Kind = "TargetRecovered", TargetId = target.Id, TargetHost = target.Host } } };
            Dictionary<string, object> replay = Packet(NativeController.BuildMonitoringReplay(record, 1));
            Check(replay["durationMs"] != null && Convert.ToInt64(replay["durationMs"]) == 0 && !(bool)replay["recovered"], "A later recovery event across a sampling gap cannot bridge or lengthen the observed interval");
            record.Samples = new[] { DisplaySample(target, start, 1, false), DisplaySample(target, start, 2, false) };
            replay = Packet(NativeController.BuildMonitoringReplay(record, 1));
            Check(Convert.ToInt64(replay["durationMs"]) == 1000 && !(bool)replay["recovered"], "An open two-probe interval spans only its real failed sample endpoints and never session end");
            record.Samples = new MonitorSample[0]; replay = Packet(NativeController.BuildMonitoringReplay(record, 1));
            Check(replay["durationMs"] == null && !(bool)replay["recovered"], "An event without an associated real sample has unknown duration rather than fabricated outage evidence");
        }

        private static void MonitoringSummaryPreviewChecks()
        {
            DateTime start = DateTime.UtcNow.AddHours(-2); MonitorTarget[] targets = Enumerable.Range(1, 4).Select(delegate(int i) { return new MonitorTarget { Id = "preview-" + i, Host = "preview" + i + ".example.test", Method = "icmp" }; }).ToArray();
            var record = new MonitoringHistoryRecord { Id = Guid.NewGuid().ToString("N"), State = "Completed", StartedUtc = start, EndedUtc = start.AddSeconds(3601), IntervalMs = 1000, RequestedCount = 3600, Targets = targets };
            record.Samples = targets.SelectMany(delegate(MonitorTarget target) { return Enumerable.Range(1, 3600).Select(delegate(int i) { return DisplaySample(target, start, i, i % 2 == 0); }); }).ToArray();
            Dictionary<string, object> preview = Packet(NativeController.BuildMonitoringSummaryPreview(record)); Dictionary<string, object>[] rows = Rows(preview["Targets"]);
            Check((int)preview["TotalIntervals"] == 7200 && (bool)preview["Truncated"] && rows.Sum(delegate(Dictionary<string, object> row) { return Rows(row["Intervals"]).Length; }) == 200, "A 7200 interval summary computes full totals while previewing at most 200 intervals across all targets");
            Check(rows.Sum(delegate(Dictionary<string, object> row) { return (int)row["TotalIntervals"]; }) == 7200 && rows.All(delegate(Dictionary<string, object> row) { return (int)row["SampleCount"] == 3600 && (int)row["ResponseCount"] == 1800; }), "Preview caps never shrink per-target sample counts or interval totals");
            record.Samples = targets.SelectMany(delegate(MonitorTarget target) { return Enumerable.Range(0, 1800).Select(delegate(int i) { return DisplaySample(target, start, 2 * i + 1, true); }); }).ToArray();
            preview = Packet(NativeController.BuildMonitoringSummaryPreview(record)); rows = Rows(preview["Targets"]);
            Check((int)preview["GapCount"] == 7200 && (int)preview["TotalIntervals"] == 0 && (bool)preview["Truncated"] && rows.Sum(delegate(Dictionary<string, object> row) { return Rows(row["Gaps"]).Length; }) == 200, "Gap preview shares a global 200 item bound and retains all 7200 missing-observation gaps in its totals");
        }

        private static void TrayLifecycleChecks()
        {
            Check(NativeController.ShouldHideOnClose(true, "monitor", false), "Only an enabled active monitor can hide on normal close");
            foreach (string operation in new[] { (string)null, "repair", "diagnose", "speed", "site" }) Check(!NativeController.ShouldHideOnClose(true, operation, false), "Closing a non-monitor operation cannot unexpectedly hide: " + operation);
            Check(!NativeController.ShouldHideOnClose(false, "monitor", false) && !NativeController.ShouldHideOnClose(true, "monitor", true), "Disabled tray and explicit exit never hide the monitor");
            foreach (string command in new[] { "setTray", "setTrayMuted", "hideToTray" }) Check(NativeController.AllowsTrayCommand(command, "monitor") && !NativeController.AllowsTrayCommand(command, "repair"), "Only monitor-safe tray command bypasses busy guard: " + command);
            Check(NativeController.AllowsTrayCommand("exitApplication", "repair") && !NativeController.AllowsTrayCommand("scan", "monitor") && !NativeController.AllowsTrayCommand("exportMonitoringHtml", "monitor"), "Exit reaches normal close handling while unrelated commands retain busy guards");
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic; NativeController controller = UninitializedController();
            var tracker = new MonitorAlertTracker(TimeSpan.Zero, TimeSpan.Zero); tracker.BeginSession(new[] { "alert" }, 1000);
            typeof(NativeController).GetField("alertGate", flags).SetValue(controller, new object()); typeof(NativeController).GetField("alerts", flags).SetValue(controller, tracker);
            typeof(NativeController).GetField("trayEnabled", flags).SetValue(controller, true); typeof(NativeController).GetField("notificationEpoch", flags).SetValue(controller, 7L);
            DateTime floor = DateTime.UtcNow; typeof(NativeController).GetField("notificationFloorUtc", flags).SetValue(controller, floor);
            MethodInfo deliver = typeof(NativeController).GetMethod("IsAlertDeliveryCurrent", flags), current = typeof(NativeController).GetMethod("IsObservationCurrent", flags), power = typeof(NativeController).GetMethod("OnPowerModeChanged", flags);
            Check((bool)deliver.Invoke(controller, new object[] { 7L, floor }) && !(bool)deliver.Invoke(controller, new object[] { 6L, floor }) && !(bool)deliver.Invoke(controller, new object[] { 7L, floor.AddTicks(-1) }), "Queued alerts require both the current epoch and post-resume observation floor");
            Check(!(bool)current.Invoke(controller, new object[] { floor.AddTicks(-1) }) && (bool)current.Invoke(controller, new object[] { floor }), "Old in-flight observations are filtered independently of alert delivery");
            var target = new MonitorTarget { Id = "alert", Host = "alert.example.test", Method = "icmp" };
            tracker.Observe(DisplaySample(target, floor.AddSeconds(-3), 1, false)); tracker.Observe(DisplaySample(target, floor.AddSeconds(-3), 2, false));
            power.Invoke(controller, new object[] { null, new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Suspend) });
            DateTime resumed = (DateTime)typeof(NativeController).GetField("notificationFloorUtc", flags).GetValue(controller);
            Check((long)typeof(NativeController).GetField("notificationEpoch", flags).GetValue(controller) == 8 && resumed >= floor && !(bool)deliver.Invoke(controller, new object[] { 7L, resumed.AddSeconds(1) }), "Suspend invalidates queued delivery and advances its observation floor");
            Check(tracker.Observe(DisplaySample(target, resumed, 1, false)) == null && tracker.Observe(DisplaySample(target, resumed, 2, false)) == null && tracker.Observe(DisplaySample(target, resumed, 3, false)) != null, "Power mode change clears consecutive failure counts before monitoring continues");
            power.Invoke(controller, new object[] { null, new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Resume) });
            Check(tracker.Observe(DisplaySample(target, resumed.AddSeconds(4), 1, true)) == null && tracker.Observe(DisplaySample(target, resumed.AddSeconds(4), 2, true)) == null, "Resume clears prior outage pairing so it cannot invent a recovery notification");
            long epoch = (long)typeof(NativeController).GetField("notificationEpoch", flags).GetValue(controller);
            typeof(NativeController).GetMethod("SetTrayMuted", flags).Invoke(controller, new object[] { true });
            Check((long)typeof(NativeController).GetField("notificationEpoch", flags).GetValue(controller) == epoch + 1 && !(bool)deliver.Invoke(controller, new object[] { epoch, DateTime.UtcNow.AddSeconds(1) }), "Muting invalidates already queued alert callbacks");
            var ending = (System.Windows.SessionEndingCancelEventArgs)FormatterServices.GetUninitializedObject(typeof(System.Windows.SessionEndingCancelEventArgs));
            using (var source = new CancellationTokenSource())
            {
                typeof(NativeController).GetField("operation", flags).SetValue(controller, "repair"); typeof(NativeController).GetField("cancellation", flags).SetValue(controller, source);
                typeof(NativeController).GetMethod("OnSessionEnding", flags).Invoke(controller, new object[] { null, ending });
                Check(!ending.Cancel && !source.IsCancellationRequested && (bool)typeof(NativeController).GetField("sessionEnding", flags).GetValue(controller) && (bool)typeof(NativeController).GetField("exitRequested", flags).GetValue(controller), "Windows session end remains unblocked and does not cancel an active repair write token");
                typeof(NativeController).GetField("operation", flags).SetValue(controller, "monitor");
                typeof(NativeController).GetMethod("OnSessionEnding", flags).Invoke(controller, new object[] { null, ending });
                Check(source.IsCancellationRequested && !ending.Cancel, "Windows session end cancels read-only monitoring without vetoing logoff");
            }
        }

        private static void MonitoringHtmlSelectionChecks()
        {
            NativeController controller = UninitializedController(); BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(NativeController).GetField("mode", flags).SetValue(controller, "advanced");
            var selected = new MonitoringHistoryRecord { Id = Guid.NewGuid().ToString("N") }; typeof(NativeController).GetField("selectedMonitoringHistory", flags).SetValue(controller, selected);
            bool rejected = false;
            try { ((Task)typeof(NativeController).GetMethod("Dispatch", flags).Invoke(controller, new object[] { new Dictionary<string, object> { { "command", "exportMonitoringHtml" }, { "id", Guid.NewGuid().ToString("N") } } })).GetAwaiter().GetResult(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && typeof(NativeController).GetField("operation", flags).GetValue(controller) == null, "HTML export rejects a stale selected history id before report generation or any native dialog");
        }

        private static void MonitoringPersistenceFailureChecks()
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "monitor-save-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string blocked = Path.Combine(directory, "blocked");
            File.WriteAllText(blocked, "keep");
            try
            {
                NativeController controller = UninitializedController();
                FieldInfo store = typeof(NativeController).GetField("monitoringHistory", flags);
                MethodInfo checkpoint = typeof(NativeController).GetMethod("SaveMonitoringCheckpoint", flags);
                store.SetValue(controller, new MonitoringHistoryStore(blocked));
                var target = new MonitorTarget { Id = "safe", Host = "example.test", Method = "https" };
                var session = new NativeController.MonitoringSession(new[] { target });
                checkpoint.Invoke(controller, new object[] { session, "Running" });
                Check(session.HistorySaveFailed && File.ReadAllText(blocked) == "keep", "Unavailable history path degrades without touching existing files or the absent closed view");
                checkpoint.Invoke(controller, new object[] { session, "Stopped" });
                checkpoint.Invoke(controller, new object[] { session, "Running" });
                Check(session.HistoryState == "Stopped", "Late checkpoint cannot revive a terminal session even when storage failed");
                session = new NativeController.MonitoringSession(new[] { target });
                string corrupted = Path.Combine(directory, session.HistoryId + ".nrh");
                File.WriteAllText(corrupted, "corrupt-preserve");
                store.SetValue(controller, new MonitoringHistoryStore(directory));
                try
                {
                    checkpoint.Invoke(controller, new object[] { session, "Running" });
                    Check(session.HistorySaveFailed && File.ReadAllText(corrupted) == "corrupt-preserve", "Unreadable checkpoint is preserved and cannot abort monitoring or normal close");
                }
                finally { File.Delete(corrupted); }
            }
            finally { File.Delete(blocked); Directory.Delete(directory); }
        }

        private static void WifiMonitoringBridgeChecks()
        {
            var target = new MonitorTarget { Id = "wifi-target", Host = "example.test", Method = "icmp" };
            var session = new NativeController.MonitoringSession(new[] { target }) { StartedUtc = DateTime.UtcNow.AddMinutes(-20), EndedUtc = DateTime.UtcNow };
            string interfaceId = Guid.NewGuid().ToString("D");
            for (int i = 0; i < 725; i++)
            {
                session.WifiSamples.Enqueue(new WifiStabilitySample { TimeUtc = session.StartedUtc.AddSeconds(i), StateCode = "Available",
                    Interfaces = new[] { new WifiLinkSample { InterfaceId = interfaceId, State = 1, Readability = "Available", SignalPercent = i % 101, RxKbps = 54000, TxKbps = 36000 } } });
                if (session.WifiSamples.Count > 720) session.WifiSamples.Dequeue();
                session.WifiTotalSamples++;
            }
            session.Changes.ObserveSample(new MonitorSample { TargetId = target.Id, TargetHost = target.Host, Method = "ICMP", TimeUtc = session.StartedUtc.AddSeconds(100), Success = false });
            MonitoringHistoryRecord record = NativeController.CaptureMonitoringRecord(session, "Completed");
            Check(record.WifiCollectionEnabled && record.WifiSamples.Length == 720 && record.WifiTotalSamples == 725 && record.WifiTruncated, "Monitoring records retain recent Wi-Fi evidence with an explicit complete observation count");
            WifiStabilityReader.Validate(record.WifiSamples);
            session.WifiSamples.Peek().Interfaces[0].SignalPercent = 100;
            Check(record.WifiSamples[0].Interfaces[0].SignalPercent == 5, "Checkpoint freezes Wi-Fi evidence instead of sharing mutable live fields");
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            var replay = serializer.Deserialize<Dictionary<string, object>>(serializer.Serialize(NativeController.BuildMonitoringReplay(record, 1)));
            object[] wifi = ((System.Collections.IEnumerable)replay["wifiSamples"]).Cast<object>().ToArray();
            Check(wifi.Length == 21 && (bool)replay["wifiSupported"] && (bool)replay["wifiTruncated"], "Replay returns only actual Wi-Fi observations within ten seconds of the selected event");
            string exported = NativeController.BuildMonitoringRecordText(record, delegate(string key) { return key; });
            Check(exported.Contains("wifiStabilityTitle") && exported.Contains("wifiTruncated") && exported.Contains("54.0 Mbps") && !exported.Contains(interfaceId), "Export includes real Wi-Fi rates and truncation without exposing stable adapter identities");
            using (var release = new ManualResetEventSlim(false))
            using (var started = new ManualResetEventSlim(false))
            using (var finished = new ManualResetEventSlim(false))
            using (var cancel = new CancellationTokenSource())
            {
                int readers = 0;
                Task watch = NativeController.ObserveWifiAsync(session, null, cancel.Token, delegate
                {
                    Interlocked.Increment(ref readers); started.Set(); release.Wait(); finished.Set();
                    return new WifiStabilitySample { TimeUtc = DateTime.UtcNow, StateCode = "NoWirelessInterface" };
                });
                try
                {
                    Check(started.Wait(1500), "Wi-Fi observer starts reads off the controller thread");
                    var clock = System.Diagnostics.Stopwatch.StartNew(); cancel.Cancel(); bool stopped = false;
                    try { watch.GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
                    Check(stopped && clock.ElapsedMilliseconds < 1000 && readers == 1, "Stopping monitoring does not wait for or multiply a stalled Wi-Fi native read");
                }
                finally { release.Set(); Check(finished.Wait(1500), "Fixture releases its stalled Wi-Fi reader"); }
            }
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
            string guardedDirectory = Path.Combine(Path.GetTempPath(), "NetworkRescueUpdateSource-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(guardedDirectory, "fixture blocks persistence before any network request");
            typeof(NativeController).GetField("settingsRoot", flags).SetValue(controller, guardedDirectory);
            try
            {
                foreach (string repository in new[] { (string)null, "", " \t ", " custom-owner/project " })
                {
                    bool blocked = false;
                    try { ((Task)typeof(NativeController).GetMethod("Dispatch", flags).Invoke(controller, new object[] { new Dictionary<string, object> { { "command", "checkUpdates" }, { "repository", repository } } })).GetAwaiter().GetResult(); }
                    catch (IOException) { blocked = true; }
                    Check(blocked && (string)typeof(NativeController).GetField("updateRepository", flags).GetValue(controller) == (String.IsNullOrWhiteSpace(repository) ? UpdateService.DefaultRepository : "custom-owner/project"), "Explicit blank checks use official source and custom checks retain their source before persistence");
                    Check(typeof(NativeController).GetField("operation", flags).GetValue(controller) == null, "Failed settings persistence prevents the update network request");
                }
            }
            finally { File.Delete(guardedDirectory); }
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
        private static void MonitoringCombinedMessageBudgetChecks()
        {
            DateTime start = DateTime.UtcNow.AddMinutes(-20);
            string domain = new string('a', 63) + "." + new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 61);
            MonitorTarget[] targets = Enumerable.Range(1, 4).Select(delegate(int i) { return new MonitorTarget { Id = new string('t', 31) + i, Host = domain, Method = "icmp" }; }).ToArray();
            MonitoringHistoryRecord record = new MonitoringHistoryRecord { Id = Guid.NewGuid().ToString("N"), State = "Completed", StartedUtc = start, EndedUtc = DateTime.UtcNow,
                IntervalMs = 1000, RequestedCount = 300, Targets = targets, TotalEvents = 500, WifiCollectionEnabled = true, WifiTotalSamples = 720 };
            record.Samples = targets.SelectMany(delegate(MonitorTarget target) { return Enumerable.Range(1, 300).Select(delegate(int i) { return new MonitorSample {
                TargetId = target.Id, TargetHost = target.Host, Method = "ICMP", Route = "Network", Sequence = i, Sent = i, Received = i, Success = true,
                TimeUtc = start.AddSeconds(i), RttMs = 86400000, Min = 86400000, Avg = 86400000, Max = 86400000, JitterMs = 86400000,
                Status = new string('S', 96), ErrorCode = new string('E', 96), ResolvedAddress = "2001:ffff:ffff:ffff:ffff:ffff:ffff:c000" }; }); }).ToArray();
            string before = String.Join(", ", Enumerable.Range(0, 50).Select(delegate(int i) { return "2001:ffff:ffff:ffff:ffff:ffff:ffff:" + (0xc000 + i).ToString("x4"); }));
            string after = String.Join(", ", Enumerable.Range(0, 50).Select(delegate(int i) { return "2001:ffff:ffff:ffff:ffff:ffff:ffff:" + (0xd000 + i).ToString("x4"); }));
            record.Events = Enumerable.Range(1, 500).Select(delegate(int i) { return new NetworkChangeEvent { Sequence = i, TimeUtc = start.AddSeconds(i), Kind = "SettingChanged", Setting = "Dns",
                TargetId = Guid.NewGuid().ToString("D"), AdapterName = new string((char)39, 256), Before = before, After = after }; }).ToArray();
            record.WifiSamples = Enumerable.Range(0, 720).Select(delegate(int i) { return new WifiStabilitySample { TimeUtc = start.AddSeconds(i), StateCode = "Available",
                Interfaces = Enumerable.Range(0, 8).Select(delegate(int j) { return new WifiLinkSample { InterfaceId = Guid.NewGuid().ToString("D"), State = 1, Readability = "Available", SignalPercent = 100, RxKbps = 5400000, TxKbps = 5400000 }; }).ToArray() }; }).ToArray();
            typeof(MonitoringHistoryStore).GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { record, false });
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = NativeController.MaximumUiJsonLength, RecursionLimit = 32 };
            string packet = serializer.Serialize(new { type = "monitorHistoryLoaded", targets = record.Targets, events = record.Events, wifiSamples = record.WifiSamples, summary = NativeController.BuildMonitoringSummaryPreview(record),
                series = targets.Select(delegate(MonitorTarget target) { return new { TargetId = target.Id, samples = record.Samples.Where(delegate(MonitorSample sample) { return sample.TargetId == target.Id; }).ToArray() }; }).ToArray() });
            Check(packet.Length > 4 * 1024 * 1024 && packet.Length < NativeController.MaximumUiJsonLength && serializer.DeserializeObject(packet) != null,
                "Legal maximum combined monitoring evidence with JSON-escaped adapter names fits the bounded shared UI envelope without truncation");
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
