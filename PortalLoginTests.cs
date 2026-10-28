using System;
using System.Collections.Generic;

namespace NetworkRescue
{
    internal static class PortalLoginTests
    {
        private static int count;
        private static void Check(bool condition, string message)
        {
            count++;
            if (!condition) throw new Exception(message);
        }

        private static DiagnosticReport Sample(int status)
        {
            return new DiagnosticReport
            {
                EvidenceSnapshot = new Snapshot
                {
                    Http = new List<Probe>
                    {
                        new Probe { Endpoint = "http://www.msftconnecttest.com/connecttest.txt", Marker = true,
                            Responded = true, HttpStatus = status, Redirected = status >= 300 && status < 400 }
                    }
                }
            };
        }

        public static int Main()
        {
            try
            {
                Uri uri;
                Check(!PortalLogin.Inspect(null).Suspected && !PortalLogin.TryGetBrowserUri(null, out uri) && uri == null, "No report offers no browser action");
                Check(!PortalLogin.Inspect(new DiagnosticReport()).Suspected, "No snapshot cannot establish a portal hint");
                foreach (int status in new[] { 301, 302, 303, 307, 308 })
                {
                    DiagnosticReport report = Sample(status);
                    PortalLoginInfo info = PortalLogin.Inspect(report);
                    Check(info.Suspected && info.IsTriggerPage && info.DisplayOrigin == "http://www.msftconnecttest.com", "Marker redirect offers an accurately labelled trigger");
                    Check(PortalLogin.TryGetBrowserUri(report, out uri) && uri.AbsoluteUri == PortalLogin.TriggerUrl && uri.UserInfo == "", "Only the fixed trigger is constructed");
                }
                foreach (int status in new[] { 0, 200, 204, 300, 304, 305, 306, 400, 407, 500 })
                    Check(!PortalLogin.Inspect(Sample(status)).Suspected, "Other HTTP status alone is not a login redirect: " + status);

                DiagnosticReport sample = Sample(200);
                sample.EvidenceSnapshot.Http[0].ContentMismatch = true;
                Check(PortalLogin.Inspect(sample).Suspected, "HTTP 200 marker content mismatch is a qualified portal hint");
                sample.EvidenceSnapshot.Http[0].HttpStatus = 407;
                Check(!PortalLogin.Inspect(sample).Suspected, "A proxy authentication error is not a Wi-Fi portal");
                sample = Sample(302); sample.EvidenceSnapshot.Http[0].Marker = false;
                Check(!PortalLogin.Inspect(sample).Suspected, "An ordinary website redirect is not a portal");
                sample.EvidenceSnapshot.Http[0].Marker = true;
                sample.EvidenceSnapshot.Http[0].Endpoint = "https://www.gstatic.com/generate_204";
                Check(!PortalLogin.Inspect(sample).Suspected, "A TLS marker redirect alone is not the plain HTTP interception signal");
                sample = Sample(302); sample.EvidenceSnapshot.Http[0].Uncertain = true;
                Check(!PortalLogin.Inspect(sample).Suspected, "Uncertain observations cannot offer an inferred login action");
                sample = Sample(302); sample.EvidenceSnapshot.Http[0].Responded = false;
                Check(!PortalLogin.Inspect(sample).Suspected, "A response must actually have been received");
                sample = Sample(302); sample.EvidenceSnapshot.Http[0].AutomaticProxySkipped = true;
                Check(!PortalLogin.Inspect(sample).Suspected, "A skipped PAC/WPAD path cannot identify a portal");
                sample = Sample(302); sample.EvidenceSnapshot.Http.Add(new Probe { Marker = true, Success = true });
                Check(!PortalLogin.Inspect(sample).Suspected && !PortalLogin.TryGetBrowserUri(sample, out uri), "Another successful marker suppresses portal action");
                sample = Sample(302); sample.EvidenceSnapshot.DirectHttp = sample.EvidenceSnapshot.Http; sample.EvidenceSnapshot.Http = new List<Probe>();
                Check(!PortalLogin.Inspect(sample).Suspected, "Direct bypass results do not replace system-path portal evidence");
                sample = Sample(302); sample.EvidenceSnapshot.Http[0].Endpoint += "?token=secret";
                Check(!PortalLogin.Inspect(sample).Suspected, "Only the fixed diagnostic endpoint is accepted");

                foreach (string safe in new[] { PortalLogin.TriggerUrl, "https://login.example.test/", "http://192.168.1.1/login", "http://[2001:db8::1]/login", "https://login.example.test:8443/login?token=private#step" })
                    Check(PortalLogin.TryValidateBrowserUri(safe, out uri) && uri != null, "HTTP(S) entries may be safely represented as a URI");
                foreach (string unsafeUrl in new[] { null, "", "javascript:alert(1)", "file:///C:/Windows/system32/cmd.exe", "data:text/html,login", "ms-settings:network", "//login.example.test/", "http:login.example.test", "https:///login", "https://user:password@login.example.test/", "http://login.example.test/\r\nwhoami", " http://login.example.test/", "http://login.example.test/ ", "http://login.example.test/%0aevil", "http://login.example.test/%00evil", "http://login.example.test/%7fevil", "http:\\login.example.test\\login" })
                    Check(!PortalLogin.TryValidateBrowserUri(unsafeUrl, out uri) && uri == null, "Unsafe entry is rejected without a browser target");

                sample = Sample(302);
                string before = sample.ToPlainText();
                PortalLogin.Inspect(sample);
                PortalLogin.TryGetBrowserUri(sample, out uri);
                Check(sample.ToPlainText() == before && !sample.ToPlainText().Contains(PortalLogin.TriggerUrl), "Inspecting the hint does not mutate or persist a login address in the report");
                Check(!PortalLogin.Inspect(new DiagnosticReport { Summary = "portal login redirect" }).Suspected, "Translated or arbitrary report prose is not evidence");
                Console.WriteLine("PASS: " + count + " portal login assertions; no network requests or browser launch.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
