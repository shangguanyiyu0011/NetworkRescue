using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkRescue
{
    internal static class EngineTests
    {
        private const string Id = "12345678-1234-1234-1234-123456789abc";
        private const string OtherId = "23456789-1234-1234-1234-123456789abc";
        private static int assertions;

        private static void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new Exception("FAIL: " + message);
        }

        private static AdapterSnapshot Adapter(string id)
        {
            return new AdapterSnapshot { Id = id, Name = "测试网卡", Up = true, Dhcp = true, DnsAutomatic = false, DnsConfigurationReadable = true, InformationReadable = true, Addresses = new List<string> { "192.168.1.10" }, Gateways = new List<string> { "192.168.1.1" }, Dns = new List<string> { "8.8.8.8" }, ConfiguredDns = new List<string> { "8.8.8.8" } };
        }

        private static Snapshot Sample(bool direct, bool dns, bool http)
        {
            Snapshot s = new Snapshot { Proxy = new ProxySnapshot { Readable = true } };
            s.Adapters.Add(Adapter(Id));
            s.Tcp.Add(new Probe { Label = "IP-1", Success = direct, Detail = "TCP" });
            s.Tcp.Add(new Probe { Label = "IP-2", Success = direct, Detail = "TCP" });
            s.Dns.Add(new Probe { Label = "DNS-1", Success = dns, Detail = "DNS" });
            s.Dns.Add(new Probe { Label = "DNS-2", Success = dns, Detail = "DNS" });
            s.Http.Add(new Probe { Label = "HTTP-1", Success = http, Detail = "HTTP", Marker = true });
            s.Http.Add(new Probe { Label = "HTTP-2", Success = http, Detail = "HTTP", Marker = true });
            return s;
        }

        private static List<RepairAction> Actions(Snapshot s)
        {
            return RescueEngine.BuildReport(s).Items.SelectMany(delegate(DiagnosticItem item) { return item.Actions; }).ToList();
        }

        private static Snapshot ResidualProxy(string server)
        {
            Snapshot snapshot = Sample(true, true, false);
            snapshot.Proxy.Enabled = true;
            snapshot.Proxy.Server = server;
            snapshot.Proxy.Endpoints.Add(new Probe { Label = "configured endpoint", Success = false });
            snapshot.DirectHttp.Add(new Probe { Label = "direct HTTPS", Success = true, Marker = false });
            return snapshot;
        }

        private static void VpnResidualChecks()
        {
            Snapshot residual = ResidualProxy("127.0.0.1:10808");
            Assert(RescueEngine.HasResidualLocalProxy(residual), "dead local proxy plus real direct HTTP is strong residual evidence");
            Assert(Actions(residual).Any(delegate(RepairAction a) { return a.Id == "DisableProxy" && a.Suggested; }), "new-user default repair includes strongly evidenced local proxy residue");
            Assert(RescueEngine.BuildReport(residual).Summary.Contains("本地代理残留"), "summary identifies likely VPN shutdown residue");
            Assert(!RescueEngine.BuildReport(residual).InternetAvailable, "direct HTTP success does not claim current system proxy is working");
            residual.Tcp.ForEach(delegate(Probe p) { p.Success = false; });
            Assert(RescueEngine.HasResidualLocalProxy(residual), "strong direct HTTP evidence does not require unrelated fixed IP endpoints");
            residual.Proxy.Endpoints[0].Success = true;
            Assert(!RescueEngine.HasResidualLocalProxy(residual) && !Actions(residual).Any(delegate(RepairAction a) { return a.Id == "DisableProxy"; }), "running VPN/local proxy is preserved even if its HTTP upstream fails");
            residual = ResidualProxy("[::1]:10808");
            Assert(RescueEngine.HasResidualLocalProxy(residual), "IPv6 loopback proxy residue recognized");
            residual = ResidualProxy("localhost:10808");
            Assert(RescueEngine.HasResidualLocalProxy(residual), "localhost proxy residue recognized");
            residual = ResidualProxy("127.25.34.1:10808");
            Assert(RescueEngine.HasResidualLocalProxy(residual), "entire 127/8 is loopback");
            residual = ResidualProxy("corp-proxy.example.com:8080");
            Assert(!RescueEngine.HasResidualLocalProxy(residual), "failed remote company proxy is not a local residue");
            Assert(!Actions(residual).Any(delegate(RepairAction a) { return a.Id == "DisableProxy" && a.Suggested; }), "company proxy never selected by new-user default rule");
            residual = ResidualProxy("http=127.0.0.1:10808;https=corp-proxy.example.com:8080");
            Assert(!RescueEngine.HasResidualLocalProxy(residual), "mixed remote and local proxy configuration is preserved by default");
            residual = ResidualProxy("127.0.0.1:10808");
            residual.Proxy.Pac = "https://company.example/config.pac";
            Assert(!RescueEngine.HasResidualLocalProxy(residual) && !Actions(residual).Any(delegate(RepairAction a) { return a.Id == "DisableProxy"; }), "PAC configuration excludes default and manual proxy action");
            residual = ResidualProxy("127.0.0.1:10808");
            residual.Http[0].Success = true;
            Assert(!RescueEngine.HasResidualLocalProxy(residual) && !Actions(residual).Any(delegate(RepairAction a) { return a.Id == "DisableProxy"; }), "working proxy HTTP always preserved");
            residual = ResidualProxy("127.0.0.1:10808");
            residual.DirectHttp[0].Success = false;
            Assert(!RescueEngine.HasResidualLocalProxy(residual), "IP connectivity without direct HTTP is insufficient for new-user proxy repair");
            Assert(!Actions(residual).Any(delegate(RepairAction a) { return a.Id == "DisableProxy" && a.Suggested; }), "failed direct HTTP leaves proxy action unchecked");
            residual.DirectHttp.Clear();
            Assert(!RescueEngine.HasResidualLocalProxy(residual), "missing direct HTTP evidence is insufficient");
            residual = ResidualProxy("127.0.0.1:10808");
            residual.Proxy.Endpoints[0].Uncertain = true;
            Assert(!RescueEngine.HasResidualLocalProxy(residual), "uncertain endpoint failure is insufficient");
            residual = ResidualProxy("http=127.0.0.1:10808;https=bad/url");
            Assert(!RescueEngine.HasResidualLocalProxy(residual), "unparseable proxy item blocks default repair");
            Assert(!RescueEngine.IsLoopbackHost("localhost.example.com") && !RescueEngine.IsLoopbackHost("192.168.1.1"), "lookalike host and private LAN are not loopback");
            ProxySnapshot excessive = new ProxySnapshot { Readable = true, Enabled = true, Server = new string('a', 8193) };
            typeof(RescueEngine).GetMethod("ProbeProxy", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { excessive });
            Assert(excessive.Endpoints.Count == 1 && excessive.Endpoints[0].Uncertain && !excessive.DefinitelyUnreachable, "oversized proxy configuration remains unknown without network probing");

            Snapshot localDns = Sample(true, false, false);
            localDns.Adapters[0].Dns = new List<string> { "127.0.0.1" };
            localDns.Adapters[0].ConfiguredDns = new List<string> { "127.0.0.1" };
            localDns.LocalDns.Add(new Probe { Label = "127.0.0.1:53 TCP" });
            localDns.LocalDns.Add(new Probe { Label = "127.0.0.1:53 UDP" });
            Assert(RescueEngine.HasResidualLoopbackDnsRisk(localDns), "all loopback DNS plus failed system DNS and absent local responses warns of client residue");
            Assert(RescueEngine.BuildReport(localDns).Summary.Contains("VPN/DNS 客户端"), "loopback DNS warning explains likely client cause");
            Assert(!Actions(localDns).Any(delegate(RepairAction a) { return a.Id == "AutoDns" && a.Suggested; }), "DNS residue does not silently reset custom DNS");
            Assert(!Actions(localDns).Any(delegate(RepairAction a) { return a.Id == "FlushDns"; }), "dead DNS service is not misdiagnosed as cache issue");
            localDns.LocalDns[1].Success = true;
            Assert(!RescueEngine.HasResidualLoopbackDnsRisk(localDns), "UDP DNS response preserves an intentional local DNS service");
            localDns.LocalDns[1].Success = false;
            localDns.Adapters[0].Dns.Add("192.168.1.1");
            Assert(!RescueEngine.HasResidualLoopbackDnsRisk(localDns), "mixed local and LAN DNS is not classified all-local");
            localDns.Adapters[0].Dns = new List<string>();
            localDns.Adapters[0].AllDns = new List<string> { "::1" };
            Assert(RescueEngine.HasResidualLoopbackDnsRisk(localDns), "IPv6 loopback DNS is included in residue warning");
            localDns.Dns[0].Success = true;
            Assert(!RescueEngine.HasResidualLoopbackDnsRisk(localDns), "successful system DNS prevents a residue warning");
            localDns.Dns[0].Success = false;
            localDns.Adapters[0].Dhcp = false;
            Assert(!RescueEngine.HasResidualLoopbackDnsRisk(localDns), "static-only DNS configuration is not assigned DHCP residue risk");
        }

        private static void RejectBackup(RescueBackup backup, string label)
        {
            bool rejected = false;
            try { RescueEngine.ValidateBackup(backup); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected, label);
        }

        private static void BackupRoundTrip()
        {
            string temporary = Path.Combine(Path.GetTempPath(), "NetworkRescueTests-" + Guid.NewGuid().ToString("N"));
            RescueEngine engine = new RescueEngine();
            typeof(RescueEngine).GetProperty("BackupRoot").GetSetMethod(true).Invoke(engine, new object[] { temporary });
            MethodInfo create = typeof(RescueEngine).GetMethod("CreateBackup", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo save = typeof(RescueEngine).GetMethod("SaveBackup", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo load = typeof(RescueEngine).GetMethod("LoadBackup", BindingFlags.NonPublic | BindingFlags.Instance);
            try
            {
                Snapshot sample = Sample(true, false, false);
                sample.Adapters[0].Dns = new List<string> { "9.9.9.9" };
                sample.Adapters[0].ConfiguredDns = new List<string> { "8.8.8.8", "1.1.1.1" };
                sample.Proxy.EnableExists = true;
                sample.Proxy.EnableValue = 1;
                sample.Proxy.Server = "http://test-user:test-secret@127.0.0.1:10809";
                RescueBackup original = (RescueBackup)create.Invoke(null, new object[] { sample });
                Assert(original.Adapters[0].Dns.SequenceEqual(sample.Adapters[0].ConfiguredDns), "backup preserves original custom DNS rather than effective resolver list");
                string path = (string)save.Invoke(engine, new object[] { original, null });
                Assert(File.Exists(path) && new FileInfo(path).Length > 0, "DPAPI backup is written to disk");
                Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("test-secret"), "proxy credentials absent from encrypted file plaintext");
                RescueBackup restored = (RescueBackup)load.Invoke(engine, new object[] { path });
                Assert(restored.Adapters[0].Dns.SequenceEqual(original.Adapters[0].Dns), "encrypted backup DNS roundtrip");
                Assert(restored.ProxyEnableExists && restored.ProxyEnableValue == 1 && restored.ProxyServer == original.ProxyServer, "encrypted backup proxy roundtrip");
                Assert(!engine.BackupNeedsAdministrator(path), "proxy-only backup does not require administrator");
                original.ProxyStarted = true;
                original.Adapters[0].DnsStarted = true;
                save.Invoke(engine, new object[] { original, path });
                restored = (RescueBackup)load.Invoke(engine, new object[] { path });
                Assert(restored.ProxyStarted && restored.Adapters[0].DnsStarted, "started markers persist through atomic backup update");
                Assert(engine.BackupNeedsAdministrator(path), "DNS restoration requires administrator");
                original.Adapters[0].Dns = new string[0];
                bool refused = false;
                try { save.Invoke(engine, new object[] { original, path }); }
                catch (TargetInvocationException ex) { refused = ex.InnerException is InvalidDataException; }
                Assert(refused, "invalid backup refused before replacing valid disk backup");
                restored = (RescueBackup)load.Invoke(engine, new object[] { path });
                Assert(restored.Adapters[0].Dns.Length == 2, "previous valid backup survives failed validation");
            }
            finally
            {
                string full = Path.GetFullPath(temporary);
                string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (String.Equals(Path.GetDirectoryName(full), expectedParent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("NetworkRescueTests-", StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full, true);
            }
        }

        private static RepairItemResult TrackedItem(string action)
        {
            return new RepairItemResult { ActionId = action, State = RepairItemState.Prepared, Code = "Prepared" };
        }

        private static void TypedOperationChecks()
        {
            int changes = 0, checkpoints = 0;
            RepairResult result = new RepairResult();
            RepairItemResult item = TrackedItem("FlushDns"); result.Items.Add(item);
            Action save = delegate { checkpoints++; };
            bool continued = RescueEngine.RunTrackedItem(result, item, delegate
            {
                RescueEngine.BeginItemWrite(item, save, delegate { });
                Assert(checkpoints == 1 && item.State == RepairItemState.Writing, "write begins only after the writing checkpoint has completed");
                changes++;
            }, save, CancellationToken.None);
            RescueEngine.SummarizeItems(result);
            Assert(continued && result.Success && result.Outcome == RepairOutcome.Completed && checkpoints == 2, "one completed action has typed success and a final checkpoint");
            Assert(item.WriteStarted && item.MayHaveChanged && changes == 1, "completed request records that settings could have changed without automatic rollback");

            RepairItemResult rejected = TrackedItem("DisableProxy");
            RescueEngine.RunTrackedItem(result, rejected, delegate { throw new RepairRefusedException("PreconditionChanged", "fixture refusal"); }, save, CancellationToken.None);
            Assert(rejected.State == RepairItemState.Rejected && rejected.Code == "PreconditionChanged" && !rejected.WriteStarted && !rejected.MayHaveChanged, "safety refusal is typed and distinct from execution failure");
            result.Items.Add(rejected); RescueEngine.SummarizeItems(result);
            Assert(result.Outcome == RepairOutcome.Partial && result.PartialSuccess && !result.Success, "completed plus safely rejected is partial success");

            RepairItemResult failure = TrackedItem("AutoDns");
            RescueEngine.RunTrackedItem(new RepairResult(), failure, delegate { throw new IOException("fixture read error"); }, save, CancellationToken.None);
            Assert(failure.State == RepairItemState.Failed && failure.Code == "ExecutionFailed" && !failure.MayHaveChanged, "failure before the write boundary does not claim settings changed");

            RepairItemResult uncertain = TrackedItem("AutoDns");
            RescueEngine.RunTrackedItem(new RepairResult(), uncertain, delegate { RescueEngine.BeginItemWrite(uncertain, save, delegate { }); throw new IOException("fixture post-write error"); }, save, CancellationToken.None);
            Assert(uncertain.State == RepairItemState.Unconfirmed && uncertain.Code == "WriteUnconfirmed" && uncertain.MayHaveChanged, "failure after write boundary remains unconfirmed rather than undone");
            RepairResult uncertainResult = new RepairResult(); uncertainResult.Items.Add(uncertain); RescueEngine.SummarizeItems(uncertainResult);
            Assert(uncertainResult.Outcome == RepairOutcome.NeedsReview && !uncertainResult.Success, "unknown post-write state requires review");

            RepairItemResult launch = TrackedItem("FlushDns");
            RescueEngine.RunTrackedItem(new RepairResult(), launch, delegate { RescueEngine.BeginItemWrite(launch, save, delegate { }); throw new RepairExecutionException("ProcessStartFailed", false, "fixture process never started"); }, save, CancellationToken.None);
            Assert(launch.State == RepairItemState.Failed && launch.WriteStarted && !launch.MayHaveChanged, "known process launch failure distinguishes attempted write from actual possible change");

            RepairItemResult reboot = TrackedItem("AutoDns");
            RepairResult rebootResult = new RepairResult(); rebootResult.Items.Add(reboot);
            RescueEngine.RunTrackedItem(rebootResult, reboot, delegate { RescueEngine.BeginItemWrite(reboot, save, delegate { }); throw new RestartNeededException(); }, save, CancellationToken.None);
            RescueEngine.SummarizeItems(rebootResult);
            Assert(reboot.State == RepairItemState.RestartRequired && reboot.NativeCode == 1 && reboot.RestartRequired && reboot.MayHaveChanged, "WMI code one is typed as restart-required rather than an ordinary failure");
            Assert(rebootResult.Outcome == RepairOutcome.RestartRequired && rebootResult.RestartRequired && !rebootResult.Success, "legacy success stays false until restart/recheck semantics can be shown by the caller");
            rebootResult.Items.Add(item); RescueEngine.SummarizeItems(rebootResult);
            Assert(rebootResult.Outcome == RepairOutcome.RestartRequired && !rebootResult.Success, "completed plus restart-required does not claim verified success");

            RepairItemResult nativeFailure = TrackedItem("AutoDns");
            RescueEngine.RunTrackedItem(new RepairResult(), nativeFailure, delegate { RescueEngine.BeginItemWrite(nativeFailure, save, delegate { }); throw new RepairExecutionException("WmiReturnCode", true, "fixture native return", 65); }, save, CancellationToken.None);
            Assert(nativeFailure.State == RepairItemState.Unconfirmed && nativeFailure.NativeCode == 65, "native failure code is preserved without parsing translated text");

            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                RepairItemResult cancelled = TrackedItem("FlushDns"); int invoked = 0;
                RescueEngine.RunTrackedItem(new RepairResult(), cancelled, delegate { invoked++; }, save, cancel.Token);
                Assert(cancelled.State == RepairItemState.Cancelled && !cancelled.WriteStarted && invoked == 0, "cancellation skips the whole not-yet-started action");
            }
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                RepairItemResult active = TrackedItem("FlushDns"), later = TrackedItem("DisableProxy");
                RescueEngine.RunTrackedItem(new RepairResult(), active, delegate { RescueEngine.BeginItemWrite(active, save, delegate { }); changes++; cancel.Cancel(); }, save, cancel.Token);
                RescueEngine.RunTrackedItem(new RepairResult(), later, delegate { changes++; }, save, cancel.Token);
                Assert(active.State == RepairItemState.Completed && later.State == RepairItemState.Cancelled && changes == 2, "cancellation allows active mutation to finish and skips later mutation");
            }
            RepairItemResult interrupted = TrackedItem("AutoDns");
            RescueEngine.RunTrackedItem(new RepairResult(), interrupted, delegate { RescueEngine.BeginItemWrite(interrupted, save, delegate { }); throw new OperationCanceledException(); }, save, CancellationToken.None);
            Assert(interrupted.State == RepairItemState.Unconfirmed && interrupted.Code == "CancelledAfterWrite", "unexpected cancellation inside a write does not claim rollback");

            RepairItemResult blocked = TrackedItem("FlushDns"); bool marker = true; int gateCalls = 0, blockedChanges = 0;
            Action failingCheckpoint = delegate { gateCalls++; if (gateCalls == 1) throw new IOException("fixture backup full"); };
            continued = RescueEngine.RunTrackedItem(new RepairResult(), blocked, delegate { RescueEngine.BeginItemWrite(blocked, failingCheckpoint, delegate { marker = false; }); blockedChanges++; }, failingCheckpoint, CancellationToken.None);
            Assert(!continued && blocked.State == RepairItemState.Failed && blocked.Code == "BackupWriteFailed", "failed pre-write checkpoint prevents subsequent actions even if result recording recovers");
            Assert(blockedChanges == 0 && !marker && !blocked.WriteStarted && !blocked.MayHaveChanged, "backup failure stops mutation and resets unsaved started markers");

            RepairResult finalFailure = new RepairResult { BackupPath = "fixture-preserved.nrb" };
            RepairItemResult done = TrackedItem("FlushDns"); finalFailure.Items.Add(done); int finalSaves = 0;
            Action failedFinalSave = delegate { if (++finalSaves > 1) throw new IOException("fixture result checkpoint failed"); };
            continued = RescueEngine.RunTrackedItem(finalFailure, done, delegate { RescueEngine.BeginItemWrite(done, failedFinalSave, delegate { }); }, failedFinalSave, CancellationToken.None);
            Assert(!continued && done.State == RepairItemState.Completed && !finalFailure.JournalSaved, "failed result checkpoint preserves the actual completed request");
            RescueBackup safety = new RescueBackup { Operation = new RepairOperationRecord { OperationId = Guid.NewGuid().ToString("D"), Kind = "Restore", StartedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, Items = finalFailure.Items } };
            RescueEngine.AbortOperation(finalFailure, safety, failedFinalSave, new IOException("fixture outer failure"));
            Assert(finalFailure.BackupPath == "fixture-preserved.nrb" && finalFailure.Items[0] == done && finalFailure.Outcome == RepairOutcome.NeedsReview && !finalFailure.Success, "outer restore/result failure retains backup path and completed steps");

            RepairResult partialRestore = new RepairResult { BackupPath = "fixture-restore-safety.nrb" };
            partialRestore.Items.Add(new RepairItemResult { ActionId = "RestoreProxy", State = RepairItemState.Completed, Code = "Completed", WriteStarted = true, MayHaveChanged = true });
            partialRestore.Items.Add(TrackedItem("RestoreDns"));
            RescueEngine.AbortOperation(partialRestore, null, save, new IOException("fixture outer preparation failure"));
            Assert(partialRestore.Outcome == RepairOutcome.Partial && partialRestore.PartialSuccess && partialRestore.BackupPath == "fixture-restore-safety.nrb" && partialRestore.Items[0].State == RepairItemState.Completed && partialRestore.Items[1].State == RepairItemState.Failed, "outer restore failure cannot erase partial completion");
            RepairResult irreversible = new RepairResult(); irreversible.Items.Add(new RepairItemResult { ActionId = "FlushDns", State = RepairItemState.NotRestorable, Code = "NotRestorable" }); RescueEngine.SummarizeItems(irreversible);
            Assert(irreversible.Outcome == RepairOutcome.NoChange && !irreversible.Items[0].WriteStarted, "unrestorable cache and lease states are not reported as restored configuration");
        }

        private static void OperationJournalChecks()
        {
            string temporary = Path.Combine(Path.GetTempPath(), "NetworkRescueJournalTests-" + Guid.NewGuid().ToString("N"));
            RescueEngine engine = new RescueEngine();
            typeof(RescueEngine).GetProperty("BackupRoot").GetSetMethod(true).Invoke(engine, new object[] { temporary });
            MethodInfo save = typeof(RescueEngine).GetMethod("SaveBackup", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo load = typeof(RescueEngine).GetMethod("LoadBackup", BindingFlags.NonPublic | BindingFlags.Instance);
            try
            {
                Assert(engine.FindIncompleteOperation() == null && !Directory.Exists(temporary), "discovery never creates a missing backup directory");
                bool missing = false;
                try { engine.ReadBackupOperation(Path.Combine(temporary, "missing.nrb")); } catch (DirectoryNotFoundException) { missing = true; }
                Assert(missing && !Directory.Exists(temporary), "read-only backup inspection cannot create its storage root");
                Directory.CreateDirectory(temporary);
                string oldPath = Path.Combine(temporary, "legacy.nrb");
                byte[] oldPlain = Encoding.UTF8.GetBytes("<RescueBackup><Schema>1</Schema><CreatedUtc>2026-10-25T00:00:00Z</CreatedUtc><ProxyStarted>true</ProxyStarted><ProxyEnableExists>true</ProxyEnableExists><ProxyEnableValue>1</ProxyEnableValue><ProxyServer>http://fixture-user:fixture-secret@127.0.0.1:10809</ProxyServer><ProxyPac></ProxyPac><ProxyOverride></ProxyOverride><Adapters /></RescueBackup>");
                byte[] encrypted = ProtectedData.Protect(oldPlain, Encoding.UTF8.GetBytes("NetworkRescue.Backup.v1"), DataProtectionScope.CurrentUser);
                File.WriteAllBytes(oldPath, encrypted); Array.Clear(oldPlain, 0, oldPlain.Length);
                RescueBackup legacy = (RescueBackup)load.Invoke(engine, new object[] { oldPath });
                Assert(legacy.Operation == null && legacy.ProxyStarted && legacy.ProxyEnableValue == 1 && legacy.ProxyServer.Contains("fixture-secret"), "genuine schema-one XML without journal decrypts and preserves restorable proxy settings");
                Assert(engine.ReadBackupOperation(oldPath) == null && !engine.BackupNeedsAdministrator(oldPath), "legacy backup remains eligible for existing proxy restore semantics without a synthetic operation");

                RepairItemResult item = TrackedItem("DisableProxy");
                item.Message = "fixture-secret must never appear in startup metadata";
                RescueBackup backup = new RescueBackup { ProxyServer = legacy.ProxyServer, Operation = new RepairOperationRecord { OperationId = Guid.NewGuid().ToString("D"), Kind = "Repair", StartedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, Items = new List<RepairItemResult> { item } } };
                string path = (string)save.Invoke(engine, new object[] { backup, null });
                BackupOperationInfo info = engine.FindIncompleteOperation();
                Assert(info != null && info.OperationId == backup.Operation.OperationId && info.Kind == "Repair" && !info.Finished && info.Items[0].State == RepairItemState.Prepared, "prepared backup is discoverable before any setting write starts");
                Assert(info.Items[0].Message == null && !Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("fixture-secret"), "startup DTO omits free-form messages and disk journal remains DPAPI protected");
                info.Items[0].Code = "Cancelled";
                Assert(engine.ReadBackupOperation(path).Items[0].Code == "Prepared", "inspection returns an independent metadata snapshot");

                backup.ProxyStarted = true;
                RescueEngine.BeginItemWrite(item, delegate { save.Invoke(engine, new object[] { backup, path }); }, delegate { backup.ProxyStarted = false; });
                RescueEngine reopened = new RescueEngine(); typeof(RescueEngine).GetProperty("BackupRoot").GetSetMethod(true).Invoke(reopened, new object[] { temporary });
                info = reopened.FindIncompleteOperation();
                Assert(info != null && info.Items[0].State == RepairItemState.Writing && info.Items[0].WriteStarted && info.Items[0].MayHaveChanged, "a fresh engine identifies persisted writing state after a simulated process interruption");
                item.State = RepairItemState.Completed; item.Code = "Completed";
                save.Invoke(engine, new object[] { backup, path });
                Assert(engine.FindIncompleteOperation() != null, "crash between final item and operation-final checkpoint remains discoverable");

                byte[] previous = File.ReadAllBytes(path);
                bool lockedFailure = false;
                using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    try { save.Invoke(engine, new object[] { backup, path }); }
                    catch (TargetInvocationException ex) { lockedFailure = ex.InnerException is IOException; }
                }
                Assert(lockedFailure && previous.SequenceEqual(File.ReadAllBytes(path)), "actual atomic replacement failure preserves the previous encrypted checkpoint");
                Assert(Directory.GetFiles(temporary, "*.tmp").Length == 0, "failed atomic replacement cleans only its temporary file");
                backup.Operation.Finished = true; backup.Operation.Outcome = RepairOutcome.Completed;
                save.Invoke(engine, new object[] { backup, path });
                Assert(engine.FindIncompleteOperation() == null && engine.ReadBackupOperation(path).Finished, "finished operations are not repeatedly reported as interrupted");

                backup.Operation.Finished = false; item.State = RepairItemState.Prepared; item.Code = "Prepared"; item.WriteStarted = item.MayHaveChanged = false;
                save.Invoke(engine, new object[] { backup, path });
                string corrupt = Path.Combine(temporary, "corrupt.nrb"); File.WriteAllBytes(corrupt, new byte[] { 1, 2, 3 }); File.SetLastWriteTimeUtc(corrupt, DateTime.UtcNow.AddMinutes(1));
                Assert(engine.FindIncompleteOperation().OperationId == backup.Operation.OperationId, "newer corrupt file does not hide the older valid unfinished operation");
                backup.Operation.Schema = 99; RejectBackup(backup, "unknown journal schema rejected"); backup.Operation.Schema = 1;
                item.Code = "http://fixture-secret"; RejectBackup(backup, "free-form journal codes cannot escape into startup DTO"); item.Code = "Prepared";
                item.State = RepairItemState.Writing; RejectBackup(backup, "writing status without a persisted write marker is invalid"); item.State = RepairItemState.Prepared;
                backup.Operation.Items.Add(item); RejectBackup(backup, "duplicate journal actions rejected"); backup.Operation.Items.RemoveAt(1);
                backup.Operation.Finished = true; backup.Operation.Outcome = RepairOutcome.Failed; RejectBackup(backup, "finished journal cannot contain unattempted prepared actions"); backup.Operation.Finished = false;

                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1));
                for (int i = 0; i < 100; i++) { string candidate = Path.Combine(temporary, "newer-" + i.ToString("D3") + ".nrb"); File.WriteAllBytes(candidate, new byte[] { 1, 2, 3 }); File.SetLastWriteTimeUtc(candidate, DateTime.UtcNow.AddMinutes(2)); }
                Assert(engine.FindIncompleteOperation() == null, "discovery reads no more than the newest one hundred ordinary backup files");
                for (int i = 0; i < 100; i++) File.Delete(Path.Combine(temporary, "newer-" + i.ToString("D3") + ".nrb"));
                Assert(engine.FindIncompleteOperation() != null, "bounded discovery can find the operation once it is inside the inspected window");
            }
            finally
            {
                string full = Path.GetFullPath(temporary), expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (String.Equals(Path.GetDirectoryName(full), expectedParent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("NetworkRescueJournalTests-", StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full, true);
            }
        }

        private sealed class NoProbeProgress : IProgress<string>
        {
            public int Calls;
            public void Report(string value) { Calls++; throw new InvalidOperationException("Fixture must never begin actual diagnosis or restore."); }
        }

        private static RescueEngine EngineAt(string root)
        {
            RescueEngine engine = new RescueEngine();
            typeof(RescueEngine).GetProperty("BackupRoot").GetSetMethod(true).Invoke(engine, new object[] { root });
            return engine;
        }

        private static int HoldFixtureLock(string root)
        {
            string full = Path.GetFullPath(root), parent = Path.GetDirectoryName(full);
            Guid fixtureId;
            string name = Path.GetFileName(parent);
            if (Path.GetFileName(full) != "Backups" || !String.Equals(Path.GetDirectoryName(parent), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || !name.StartsWith("NetworkRescueLockTests-", StringComparison.Ordinal) || !Guid.TryParseExact(name.Substring("NetworkRescueLockTests-".Length), "N", out fixtureId)) return 2;
            using (EngineAt(full).AcquireOperationLock())
            using (ManualResetEvent lifetime = new ManualResetEvent(false))
            {
                Console.WriteLine("FIXTURE_LOCK_HELD"); Console.Out.Flush();
                lifetime.WaitOne();
            }
            return 0;
        }

        private static void OperationLockChecks()
        {
            string temporary = Path.Combine(Path.GetTempPath(), "NetworkRescueLockTests-" + Guid.NewGuid().ToString("N"));
            string root = Path.Combine(temporary, "Backups"), lockPath = Path.Combine(root, "network-operation.lock");
            RescueEngine first = EngineAt(root), second = EngineAt(root);
            try
            {
                using (FileStream held = first.AcquireOperationLock())
                {
                    bool busy = false;
                    try { using (second.AcquireOperationLock()) { } } catch (RepairRefusedException ex) { busy = ex.Code == "OperationBusy"; }
                    Assert(busy, "different engine instances cannot share the same write lock");
                    bool shareDenied = false;
                    try { using (new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { } } catch (IOException) { shareDenied = true; }
                    Assert(shareDenied, "actual lock handle enforces FileShare.None");
                    NoProbeProgress progress = new NoProbeProgress();
                    RepairResult blocked = second.RepairAsync(new[] { new RepairAction { Id = "FlushDns" } }, progress, CancellationToken.None).GetAwaiter().GetResult();
                    Assert(blocked.Outcome == RepairOutcome.Rejected && blocked.Code == "OperationBusy" && blocked.Items[0].State == RepairItemState.Rejected && blocked.Items[0].Code == "OperationBusy", "busy repair returns a typed safety refusal");
                    Assert(blocked.BackupPath == null && !blocked.Items[0].WriteStarted && progress.Calls == 0 && !blocked.BeforeSampledUtc.HasValue, "busy refusal occurs before capture, backup or mutation");
                    string original = Path.Combine(root, "fixture.nrb"); File.WriteAllBytes(original, new byte[] { 1, 2, 3 });
                    RepairResult restore = second.RestoreAsync(original, progress, CancellationToken.None).GetAwaiter().GetResult();
                    Assert(restore.Outcome == RepairOutcome.Rejected && restore.Code == "OperationBusy" && restore.BackupPath == null && progress.Calls == 0, "restore shares the repair lock before reading configurations or creating a safety backup");
                    Assert(Directory.GetFiles(root, "*.nrb").Length == 1 && File.ReadAllBytes(original).SequenceEqual(new byte[] { 1, 2, 3 }), "failed lock acquisition does not create or overwrite backups");
                }
                using (FileStream acquired = second.AcquireOperationLock()) Assert(acquired.Length == 0, "normal disposal releases the handle and a leftover empty file does not mean busy");

                string badRoot = Path.Combine(temporary, "not-a-directory"); File.WriteAllText(badRoot, "fixture");
                RescueEngine unavailable = EngineAt(badRoot);
                bool readError = false;
                try { unavailable.FindIncompleteOperation(); } catch (IOException ex) { readError = !ex.Message.Contains(temporary); }
                Assert(readError && File.ReadAllText(badRoot) == "fixture", "top-level discovery failure is reported without exposing the path or treating it as no pending operation");
                bool unsafeRoot = false;
                try { using (unavailable.AcquireOperationLock()) { } } catch (RepairRefusedException ex) { unsafeRoot = ex.Code == "OperationLockUnavailable"; }
                Assert(unsafeRoot, "invalid lock root fails closed without changing the file");

                using (Process child = new Process())
                {
                    bool started = false;
                    try
                    {
                        child.StartInfo = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--hold-operation-lock \"" + root + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                        started = child.Start(); if (!started) throw new Exception("Fixture child could not start.");
                        Task<string> ready = child.StandardOutput.ReadLineAsync();
                        if (!ready.Wait(5000) || ready.Result != "FIXTURE_LOCK_HELD") throw new Exception("Fixture child did not acquire its lock.");
                        bool childBusy = false;
                        try { using (first.AcquireOperationLock()) { } } catch (RepairRefusedException ex) { childBusy = ex.Code == "OperationBusy"; }
                        Assert(childBusy, "a separate fixture process excludes writes in this process");
                        child.Kill(); if (!child.WaitForExit(5000)) throw new Exception("Owned fixture child did not exit.");
                        using (first.AcquireOperationLock()) Assert(true, "OS releases the file lock after the owned fixture process is terminated");
                    }
                    finally { if (started && !child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
                }
            }
            finally
            {
                string full = Path.GetFullPath(temporary), expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (String.Equals(Path.GetDirectoryName(full), expectedParent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("NetworkRescueLockTests-", StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full, true);
            }
        }

        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--hold-operation-lock") return HoldFixtureLock(args[1]);
            if (args.Length == 1 && args[0] == "--ipv6-loopback")
            {
                if (!Socket.OSSupportsIPv6) { Console.WriteLine("SKIP: IPv6 unavailable on this host."); return 0; }
                TcpListener listener = new TcpListener(IPAddress.IPv6Loopback, 0);
                try
                {
                    listener.Start();
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    Probe probe = RescueEngine.TcpProbe("::1", port);
                    if (!probe.Success) throw new Exception("IPv6 loopback probe failed: " + probe.Detail);
                    Console.WriteLine("PASS: actual IPv6 loopback TCP probe. No network configuration modified.");
                    return 0;
                }
                finally { listener.Stop(); }
            }
            if (args.Length == 1 && args[0] == "--diagnose")
            {
                DiagnosticReport report = new RescueEngine().DiagnoseAsync(null, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine(report.ToPlainText());
                return 0;
            }
            try
            {
                Snapshot offline = Sample(false, false, false);
                offline.Adapters.Clear();
                Assert(!RescueEngine.BuildReport(offline).InternetAvailable, "offline does not claim Internet");
                Assert(Actions(offline).Count == 0, "offline without evidence offers no reset");

                Snapshot apipa = Sample(false, false, false);
                apipa.Adapters[0].Addresses = new List<string> { "169.254.4.5" };
                apipa.Adapters[0].Gateways.Clear();
                Assert(Actions(apipa).Any(delegate(RepairAction a) { return a.Id == "RenewDhcp" && a.AdapterId == Id; }), "APIPA on DHCP offers targeted renew");
                apipa.Adapters[0].InformationReadable = false;
                Assert(!Actions(apipa).Any(delegate(RepairAction a) { return a.Id == "RenewDhcp"; }), "partial adapter reads cannot trigger DHCP renewal");
                apipa.Adapters[0].InformationReadable = true;
                apipa.Adapters[0].Dhcp = false;
                Assert(Actions(apipa).Count == 0, "static APIPA does not renew DHCP");

                Snapshot blockedDirect = Sample(false, true, true);
                Assert(RescueEngine.BuildReport(blockedDirect).InternetAvailable, "HTTP success overrides blocked direct/ICMP assumptions");
                Assert(Actions(blockedDirect).Count == 0, "no repair when only direct probes blocked");
                Assert(RescueEngine.BuildReport(blockedDirect).ToPlainText().Contains("ICMP"), "report states ICMP is not decisive");
                blockedDirect.Adapters.Clear();
                Assert(RescueEngine.BuildReport(blockedDirect).Items.First(delegate(DiagnosticItem item) { return item.Id == "Adapters"; }).State != CheckState.Failed, "IPv6/HTTP-only online host does not fail IPv4 item");
                blockedDirect.Http.ForEach(delegate(Probe p) { p.Marker = false; });
                Assert(RescueEngine.BuildReport(blockedDirect).Summary.Contains("固定联网标记尚未验证"), "HTTPS fallback is distinguished from connectivity marker");
                Snapshot quietGateway = Sample(true, true, true);
                quietGateway.GatewayProbes.Add(new Probe { Label = "gateway ICMP", Success = false });
                Assert(RescueEngine.BuildReport(quietGateway).Items.First(delegate(DiagnosticItem item) { return item.Id == "Gateway"; }).State == CheckState.Unknown, "quiet gateway is unknown rather than failed");
                quietGateway.GatewayProbes.Add(new Probe { Label = "gateway:443", Success = true });
                Assert(RescueEngine.BuildReport(quietGateway).Items.First(delegate(DiagnosticItem item) { return item.Id == "Gateway"; }).State == CheckState.Passed, "TCP gateway response overrides blocked ICMP");

                Snapshot dnsFailure = Sample(true, false, false);
                Assert(Actions(dnsFailure).Any(delegate(RepairAction a) { return a.Id == "FlushDns"; }), "DNS failure with IP connectivity offers flush");
                Assert(Actions(dnsFailure).Any(delegate(RepairAction a) { return a.Id == "AutoDns" && !a.Suggested; }), "automatic DNS is optional and unchecked");
                dnsFailure.Adapters[0].Dhcp = false;
                Assert(!Actions(dnsFailure).Any(delegate(RepairAction a) { return a.Id == "AutoDns"; }), "static adapter DNS preserved");
                dnsFailure = Sample(true, false, false);
                dnsFailure.Adapters[0].DnsAutomatic = true;
                Assert(!Actions(dnsFailure).Any(delegate(RepairAction a) { return a.Id == "AutoDns"; }), "already automatic DNS not changed");
                dnsFailure.Adapters[0].DnsAutomatic = false;
                dnsFailure.Adapters[0].DnsConfigurationReadable = false;
                Assert(!Actions(dnsFailure).Any(delegate(RepairAction a) { return a.Id == "AutoDns"; }), "incomplete DNS configuration never offered for modification");
                dnsFailure.Adapters[0].DnsConfigurationReadable = true;
                dnsFailure.Adapters[0].ConfiguredDns.Clear();
                Assert(!Actions(dnsFailure).Any(delegate(RepairAction a) { return a.Id == "AutoDns"; }), "empty original custom DNS cannot produce a repair");

                Snapshot proxy = Sample(false, false, true);
                proxy.Proxy.Enabled = true;
                proxy.Proxy.Server = "127.0.0.1:10809";
                proxy.Proxy.Endpoints.Add(new Probe { Label = "127.0.0.1:10809", Success = true });
                Assert(RescueEngine.BuildReport(proxy).InternetAvailable, "normal local proxy is online");
                Assert(Actions(proxy).Count == 0, "normal proxy kept");
                proxy = Sample(true, true, false);
                proxy.Proxy.Enabled = true;
                proxy.Proxy.Server = "127.0.0.1:10809";
                proxy.Proxy.Endpoints.Add(new Probe { Label = "127.0.0.1:10809", Success = true });
                Assert(!Actions(proxy).Any(delegate(RepairAction a) { return a.Id == "DisableProxy"; }), "intentional listening proxy is not called harmful");
                proxy.Proxy.Endpoints[0].Success = false;
                Assert(Actions(proxy).Any(delegate(RepairAction a) { return a.Id == "DisableProxy" && !a.Suggested; }), "bad proxy plus direct connectivity offers unchecked disable");
                proxy.Proxy.Endpoints[0].Uncertain = true;
                Assert(!Actions(proxy).Any(delegate(RepairAction a) { return a.Id == "DisableProxy"; }), "unsupported address family or uncertain proxy probe cannot disable proxy");
                proxy.Proxy.Endpoints[0].Uncertain = false;
                proxy.Tcp.ForEach(delegate(Probe p) { p.Success = false; });
                Assert(!Actions(proxy).Any(delegate(RepairAction a) { return a.Id == "DisableProxy"; }), "bad proxy without direct evidence preserved");
                proxy.Tcp[0].Success = true;
                proxy.Proxy.Pac = "https://user:secret@example.com/private-token.pac";
                Assert(!Actions(proxy).Any(delegate(RepairAction a) { return a.Id == "DisableProxy"; }), "PAC excludes manual proxy fix");
                string text = RescueEngine.BuildReport(proxy).ToPlainText();
                Assert(!text.Contains("secret") && !text.Contains("private-token"), "public report redacts PAC credentials and path");

                Snapshot multi = Sample(true, true, true);
                AdapterSnapshot spare = Adapter(OtherId);
                spare.Addresses = new List<string> { "169.254.20.30" };
                multi.Adapters.Add(spare);
                Assert(!Actions(multi).Any(delegate(RepairAction a) { return a.Id == "RenewDhcp"; }), "online multihomed machine does not renew unused APIPA interface");
                multi = Sample(false, false, false);
                multi.Proxy.Enabled = true;
                Assert(Actions(multi).Count == 0, "unparseable proxy plus no DNS evidence offers no speculative fix");
                multi.Dns.Clear();
                multi.Tcp[0].Success = true;
                Assert(!Actions(multi).Any(delegate(RepairAction a) { return a.Id == "FlushDns"; }), "missing DNS results are not failures");

                Assert(RescueEngine.IsAllowedAction(new RepairAction { Id = "FlushDns" }), "flush allowlist");
                Assert(RescueEngine.IsAllowedAction(new RepairAction { Id = "AutoDns", AdapterId = Id }), "GUID adapter accepted");
                Assert(!RescueEngine.IsAllowedAction(new RepairAction { Id = "WinsockReset" }), "unknown reset rejected");
                Assert(!RescueEngine.IsAllowedAction(new RepairAction { Id = "RenewDhcp", AdapterId = Id + " & calc.exe" }), "adapter injection rejected");
                Assert(!RescueEngine.IsAllowedAction(new RepairAction { Id = "DisableProxy", AdapterId = Id }), "unexpected adapter rejected");
                Assert(!RescueEngine.IsAllowedAction(new RepairAction { Id = "RenewDhcp", AdapterId = Guid.Empty.ToString("D") }), "empty GUID rejected");
                RepairResult invalid = new RescueEngine().RepairAsync(new[] { new RepairAction { Id = "Unknown" } }, null, CancellationToken.None).GetAwaiter().GetResult();
                Assert(!invalid.Success && invalid.BackupPath == null, "invalid action rejected before capture or mutation");
                Assert(invalid.Outcome == RepairOutcome.Rejected && invalid.Items.Count == 1 && invalid.Items[0].State == RepairItemState.Rejected, "invalid action produces an explicit typed safe refusal");
                Assert(!invalid.BeforeInternetAvailable.HasValue && !invalid.BeforeSampledUtc.HasValue, "rejected repair does not reuse a stale pre-operation report");
                DateTime sampledUtc = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
                RepairResult before = new RepairResult();
                Snapshot beforeSnapshot = Sample(true, true, true);
                RescueEngine.RecordBeforeSample(before, beforeSnapshot, sampledUtc);
                Assert(before.BeforeInternetAvailable == true && before.BeforeSampledUtc == sampledUtc, "fresh successful HTTP baseline is typed and timestamped");
                beforeSnapshot = Sample(false, false, false);
                RescueEngine.RecordBeforeSample(before, beforeSnapshot, sampledUtc);
                Assert(before.BeforeInternetAvailable == false && before.BeforeSampledUtc == sampledUtc, "completed unsuccessful HTTP baseline records unconfirmed reachability without claiming outage cause");
                beforeSnapshot.Http[0].Uncertain = true;
                RescueEngine.RecordBeforeSample(before, beforeSnapshot, sampledUtc);
                Assert(!before.BeforeInternetAvailable.HasValue && before.BeforeSampledUtc == sampledUtc, "unknown HTTP baseline is not stored as false");
                beforeSnapshot = Sample(true, true, true);
                beforeSnapshot.Proxy.Pac = "https://fixture.test/proxy.pac";
                RescueEngine.RecordBeforeSample(before, beforeSnapshot, sampledUtc);
                Assert(!before.BeforeInternetAvailable.HasValue && !before.BeforeSampledUtc.HasValue, "PAC baseline stays unknown without evaluating script");
                beforeSnapshot.Proxy.Pac = ""; beforeSnapshot.Proxy.AutoDetect = true;
                RescueEngine.RecordBeforeSample(before, beforeSnapshot, sampledUtc);
                Assert(!before.BeforeInternetAvailable.HasValue, "WPAD baseline stays unknown");
                beforeSnapshot.Proxy.AutoDetect = false; beforeSnapshot.Proxy.AutoConfigKnown = false;
                RescueEngine.RecordBeforeSample(before, beforeSnapshot, sampledUtc);
                Assert(!before.BeforeInternetAvailable.HasValue, "unreadable automatic-proxy baseline stays unknown");
                beforeSnapshot.Proxy.AutoConfigKnown = true; beforeSnapshot.Http[0].AutomaticProxySkipped = true;
                RescueEngine.RecordBeforeSample(before, beforeSnapshot, sampledUtc);
                Assert(!before.BeforeInternetAvailable.HasValue && !before.BeforeSampledUtc.HasValue, "skipped HTTP cannot manufacture a pre-operation observation");
                beforeSnapshot.Http.Clear();
                RescueEngine.RecordBeforeSample(before, beforeSnapshot, sampledUtc);
                Assert(!before.BeforeInternetAvailable.HasValue && !before.BeforeSampledUtc.HasValue, "missing HTTP baseline stays absent");
                RepairResult invalidRestore = new RescueEngine().RestoreAsync("outside.nrb", null, CancellationToken.None).GetAwaiter().GetResult();
                Assert(!invalidRestore.Success && !invalidRestore.BeforeInternetAvailable.HasValue && !invalidRestore.BeforeSampledUtc.HasValue, "restore validation error has no synthetic HTTP baseline");

                string root = Path.Combine(Path.GetTempPath(), "NetworkRescueTests", "Backups");
                Assert(RescueEngine.IsAllowedBackupPath(Path.Combine(root, "good.nrb"), root), "direct child backup accepted");
                Assert(!RescueEngine.IsAllowedBackupPath(Path.Combine(root, "..", "outside.nrb"), root), "parent traversal rejected");
                Assert(!RescueEngine.IsAllowedBackupPath(Path.Combine(root + "-evil", "outside.nrb"), root), "prefix sibling rejected");
                Assert(!RescueEngine.IsAllowedBackupPath(Path.Combine(root, "nested", "x.nrb"), root), "nested path rejected");
                Assert(!RescueEngine.IsAllowedBackupPath(Path.Combine(root, "x.xml"), root), "wrong extension rejected");
                Assert(!RescueEngine.IsAllowedBackupPath(Path.Combine(root, "x.nrb:stream.nrb"), root), "alternate data stream rejected");

                RescueBackup good = new RescueBackup();
                good.Adapters.Add(new AdapterBackup { Id = Id, Name = "adapter", Dhcp = true, DnsStarted = true, Dns = new[] { "8.8.8.8" } });
                RescueEngine.ValidateBackup(good);
                Assert(true, "valid backup accepted");
                good.Schema = 99;
                RejectBackup(good, "unknown backup schema rejected");
                good.Schema = 1;
                good.Adapters[0].Dns = new[] { "8.8.8.8 & calc.exe" };
                RejectBackup(good, "DNS injection rejected");
                good.Adapters[0].Dns = new string[0];
                RejectBackup(good, "empty custom DNS cannot restore");
                good.Adapters[0].Dns = new[] { "8.8.8.8" };
                good.Adapters.Add(good.Adapters[0]);
                RejectBackup(good, "duplicate adapter rejected");
                good.Adapters.RemoveAt(1);
                good.Adapters[0].Dhcp = false;
                RejectBackup(good, "static adapter cannot carry DNS restore action");

                Assert(RescueEngine.ParseProxyEndpoints("http=127.0.0.1:10809;https=127.0.0.1:10809").Count == 1, "proxy endpoints deduplicated");
                Assert(RescueEngine.ParseProxyEndpoints("http://user:password@127.0.0.1:10809")[0].Host == "127.0.0.1", "proxy credentials not used in endpoint label");
                Assert(RescueEngine.ParseProxyEndpoints("http=[::1]:10809;https=[::1]:10809").Count == 1, "IPv6 proxy endpoints parsed and deduplicated");
                Assert(RescueEngine.ProbeAddressFamily("::1") == AddressFamily.InterNetworkV6, "IPv6 literal uses IPv6 socket");
                Assert(RescueEngine.ProbeAddressFamily("[::1]") == AddressFamily.InterNetworkV6, "bracketed IPv6 literal uses IPv6 socket");
                Assert(RescueEngine.ProbeAddressFamily("127.0.0.1") == AddressFamily.InterNetwork, "IPv4 literal uses IPv4 socket");
                Assert(RescueEngine.ProbeAddressFamily("localhost") == (Socket.OSSupportsIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork), "hostnames use dual-stack socket when supported");
                Snapshot uncertainDns = Sample(true, false, false);
                uncertainDns.Dns.ForEach(delegate(Probe p) { p.Uncertain = true; });
                Assert(!uncertainDns.DnsFailed && !Actions(uncertainDns).Any(delegate(RepairAction a) { return a.Id == "FlushDns" || a.Id == "AutoDns"; }), "all uncertain DNS probes do not establish DNS failure or justify resetting configuration");
                uncertainDns.Dns[0].Uncertain = false;
                Assert(!uncertainDns.DnsFailed, "one definite DNS failure is insufficient when another is uncertain");
                uncertainDns.Dns[1].Uncertain = false;
                Assert(uncertainDns.DnsFailed, "at least two definite DNS failures meet the existing failure requirement");
                uncertainDns.Dns[0].Success = true;
                Assert(!uncertainDns.DnsFailed, "one successful system resolution rules out the all-failed classification");
                BackupRoundTrip();
                TypedOperationChecks();
                OperationJournalChecks();
                OperationLockChecks();
                VpnResidualChecks();
                Console.WriteLine("PASS: " + assertions + " assertions. No real repair executed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }
    }
}
