using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Windows.Interop;
using IWin32Window = System.Windows.Forms.IWin32Window;

namespace NetworkRescue
{
    internal sealed class NativeController
    {
        private readonly NativeWindow view;
        private readonly RescueEngine engine = new RescueEngine();
        private readonly RepairHistoryStore history = new RepairHistoryStore();
        // Structured evidence can exceed the old limit; keep the sender bounded to the native view's limit.
        internal const int MaximumUiJsonLength = 4 * 1024 * 1024;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = MaximumUiJsonLength, RecursionLimit = 32 };
        private readonly string settingsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkRescue");
        private readonly bool startScan;
        private DiagnosticReport report;
        private RepairHistoryEntry comparison;
        private CancellationTokenSource cancellation;
        private string operation, reviewedOperationId;
        private string mode = "beginner";
        private string theme = "light";
        private Dictionary<string, string> ui = new Dictionary<string, string>();
        private bool ready, closed;
        private long generation;
        private BackupOperationInfo incomplete;
        private SiteDiagnosticReport siteReport;
        private UpdateCheckResult updateResult;
        private string updateRepository = "";
        private MonitoringSession monitoring;
        private NetworkChangeSnapshot healthyConfiguration;
        private DiagnosticReport healthyReport;
        internal const string DisplayVersion = "1.0.0";
        internal sealed class MonitoringSession
        {
            internal readonly object Sync = new object();
            internal readonly MonitorTarget[] Targets;
            internal readonly Dictionary<string, Queue<MonitorSample>> Samples = new Dictionary<string, Queue<MonitorSample>>(StringComparer.Ordinal);
            internal readonly NetworkChangeTracker Changes = new NetworkChangeTracker();
            internal DateTime StartedUtc = DateTime.UtcNow, EndedUtc;
            internal bool Cancelled, Failed;
            internal MultiMonitorSummary Final;
            internal MonitoringSession(MonitorTarget[] targets)
            {
                Targets = targets;
                foreach (MonitorTarget target in targets) Samples.Add(target.Id, new Queue<MonitorSample>());
            }
            internal void Append(MonitorSample sample)
            {
                lock (Sync)
                {
                    Queue<MonitorSample> series;
                    if (!Samples.TryGetValue(sample.TargetId ?? "", out series)) return;
                    series.Enqueue(sample);
                    if (series.Count > 300) series.Dequeue();
                }
            }
        }
        private sealed class WindowOwner : IWin32Window
        {
            public IntPtr Handle { get; set; }
        }
        private IWin32Window DialogOwner { get { return new WindowOwner { Handle = new WindowInteropHelper(view).Handle }; } }

