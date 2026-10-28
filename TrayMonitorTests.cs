using System;

namespace NetworkRescue
{
    internal static class TrayMonitorTests
    {
        private static int checks;
        private static readonly DateTime Epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
        private static MonitorSample Sample(int seconds, bool response, string id = "a", string method = "ICMP")
        {
            return new MonitorSample { TargetId = id, TargetHost = "example.test", Method = method, TimeUtc = Epoch.AddSeconds(seconds),
                Success = response, ResponseReceived = response, HttpSuccess = response };
        }
        private static MonitorAlertTracker Tracker(int intervalMs = 1000)
        {
            MonitorAlertTracker value = new MonitorAlertTracker(TimeSpan.Zero, TimeSpan.Zero);
            value.BeginSession(new[] { "a" }, intervalMs); return value;
        }
        private static MonitorAlert Down(MonitorAlertTracker tracker, int start, string id = "a")
        {
            Check(tracker.Observe(Sample(start, false, id)) == null, "One missing response is not an outage");
            Check(tracker.Observe(Sample(start + 1, false, id)) == null, "Two missing responses are not an outage");
            return tracker.Observe(Sample(start + 2, false, id));
        }
        public static int Main(string[] args)
        {
            try
            {
                MonitorAlertTracker tracker = Tracker();
                Check(tracker.Observe(Sample(0, true)) == null && tracker.Observe(Sample(1, true)) == null, "Initial success is not a recovery");
                Check(tracker.Observe(Sample(2, false)) == null && tracker.Observe(Sample(3, false)) == null, "Short loss is suppressed");
                Check(tracker.Observe(Sample(4, true)) == null, "A responding sample resets consecutive failures");
                MonitorAlert alert = Down(tracker, 5);
                Check(alert != null && alert.Kind == MonitorAlertKind.NoResponse && alert.TargetId == "a" && alert.TimeUtc == Epoch.AddSeconds(7), "Third failure returns typed target-specific alert");
                Check(alert.TargetHost == "example.test", "Alert preserves the affected target label");
                Check(tracker.Observe(Sample(8, false)) == null, "An unchanged outage does not repeat");
                Check(tracker.Observe(Sample(9, true)) == null, "One response is not a recovery");
                Check(tracker.Observe(Sample(10, false)) == null && tracker.Observe(Sample(11, true)) == null, "Recovery jitter resets the response streak");
                alert = tracker.Observe(Sample(12, true));
                Check(alert != null && alert.Kind == MonitorAlertKind.Recovered, "Two consecutive responses recover");
                Check(tracker.Observe(Sample(13, true)) == null, "An unchanged healthy state does not repeat");

                tracker = Tracker(); Down(tracker, 0);
                MonitorSample forbidden = Sample(3, false, "a", "HTTPS"); forbidden.HttpStatusCode = 403; forbidden.ResponseReceived = true;
                Check(tracker.Observe(forbidden) == null, "403 is first HTTP response, independent of HTTP success");
                forbidden = Sample(4, false, "a", "https"); forbidden.HttpStatusCode = 403; forbidden.ResponseReceived = true;
                alert = tracker.Observe(forbidden);
                Check(alert != null && alert.Kind == MonitorAlertKind.Recovered, "Repeated HTTP 403 proves response and recovers");
                tracker = Tracker();
                for (int i = 0; i < 5; i++)
                {
                    MonitorSample fail = Sample(i, true, "a", "HTTPS"); fail.ResponseReceived = false;
                    alert = tracker.Observe(fail);
                    Check((i == 2) == (alert != null), "HTTPS response status uses ResponseReceived rather than Success");
                }

                tracker = Tracker(); tracker.SetMuted(true);
                Check(Down(tracker, 0) == null, "Muted transitions are suppressed");
                tracker.SetMuted(false);
                Check(tracker.Observe(Sample(3, false)) == null, "Unmuting does not replay an old outage");
                tracker.Observe(Sample(4, true)); alert = tracker.Observe(Sample(5, true));
                Check(alert == null, "A muted outage cannot produce an isolated recovery after unmuting");
                tracker.SetMuted(true); Down(tracker, 6); tracker.Observe(Sample(9, true)); tracker.Observe(Sample(10, true)); tracker.SetMuted(false);
                Check(tracker.Observe(Sample(11, true)) == null, "Muted recovery is not replayed either");

                tracker = new MonitorAlertTracker(TimeSpan.FromSeconds(60), TimeSpan.Zero); tracker.BeginSession(new[] { "a" }, 1000);
                Check(Down(tracker, 0) != null, "First alert bypasses cooldown");
                tracker.Observe(Sample(3, true)); Check(tracker.Observe(Sample(4, true)) != null, "Opposite kind can recover during target cooldown");
                Check(Down(tracker, 5) == null, "Same target and kind obey sixty-second cooldown");
                Check(tracker.Observe(Sample(8, false)) == null, "Suppressed transition is not queued");
                tracker.Observe(Sample(9, true)); Check(tracker.Observe(Sample(10, true)) == null, "Suppressed outage has no isolated recovery");
                tracker.ResetContinuity();
                Check(Down(tracker, 60) != null, "Same-kind cooldown allows alert exactly sixty seconds later");

                tracker = new MonitorAlertTracker(); tracker.BeginSession(new[] { "a", "b", "c", "d" }, 1000);
                Check(Down(tracker, 0, "a") != null, "First target alerts");
                Check(Down(tracker, 0, "b") == null, "Global cooldown prevents simultaneous target notification spam");
                Check(tracker.Observe(Sample(12, false, "b")) == null, "Global suppression does not replay an unchanged state");
                Check(Down(tracker, 10, "c") != null, "Global cooldown allows a new transition at ten seconds");
                tracker = new MonitorAlertTracker(); tracker.BeginSession(new[] { "a" }, 1000);
                Check(Down(tracker, 0) != null, "Default policy emits first outage");
                tracker.Observe(Sample(3, true)); alert = tracker.Observe(Sample(4, true));
                Check(alert != null && alert.Kind == MonitorAlertKind.Recovered, "Paired recovery bypasses both target and global cooldown");
                Check(Down(tracker, 5) == null, "Repeated outage remains throttled after its paired recovery");
                tracker = Tracker(); Down(tracker, 0); tracker.SetMuted(true); tracker.Observe(Sample(3, true)); tracker.Observe(Sample(4, true)); tracker.SetMuted(false);
                Check(tracker.Observe(Sample(5, true)) == null, "A paired recovery consumed during mute never replays");

                tracker = Tracker(); tracker.Observe(Sample(0, false)); tracker.Observe(Sample(1, false));
                Check(tracker.Observe(Sample(12, false)) == null, "A long sample gap discards previous failures");
                Check(tracker.Observe(Sample(13, false)) == null && tracker.Observe(Sample(14, false)) != null, "Gap resumes counting from current sample");
                Check(tracker.Observe(Sample(25, true)) == null && tracker.Observe(Sample(26, true)) == null, "After a gap unknown does not produce a synthetic recovery");
                tracker = Tracker(10000); tracker.Observe(Sample(0, false)); tracker.Observe(Sample(10, false));
                Check(tracker.Observe(Sample(40, false)) != null, "At three intervals the streak remains continuous");
                tracker = Tracker(10000); tracker.Observe(Sample(0, false)); tracker.Observe(Sample(10, false));
                Check(tracker.Observe(Sample(41, false)) == null, "Beyond three intervals resets the streak");
                tracker = Tracker(); tracker.Observe(Sample(0, false)); tracker.Observe(Sample(1, false)); tracker.ResetContinuity();
                Check(tracker.Observe(Sample(2, false)) == null, "Explicit suspend/resume resets consecutive evidence");
                Check(tracker.Observe(Sample(2, false)) == null && tracker.Observe(Sample(1, false)) == null, "Duplicate and out-of-order samples cannot inflate a streak");
                Check(tracker.Observe(Sample(3, false)) == null && tracker.Observe(Sample(4, false)) != null, "Only distinct increasing samples count");

                tracker.Stop(); Check(tracker.Observe(Sample(5, false)) == null, "Stop ignores further samples");
                tracker.BeginSession(new[] { "a" }, 1000);
                Check(tracker.Observe(Sample(6, false)) == null, "A new session starts fresh");
                Check(tracker.Observe(Sample(7, false, "unknown")) == null, "Unregistered targets cannot enter session state");
                Check(tracker.Observe(null) == null, "Absent samples do not invent loss");
                MonitorSample local = Sample(7, false); local.TimeUtc = DateTime.SpecifyKind(local.TimeUtc, DateTimeKind.Local);
                Check(tracker.Observe(local) == null, "Non-UTC timestamp cannot distort continuity");
                tracker.Reset(); Check(tracker.Observe(Sample(8, false)) == null, "Reset ends the active session");
                bool rejected = false;
                try { tracker.BeginSession(new[] { "a", "b", "c", "d", "e" }, 1000); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Session accepts at most four targets");
                rejected = false;
                try { tracker.BeginSession(new[] { "a", "a" }, 1000); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Duplicate targets are rejected");
                Console.WriteLine("Tray monitor policy checks passed: " + checks + " (no NotifyIcon created)."); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
