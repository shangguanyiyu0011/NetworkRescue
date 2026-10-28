using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;

namespace NetworkRescue
{
    public sealed class ConnectionProcessRow
    {
        public string LocalAddress, RemoteAddress, State, ProcessName, ProcessStatus;
        public int LocalPort, RemotePort;
        public int? ProcessId;
    }

    public sealed class ConnectionProcessSnapshot
    {
        public DateTime Timestamp;
        public ConnectionProcessRow[] Rows = new ConnectionProcessRow[0];
        public int TotalRows;
        public bool Truncated;
        public string ErrorCode;
    }

    public static class ConnectionProcesses
    {
        private const int MaximumTableBytes = 16 * 1024 * 1024;
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sort, int family, int tableClass, uint reserved);

        internal sealed class ProcessIdentity
        {
            internal string Name = "Unknown", Status;
        }

        public static ConnectionProcessSnapshot Capture()
        {
            return CaptureCore(ReadFamily, delegate { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections(); }, ReadProcess, DateTime.UtcNow);
        }

        internal static ConnectionProcessSnapshot CaptureCore(Func<bool, ConnectionProcessRow[]> readFamily, Func<TcpConnectionInformation[]> fallback,
            Func<int, DateTime, ProcessIdentity> readProcess, DateTime sampled)
        {
            List<ConnectionProcessRow> rows = new List<ConnectionProcessRow>();
            List<string> errors = new List<string>();
            TcpConnectionInformation[] fallbackRows = null;
            bool fallbackRead = false;
            foreach (bool ipv6 in new[] { false, true })
            {
                string family = ipv6 ? "Ipv6" : "Ipv4";
                try { rows.AddRange(readFamily(ipv6)); }
                catch (Exception ex)
                {
                    if (!IsCaptureFailure(ex)) throw;
                    Win32Exception native = ex as Win32Exception;
                    errors.Add(family + "OwnerTable" + (native == null ? ex.GetType().Name : "Error" + native.NativeErrorCode));
                    if (!fallbackRead)
                    {
                        fallbackRead = true;
                        try { fallbackRows = fallback(); }
                        catch (Exception failed)
                        {
                            if (!IsCaptureFailure(failed)) throw;
                            errors.Add("Fallback" + failed.GetType().Name);
                        }
                    }
                    foreach (TcpConnectionInformation connection in fallbackRows ?? new TcpConnectionInformation[0])
                    {
                        if ((connection.LocalEndPoint.AddressFamily == AddressFamily.InterNetworkV6) != ipv6) continue;
                        if (connection.State == TcpState.Listen) continue;
                        rows.Add(new ConnectionProcessRow
                        {
                            LocalAddress = connection.LocalEndPoint.Address.ToString(), LocalPort = connection.LocalEndPoint.Port,
                            RemoteAddress = connection.RemoteEndPoint.Address.ToString(), RemotePort = connection.RemoteEndPoint.Port,
                            State = connection.State.ToString(), ProcessName = "Unknown", ProcessStatus = "AssociationUnavailable"
                        });
                    }
                }
            }
            return BuildSnapshot(rows, sampled, errors.Count == 0 ? null : String.Join(";", errors), readProcess);
        }

        private static bool IsCaptureFailure(Exception ex)
        {
            return ex is Win32Exception || ex is NetworkInformationException || ex is InvalidDataException || ex is DllNotFoundException
                || ex is EntryPointNotFoundException || ex is BadImageFormatException || ex is NotSupportedException || ex is SecurityException || ex is UnauthorizedAccessException;
        }