        internal NativeController(NativeWindow window, bool scan, string forcedLanguage)
        {
            view = window; startScan = scan;
            ReadSettings();
            SoftDialog.SetTheme(theme);
            if (forcedLanguage != null) { I18n.SetLanguage(forcedLanguage); ReadUi(); }
            view.CommandRequested += async delegate(string command, Dictionary<string, object> values)
            {
                try
                {
                    Dictionary<string, object> message = values == null ? new Dictionary<string, object>() : new Dictionary<string, object>(values);
                    message["command"] = command;
                    await Dispatch(message);
                }
                catch (OperationCanceledException) { Emit("notice", U("native.cancelled")); }
                catch (Exception ex) { Emit("error", String.Format(U("native.failure"), ex.Message)); }
                finally { if (ready && !closed) Emit(new { type = "busy", operation = operation }); }
            };
            view.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (operation == "repair")
                {
                    e.Cancel = true;
                    SoftDialog.Show(DialogOwner, U("native.operationActive"), U("native.resultTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else if (cancellation != null) cancellation.Cancel();
            };
            view.Closed += delegate { closed = true; if (cancellation != null) cancellation.Cancel(); };
        }
        private async Task Dispatch(Dictionary<string, object> message)
        {
            string command = TextValue(message, "command", 32);
            if (command == "init")
            {
                bool first = !ready;
                ready = true;
                
                SendInit();
                if (report != null) SendReport();
                SendHistory();
                await RefreshRecovery();
                if (first && startScan) await Diagnose();
                return;
            }
            if (command == "cancel") { if (operation != "repair" && cancellation != null) cancellation.Cancel(); return; }
            if (!String.IsNullOrEmpty(operation)) { Emit("notice", U("native.busy")); return; }
            switch (command)
            {
                case "setMode":
                    string newMode = TextValue(message, "mode", 12);
                    if (newMode != "beginner" && newMode != "advanced") throw new InvalidDataException(U("native.invalidMessage"));
                    mode = newMode; SaveSettings(); SendInit(); break;
                case "setLanguage":
                    I18n.SetLanguage(TextValue(message, "language", 12));
                    ReadUi(); SaveSettings(); report = null; siteReport = null; SendInit();
                    Emit(new { type = "report", report = (object)null }); SendHistory(); break;
                case "setTheme":
                    string selectedTheme = TextValue(message, "theme", 12);
                    if (selectedTheme != "light" && selectedTheme != "dark") throw new InvalidDataException(U("native.invalidMessage"));
                    theme = selectedTheme; SoftDialog.SetTheme(theme); SaveSettings(); SendInit(); break;
                case "diagnose": await Diagnose(); break;
                case "repair": await Repair(message); break;
                case "restore": await Restore(); break;
                case "restoreLatest": await RestoreLatest(); break;
                case "restoreIncomplete": await RestoreIncomplete(); break;
                case "reviewIncomplete":
                    if (incomplete != null) { reviewedOperationId = incomplete.OperationId; SaveSettings(); }
                    await RefreshRecovery(); break;
                case "export": Export(); break;
                case "history": SendHistory(); break;
                case "exportHistory": ExportHistory(); break;
                case "healthProfiles": case "saveHealthProfile": case "compareHealthProfile": case "deleteHealthProfile":
                case "monitorPlans": case "saveMonitorPlan": case "deleteMonitorPlan":
                    await Workspace(command, message); break;
                case "exportMonitoring":
                    RequireAdvanced();
                    if (monitoring == null) { Emit("notice", U("native.notReady")); break; }
                    SaveRedactedReport(BuildMonitoringText(monitoring, U)); break;
                case "checkUpdates":
                    string source = OptionalText(message, "repository", 160), normalized;
                    if (source.Length > 0 && !UpdateService.TryNormalizeRepository(source, out normalized)) throw new InvalidDataException(U("updateRepositoryHint"));
                    updateRepository = source.Trim(); SaveSettings(); updateResult = null;
                    await Run("checkUpdates", async delegate(CancellationToken token)
                    {
                        updateResult = await UpdateService.CheckAsync(updateRepository, DisplayVersion, token);
                        Emit(new { type = "updateResult", result = updateResult });
                    }); break;
                case "openUpdate":
                    if (updateResult == null || updateResult.Status != "UpdateAvailable" || !UpdateService.IsAllowedReleaseUrl(updateResult.Repository, updateResult.ReleaseUrl)) { Emit("notice", U("updateNotConfigured")); return; }
                    Process.Start(new ProcessStartInfo(updateResult.ReleaseUrl) { UseShellExecute = true }); break;
                case "site":
                    string siteInput = TextValue(message, "target", 2048);
                    SiteTarget siteTarget; string siteError;
                    if (!SiteDiagnostics.TryNormalizeTarget(siteInput, out siteTarget, out siteError)) throw new InvalidDataException(U("siteInputError"));
                    await Run("site", async delegate(CancellationToken token)
                    {
                        siteReport = null;
                        siteReport = await SiteDiagnostics.RunAsync(siteInput, UiProgress<SiteProbe>(delegate(SiteProbe probe) { Emit(new { type = "siteProbe", probe = probe }); }), token);
                        Emit(new { type = "siteResult", result = siteReport });
                    }); break;
                case "monitor":
                    RequireAdvanced();
                    int interval = IntValue(message, "intervalMs", 1000, 10000), count = IntValue(message, "count", 1, 3600);
                    MonitorTarget[] targets = ReadMonitorTargets(message, U("native.invalidMessage"));
                    await Run("monitor", async delegate(CancellationToken token)
                    {
                        await RunMonitoring(targets, interval, count, token);
                    }); break;
                case "dnscompare":
                    RequireAdvanced();
                    string dnsHost = TextValue(message, "host", 253);
                    object serverValue;
                    object[] serverItems = message.TryGetValue("servers", out serverValue) ? serverValue as object[] : null;
                    if (serverItems == null || serverItems.Length < 1 || serverItems.Length > 4 || serverItems.Any(x => !(x is string) || ((string)x).Length > 64)) throw new InvalidDataException(U("dnsCompareInputError"));
                    string[] servers = serverItems.Cast<string>().ToArray();
                    await Run("dnscompare", async delegate(CancellationToken token)
                    {
                        var results = await NetworkMonitor.CompareDnsAsync(dnsHost, servers, UiProgress<DnsComparisonResult>(delegate(DnsComparisonResult result) { Emit(new { type = "dnsComparison", result = result }); }), token);
                        foreach (var result in results) Emit(new { type = "dnsComparison", result = result });
                    }); break;
                case "speed":
                    bool useProxy = BoolValue(message, "useProxy");
                    await Run("speed", async delegate(CancellationToken token)
                    {
                        var updates = UiProgress<SpeedTestResult>(delegate(SpeedTestResult result) { Emit(new { type = "speed", result = result }); });
                        SpeedTestResult final = await Task.Run(delegate { return new SpeedTest().Run(useProxy, updates, token); });
                        Emit(new { type = "speed", result = final });
                    }); break;
                case "advanced": await Advanced(message); break;
                case "adapters":
                    RequireAdvanced();
                    await Run("advanced", async delegate(CancellationToken token)
                    {
                        AdapterDisplaySnapshot snapshot = await Task.Run(delegate { return ReadAdapterSnapshot(token); });
                        token.ThrowIfCancellationRequested();
                        Emit(new { type = "adapters", text = snapshot.Text, Rows = snapshot.Rows, Timestamp = snapshot.Timestamp });
                    }); break;
                case "connections":
                    RequireAdvanced();
                    await Run("advanced", async delegate(CancellationToken token)
                    {
                        ConnectionProcessSnapshot snapshot = await Task.Run(delegate { token.ThrowIfCancellationRequested(); return ConnectionProcesses.Capture(); });
                        token.ThrowIfCancellationRequested();
                        Emit(new { type = "connections", Rows = snapshot.Rows, Timestamp = snapshot.Timestamp.ToString("o"), TotalRows = snapshot.TotalRows, Truncated = snapshot.Truncated, ErrorCode = snapshot.ErrorCode });
                    }); break;
                default: throw new InvalidDataException(U("native.invalidMessage"));
            }
        }

        private async Task Diagnose()
        {
            report = null; healthyConfiguration = null; healthyReport = null;
            await Run("diagnose", async delegate(CancellationToken token)
            {
                report = await DiagnosticService.DiagnoseAsync(engine, UiProgress<string>(delegate(string text) { Emit("progress", text); }), token);
                if (IsHealthyReportFresh(report, DateTime.UtcNow))
                {
                    NetworkChangeSnapshot candidate = await ReadConfigurationAsync(NetworkChangeReader.Capture, token);
                    if (candidate.Complete) { healthyConfiguration = candidate; healthyReport = report; }
                }
                SendReport();
                if (report.InternetAvailable && healthyConfiguration == null) Emit("notice", U("healthComparisonPartial"));
            });
        }

        internal static bool IsHealthyReportFresh(DiagnosticReport value, DateTime utcNow)
        {
            if (value == null || !value.InternetAvailable) return false;
            TimeSpan age = utcNow - value.Timestamp.ToUniversalTime();
            return age >= TimeSpan.Zero && age <= TimeSpan.FromMinutes(5);
        }
        private bool CanSaveHealthProfile { get { return Object.ReferenceEquals(report, healthyReport) && healthyConfiguration != null && healthyConfiguration.Complete && IsHealthyReportFresh(report, DateTime.UtcNow); } }
        internal static bool SameHealthConfiguration(NetworkChangeSnapshot before, NetworkChangeSnapshot after)
        {
            if (before == null || after == null || !before.Complete || !after.Complete || before.Entries == null || after.Entries == null || before.Entries.Length != after.Entries.Length) return false;
            var previous = before.Entries.ToDictionary(x => x.Key, StringComparer.Ordinal);
            foreach (NetworkConfigurationEntry entry in after.Entries)
            {
                NetworkConfigurationEntry original;
                if (!previous.TryGetValue(entry.Key, out original) || original.Value != entry.Value || original.Fingerprint != entry.Fingerprint) return false;
            }
            return true;
        }
        internal static async Task<NetworkChangeSnapshot> ReadConfigurationAsync(Func<NetworkChangeSnapshot> capture, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Task<NetworkChangeSnapshot> pending = Task.Run(capture);
            pending.ContinueWith(delegate(Task<NetworkChangeSnapshot> failed) { var ignored = failed.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Task completed = await Task.WhenAny(pending, Task.Delay(1500, token));
            if (completed != pending)
            {
                token.ThrowIfCancellationRequested();
                return new NetworkChangeSnapshot { TimeUtc = DateTime.UtcNow, ErrorCode = "CaptureTimedOut" };
            }
            token.ThrowIfCancellationRequested();
            try { return await pending ?? new NetworkChangeSnapshot { TimeUtc = DateTime.UtcNow, ErrorCode = "CaptureUnavailable" }; }
            catch { return new NetworkChangeSnapshot { TimeUtc = DateTime.UtcNow, ErrorCode = "CaptureUnavailable" }; }
        }
        private async Task Workspace(string command, Dictionary<string, object> message)
        {
            if (command.IndexOf("MonitorPlan", StringComparison.Ordinal) >= 0 || command == "monitorPlans") RequireAdvanced();
            bool automatic = command == "saveHealthProfile" && message.ContainsKey("autoName") && BoolValue(message, "autoName");
            string name = command == "saveHealthProfile" ? automatic ? HealthSnapshotAutomaticName(U("healthSnapshotAutoNamePrefix"), DateTime.Now) : TextValue(message, "name", 64)
                : command == "saveMonitorPlan" ? TextValue(message, "name", 64) : null;
            string id = command == "compareHealthProfile" || command == "deleteHealthProfile" || command == "deleteMonitorPlan" ? TextValue(message, "id", 36) : null;
            Guid identifier;
            if (id != null && (id.Length != 32 || !Guid.TryParseExact(id, "N", out identifier))) throw new InvalidDataException(U("workspaceInvalid"));
            MonitorTarget[] targets = command == "saveMonitorPlan" ? ReadMonitorTargets(message, U("native.invalidMessage")) : null;
            int interval = targets == null ? 0 : IntValue(message, "intervalMs", 1000, 10000), count = targets == null ? 0 : IntValue(message, "count", 1, 3600);
            if (command == "saveHealthProfile" && !CanSaveHealthProfile) { Emit("error", U("healthProfileGate")); return; }
            if ((command == "deleteHealthProfile" || command == "deleteMonitorPlan") && SoftDialog.Show(DialogOwner, U("workspaceDeletePrompt"), U("workspaceDeleteTitle"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            await Run("workspace", async delegate(CancellationToken token)
            {
                try
                {
                    NetworkWorkspaceStore store = new NetworkWorkspaceStore(Path.Combine(settingsRoot, "Workspace"));
                    if (command == "saveHealthProfile")
                    {
                        NetworkChangeSnapshot current = await ReadConfigurationAsync(NetworkChangeReader.Capture, token);
                        if (!CanSaveHealthProfile || !SameHealthConfiguration(healthyConfiguration, current)) { healthyConfiguration = null; Emit("error", U("healthConfigurationChanged")); Emit(new { type = "healthProfiles", profiles = store.LoadHealthProfiles().Select(HealthProfileMetadata).ToArray(), canSave = false }); return; }
                        NetworkChangeSnapshot snapshot = healthyConfiguration;
                        HealthProfile saved = await Task.Run(delegate { return SaveNamedHealthProfile(name, automatic, delegate(string candidate) { return store.SaveHealthProfile(candidate, snapshot); }); });
                        Emit(HealthProfileSavedPacket(saved, automatic));
                    }
                    else if (command == "compareHealthProfile")
                    {
                        HealthProfile profile = (await Task.Run(delegate { return store.LoadHealthProfiles(); })).FirstOrDefault(x => x.Id == id);
                        if (profile == null) throw new InvalidDataException("WorkspaceNotFound");
                        NetworkChangeSnapshot current = await ReadConfigurationAsync(NetworkChangeReader.Capture, token);
                        Emit(BuildHealthComparisonPacket(profile, NetworkWorkspaceStore.Compare(profile, current))); return;
                    }
                    else if (command == "deleteHealthProfile") { if (!await Task.Run(delegate { return store.DeleteHealthProfile(id); })) throw new InvalidDataException("WorkspaceNotFound"); }
                    else if (command == "saveMonitorPlan") await Task.Run(delegate { store.SaveMonitorPlan(name, targets, interval, count); });
                    else if (command == "deleteMonitorPlan") { if (!await Task.Run(delegate { return store.DeleteMonitorPlan(id); })) throw new InvalidDataException("WorkspaceNotFound"); }
                    if (command == "healthProfiles" || command == "saveHealthProfile" || command == "deleteHealthProfile")
                        Emit(new { type = "healthProfiles", profiles = await Task.Run(delegate { return store.LoadHealthProfiles().Select(HealthProfileMetadata).ToArray(); }), canSave = CanSaveHealthProfile });
                    else Emit(new { type = "monitorPlans", plans = await Task.Run(delegate { return store.LoadMonitorPlans(); }) });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    string key = ex.Message == "WorkspaceDuplicateName" ? "workspaceDuplicateName" : ex.Message == "WorkspaceHealthLimit" || ex.Message == "WorkspacePlanLimit" ? "workspaceLimit" : ex.Message == "WorkspaceNotFound" ? "workspaceNotFound" : ex is ArgumentException ? "workspaceInvalid" : "workspaceUnavailable";
                    Emit("error", U(key));
                }
            });
        }

        internal static string HealthSnapshotAutomaticName(string prefix, DateTime localNow)
        {
            return prefix + " " + localNow.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }
        internal static HealthProfile SaveNamedHealthProfile(string name, bool automatic, Func<string, HealthProfile> save)
        {
            for (int attempt = 1; ; attempt++)
            {
                string candidate = name + (attempt == 1 ? "" : " (" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
                try { return save(candidate); }
                catch (ArgumentException ex) { if (!automatic || ex.Message != "WorkspaceDuplicateName" || attempt >= 21) throw; }
            }
        }
        internal static object HealthProfileSavedPacket(HealthProfile profile, bool automatic) { return new { type = "healthProfileSaved", profile = HealthProfileMetadata(profile), automatic = automatic }; }
        internal static object HealthProfileMetadata(HealthProfile profile) { return new { profile.Id, profile.Name, profile.CreatedUtc }; }
        internal static object BuildHealthComparisonPacket(HealthProfile profile, HealthComparison comparison)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = MaximumUiJsonLength, RecursionLimit = 32 };
            NetworkChangeEvent[] changes = comparison.Changes ?? new NetworkChangeEvent[0];
            List<NetworkChangeEvent> preview = new List<NetworkChangeEvent>();
            int size = 0;
            foreach (NetworkChangeEvent change in changes)
            {
                int length = serializer.Serialize(change).Length;
                if (preview.Count >= 256 || size + length > 2 * 1024 * 1024) break;
                preview.Add(change); size += length;
            }
            return new { type = "healthComparison", profile = HealthProfileMetadata(profile), comparison = new { comparison.ComparedUtc, comparison.Complete, comparison.ErrorCode, Changes = preview.ToArray(), TotalChanges = changes.Length, Truncated = preview.Count < changes.Length } };
        }

        internal static MonitorTarget[] ReadMonitorTargets(Dictionary<string, object> message, string invalidMessage = "native.invalidMessage")
        {
            object raw;
            object[] items;
            if (message.TryGetValue("targets", out raw))
            {
                items = raw as object[];
                if (items == null || items.Length < 1 || items.Length > 4) throw new InvalidDataException(invalidMessage);
            }
            else items = new object[] { new Dictionary<string, object> { { "id", "target-1" }, { "host", TextValue(message, "host", 253) }, { "method", TextValue(message, "method", 8) }, { "useSystemProxy", BoolValue(message, "useSystemProxy") } } };
            List<MonitorTarget> result = new List<MonitorTarget>();
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in items)
            {
                Dictionary<string, object> values = item as Dictionary<string, object>;
                if (values == null) throw new InvalidDataException(invalidMessage);
                string id = TextValue(values, "id", 32), method = TextValue(values, "method", 8);
                if ((method != "https" && method != "icmp") || !ids.Add(id) || id.Any(delegate(char c) { return !(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '-' && c != '_'; })) throw new InvalidDataException(invalidMessage);
                bool proxy = BoolValue(values, "useSystemProxy");
                string input = TextValue(values, "host", 253), host, error;
                if (!AdvancedTools.TryNormalizeHost(input, out host, out error)) throw new InvalidDataException(invalidMessage);
                result.Add(new MonitorTarget { Id = id, Host = host, Method = method, UseSystemProxy = proxy });
            }
            return result.ToArray();
        }

        private async Task RunMonitoring(MonitorTarget[] targets, int interval, int count, CancellationToken token)
        {
            MonitoringSession session = new MonitoringSession(targets);
            monitoring = session;
            long run = generation;
            Emit(new { type = "monitorStarted", targets = targets, startedUtc = session.StartedUtc });
            Emit(new { type = "networkChange", @event = session.Changes.Record("MonitoringStarted", session.StartedUtc) });
            Action<NetworkChangeEvent> publish = delegate(NetworkChangeEvent change) { PostForRun(run, delegate { Emit(new { type = "networkChange", @event = change }); }); };
            using (CancellationTokenSource observe = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task watcher = ObserveConfigurationAsync(session, publish, observe.Token, NetworkChangeReader.Capture);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo failure = null;
                try
                {
                    var updates = new DirectProgress<MonitorSample>(delegate(MonitorSample sample)
                    {
                        session.Append(sample);
                        NetworkChangeEvent[] changes = session.Changes.ObserveSample(sample);
                        PostForRun(run, delegate
                        {
                            Emit(new { type = "monitorSample", sample = sample });
                            foreach (NetworkChangeEvent change in changes) Emit(new { type = "networkChange", @event = change });
                        });
                    });
                    session.Final = await NetworkMonitor.RunTargetsAsync(targets, interval, count, updates, token);
                    Emit(new { type = "monitorResult", result = session.Final });
                }
                catch (Exception ex)
                {
                    session.Cancelled = ex is OperationCanceledException;
                    session.Failed = !session.Cancelled;
                    failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                }
                observe.Cancel();
                try { await watcher; } catch (OperationCanceledException) { }
                catch (Exception ex) { session.Failed = true; if (failure == null) failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
                session.EndedUtc = DateTime.UtcNow;
                NetworkChangeEvent ended = session.Changes.Record(session.Cancelled || session.Failed ? "MonitoringStopped" : "MonitoringCompleted", session.EndedUtc);
                Emit(new { type = "networkChange", @event = ended });
                object[] series;
                lock (session.Sync) series = targets.Select(delegate(MonitorTarget target) { return (object)new { TargetId = target.Id, samples = session.Samples[target.Id].ToArray() }; }).ToArray();
                Emit(new { type = "monitorEnded", cancelled = session.Cancelled, failed = session.Failed, totalEvents = session.Changes.TotalEvents, truncated = session.Changes.TotalEvents > NetworkChangeTracker.MaximumEvents, events = session.Changes.RecentEvents, series = series });
                if (failure != null) failure.Throw();
            }
        }

        internal static async Task ObserveConfigurationAsync(MonitoringSession session, Action<NetworkChangeEvent> publish, CancellationToken token, Func<NetworkChangeSnapshot> capture)
        {
            Task<NetworkChangeSnapshot> pending = null;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (pending == null) pending = Task.Run(capture);
                    Task completed = await Task.WhenAny(pending, Task.Delay(1500, token)).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    NetworkChangeSnapshot snapshot;
                    if (completed == pending)
                    {
                        try { snapshot = await pending.ConfigureAwait(false); }
                        catch { snapshot = new NetworkChangeSnapshot { TimeUtc = DateTime.UtcNow, Complete = false, ErrorCode = "CaptureUnavailable" }; }
                        pending = null;
                    }
                    else snapshot = new NetworkChangeSnapshot { TimeUtc = DateTime.UtcNow, Complete = false, ErrorCode = "CaptureTimedOut" };
                    foreach (NetworkChangeEvent change in session.Changes.ObserveConfiguration(snapshot)) if (publish != null) publish(change);
                    await Task.Delay(2000, token).ConfigureAwait(false);
                }
            }
            finally
            {
                // Keep at most one slow OS read outstanding; cancellation must not wait on a stuck adapter API.
                if (pending != null) pending.ContinueWith(delegate(Task<NetworkChangeSnapshot> failed) { var ignored = failed.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        internal static string BuildMonitoringText(MonitoringSession session, Func<string, string> text)
        {
            StringBuilder output = new StringBuilder();
            output.AppendLine(text("redactionNotice"));
            output.AppendLine(text("brand") + " " + DisplayVersion + " · " + text("monitorExport"));
            output.AppendLine(session.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " → " + session.EndedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            output.AppendLine(text(session.Failed ? "monitorSessionFailed" : session.Cancelled ? "monitorEventMonitoringStopped" : "monitorEventMonitoringCompleted"));
            output.AppendLine(text("monitorSessionPrivacy"));
            lock (session.Sync)
            {
                foreach (MonitorTarget target in session.Targets)
                {
                    MonitorSample latest = session.Final == null ? null : session.Final.Targets.FirstOrDefault(delegate(MonitorSummary result) { return result.TargetId == target.Id; });
                    Queue<MonitorSample> samples = session.Samples[target.Id];
                    if (latest == null) latest = samples.LastOrDefault();
                    output.AppendLine();
                    output.AppendLine(target.Id + " · " + target.Host + " · " + target.Method + " · " + text(target.Method == "icmp" ? "monitorIcmp" : target.UseSystemProxy ? "monitorProxyFollow" : "monitorProxyDirect"));
                    if (latest != null)
                    {
                        output.AppendLine(text("monitorSent") + ": " + latest.Received + "/" + latest.Sent + " · " + text("monitorAvg") + ": " + (latest.Avg.HasValue ? latest.Avg.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "—") + " ms · " + latest.Status + " · " + latest.ErrorCode);
                        output.AppendLine(text("monitorChartHint"));
                    }
                    foreach (MonitorSample sample in samples) output.AppendLine(sample.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") + " · " + sample.Sequence + " · " + sample.Status + " · " + (sample.RttMs.HasValue ? sample.RttMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—") + " ms · HTTP " + sample.HttpStatusCode + " · " + sample.ErrorCode);
                }
            }
            output.AppendLine();
            output.AppendLine(text("monitorTimelineTitle") + " · " + text("monitorTimelineCount") + ": " + session.Changes.TotalEvents);
            if (session.Changes.TotalEvents > NetworkChangeTracker.MaximumEvents) output.AppendLine(text("monitorTimelineTruncated"));
            foreach (NetworkChangeEvent change in session.Changes.RecentEvents)
            {
                output.AppendLine(change.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") + " · " + text("monitorEvent" + change.Kind) + " · " + change.TargetHost + " · " + change.AdapterName + " · " + (String.IsNullOrEmpty(change.Setting) ? "" : text("monitorSetting" + change.Setting)));
                if (change.Kind == "SettingChanged") output.AppendLine(change.Before + " → " + change.After);
                if (!String.IsNullOrEmpty(change.ErrorCode)) output.AppendLine(change.ErrorCode);
            }
            return output.ToString();
        }

        private async Task Repair(Dictionary<string, object> message)
        {
            if (report == null) { Emit("notice", U("native.notReady")); return; }
            object requested;
            message.TryGetValue("actions", out requested);
            List<RepairAction> selected = SelectActions(report, mode, requested as object[]);
            if (selected.Count == 0) { Emit("notice", U("native.nothing")); return; }
            if (selected.Any(x => x.Id != "DisableProxy") && !EnsureAdministrator()) return;
            string items = String.Join(Environment.NewLine + Environment.NewLine, selected.Select(x => "• " + x.Title + Environment.NewLine + RepairImpact(x)));
            if (SoftDialog.Show(DialogOwner, String.Format(U("native.repairPrompt"), items), U("native.repairTitle"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            await RecordOperation("repair", selected.Select(x => x.Title).ToList(), delegate { return engine.RepairAsync(selected, UiProgress<string>(delegate(string text) { Emit("progress", text); }), CancellationToken.None); });
        }

        private async Task Restore()
        {
            RequireAdvanced();
            Directory.CreateDirectory(engine.BackupRoot);
            using (var dialog = new OpenFileDialog { Title = U("native.restoreTitle"), InitialDirectory = engine.BackupRoot, Filter = U("native.backupFilter"), CheckFileExists = true })
            {
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
                if (engine.BackupNeedsAdministrator(dialog.FileName) && !EnsureAdministrator()) return;
                if (SoftDialog.Show(DialogOwner, U("native.restorePrompt"), U("native.restoreTitle"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                string path = dialog.FileName;
                await RecordOperation("restore", new List<string> { U("restoreConfig") }, delegate { return engine.RestoreAsync(path, UiProgress<string>(delegate(string text) { Emit("progress", text); }), CancellationToken.None); });
            }
        }

        private async Task RestoreLatest()
        {
            string path, error;
            if (!history.TryGetLatestBackupPath(engine.BackupRoot, out path, out error)) { Emit("notice", U("noRestorableBackup")); return; }
            if (engine.BackupNeedsAdministrator(path) && !EnsureAdministrator()) return;
            if (SoftDialog.Show(DialogOwner, U("native.restorePrompt"), U("native.restoreTitle"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            await RecordOperation("restore", new List<string> { U("restoreLatest") }, delegate { return engine.RestoreAsync(path, UiProgress<string>(delegate(string text) { Emit("progress", text); }), CancellationToken.None); });
        }

        private static async Task<ConfigurationSnapshot> CaptureComparison()
        {
            try { return await Task.Run(delegate { return RepairHistoryStore.CaptureConfiguration(); }); }
            catch { return new ConfigurationSnapshot(); }
        }

        private async Task RefreshRecovery()
        {
            try { incomplete = await Task.Run(delegate { return engine.FindIncompleteOperation(); }); }
            catch { incomplete = null; Emit("notice", U("recoveryReadError")); }
            Emit(new { type = "recovery", pending = incomplete != null && incomplete.OperationId != reviewedOperationId, StartedUtc = incomplete == null ? null : incomplete.StartedUtc.ToString("o") });
        }

        private async Task RestoreIncomplete()
        {
            await RefreshRecovery();
            if (incomplete == null || incomplete.OperationId == reviewedOperationId) { Emit("notice", U("noRestorableBackup")); return; }
            string path = incomplete.BackupPath, id = incomplete.OperationId;
            if (engine.BackupNeedsAdministrator(path) && !EnsureAdministrator()) return;
            if (SoftDialog.Show(DialogOwner, U("native.restorePrompt"), U("recoveryTitle"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            await RecordOperation("restore", new List<string> { U("recoveryRestore") }, delegate { return engine.RestoreAsync(path, UiProgress<string>(delegate(string value) { Emit("progress", value); }), CancellationToken.None); });
            if (comparison != null && comparison.Success) { reviewedOperationId = id; SaveSettings(); await RefreshRecovery(); }
        }

        private async Task RecordOperation(string kind, List<string> titles, Func<Task<RepairResult>> execute)
        {
            await Run("repair", async delegate(CancellationToken token)
            {
                DateTime started = DateTime.UtcNow;
                ConfigurationSnapshot before = await CaptureComparison();
                RepairResult result = null;
                Exception failure = null;
                try { result = await execute(); }
                catch (Exception ex) { failure = ex; }
                report = null;
                try
                {
                    Emit("progress", U("repairRechecking"));
                    report = await DiagnosticService.DiagnoseAsync(engine, UiProgress<string>(delegate(string text) { Emit("progress", text); }), CancellationToken.None);
                    SendReport();
                }
                catch (Exception ex) { Emit(new { type = "report", report = (object)null }); Emit("error", String.Format(U("native.failure"), ex.Message)); }
                ConfigurationSnapshot after = await CaptureComparison();
                comparison = new RepairHistoryEntry { StartedUtc = started, EndedUtc = DateTime.UtcNow, Operation = kind, Success = result != null && result.Success, BeforeOnline = result == null ? null : result.BeforeInternetAvailable, AfterOnline = report == null ? (bool?)null : report.InternetAvailable, ActionTitles = titles, BackupPath = result == null ? null : result.BackupPath, ConfigurationChanges = RepairHistoryStore.Compare(before, after) };
                comparison.Outcome = result == null ? "Failed" : result.Outcome.ToString();
                comparison.RestartRequired = result != null && result.RestartRequired;
                if (result != null) comparison.Steps = result.Items.Select(x => new RepairStepRecord { ActionId = x.ActionId, State = x.State.ToString(), Code = x.Code, MayHaveChanged = x.MayHaveChanged }).ToList();
                try { if (!history.Record(comparison).Success) Emit("notice", U("historyWriteWarning")); }
                catch { Emit("notice", U("historyWriteWarning")); }
                SendHistory();
                Emit(new { type = "comparison", entry = HistoryPreview(comparison) });
                if (result != null) ShowResult(result);
                if (failure != null) throw failure;
            });
        }

        private static string PreviewText(string value, int maximum) { return value != null && value.Length > maximum ? value.Substring(0, maximum) + "…" : value; }
        private string RepairImpact(RepairAction action)
        {
            if (action.Id == "DisableProxy" || action.Id == "FlushDns" || action.Id == "RenewDhcp" || action.Id == "AutoDns") return U("impact" + action.Id);
            return action.Impact;
        }
        private static object HistoryPreview(RepairHistoryEntry entry)
        {
            return new { entry.Id, StartedUtc = entry.StartedUtc.ToString("o"), EndedUtc = entry.EndedUtc.ToString("o"), entry.Operation, entry.Success, entry.Outcome, entry.RestartRequired, Steps = entry.Steps.Take(64).ToArray(), entry.BeforeOnline, entry.AfterOnline, ActionTitles = entry.ActionTitles.Take(4).Select(x => PreviewText(x, 120)).ToArray(), ConfigurationChanges = entry.ConfigurationChanges.Take(16).Select(x => new { Key = PreviewText(x.Key, 96), Before = PreviewText(x.Before, 256), After = PreviewText(x.After, 256) }).ToArray(), PreviewLimited = entry.Steps.Count > 64 || entry.ActionTitles.Count > 4 || entry.ConfigurationChanges.Count > 16 || entry.ConfigurationChanges.Any(x => (x.Before ?? "").Length > 256 || (x.After ?? "").Length > 256) };
        }

        private void SendHistory()
        {
            HistoryReadResult result = history.ReadAll();
            string path, error;
            bool hasBackup = history.TryGetLatestBackupPath(engine.BackupRoot, out path, out error);
            List<object> entries = new List<object>();
            int payload = 0;
            foreach (RepairHistoryEntry entry in result.Entries.Take(20))
            {
                object preview = HistoryPreview(entry);
                payload += json.Serialize(preview).Length;
                if (payload > 400000) break;
                entries.Add(preview);
            }
            Emit(new { type = "history", entries = entries.ToArray(), error = result.ErrorCode, hasBackup = hasBackup });
        }

        internal static List<RepairAction> SelectActions(DiagnosticReport report, string mode, object[] requests)
        {
            if (report == null || (mode != "beginner" && mode != "advanced")) throw new InvalidDataException("Invalid diagnosis or mode.");
            List<RepairAction> available = report.Items.SelectMany(x => x.Actions).GroupBy(x => x.Id + ":" + x.AdapterId).Select(x => x.First()).ToList();
            if (mode == "beginner") available = available.Where(x => x.Suggested).ToList();
            if (mode == "beginner" && requests == null) return available;
            if (requests == null || requests.Length > 64) throw new InvalidDataException("Invalid repair request.");
            var selected = new List<RepairAction>();
            foreach (object request in requests)
            {
                var item = request as Dictionary<string, object>;
                if (item == null) throw new InvalidDataException("Invalid repair request.");
                string id = TextValue(item, "Id", 32);
                string adapter = OptionalText(item, "AdapterId", 40);
                RepairAction action = available.FirstOrDefault(x => x.Id == id && (x.AdapterId ?? "") == adapter);
                if (action == null) throw new InvalidDataException("The action is not in the current diagnosis.");
                if (!selected.Any(x => x.Id == id && (x.AdapterId ?? "") == adapter)) selected.Add(action);
            }
            return selected;
        }

        private static AdapterDisplaySnapshot ReadAdapterSnapshot(CancellationToken token)
        {
            AdapterDisplaySnapshot result = new AdapterDisplaySnapshot { Timestamp = DateTime.Now.ToString("o") };
            var text = new StringBuilder();
            text.AppendLine("Snapshot: " + result.Timestamp);
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces().Take(64))
            {
                token.ThrowIfCancellationRequested();
                AdapterDisplayRow row = new AdapterDisplayRow { Id = adapter.Id, Name = PreviewText(adapter.Name, 256), Description = PreviewText(adapter.Description, 512), Type = adapter.NetworkInterfaceType.ToString(), Status = adapter.OperationalStatus.ToString(), Mac = BitConverter.ToString(adapter.GetPhysicalAddress().GetAddressBytes()) };
                result.Rows.Add(row);
                text.AppendLine();
                text.AppendLine("Name: " + adapter.Name);
                text.AppendLine("Description: " + adapter.Description);
                text.AppendLine("Type: " + adapter.NetworkInterfaceType + " | Status: " + adapter.OperationalStatus);
                text.AppendLine("MAC: " + adapter.GetPhysicalAddress());
                try
                {
                    var ip = adapter.GetIPProperties();
                    var v4 = ip.GetIPv4Properties();
                    row.Dhcp = v4 == null ? (bool?)null : v4.IsDhcpEnabled;
                    row.Ipv4 = ip.UnicastAddresses.Where(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Take(16).Select(x => x.Address.ToString()).ToArray();
                    row.Ipv6 = ip.UnicastAddresses.Where(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6).Take(16).Select(x => x.Address.ToString()).ToArray();
                    row.Gateways = ip.GatewayAddresses.Take(16).Select(x => x.Address.ToString()).ToArray();
                    row.Dns = ip.DnsAddresses.Take(16).Select(x => x.ToString()).ToArray();
                    text.AppendLine("DHCP: " + (v4 == null ? "N/A" : v4.IsDhcpEnabled.ToString()));
                    text.AppendLine("IP: " + String.Join(", ", ip.UnicastAddresses.Take(12).Select(x => x.Address.ToString())));
                    text.AppendLine("Gateway: " + String.Join(", ", ip.GatewayAddresses.Select(x => x.Address.ToString())));
                    text.AppendLine("DNS: " + String.Join(", ", ip.DnsAddresses.Select(x => x.ToString())));
                }
                catch (NetworkInformationException ex) { row.ReadError = "NetworkInformation:" + ex.ErrorCode; text.AppendLine("Read error: " + ex.ErrorCode); }
            }
            result.Text = text.ToString();
            result.Rows = result.Rows.OrderByDescending(x => x.Status == "Up").ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return result;
        }

        private async Task Advanced(Dictionary<string, object> message)
        {
            RequireAdvanced();
            string name = TextValue(message, "tool", 12);
            AdvancedToolKind tool;
            switch (name) { case "ping": tool = AdvancedToolKind.Ping; break; case "dns": tool = AdvancedToolKind.Dns; break; case "trace": tool = AdvancedToolKind.Trace; break; case "tcp": tool = AdvancedToolKind.Tcp; break; default: throw new InvalidDataException(U("native.invalidMessage")); }
            string host = TextValue(message, "host", 1024);
            int port = 443;
            object rawPort;
            if (message.TryGetValue("port", out rawPort) && !AdvancedTools.TryParsePort(Convert.ToString(rawPort, System.Globalization.CultureInfo.InvariantCulture), out port)) throw new InvalidDataException(U("native.invalidMessage"));
            var output = new StringBuilder();
            Emit("advanced", "");
            await Run("advanced", async delegate(CancellationToken token)
            {
                long run = generation;
                var samples = UiProgress<AdvancedToolSample>(delegate(AdvancedToolSample sample) { Emit(new { type = "advancedSample", sample = sample }); });
                var result = await AdvancedTools.RunAsync(tool, host, port, new DirectProgress<string>(delegate(string text)
                {
                    string snapshot;
                    lock (output) { output.AppendLine(text); snapshot = output.ToString(); }
                    if (snapshot.Length < 65536) PostForRun(run, delegate { Emit("advanced", snapshot); });
                }), token, samples);
                lock (output) { output.AppendLine(result.Summary); Emit("advanced", output.ToString()); }
                Emit(new { type = "advancedResult", result = result });
            });
        }

        private async Task Run(string name, Func<CancellationToken, Task> work)
        {
            if (!String.IsNullOrEmpty(operation)) { Emit("notice", U("native.busy")); return; }
            operation = name;
            Interlocked.Increment(ref generation);
            cancellation = new CancellationTokenSource();
            Emit(new { type = "busy", operation = operation });
            try { await work(cancellation.Token); }
            catch (OperationCanceledException) { Emit("notice", U("native.cancelled")); }
            catch (Exception ex) { Emit("error", String.Format(U("native.failure"), ex.Message)); }
            finally
            {
                Interlocked.Increment(ref generation);
                cancellation.Dispose(); cancellation = null; operation = null;
                if (!closed) Emit(new { type = "busy", operation = (string)null });
            }
        }

        private IProgress<T> UiProgress<T>(Action<T> callback)
        {
            long run = generation;
            return new DirectProgress<T>(delegate(T value) { PostForRun(run, delegate { callback(value); }); });
        }
        private void PostForRun(long run, Action callback)
        {
            Action guarded = delegate { if (!closed && generation == run && operation != null && cancellation != null && !cancellation.IsCancellationRequested) callback(); };
            if (closed || view.Dispatcher.HasShutdownStarted) return;
            try { if (view.Dispatcher.CheckAccess()) guarded(); else view.Dispatcher.BeginInvoke(guarded); }
            catch (InvalidOperationException) { }
        }
        internal sealed class DirectProgress<T> : IProgress<T>
        {
            private readonly Action<T> callback;
            internal DirectProgress(Action<T> value) { callback = value; }
            public void Report(T value) { callback(value); }
        }

        private void Export()
        {
            if (report == null) { Emit("notice", U("native.notReady")); return; }
            SaveRedactedReport(U("redactionNotice") + Environment.NewLine + report.ToPlainText());
        }

        private void ExportHistory()
        {
            HistoryReadResult stored = history.ReadAll();
            if (report == null && siteReport == null && stored.Entries.Count == 0) { Emit("notice", U("historyEmpty")); return; }
            StringBuilder output = new StringBuilder();
            output.AppendLine(U("redactionNotice"));
            if (report != null) output.AppendLine(report.ToPlainText());
            if (siteReport != null)
            {
                output.AppendLine(U("siteTitle"));
                output.AppendLine(siteReport.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " · " + siteReport.Target.Origin);
                output.AppendLine(U("site" + siteReport.ConclusionCode));
                foreach (SiteProbe probe in siteReport.Probes) output.AppendLine(U("site" + probe.Kind) + " · " + probe.Status + " · " + probe.DurationMs + " ms · HTTP " + probe.HttpStatusCode + " · " + probe.ErrorCode);
                output.AppendLine(U("siteLimit"));
            }
            output.AppendLine(U("historyTitle"));
            foreach (RepairHistoryEntry entry in stored.Entries)
            {
                output.AppendLine();
                output.AppendLine(entry.EndedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " · " + U(entry.Operation == "restore" ? "restoreComparison" : "repairComparison") + " · " + U(String.IsNullOrEmpty(entry.Outcome) ? entry.Success ? "operationSucceeded" : "operationFailed" : "outcome" + entry.Outcome));
                foreach (RepairStepRecord step in entry.Steps) output.AppendLine(U("action" + step.ActionId) + ": " + U("step" + step.State) + (String.IsNullOrEmpty(step.Code) ? "" : " [" + step.Code + "]"));
                output.AppendLine(U("beforeRepair") + ": " + OnlineText(entry.BeforeOnline) + " → " + U("afterRepair") + ": " + OnlineText(entry.AfterOnline));
                foreach (string title in entry.ActionTitles) output.AppendLine(title);
                if (entry.ConfigurationChanges.Count == 0) output.AppendLine(U("noConfirmedChanges"));
                foreach (ConfigurationChange change in entry.ConfigurationChanges) output.AppendLine(change.Key + ": " + change.Before + " → " + change.After);
            }
            if (!String.IsNullOrEmpty(stored.ErrorCode)) output.AppendLine(U("historyReadError"));
            SaveRedactedReport(output.ToString());
        }

        private void SaveRedactedReport(string source)
        {
            string content = ReportRedactor.Redact(source);
            if (SoftDialog.Show(DialogOwner, content, U("feedbackPreviewTitle"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            using (var dialog = new SaveFileDialog { Title = U("exportRedacted"), FileName = "NetworkRescue-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", Filter = U("native.reportFilter"), OverwritePrompt = true })
            {
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
                File.WriteAllText(dialog.FileName, content, new UTF8Encoding(true));
                Emit("notice", U("native.saveSuccess"));
            }
        }

        private string OnlineText(bool? value) { return U(value == true ? "internetConfirmed" : value == false ? "internetNotConfirmed" : "unknown"); }

        private void ShowResult(RepairResult result)
        {
            Emit(new { type = "operationDetails", details = result.Details });
            string text = result.Code == "OperationBusy" ? result.Summary : U("outcome" + result.Outcome);
            if (result.Items.Count > 0) text += Environment.NewLine + String.Format(U("repairStepCount"), result.Items.Count(x => x.State == RepairItemState.Completed), result.Items.Count);
            if (!String.IsNullOrEmpty(result.BackupPath)) text += Environment.NewLine + U("repairBackupSaved");
            if (mode == "advanced" && !String.IsNullOrEmpty(result.BackupPath)) text += Environment.NewLine + String.Format(U("native.backupPath"), result.BackupPath);
            if (result.RestartRequired) text += Environment.NewLine + U("native.restart");
            SoftDialog.Show(DialogOwner, text, U("native.resultTitle"), MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private bool EnsureAdministrator()
        {
            if (IsAdministrator()) return true;
            if (SoftDialog.Show(DialogOwner, U("native.elevatePrompt"), U("native.permissionTitle"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.OK) return false;
            try
            {
                Process.Start(new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath, "--scan --owner-sid " + WindowsIdentity.GetCurrent().User.Value + " --language " + I18n.Language) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory });
                view.Close();
            }
            catch (System.ComponentModel.Win32Exception ex) { Emit("notice", ex.NativeErrorCode == 1223 ? U("native.cancelled") : String.Format(U("native.failure"), ex.Message)); }
            return false;
        }

        private void RequireAdvanced() { if (mode != "advanced") throw new InvalidOperationException(U("native.advancedOnly")); }
        private static bool IsAdministrator() { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        private static string TextValue(Dictionary<string, object> value, string key, int limit)
        {
            object item;
            string text = value.TryGetValue(key, out item) ? item as string : null;
            if (text == null || text.Length == 0 || text.Length > limit) throw new InvalidDataException("Invalid request field: " + key);
            return text;
        }
        private static string OptionalText(Dictionary<string, object> value, string key, int limit)
        {
            object item;
            if (!value.TryGetValue(key, out item) || item == null) return "";
            string text = item as string;
            if (text == null || text.Length > limit) throw new InvalidDataException("Invalid request field: " + key);
            return text;
        }
        private static bool BoolValue(Dictionary<string, object> value, string key)
        {
            object item;
            if (!value.TryGetValue(key, out item) || !(item is bool)) throw new InvalidDataException("Invalid request field: " + key);
            return (bool)item;
        }
        internal static int IntValue(Dictionary<string, object> value, string key, int minimum, int maximum)
        {
            object item;
            if (!value.TryGetValue(key, out item) || !(item is int) || (int)item < minimum || (int)item > maximum) throw new InvalidDataException("Invalid request field: " + key);
            return (int)item;
        }
        private void SendInit() { Emit(new { type = "init", language = I18n.Language, mode = mode, theme = theme, version = DisplayVersion, updateRepository = updateRepository, monitorGateway = NetworkChangeReader.FindGateway(), canSaveHealthProfile = CanSaveHealthProfile }); }
        private void SendReport()
        {
            Emit(new { type = "report", report = new { Timestamp = report.Timestamp.ToString("o"), Items = report.Items, Summary = report.Summary, InternetAvailable = report.InternetAvailable, Fault = report.Fault, SpecialChecks = report.SpecialChecks }, canSaveHealthProfile = CanSaveHealthProfile });
        }
        private void Emit(string type, string text) { Emit(new { type = type, text = text }); }
        private void Emit(object value)
        {
            if (!closed) view.Receive(json.Serialize(value));
        }

        private void ReadSettings()
        {
            try
            {
                string path = Path.Combine(settingsRoot, "settings.json");
                if (File.Exists(path) && new FileInfo(path).Length < 8192)
                {
                    var data = json.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                    string value;
                    if (data.TryGetValue("language", out value)) I18n.SetLanguage(value);
                    if (data.TryGetValue("mode", out value) && (value == "beginner" || value == "advanced")) mode = value;
                    if (data.TryGetValue("theme", out value) && (value == "light" || value == "dark")) theme = value;
                    string normalized; if (data.TryGetValue("updateRepository", out value) && UpdateService.TryNormalizeRepository(value, out normalized)) updateRepository = normalized;
                    Guid reviewed; if (data.TryGetValue("reviewedOperationId", out value) && Guid.TryParse(value, out reviewed)) reviewedOperationId = reviewed.ToString("D");
                }
            }
            catch { }
            ReadUi();
        }
        private void ReadUi()
        {
            try { ui = json.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "locales", I18n.Language + ".json"))); }
            catch { ui = new Dictionary<string, string>(); }
            string brand;
            if (ui.TryGetValue("brand", out brand)) view.Title = brand + " · v" + DisplayVersion;
        }
        private string U(string key) { string value; return ui.TryGetValue(key, out value) ? value : key; }
        internal static string LocalText(string key)
        {
            try
            {
                var values = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "locales", I18n.Language + ".json")));
                string value;
                if (values.TryGetValue(key, out value)) return value;
            }
            catch { }
            return key;
        }
        private void SaveSettings()
        {
            Directory.CreateDirectory(settingsRoot);
            string path = Path.Combine(settingsRoot, "settings.json");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, json.Serialize(new { language = I18n.Language, mode = mode, theme = theme, reviewedOperationId = reviewedOperationId, updateRepository = updateRepository }), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
        }
    }
    internal sealed class AdapterDisplayRow
    {
        public string Id, Name, Description, Type, Status, Mac, ReadError;
        public bool? Dhcp;
        public string[] Ipv4 = new string[0], Ipv6 = new string[0], Gateways = new string[0], Dns = new string[0];
    }
    internal sealed class AdapterDisplaySnapshot
    {
        public string Timestamp, Text;
        public List<AdapterDisplayRow> Rows = new List<AdapterDisplayRow>();
    }
}
