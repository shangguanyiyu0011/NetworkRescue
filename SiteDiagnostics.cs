using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkRescue
{
    public sealed class SiteTarget { public string Origin, Host, Scheme; public int Port; }
    public sealed class SiteProbe
    {
        public string Kind, Status, ErrorCode, Address;
        public string[] Addresses = new string[0];
        public DateTime StartedUtc;
        public long DurationMs;
        public bool? DnsResolved, Connected, ResponseReceived, HttpSuccess;
        public int? HttpStatusCode;
    }
    public sealed class SiteDiagnosticReport
    {
        public SiteTarget Target;
        public DateTime StartedUtc, EndedUtc;
        public List<SiteProbe> Probes = new List<SiteProbe>();
        public string ConclusionCode;
    }

    public static class SiteDiagnostics
    {
        public static bool TryNormalizeTarget(string input, out SiteTarget target, out string errorCode)
        {
            target = null; errorCode = "InvalidSiteTarget";
            string value = (input ?? "").Trim(), host, ignored;
            if (value.Length == 0 || value.Length > 4096 || value.Any(Char.IsControl) || value.IndexOf('\\') >= 0) return false;
            string scheme = Uri.UriSchemeHttps;
            int port = 443;
            if (!AdvancedTools.TryNormalizeHost(value, out host, out ignored))
            {
                Uri parsed;
                if (!Uri.TryCreate(value, UriKind.Absolute, out parsed)) return false;
                if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) { errorCode = "UnsupportedSiteScheme"; return false; }
                int authorityStart = value.IndexOf("://", StringComparison.Ordinal);
                if (authorityStart < 0) return false;
                string authority = value.Substring(authorityStart + 3).Split('/', '?', '#')[0];
                if (parsed.UserInfo.Length > 0 || authority.IndexOf('@') >= 0) { errorCode = "SiteCredentialsNotAllowed"; return false; }
                if (authority.EndsWith(":", StringComparison.Ordinal)) return false;
                if (parsed.Port < 1 || parsed.Port > 65535 || !AdvancedTools.TryNormalizeHost(parsed.DnsSafeHost, out host, out ignored)) return false;
                scheme = parsed.Scheme; port = parsed.Port;
            }
            Uri origin = new UriBuilder(scheme, host, port, "/").Uri;
            target = new SiteTarget { Host = host, Scheme = scheme, Port = port, Origin = origin.AbsoluteUri };
            errorCode = null;
            return true;
        }

        public static Task<SiteDiagnosticReport> RunAsync(string input, IProgress<SiteProbe> progress, CancellationToken token)
        {
            SiteTarget target; string error;
            if (!TryNormalizeTarget(input, out target, out error)) throw new ArgumentException(error);
            IWebProxy proxy = WebRequest.GetSystemWebProxy();
            if (proxy != null) proxy.Credentials = null;
            return RunCoreAsync(target, Dns.GetHostAddressesAsync, proxy, progress, token);
        }

        internal static async Task<SiteDiagnosticReport> RunCoreAsync(SiteTarget target, Func<string, Task<IPAddress[]>> resolver, IWebProxy systemProxy, IProgress<SiteProbe> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            SiteDiagnosticReport report = new SiteDiagnosticReport { Target = target, StartedUtc = DateTime.UtcNow };
            Task direct = ProbeHttp(target, null, "HttpDirect", report, progress, token);
            Task proxy = ProbeHttp(target, systemProxy, "HttpWindowsProxy", report, progress, token);
            Task network = ProbeNetwork(target, resolver, report, progress, token);
            await Task.WhenAll(direct, proxy, network).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            report.EndedUtc = DateTime.UtcNow;
            report.ConclusionCode = Conclude(report.Probes);
            return report;
        }

        private static void Publish(SiteDiagnosticReport report, IProgress<SiteProbe> progress, SiteProbe probe)
        {
            lock (report.Probes) report.Probes.Add(probe);
            if (progress != null) progress.Report(probe);
        }
        private static void Observe(Task task)
        {
            task.ContinueWith(delegate(Task failed) { var ignored = failed.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        private static async Task ProbeNetwork(SiteTarget target, Func<string, Task<IPAddress[]>> resolver, SiteDiagnosticReport report, IProgress<SiteProbe> progress, CancellationToken token)
        {
            SiteProbe probe = new SiteProbe { Kind = "SystemDns", StartedUtc = DateTime.UtcNow, Status = "Unknown", DnsResolved = false };
            Stopwatch clock = Stopwatch.StartNew();
            IPAddress literal;
            IPAddress[] addresses = new IPAddress[0];
            try
            {
                if (IPAddress.TryParse(target.Host, out literal)) { addresses = new[] { literal }; probe.Status = "LiteralAddress"; }
                else
                {
                    Task<IPAddress[]> lookup = resolver(target.Host);
                    if (await Task.WhenAny(lookup, Task.Delay(3000, token)).ConfigureAwait(false) != lookup)
                    {
                        Observe(lookup); token.ThrowIfCancellationRequested();
                        throw new TimeoutException();
                    }
                    token.ThrowIfCancellationRequested();
                    addresses = (await lookup.ConfigureAwait(false)) ?? new IPAddress[0];
                    probe.Status = addresses.Length > 0 ? "Resolved" : "NoAddress";
                }
                addresses = addresses.Where(delegate(IPAddress a) { return a != null && (a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6); }).Distinct().ToArray();
                if (probe.Status != "LiteralAddress") probe.Status = addresses.Length > 0 ? "Resolved" : "NoAddress";
                probe.DnsResolved = addresses.Length > 0;
                probe.Addresses = addresses.Take(16).Select(delegate(IPAddress a) { return a.ToString(); }).ToArray();
            }
            catch (TimeoutException) { probe.Status = "Unknown"; probe.ErrorCode = "SiteDnsTimeout"; }
            catch (SocketException ex) { probe.Status = "Unknown"; probe.ErrorCode = "SiteDns" + ex.SocketErrorCode; }
            token.ThrowIfCancellationRequested();
            probe.DurationMs = clock.ElapsedMilliseconds;
            Publish(report, progress, probe);
            await Task.WhenAll(ProbeTcp(addresses.FirstOrDefault(delegate(IPAddress a) { return a.AddressFamily == AddressFamily.InterNetwork; }), target.Port, "TcpV4", report, progress, token), ProbeTcp(addresses.FirstOrDefault(delegate(IPAddress a) { return a.AddressFamily == AddressFamily.InterNetworkV6; }), target.Port, "TcpV6", report, progress, token)).ConfigureAwait(false);
        }
        private static async Task ProbeTcp(IPAddress address, int port, string kind, SiteDiagnosticReport report, IProgress<SiteProbe> progress, CancellationToken token)
        {
            SiteProbe probe = new SiteProbe { Kind = kind, StartedUtc = DateTime.UtcNow, Address = address == null ? null : address.ToString(), Status = "NoAddress" };
            Stopwatch clock = Stopwatch.StartNew();
            if (address != null)
            {
                probe.Connected = false;
                using (TcpClient client = new TcpClient(address.AddressFamily))
                using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                using (deadline.Token.Register(client.Close))
                {
                    deadline.CancelAfter(3000);
                    try
                    {
                        Task connecting = client.ConnectAsync(address, port);
                        if (await Task.WhenAny(connecting, Task.Delay(3000, token)).ConfigureAwait(false) != connecting)
                        {
                            Observe(connecting); token.ThrowIfCancellationRequested();
                            throw new TimeoutException();
                        }
                        await connecting.ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        probe.Connected = true; probe.Status = "Connected";
                    }
                    catch (TimeoutException) { probe.Status = "Unknown"; probe.ErrorCode = "SiteTcpTimeout"; }
                    catch (SocketException ex) { probe.Status = "Unknown"; probe.ErrorCode = deadline.IsCancellationRequested ? "SiteTcpTimeout" : "SiteTcp" + ex.SocketErrorCode; }
                    catch (ObjectDisposedException) { token.ThrowIfCancellationRequested(); probe.Status = "Unknown"; probe.ErrorCode = "SiteTcpTimeout"; }
                }
            }
            token.ThrowIfCancellationRequested();
            probe.DurationMs = clock.ElapsedMilliseconds;
            Publish(report, progress, probe);
        }
        private static async Task ProbeHttp(SiteTarget target, IWebProxy proxy, string kind, SiteDiagnosticReport report, IProgress<SiteProbe> progress, CancellationToken token)
        {
            SiteProbe probe = new SiteProbe { Kind = kind, StartedUtc = DateTime.UtcNow };
            Stopwatch clock = Stopwatch.StartNew();
            MonitorSummary result = await NetworkMonitor.RunHttpEndpointAsync(new Uri(target.Origin), 1000, 1, proxy, kind == "HttpDirect" ? "Direct" : "WindowsProxy", null, token).ConfigureAwait(false);
            probe.DurationMs = clock.ElapsedMilliseconds;
            probe.ResponseReceived = result.ResponseReceived; probe.HttpSuccess = result.HttpSuccess;
            probe.HttpStatusCode = result.HttpStatusCode; probe.ErrorCode = result.ErrorCode;
            int code = result.HttpStatusCode ?? 0;
            probe.Status = !result.ResponseReceived ? "HttpNoResponse" : code == 407 ? "ProxyAuthenticationRequired" : code >= 500 ? "HttpServerError" : code >= 400 ? "HttpClientError" : code >= 300 ? "HttpRedirect" : "HttpResponse";
            Publish(report, progress, probe);
        }

        internal static string Conclude(IList<SiteProbe> probes)
        {
            SiteProbe direct = probes.FirstOrDefault(delegate(SiteProbe p) { return p.Kind == "HttpDirect"; });
            SiteProbe proxy = probes.FirstOrDefault(delegate(SiteProbe p) { return p.Kind == "HttpWindowsProxy"; });
            if (direct != null && direct.HttpSuccess == true) return proxy != null && proxy.HttpSuccess == true ? "BothPaths2xx" : "DirectPath2xx";
            if (proxy != null && proxy.HttpSuccess == true) return "WindowsProxyPath2xx";
            if (probes.Any(delegate(SiteProbe p) { return p.HttpStatusCode == 407; })) return "ProxyAuthenticationRequired";
            if (probes.Any(delegate(SiteProbe p) { return p.HttpStatusCode == 401 || p.HttpStatusCode == 403; })) return "HttpAccessRestricted";
            if (probes.Any(delegate(SiteProbe p) { return p.HttpStatusCode >= 300 && p.HttpStatusCode < 400; })) return "HttpRedirectReceived";
            if (probes.Any(delegate(SiteProbe p) { return p.HttpStatusCode >= 500; })) return "HttpServerErrorReceived";
            if (probes.Any(delegate(SiteProbe p) { return p.HttpStatusCode >= 400; })) return "HttpClientErrorReceived";
            if (probes.Any(delegate(SiteProbe p) { return p.ErrorCode == "HttpTrustFailure" || p.ErrorCode == "HttpSecureChannelFailure"; })) return "TlsValidationOrHandshakeFailed";
            if (probes.Any(delegate(SiteProbe p) { return p.ResponseReceived == true; })) return "HttpResponseReceived";
            if (probes.Any(delegate(SiteProbe p) { return p.Connected == true; })) return "TcpConnectedHttpUnconfirmed";
            if (!probes.Any(delegate(SiteProbe p) { return p.DnsResolved == true; })) return "SystemDnsUnconfirmed";
            return "SiteConnectivityUnconfirmed";
        }
    }
}
