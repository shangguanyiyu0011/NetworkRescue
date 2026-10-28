using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
        // Address attempts describe only the bounded sample, never every address of a family.
        public string AddressFamily, TlsServerName;
        public int AddressCount, AttemptedAddressCount;
        public SiteProbe[] AddressAttempts = new SiteProbe[0];
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

        internal static Task<SiteDiagnosticReport> RunCoreAsync(SiteTarget target, Func<string, Task<IPAddress[]>> resolver, IWebProxy systemProxy, IProgress<SiteProbe> progress, CancellationToken token)
        {
            return RunCoreAsync(target, resolver, systemProxy, progress, token, ProbeFamilyAddress);
        }
        internal static async Task<SiteDiagnosticReport> RunCoreAsync(SiteTarget target, Func<string, Task<IPAddress[]>> resolver, IWebProxy systemProxy, IProgress<SiteProbe> progress, CancellationToken token, Func<SiteTarget, IPAddress, CancellationToken, Task<SiteProbe>> addressProbe)
        {
            token.ThrowIfCancellationRequested();
            SiteDiagnosticReport report = new SiteDiagnosticReport { Target = target, StartedUtc = DateTime.UtcNow };
            Task direct = ProbeHttp(target, null, "HttpDirect", report, progress, token);
            Task proxy = ProbeHttp(target, systemProxy, "HttpWindowsProxy", report, progress, token);
            Task network = ProbeNetwork(target, resolver, report, progress, token, addressProbe);
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
        private static async Task ProbeNetwork(SiteTarget target, Func<string, Task<IPAddress[]>> resolver, SiteDiagnosticReport report, IProgress<SiteProbe> progress, CancellationToken token, Func<SiteTarget, IPAddress, CancellationToken, Task<SiteProbe>> addressProbe)
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
            await Task.WhenAll(ProbeTcp(addresses.FirstOrDefault(delegate(IPAddress a) { return a.AddressFamily == AddressFamily.InterNetwork; }), target.Port, "TcpV4", report, progress, token), ProbeTcp(addresses.FirstOrDefault(delegate(IPAddress a) { return a.AddressFamily == AddressFamily.InterNetworkV6; }), target.Port, "TcpV6", report, progress, token), ProbeFamily(target, addresses, AddressFamily.InterNetwork, probe.ErrorCode == null, report, progress, token, addressProbe), ProbeFamily(target, addresses, AddressFamily.InterNetworkV6, probe.ErrorCode == null, report, progress, token, addressProbe)).ConfigureAwait(false);
        }
        internal static async Task<SiteProbe> ProbeFamilyAsync(SiteTarget target, IPAddress[] addresses, AddressFamily family, bool dnsConfirmed, CancellationToken token, Func<SiteTarget, IPAddress, CancellationToken, Task<SiteProbe>> addressProbe)
        {
            token.ThrowIfCancellationRequested();
            bool ipv4 = family == AddressFamily.InterNetwork;
            IPAddress[] candidates = addresses.Where(delegate(IPAddress a) { return a != null && a.AddressFamily == family; }).Distinct().ToArray();
            SiteProbe probe = new SiteProbe { Kind = ipv4 ? "HttpV4" : "HttpV6", AddressFamily = ipv4 ? "IPv4" : "IPv6", StartedUtc = DateTime.UtcNow, AddressCount = candidates.Length, Addresses = candidates.Take(16).Select(delegate(IPAddress a) { return a.ToString(); }).ToArray() };
            Stopwatch clock = Stopwatch.StartNew();
            if (candidates.Length == 0)
            {
                probe.Status = dnsConfirmed ? "NotApplicable" : "Unknown";
                probe.ErrorCode = dnsConfirmed ? (ipv4 ? "SiteNoIpv4Address" : "SiteNoIpv6Address") : "SiteDnsUnconfirmed";
                return probe;
            }
            using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(3000);
                // Both sampled addresses receive a real attempt; one slow address cannot hide another.
                Task<SiteProbe>[] attempts = candidates.Take(2).Select(delegate(IPAddress address) { return addressProbe(target, address, deadline.Token); }).ToArray();
                Task<SiteProbe[]> combined = Task.WhenAll(attempts);
                try { await AwaitBounded(combined, deadline.Token).ConfigureAwait(false); probe.AddressAttempts = await combined.ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    probe.AddressAttempts = attempts.Select(delegate(Task<SiteProbe> attempt) { return attempt.Status == TaskStatus.RanToCompletion ? attempt.Result : new SiteProbe { Status = "HttpNoResponse", ErrorCode = "SiteHttpTimeout", ResponseReceived = false, HttpSuccess = false }; }).ToArray();
                }
            }
            token.ThrowIfCancellationRequested();
            for (int i = 0; i < probe.AddressAttempts.Length; i++)
            {
                probe.AddressAttempts[i].Address = candidates[i].ToString();
                probe.AddressAttempts[i].Kind = probe.Kind;
                probe.AddressAttempts[i].AddressFamily = probe.AddressFamily;
            }
            probe.AttemptedAddressCount = probe.AddressAttempts.Length;
            SiteProbe selected = probe.AddressAttempts.FirstOrDefault(delegate(SiteProbe p) { return p.HttpSuccess == true; }) ?? probe.AddressAttempts.FirstOrDefault(delegate(SiteProbe p) { return p.ResponseReceived == true; }) ?? probe.AddressAttempts[0];
            probe.Address = selected.Address; probe.Status = selected.Status;
            probe.ResponseReceived = selected.ResponseReceived; probe.HttpSuccess = selected.HttpSuccess;
            probe.Connected = probe.AddressAttempts.Any(delegate(SiteProbe p) { return p.Connected == true; }) ? (bool?)true : probe.AddressAttempts.All(delegate(SiteProbe p) { return p.Connected == false; }) ? (bool?)false : null;
            probe.HttpStatusCode = selected.HttpStatusCode; probe.TlsServerName = selected.TlsServerName;
            probe.ErrorCode = selected.ResponseReceived == true ? selected.ErrorCode : "SiteHttpAddressAttemptsUnconfirmed";
            probe.DurationMs = clock.ElapsedMilliseconds;
            return probe;
        }
        private static async Task ProbeFamily(SiteTarget target, IPAddress[] addresses, AddressFamily family, bool dnsConfirmed, SiteDiagnosticReport report, IProgress<SiteProbe> progress, CancellationToken token, Func<SiteTarget, IPAddress, CancellationToken, Task<SiteProbe>> addressProbe)
        {
            Publish(report, progress, await ProbeFamilyAsync(target, addresses, family, dnsConfirmed, token, addressProbe).ConfigureAwait(false));
        }
        private static async Task AwaitBounded(Task operation, CancellationToken token)
        {
            using (CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task timeout = Task.Delay(Timeout.Infinite, wait.Token);
                if (await Task.WhenAny(operation, timeout).ConfigureAwait(false) != operation) { Observe(operation); token.ThrowIfCancellationRequested(); }
                wait.Cancel();
                await operation.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
        }
        internal static async Task<SiteProbe> ProbeFamilyAddress(SiteTarget target, IPAddress address, CancellationToken token)
        {
            SiteProbe probe = new SiteProbe { Address = address.ToString(), StartedUtc = DateTime.UtcNow, Status = "HttpNoResponse", Connected = false, ResponseReceived = false, HttpSuccess = false, TlsServerName = target.Scheme == Uri.UriSchemeHttps ? target.Host : null };
            Stopwatch clock = Stopwatch.StartNew();
            using (TcpClient client = new TcpClient(address.AddressFamily))
            using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (deadline.Token.Register(client.Close))
            {
                deadline.CancelAfter(3000);
                try
                {
                    await AwaitBounded(client.ConnectAsync(address, target.Port), deadline.Token).ConfigureAwait(false);
                    probe.Connected = true;
                    using (Stream transport = client.GetStream())
                    {
                        SslStream tls = null;
                        try
                        {
                            Stream stream = transport;
                            if (target.Scheme == Uri.UriSchemeHttps)
                            {
                                // No certificate callback: the platform validates the original hostname and chain.
                                tls = new SslStream(transport, true);
                                // Match the application's TLS 1.2 policy without changing process-wide settings.
                                await AwaitBounded(tls.AuthenticateAsClientAsync(target.Host, new X509CertificateCollection(), SslProtocols.Tls12, true), deadline.Token).ConfigureAwait(false);
                                stream = tls;
                            }
                            string host = target.Host.IndexOf(':') >= 0 ? "[" + target.Host + "]" : target.Host;
                            if (target.Port != (target.Scheme == Uri.UriSchemeHttps ? 443 : 80)) host += ":" + target.Port;
                            byte[] request = Encoding.ASCII.GetBytes("HEAD / HTTP/1.1\r\nHost: " + host + "\r\nConnection: close\r\n\r\n");
                            await AwaitBounded(stream.WriteAsync(request, 0, request.Length, deadline.Token), deadline.Token).ConfigureAwait(false);
                            byte[] headers = new byte[32768]; int length = 0, headerStart = 0, code = 0;
                            bool complete = false;
                            while (!complete)
                            {
                                if (length == headers.Length) { probe.ErrorCode = "SiteHttpHeaderTooLarge"; break; }
                                Task<int> reading = stream.ReadAsync(headers, length, headers.Length - length, deadline.Token);
                                await AwaitBounded(reading, deadline.Token).ConfigureAwait(false);
                                int count = await reading.ConfigureAwait(false);
                                if (count == 0) { probe.ErrorCode = "SiteHttpInvalidResponse"; break; }
                                int oldLength = length; length += count;
                                for (int i = Math.Max(headerStart, oldLength - 3); i <= length - 4; i++)
                                {
                                    if (headers[i] != 13 || headers[i + 1] != 10 || headers[i + 2] != 13 || headers[i + 3] != 10) continue;
                                    string first = Encoding.ASCII.GetString(headers, headerStart, i - headerStart).Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                                    if (first.Length < 12 || !(first.StartsWith("HTTP/1.1 ", StringComparison.Ordinal) || first.StartsWith("HTTP/1.0 ", StringComparison.Ordinal)) || !Int32.TryParse(first.Substring(9, 3), out code) || code < 100 || code > 599 || (first.Length > 12 && first[12] != ' ')) { probe.ErrorCode = "SiteHttpInvalidResponse"; complete = true; break; }
                                    headerStart = i + 4;
                                    if (code < 200 && code != 101) { i = headerStart - 1; continue; }
                                    probe.ResponseReceived = true; probe.HttpStatusCode = code; probe.HttpSuccess = code >= 200 && code < 300;
                                    probe.Status = code >= 500 ? "HttpServerError" : code >= 400 ? "HttpClientError" : code >= 300 ? "HttpRedirect" : "HttpResponse";
                                    complete = true; break;
                                }
                            }
                        }
                        finally { if (tls != null) tls.Dispose(); }
                    }
                }
                catch (OperationCanceledException) { probe.ErrorCode = "SiteHttpTimeout"; }
                catch (AuthenticationException) { probe.ErrorCode = deadline.IsCancellationRequested ? "SiteHttpTimeout" : "SiteTlsValidationOrHandshakeFailed"; }
                catch (SocketException error) { probe.ErrorCode = deadline.IsCancellationRequested ? "SiteHttpTimeout" : "SiteHttp" + error.SocketErrorCode; }
                catch (IOException) { probe.ErrorCode = deadline.IsCancellationRequested ? "SiteHttpTimeout" : "SiteHttpIoFailure"; }
                catch (ObjectDisposedException) { probe.ErrorCode = "SiteHttpTimeout"; }
            }
            token.ThrowIfCancellationRequested();
            probe.DurationMs = clock.ElapsedMilliseconds;
            return probe;
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
            SiteProbe[] families = probes.Where(delegate(SiteProbe p) { return (p.Kind == "HttpV4" || p.Kind == "HttpV6") && p.Status != "NotApplicable" && p.AddressCount > 0; }).ToArray();
            if (families.Any(delegate(SiteProbe p) { return p.ResponseReceived != true; }) && probes.Any(delegate(SiteProbe p) { return p.HttpSuccess == true; })) return "AddressFamilyHttpPartialFailure";
            if (direct != null && direct.HttpSuccess == true) return proxy != null && proxy.HttpSuccess == true ? "BothPaths2xx" : "DirectPath2xx";
            if (proxy != null && proxy.HttpSuccess == true) return "WindowsProxyPath2xx";
            if (probes.Any(delegate(SiteProbe p) { return p.HttpStatusCode == 407; })) return "ProxyAuthenticationRequired";
            if (probes.Any(delegate(SiteProbe p) { return p.HttpStatusCode == 401 || p.HttpStatusCode == 403; })) return "HttpAccessRestricted";
            if (probes.Any(delegate(SiteProbe p) { return p.HttpStatusCode >= 300 && p.HttpStatusCode < 400; })) return "HttpRedirectReceived";
            if (probes.Any(delegate(SiteProbe p) { return p.HttpStatusCode >= 500; })) return "HttpServerErrorReceived";
            if (probes.Any(delegate(SiteProbe p) { return p.HttpStatusCode >= 400; })) return "HttpClientErrorReceived";
            if (probes.Any(delegate(SiteProbe p) { return p.ErrorCode == "HttpTrustFailure" || p.ErrorCode == "HttpSecureChannelFailure" || p.AddressAttempts.Any(delegate(SiteProbe a) { return a.ErrorCode == "SiteTlsValidationOrHandshakeFailed"; }); })) return "TlsValidationOrHandshakeFailed";
            if (probes.Any(delegate(SiteProbe p) { return p.ResponseReceived == true; })) return "HttpResponseReceived";
            if (probes.Any(delegate(SiteProbe p) { return p.Connected == true; })) return "TcpConnectedHttpUnconfirmed";
            if (!probes.Any(delegate(SiteProbe p) { return p.DnsResolved == true; })) return "SystemDnsUnconfirmed";
            return "SiteConnectivityUnconfirmed";
        }
    }
}
