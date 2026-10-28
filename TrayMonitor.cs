using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace NetworkRescue
{
    public enum MonitorAlertKind { NoResponse, Recovered }

    public sealed class MonitorAlert
    {
        public string TargetId { get; private set; }
        public string TargetHost { get; private set; }
        public MonitorAlertKind Kind { get; private set; }
        public DateTime TimeUtc { get; private set; }
        public MonitorAlert(string targetId, string targetHost, MonitorAlertKind kind, DateTime timeUtc)
        { TargetId = targetId; TargetHost = targetHost; Kind = kind; TimeUtc = timeUtc; }
    }

    /// <summary>Thread-safe, session-only policy. It creates no timers, threads, UI, files or native icons.</summary>
    public sealed class MonitorAlertTracker
    {
        private sealed class TargetState
        {
            public int Failures, Responses;
            public bool NoResponse, OutageNotified;
            public DateTime? LastSample, LastFailureAlert;
            public void ClearContinuity() { Failures = Responses = 0; NoResponse = OutageNotified = false; LastSample = null; }
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, TargetState> targets = new Dictionary<string, TargetState>(StringComparer.Ordinal);
        private readonly TimeSpan targetCooldown, globalCooldown;
        private TimeSpan maximumGap;
        private DateTime? lastAlert;
        private bool active, muted;

        public MonitorAlertTracker() : this(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10)) { }
        public MonitorAlertTracker(TimeSpan targetCooldown, TimeSpan globalCooldown)
        {
            if (targetCooldown < TimeSpan.Zero || globalCooldown < TimeSpan.Zero) throw new ArgumentOutOfRangeException("cooldown");
            this.targetCooldown = targetCooldown; this.globalCooldown = globalCooldown;
        }
        public void BeginSession(IEnumerable<string> targetIds, int intervalMs)
        {
            if (targetIds == null) throw new ArgumentNullException("targetIds");
            if (intervalMs < 1000 || intervalMs > 10000) throw new ArgumentOutOfRangeException("intervalMs");
            Dictionary<string, TargetState> snapshot = new Dictionary<string, TargetState>(StringComparer.Ordinal);
            foreach (string id in targetIds)
            {
                if (String.IsNullOrWhiteSpace(id) || id.Length > 32 || snapshot.ContainsKey(id) || snapshot.Count == 4) throw new ArgumentException("InvalidMonitorTargetId");
                snapshot.Add(id, new TargetState());
            }
            if (snapshot.Count == 0) throw new ArgumentException("InvalidMonitorTargetCount");
            lock (gate)
            {
                targets.Clear();
                foreach (KeyValuePair<string, TargetState> entry in snapshot) targets.Add(entry.Key, entry.Value);
                maximumGap = TimeSpan.FromMilliseconds(Math.Max(3L * intervalMs, 10000L));
                lastAlert = null; active = true;
            }
        }
        public void SetMuted(bool value) { lock (gate) muted = value; }
        public void Stop() { lock (gate) { active = false; targets.Clear(); lastAlert = null; } }
        public void Reset() { Stop(); }
        /// <summary>Call on suspend/resume. Unknown is never reported as an outage or recovery.</summary>
        public void ResetContinuity()
        {
            lock (gate) foreach (TargetState state in targets.Values) state.ClearContinuity();
        }
        public MonitorAlert Observe(MonitorSample sample)
        {
            if (sample == null || sample.TimeUtc.Kind != DateTimeKind.Utc || String.IsNullOrEmpty(sample.TargetId)) return null;
            lock (gate)
            {
                TargetState state;
                if (!active || !targets.TryGetValue(sample.TargetId, out state)) return null;
                bool isHttp = String.Equals(sample.Method, "https", StringComparison.OrdinalIgnoreCase);
                bool isIcmp = String.Equals(sample.Method, "icmp", StringComparison.OrdinalIgnoreCase);
                if (!isHttp && !isIcmp) { state.ClearContinuity(); return null; }
                if (state.LastSample.HasValue)
                {
                    if (sample.TimeUtc <= state.LastSample.Value) return null;
                    if (sample.TimeUtc - state.LastSample.Value > maximumGap) state.ClearContinuity();
                }
                state.LastSample = sample.TimeUtc;
                bool response = isHttp ? sample.ResponseReceived : sample.Success;
                MonitorAlertKind? transition = null;
                if (response)
                {
                    state.Failures = 0;
                    state.Responses = Math.Min(2, state.Responses + 1);
                    if (state.NoResponse && state.Responses == 2) { state.NoResponse = false; transition = MonitorAlertKind.Recovered; }
                }
                else
                {
                    state.Responses = 0;
                    state.Failures = Math.Min(3, state.Failures + 1);
                    if (!state.NoResponse && state.Failures == 3) { state.NoResponse = true; state.OutageNotified = false; transition = MonitorAlertKind.NoResponse; }
                }
                // Suppressed transitions still advance state; there is deliberately no pending queue.
                if (!transition.HasValue) return null;
                if (transition == MonitorAlertKind.Recovered)
                {
                    bool paired = state.OutageNotified;
                    state.OutageNotified = false;
                    // One recovery pairs with an emitted outage; cooldown never swallows that pair.
                    if (muted || !paired) return null;
                }
                else
                {
                    if (muted || (state.LastFailureAlert.HasValue && sample.TimeUtc - state.LastFailureAlert.Value < targetCooldown)
                        || (lastAlert.HasValue && sample.TimeUtc - lastAlert.Value < globalCooldown)) return null;
                    state.LastFailureAlert = sample.TimeUtc;
                    state.OutageNotified = true;
                    lastAlert = sample.TimeUtc;
                }
                return new MonitorAlert(sample.TargetId, sample.TargetHost, transition.Value, sample.TimeUtc);
            }
        }
    }

    /// <summary>All calls belong to the existing WPF UI dispatcher. Only explicit enablement creates an icon.</summary>
    public sealed class TrayMonitor : IDisposable
    {
        private readonly Dispatcher dispatcher;
        private readonly Func<string, string> text;
        private readonly Action showWindow, stopMonitoring, exitApplication;
        private readonly Action<bool> mutedChanged;
        private Forms.NotifyIcon icon;
        private Forms.ContextMenuStrip menu;
        private Forms.ToolStripMenuItem showItem, stopItem, muteItem, exitItem;
        private Icon ownedIcon;
        private bool disposed;
        public bool Enabled { get { return icon != null; } }
        public bool IsMonitoring { get; private set; }
        public bool IsMuted { get; private set; }

        public TrayMonitor(Dispatcher dispatcher, Func<string, string> text, Action showWindow, Action stopMonitoring, Action<bool> mutedChanged, Action exitApplication)
        {
            if (dispatcher == null) throw new ArgumentNullException("dispatcher");
            if (text == null) throw new ArgumentNullException("text");
            this.dispatcher = dispatcher; this.text = text; this.showWindow = showWindow;
            this.stopMonitoring = stopMonitoring; this.mutedChanged = mutedChanged; this.exitApplication = exitApplication;
        }
        private void Verify() { dispatcher.VerifyAccess(); if (disposed) throw new ObjectDisposedException("TrayMonitor"); }
        public void SetEnabled(bool enabled)
        {
            Verify();
            if (enabled == Enabled) return;
            if (!enabled) { RemoveIcon(); return; }
            try
            {
                menu = new Forms.ContextMenuStrip();
                showItem = new Forms.ToolStripMenuItem(); stopItem = new Forms.ToolStripMenuItem();
                muteItem = new Forms.ToolStripMenuItem(); exitItem = new Forms.ToolStripMenuItem();
                showItem.Click += delegate { if (showWindow != null) showWindow(); };
                stopItem.Click += delegate { if (stopMonitoring != null) stopMonitoring(); };
                muteItem.Click += delegate { SetMuted(!IsMuted); if (mutedChanged != null) mutedChanged(IsMuted); };
                exitItem.Click += delegate { if (exitApplication != null) exitApplication(); };
                menu.Items.AddRange(new Forms.ToolStripItem[] { showItem, stopItem, muteItem, new Forms.ToolStripSeparator(), exitItem });
                ownedIcon = LoadIcon();
                icon = new Forms.NotifyIcon { Icon = ownedIcon, ContextMenuStrip = menu };
                icon.DoubleClick += delegate { if (showWindow != null) showWindow(); };
                RefreshText();
                icon.Visible = true;
            }
            catch { RemoveIcon(); throw; }
        }
        private static Icon LoadIcon()
        {
            try
            {
                Icon extracted = Icon.ExtractAssociatedIcon(typeof(TrayMonitor).Assembly.Location);
                if (extracted != null) return extracted;
            }
            catch (ArgumentException) { }
            catch (System.IO.IOException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return (Icon)SystemIcons.Application.Clone();
        }
        public void SetMonitoring(bool monitoring) { Verify(); IsMonitoring = monitoring; RefreshText(); }
        public void SetMuted(bool muted) { Verify(); IsMuted = muted; RefreshText(); }
        public void RefreshText()
        {
            Verify();
            if (icon == null) return;
            showItem.Text = text("trayShowWindow"); stopItem.Text = text("trayStopMonitoring");
            stopItem.Enabled = IsMonitoring; muteItem.Text = text("trayMuteNotifications"); muteItem.Checked = IsMuted;
            exitItem.Text = text("trayExitApplication");
            icon.Text = Limit("NetworkRescue · " + text(IsMonitoring ? "trayMonitoring" : "trayIdle"), 63);
        }
        public void NotifyAlert(MonitorAlert alert)
        {
            Verify();
            if (alert == null || icon == null || IsMuted || !IsMonitoring) return;
            bool recovered = alert.Kind == MonitorAlertKind.Recovered;
            string target = String.IsNullOrEmpty(alert.TargetHost) ? alert.TargetId : alert.TargetHost;
            icon.BalloonTipTitle = Limit(text(recovered ? "trayRecoveredTitle" : "trayNoResponseTitle"), 63);
            icon.BalloonTipText = Limit((text(recovered ? "trayRecoveredBody" : "trayNoResponseBody") ?? String.Empty).Replace("{target}", target ?? String.Empty), 500);
            icon.BalloonTipIcon = recovered ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning;
            icon.ShowBalloonTip(6000);
        }
        public void NotifySessionEnded(bool failed, bool stopped)
        {
            Verify();
            SetMonitoring(false);
            if (icon == null || IsMuted) return;
            icon.BalloonTipTitle = Limit(text(stopped ? "traySessionStoppedTitle" : failed ? "traySessionFailedTitle" : "traySessionCompletedTitle"), 63);
            icon.BalloonTipText = Limit(text("traySessionEndedBody"), 500);
            icon.BalloonTipIcon = failed && !stopped ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info;
            icon.ShowBalloonTip(6000);
        }
        private static string Limit(string value, int length)
        { return String.IsNullOrEmpty(value) ? "NetworkRescue" : (value.Length <= length ? value : value.Substring(0, length - 1) + "…"); }
        private void RemoveIcon()
        {
            if (icon != null) { icon.Visible = false; icon.Dispose(); icon = null; }
            if (menu != null) { menu.Dispose(); menu = null; }
            if (ownedIcon != null) { ownedIcon.Dispose(); ownedIcon = null; }
            showItem = stopItem = muteItem = exitItem = null;
        }
        public void Dispose()
        {
            dispatcher.VerifyAccess();
            if (disposed) return;
            RemoveIcon(); disposed = true;
        }
    }
}