        internal static ConnectionProcessRow[] ReadFamily(bool ipv6)
        {
            int size = 0;
            uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, ipv6 ? 23 : 2, 4, 0);
            if (status != 0 && status != 122) throw new Win32Exception((int)status);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (size < 4 || size > MaximumTableBytes) throw new InvalidDataException("InvalidTcpTableSize");
                IntPtr table = Marshal.AllocHGlobal(size);
                try
                {
                    int actual = size;
                    status = GetExtendedTcpTable(table, ref actual, false, ipv6 ? 23 : 2, 4, 0);
                    if (status == 122) { size = actual; continue; }
                    if (status != 0) throw new Win32Exception((int)status);
                    if (actual < 4 || actual > size) throw new InvalidDataException("InvalidTcpTableSize");
                    byte[] bytes = new byte[actual];
                    Marshal.Copy(table, bytes, 0, actual);
                    return ParseTable(bytes, ipv6);
                }
                finally { Marshal.FreeHGlobal(table); }
            }
            throw new InvalidDataException("TcpTableChangedRepeatedly");
        }

        private static uint Dword(byte[] bytes, int offset)
        {
            return (uint)bytes[offset] | ((uint)bytes[offset + 1] << 8) | ((uint)bytes[offset + 2] << 16) | ((uint)bytes[offset + 3] << 24);
        }

        private static string Address(byte[] bytes, int offset, bool ipv6, uint scope)
        {
            byte[] address = new byte[ipv6 ? 16 : 4];
            Buffer.BlockCopy(bytes, offset, address, 0, address.Length);
            return (ipv6 ? new IPAddress(address, scope) : new IPAddress(address)).ToString();
        }

        internal static ConnectionProcessRow[] ParseTable(byte[] bytes, bool ipv6)
        {
            int stride = ipv6 ? 56 : 24;
            if (bytes == null || bytes.Length < 4 || bytes.Length > MaximumTableBytes) throw new InvalidDataException("InvalidTcpTableSize");
            uint count = Dword(bytes, 0);
            if (count > (bytes.Length - 4) / stride) throw new InvalidDataException("InvalidTcpTableCount");
            List<ConnectionProcessRow> rows = new List<ConnectionProcessRow>();
            for (int i = 0; i < count; i++)
            {
                int offset = 4 + i * stride;
                uint state = Dword(bytes, offset + (ipv6 ? 48 : 0)), pid = Dword(bytes, offset + (ipv6 ? 52 : 20));
                if (state == (uint)TcpState.Listen) continue;
                int localPort = offset + (ipv6 ? 20 : 8), remotePort = offset + (ipv6 ? 44 : 16);
                rows.Add(new ConnectionProcessRow
                {
                    LocalAddress = Address(bytes, offset + (ipv6 ? 0 : 4), ipv6, ipv6 ? Dword(bytes, offset + 16) : 0),
                    RemoteAddress = Address(bytes, offset + (ipv6 ? 24 : 12), ipv6, ipv6 ? Dword(bytes, offset + 40) : 0),
                    LocalPort = (bytes[localPort] << 8) | bytes[localPort + 1], RemotePort = (bytes[remotePort] << 8) | bytes[remotePort + 1],
                    State = state >= 1 && state <= 12 ? ((TcpState)state).ToString() : "Unknown",
                    ProcessId = pid <= Int32.MaxValue ? (int?)pid : null,
                    ProcessName = "Unknown", ProcessStatus = pid > 0 && pid <= Int32.MaxValue ? "Pending" : "Unavailable"
                });
            }
            return rows.ToArray();
        }

        internal static ConnectionProcessSnapshot BuildSnapshot(IEnumerable<ConnectionProcessRow> rows, DateTime sampled, string error,
            Func<int, DateTime, ProcessIdentity> readProcess)
        {
            ConnectionProcessRow[] unique = rows.GroupBy(delegate(ConnectionProcessRow row)
            {
                return row.LocalAddress + "|" + row.LocalPort + "|" + row.RemoteAddress + "|" + row.RemotePort + "|" + row.State + "|" + row.ProcessId;
            }, StringComparer.Ordinal).Select(delegate(IGrouping<string, ConnectionProcessRow> group) { return group.First(); })
                .OrderByDescending(delegate(ConnectionProcessRow row) { return row.State == "Established"; })
                .ThenBy(delegate(ConnectionProcessRow row) { return row.RemoteAddress; }, StringComparer.OrdinalIgnoreCase)
                .ThenBy(delegate(ConnectionProcessRow row) { return row.RemotePort; })
                .ThenBy(delegate(ConnectionProcessRow row) { return row.LocalAddress; }, StringComparer.OrdinalIgnoreCase)
                .ThenBy(delegate(ConnectionProcessRow row) { return row.LocalPort; })
                .ThenBy(delegate(ConnectionProcessRow row) { return row.State; }, StringComparer.Ordinal)
                .ThenBy(delegate(ConnectionProcessRow row) { return row.ProcessId; }).ToArray();
            ConnectionProcessSnapshot snapshot = new ConnectionProcessSnapshot { Timestamp = sampled, TotalRows = unique.Length, Truncated = unique.Length > 512, Rows = unique.Take(512).ToArray(), ErrorCode = error };
            Dictionary<int, ProcessIdentity> processes = new Dictionary<int, ProcessIdentity>();
            foreach (ConnectionProcessRow row in snapshot.Rows)
            {
                if (!row.ProcessId.HasValue || row.ProcessId.Value <= 0) continue;
                ProcessIdentity identity;
                if (!processes.TryGetValue(row.ProcessId.Value, out identity))
                {
                    identity = readProcess(row.ProcessId.Value, sampled);
                    processes.Add(row.ProcessId.Value, identity);
                }
                row.ProcessName = identity.Name;
                row.ProcessStatus = identity.Status;
            }
            return snapshot;
        }

        internal static ProcessIdentity ReadProcess(int pid, DateTime sampled)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    if (process.HasExited) return new ProcessIdentity { Status = "Exited" };
                    if (process.StartTime.ToUniversalTime() > sampled) return new ProcessIdentity { Status = "ProcessChanged" };
                    string name = process.ProcessName;
                    if (process.HasExited) return new ProcessIdentity { Status = "Exited" };
                    if (String.IsNullOrEmpty(name)) return new ProcessIdentity { Status = "Unavailable" };
                    // PID and name are sampled separately; this is a current observation, never historical ownership.
                    return new ProcessIdentity { Name = name.Length <= 256 ? name : name.Substring(0, 256), Status = "PidNameSnapshot" };
                }
            }
            catch (ArgumentException) { return new ProcessIdentity { Status = "Exited" }; }
            catch (InvalidOperationException) { return new ProcessIdentity { Status = "Exited" }; }
            catch (Win32Exception ex) { return new ProcessIdentity { Status = ex.NativeErrorCode == 5 ? "AccessDenied" : "Unavailable" }; }
            catch (SecurityException) { return new ProcessIdentity { Status = "AccessDenied" }; }
            catch (UnauthorizedAccessException) { return new ProcessIdentity { Status = "AccessDenied" }; }
            catch (NotSupportedException) { return new ProcessIdentity { Status = "Unavailable" }; }
        }
    }
}
