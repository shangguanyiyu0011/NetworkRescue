using System;
using System.Linq;

namespace NetworkRescue
{
    /// <summary>A bounded hint from the existing diagnostic sample, never a confirmed login address.</summary>
    public sealed class PortalLoginInfo
    {
        public bool Suspected { get; private set; }
        public string DisplayOrigin { get; private set; }
        public bool IsTriggerPage { get; private set; }

        internal PortalLoginInfo(bool suspected)
        {
            Suspected = suspected;
            DisplayOrigin = suspected ? "http://www.msftconnecttest.com" : "";
            IsTriggerPage = suspected;
        }
    }

    /// <summary>
    /// Does not probe the network, follow redirects, launch a browser, or handle credentials.
    /// The current engine deliberately does not retain Location headers. Therefore the only
    /// offered address is the Windows connectivity/authentication trigger, not a discovered portal.
    /// The controller must obtain user confirmation before opening it in the system browser.
    /// </summary>
    public static class PortalLogin
    {
        public const string TriggerUrl = "http://www.msftconnecttest.com/redirect";
        private const string DetectionUrl = "http://www.msftconnecttest.com/connecttest.txt";

        public static PortalLoginInfo Inspect(DiagnosticReport report)
        {
            Snapshot snapshot = report == null ? null : report.EvidenceSnapshot;
            bool suspected = snapshot != null && snapshot.Http != null &&
                !snapshot.Http.Any(delegate(Probe p) { return p != null && p.Success && p.Marker; }) &&
                snapshot.Http.Any(InterceptedMarker);
            return new PortalLoginInfo(suspected);
        }

        public static bool TryGetBrowserUri(DiagnosticReport report, out Uri uri)
        {
            uri = null;
            return Inspect(report).Suspected && TryValidateBrowserUri(TriggerUrl, out uri);
        }

        private static bool InterceptedMarker(Probe probe)
        {
            if (probe == null || !probe.Marker || !probe.Responded || probe.Uncertain ||
                probe.AutomaticProxySkipped || !String.Equals(probe.Endpoint, DetectionUrl, StringComparison.OrdinalIgnoreCase)) return false;
            // 304 is a cache response and 300/305/306 do not identify a normal login redirect.
            bool loginRedirect = probe.Redirected && (probe.HttpStatus == 301 || probe.HttpStatus == 302 ||
                probe.HttpStatus == 303 || probe.HttpStatus == 307 || probe.HttpStatus == 308);
            return loginRedirect || (probe.HttpStatus == 200 && probe.ContentMismatch);
        }

        // Remains internal: callers cannot nominate an arbitrary external address for opening.
        // Keep this validator if the engine later stores a short-lived Location in memory.
        internal static bool TryValidateBrowserUri(string candidate, out Uri uri)
        {
            uri = null;
            if (String.IsNullOrEmpty(candidate) || candidate.Length > 4096) return false;
            for (int i = 0; i < candidate.Length; i++)
            {
                char c = candidate[i];
                if (Char.IsControl(c) || Char.IsWhiteSpace(c) || c == '\\') return false;
                if (c == '%' && i + 2 < candidate.Length)
                {
                    int hi = Hex(candidate[i + 1]), lo = Hex(candidate[i + 2]);
                    if (hi >= 0 && lo >= 0 && (hi * 16 + lo <= 32 || hi * 16 + lo == 127)) return false;
                }
            }
            Uri parsed;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
                !candidate.StartsWith(parsed.Scheme + "://", StringComparison.OrdinalIgnoreCase) ||
                !String.IsNullOrEmpty(parsed.UserInfo) || String.IsNullOrEmpty(parsed.Host) ||
                parsed.HostNameType == UriHostNameType.Unknown) return false;
            uri = parsed;
            return true;
        }

        private static int Hex(char value)
        {
            return value >= '0' && value <= '9' ? value - '0' :
                value >= 'a' && value <= 'f' ? value - 'a' + 10 :
                value >= 'A' && value <= 'F' ? value - 'A' + 10 : -1;
        }
    }
}
