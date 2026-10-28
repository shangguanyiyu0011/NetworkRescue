using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace NetworkRescue
{
    internal static class ConnectionProcessesTests
    {
        private static int assertions;
        private static void Assert(bool condition, string message) { assertions++; if (!condition) throw new Exception("FAIL: " + message); }
        private static void Put(byte[] bytes, int offset, uint value)
        {
            for (int i = 0; i < 4; i++) bytes[offset + i] = (byte)(value >> (8 * i));
        }
        private static void PutAddress(byte[] bytes, int offset, string address)
        {
            byte[] source = IPAddress.Parse(address).GetAddressBytes(); Buffer.BlockCopy(source, 0, bytes, offset, source.Length);
        }
        private static void PutPort(byte[] bytes, int offset, int port) { bytes[offset] = (byte)(port >> 8); bytes[offset + 1] = (byte)port; }
        private static byte[] Table(bool ipv6)
        {
            byte[] bytes = new byte[4 + (ipv6 ? 56 : 24)]; Put(bytes, 0, 1);
            if (ipv6)
            {
                PutAddress(bytes, 4, "fe80::1234"); Put(bytes, 20, 7); PutPort(bytes, 24, 54321);
                PutAddress(bytes, 28, "fe80::abcd"); Put(bytes, 44, 9); PutPort(bytes, 48, 443); Put(bytes, 52, 5); Put(bytes, 56, 1234);
            }
            else
            {
                Put(bytes, 4, 5); PutAddress(bytes, 8, "127.0.0.1"); PutPort(bytes, 12, 54321);
                PutAddress(bytes, 16, "192.0.2.19"); PutPort(bytes, 20, 443); Put(bytes, 24, 1234);
            }
            return bytes;
        }
        private static void Parsing()
        {
            foreach (bool ipv6 in new[] { false, true })
            {
                byte[] bytes = Table(ipv6);
                ConnectionProcessRow row = ConnectionProcesses.ParseTable(bytes, ipv6).Single();
                Assert(row.LocalAddress == (ipv6 ? "fe80::1234%7" : "127.0.0.1") && row.RemoteAddress == (ipv6 ? "fe80::abcd%9" : "192.0.2.19"), "native address bytes and IPv6 scopes remain exact");
                Assert(row.LocalPort == 54321 && row.RemotePort == 443 && row.ProcessId == 1234 && row.State == "Established", "native port byte order, PID and TCP state");
                for (int size = 0; size < bytes.Length; size++)
                {
                    bool rejected = false;
                    try { ConnectionProcesses.ParseTable(bytes.Take(size).ToArray(), ipv6); } catch (InvalidDataException) { rejected = true; }
                    Assert(rejected, "reject truncated owner table " + ipv6 + " / " + size);
                }
                Put(bytes, 0, UInt32.MaxValue);
                bool invalidCount = false;
                try { ConnectionProcesses.ParseTable(bytes, ipv6); } catch (InvalidDataException) { invalidCount = true; }
                Assert(invalidCount, "native row count cannot overflow or exceed table");
                bytes = Table(ipv6); Put(bytes, ipv6 ? 52 : 4, 2);
                Assert(ConnectionProcesses.ParseTable(bytes, ipv6).Length == 0, "exclude listeners like existing active-connection view");
                bytes = Table(ipv6); Put(bytes, ipv6 ? 52 : 4, 99); Put(bytes, ipv6 ? 56 : 24, UInt32.MaxValue);
                row = ConnectionProcesses.ParseTable(bytes, ipv6).Single();
                Assert(row.State == "Unknown" && !row.ProcessId.HasValue && row.ProcessName == "Unknown" && row.ProcessStatus == "Unavailable", "unknown native state and nonrepresentable PID remain unknown");
            }
            Assert(ConnectionProcesses.ParseTable(new byte[4], false).Length == 0, "empty native table succeeds");
            bool invalid = false;
            try { ConnectionProcesses.ParseTable(null, false); } catch (InvalidDataException) { invalid = true; }
            Assert(invalid, "null native buffer is rejected");
        }

        private static ConnectionProcessRow Row(int port, int? pid, string state)
        {
            return new ConnectionProcessRow { LocalAddress = "127.0.0.1", LocalPort = port, RemoteAddress = "192.0.2.19", RemotePort = 443, State = state, ProcessId = pid, ProcessName = "Unknown", ProcessStatus = pid.HasValue && pid.Value > 0 ? "Pending" : "Unavailable" };
        }

        private static void SortingAndProcessStatus()
        {
            DateTime sampled = DateTime.UtcNow;
            List<ConnectionProcessRow> rows = Enumerable.Range(1000, 600).Select(delegate(int port) { return Row(port, 1234, "TimeWait"); }).ToList();
            rows.Add(Row(1000, 1234, "TimeWait"));
            rows.Add(Row(65000, 1234, "Established"));
            int lookups = 0;
            ConnectionProcessSnapshot result = ConnectionProcesses.BuildSnapshot(rows, sampled, null, delegate(int pid, DateTime time)
            {
                Assert(pid == 1234 && time == sampled, "process lookup uses sampled PID and capture start");
                lookups++; return new ConnectionProcesses.ProcessIdentity { Name = "fixture", Status = "PidNameSnapshot" };
            });
            Assert(result.Timestamp == sampled && result.TotalRows == 601 && result.Rows.Length == 512 && result.Truncated && result.ErrorCode == null, "deduplicate before accurate total and bound output to 512");
            Assert(result.Rows[0].LocalPort == 65000 && result.Rows[0].State == "Established", "sort Established before truncation even when input tail");
            Assert(lookups == 1 && result.Rows.All(delegate(ConnectionProcessRow row) { return row.ProcessName == "fixture" && row.ProcessStatus == "PidNameSnapshot"; }), "cache a process identity once per retained PID");
            rows = Enumerable.Range(1000, 512).Select(delegate(int port) { return Row(port, 1234, "Established"); }).ToList();
            rows.Add(new ConnectionProcessRow { LocalAddress = "::1", RemoteAddress = "::1", LocalPort = 60000, RemotePort = 60001, State = "Established", ProcessId = 1234 });
            rows.Add(new ConnectionProcessRow { LocalAddress = "::1", RemoteAddress = "::1", LocalPort = 60001, RemotePort = 60000, State = "Established", ProcessId = 1234 });
            result = ConnectionProcesses.BuildSnapshot(rows, sampled, null, delegate(int pid, DateTime time) { return new ConnectionProcesses.ProcessIdentity { Name = "fixture", Status = "PidNameSnapshot" }; });
            Assert(result.TotalRows == 514 && result.Rows.Length == 512 && result.Truncated && result.Rows.All(delegate(ConnectionProcessRow row) { return row.RemoteAddress != "::1"; }), "512 earlier sorted established rows may legitimately exclude live IPv6 endpoints from the public preview");
            rows = new List<ConnectionProcessRow> { Row(1, 12, "Established"), Row(2, 12, "Established"), Row(3, 13, "Established"), Row(4, 13, "Established"), Row(5, 14, "Established"), Row(6, 0, "TimeWait"), Row(7, null, "TimeWait") };
            lookups = 0;
            result = ConnectionProcesses.BuildSnapshot(rows, sampled, null, delegate(int pid, DateTime time)
            {
                lookups++; return new ConnectionProcesses.ProcessIdentity { Status = pid == 12 ? "Exited" : pid == 13 ? "AccessDenied" : "ProcessChanged" };
            });
            Assert(lookups == 3 && result.Rows.Length == 7 && result.Rows.All(delegate(ConnectionProcessRow row) { return row.ProcessName == "Unknown"; }), "process exit/access denial/PID change stay row-local and cached");
            Assert(result.Rows.Count(delegate(ConnectionProcessRow row) { return row.ProcessStatus == "Exited"; }) == 2 && result.Rows.Count(delegate(ConnectionProcessRow row) { return row.ProcessStatus == "AccessDenied"; }) == 2 && result.Rows.Any(delegate(ConnectionProcessRow row) { return row.ProcessStatus == "ProcessChanged"; }), "unknown identities retain explanatory status and observed PID");
            result = ConnectionProcesses.BuildSnapshot(new[] { Row(1, 12, "Established"), Row(1, 13, "Established") }, sampled, null,
                delegate(int pid, DateTime time) { return new ConnectionProcesses.ProcessIdentity { Status = "Unavailable" }; });
            Assert(result.TotalRows == 2, "same tuple with distinct observed owners is not silently merged");
            result = ConnectionProcesses.BuildSnapshot(new ConnectionProcessRow[0], sampled, null,
                delegate(int pid, DateTime time) { throw new Exception("No PID should be queried"); });
            Assert(result.Rows.Length == 0 && result.TotalRows == 0 && !result.Truncated && result.ErrorCode == null, "empty table is a successful snapshot");
            using (Process current = Process.GetCurrentProcess())
            {
                ConnectionProcesses.ProcessIdentity actual = ConnectionProcesses.ReadProcess(current.Id, DateTime.UtcNow);
                Assert(actual.Name == current.ProcessName && actual.Status == "PidNameSnapshot", "read current process name without command line or image path");
                actual = ConnectionProcesses.ReadProcess(current.Id, new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                Assert(actual.Name == "Unknown" && actual.Status == "ProcessChanged", "do not attribute a process created after connection capture began");
            }
            Assert(ConnectionProcesses.ReadProcess(-1, sampled).Status == "Exited", "unavailable PID does not fail snapshot");
        }

        private sealed class FixtureConnection : TcpConnectionInformation
        {
            internal IPEndPoint Local, Remote;
            public override IPEndPoint LocalEndPoint { get { return Local; } }
            public override IPEndPoint RemoteEndPoint { get { return Remote; } }
            public override TcpState State { get { return TcpState.Established; } }
        }

        private static void Fallback()
        {
            TcpConnectionInformation[] basic = {
                new FixtureConnection { Local = new IPEndPoint(IPAddress.Loopback, 1), Remote = new IPEndPoint(IPAddress.Loopback, 2) },
                new FixtureConnection { Local = new IPEndPoint(IPAddress.IPv6Loopback, 3), Remote = new IPEndPoint(IPAddress.IPv6Loopback, 4) }
            };
            int fallbackReads = 0, processReads = 0;
            ConnectionProcessSnapshot result = ConnectionProcesses.CaptureCore(delegate(bool ipv6)
            {
                if (ipv6) throw new Win32Exception(5);
                return ConnectionProcesses.ParseTable(Table(false), false);
            }, delegate { fallbackReads++; return basic; }, delegate(int pid, DateTime time)
            {
                processReads++; return new ConnectionProcesses.ProcessIdentity { Name = "fixture", Status = "PidNameSnapshot" };
            }, DateTime.UtcNow);
            Assert(result.Rows.Length == 2 && fallbackReads == 1 && processReads == 1 && result.ErrorCode.Contains("Ipv6OwnerTableError5"), "one failed family falls back without discarding successful owner rows");
            Assert(result.Rows.Single(delegate(ConnectionProcessRow row) { return row.LocalAddress == "::1"; }).ProcessStatus == "AssociationUnavailable" && result.Rows.Any(delegate(ConnectionProcessRow row) { return row.ProcessId == 1234; }), "fallback never invents a PID from matching endpoints");
            fallbackReads = 0;
            result = ConnectionProcesses.CaptureCore(delegate(bool ipv6) { throw new NotSupportedException(); }, delegate { fallbackReads++; return basic; },
                delegate(int pid, DateTime time) { throw new Exception("Fallback has no process owner"); }, DateTime.UtcNow);
            Assert(fallbackReads == 1 && result.Rows.Length == 2 && result.Rows.All(delegate(ConnectionProcessRow row) { return !row.ProcessId.HasValue && row.ProcessName == "Unknown" && row.ProcessStatus == "AssociationUnavailable"; }), "both failed families share one fallback snapshot and explicitly lack association");
            result = ConnectionProcesses.CaptureCore(delegate(bool ipv6) { if (ipv6) throw new InvalidDataException(); return ConnectionProcesses.ParseTable(Table(false), false); },
                delegate { throw new NetworkInformationException(5); }, delegate(int pid, DateTime time) { return new ConnectionProcesses.ProcessIdentity { Status = "Exited" }; }, DateTime.UtcNow);
            Assert(result.Rows.Length == 1 && result.ErrorCode.Contains("Ipv6OwnerTableInvalidDataException") && result.ErrorCode.Contains("FallbackNetworkInformationException"), "fallback failure retains other family with error evidence");
        }

        private static void LiveLoopback(IPAddress address)
        {
            TcpListener listener = new TcpListener(address, 0);
            listener.Start();
            try
            {
                using (Socket client = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
                {
                    client.Connect(listener.LocalEndpoint);
                    using (Socket server = listener.AcceptSocket())
                    using (Process current = Process.GetCurrentProcess())
                    {
                        int clientPort = ((IPEndPoint)client.LocalEndPoint).Port, serverPort = ((IPEndPoint)server.LocalEndPoint).Port;
                        ConnectionProcessSnapshot snapshot = null, publicSnapshot = null;
                        ConnectionProcessRow[] matching = new ConnectionProcessRow[0];
                        Func<ConnectionProcessRow, bool> matchesEndpoints = delegate(ConnectionProcessRow row) { return row.LocalAddress == address.ToString() && row.RemoteAddress == address.ToString() && ((row.LocalPort == clientPort && row.RemotePort == serverPort) || (row.LocalPort == serverPort && row.RemotePort == clientPort)); };
                        for (int attempt = 0; attempt < 5; attempt++)
                        {
                            // Ownership is tested against the real native family table before the product's
                            // intentionally bounded preview can omit these endpoints on a busy machine.
                            DateTime sampled = DateTime.UtcNow;
                            ConnectionProcessRow[] endpoints = ConnectionProcesses.ReadFamily(address.AddressFamily == AddressFamily.InterNetworkV6).Where(matchesEndpoints).ToArray();
                            snapshot = ConnectionProcesses.BuildSnapshot(endpoints, sampled, null, ConnectionProcesses.ReadProcess);
                            matching = snapshot.Rows;
                            publicSnapshot = ConnectionProcesses.Capture();
                            if (matching.Length == 2) break;
                            Thread.Sleep(20);
                        }
                        Assert(matching.Length == 2, "native family table contains both live loopback endpoints " + address);
                        Assert(matching.All(delegate(ConnectionProcessRow row) { return row.State == "Established" && row.ProcessId == current.Id && row.ProcessName == current.ProcessName && row.ProcessStatus == "PidNameSnapshot"; }), "live socket ownership resolves to this process and PID " + address);
                        Assert(publicSnapshot.Timestamp.Kind == DateTimeKind.Utc && publicSnapshot.Rows.Length <= 512 && publicSnapshot.TotalRows >= publicSnapshot.Rows.Length && publicSnapshot.Truncated == (publicSnapshot.TotalRows > 512), "public live snapshot preserves UTC sampling and output bounds");
                        Assert(publicSnapshot.Truncated || publicSnapshot.Rows.Count(matchesEndpoints) == 2, "an untruncated public snapshot must contain both live endpoints " + address);
                        Console.WriteLine("Loopback " + address + ": PID=" + current.Id + ", Process=" + current.ProcessName + ", matched=" + matching.Length + ", total=" + publicSnapshot.TotalRows + ", publicMatched=" + publicSnapshot.Rows.Count(matchesEndpoints) + ", truncated=" + publicSnapshot.Truncated);
                    }
                }
            }
            finally { listener.Stop(); }
        }

        public static int Main()
        {
            try
            {
                Parsing(); SortingAndProcessStatus(); Fallback(); LiveLoopback(IPAddress.Loopback);
                if (Socket.OSSupportsIPv6)
                {
                    try { LiveLoopback(IPAddress.IPv6Loopback); }
                    catch (SocketException ex)
                    {
                        if (ex.SocketErrorCode != SocketError.AddressFamilyNotSupported && ex.SocketErrorCode != SocketError.AddressNotAvailable && ex.SocketErrorCode != SocketError.ProtocolNotSupported) throw;
                        Console.WriteLine("IPv6 loopback unavailable: " + ex.SocketErrorCode);
                    }
                }
                else Console.WriteLine("IPv6 loopback unavailable: operating system reports no IPv6 support");
                Console.WriteLine("ConnectionProcessesTests passed: " + assertions); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
