using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using v2rayN.Mode;

namespace v2rayN.Handler
{
    public delegate void ProcessDelegate(bool notify, string msg);

    class V2rayHandler
    {
        private static string v2rayConfigRes = Global.v2rayConfigFileName;
        private List<string> lstV2ray;
        private string coreUrl;
        public event ProcessDelegate ProcessEvent;
        private Process _process;
        private List<int> readinessPorts = new List<int>();
        private List<int> readinessUdpPorts = new List<int>();
        private bool saveCoreLogs;
        private readonly object coreLogLock = new object();

        /// <summary>
        /// True only while the process launched by this handler is still alive.
        /// The UI uses this before applying the Windows system proxy.
        /// </summary>
        public bool IsCoreRunning
        {
            get
            {
                try
                {
                    return _process != null && !_process.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        public V2rayHandler()
        {
        }

        public void LoadV2ray(Config config, bool reloadConfig)
        {
            saveCoreLogs = config != null && config.logEnabled;
            readinessPorts = new List<int>();
            readinessUdpPorts = new List<int>();
            if (config.inbound != null)
            {
                foreach (var inbound in config.inbound)
                {
                    // 系统代理依赖 SOCKS 入站；不能用其他 inbound 的端口误判核心就绪。
                    if (inbound != null
                        && inbound.protocol == Global.InboundSocks
                        && inbound.localPort > 0)
                    {
                        if (!readinessPorts.Contains(inbound.localPort))
                            readinessPorts.Add(inbound.localPort);
                        if (inbound.udpEnabled && !readinessUdpPorts.Contains(inbound.localPort))
                            readinessUdpPorts.Add(inbound.localPort);
                    }
                }
            }

            if (config.coreType == ECoreType.v2fly_core)
            {
                lstV2ray = new List<string> { "wv2ray", "v2ray" };
                coreUrl = Global.v2flyCoreUrl;
            }
            else
            {
                lstV2ray = new List<string> { "xray" };
                coreUrl = Global.xrayCoreUrl;
            }

            // 配置发生变化时需要重载；即使配置没有变化，只要本程序管理的核心已退出，
            // 也应重新生成配置并启动核心，不能仅因旧进程不再运行就报告“核心未就绪”。
            if (reloadConfig || !IsCoreRunning)
            {
                string fileName = Utils.GetPath(v2rayConfigRes);
                string msg;
                if (V2rayConfigHandler.GenerateClientConfig(config, fileName, false, out msg) != 0)
                {
                    ShowMsg(false, msg);
                    return;
                }

                // 先停止本程序自己管理的旧核心，再做端口预检。
                // 否则旧核心仍占用 SOCKS/HTTP 端口，会被误判为外部端口冲突。
                V2rayStop();

                // Do not start a second core while the previous process is still alive.
                if (IsCoreRunning)
                {
                    string stopMessage = UIRes.I18N("OldCoreStillExiting");
                    ShowMsg(false, stopMessage);
                    Utils.SaveLog(stopMessage);
                    return;
                }

                // 只清理占用本次入站端口、且可执行文件确实位于当前 v2rayN
                // 安装目录中的 v2ray/wv2ray/xray 核心。其他软件或独立核心不误杀。
                string cleanupMessage;
                if (!StopManagedCoreProcessesOnInboundPorts(config, out cleanupMessage))
                {
                    ShowMsg(false, cleanupMessage);
                    Utils.SaveLog(cleanupMessage);
                    return;
                }

                string conflict;
                if (WaitForInboundPortsRelease(config, out conflict))
                {
                    ShowMsg(false, conflict);
                    Utils.SaveLog(conflict);
                    return;
                }

                ShowMsg(true, msg);
                V2rayStart();
            }
        }

        /// <summary>
        /// 清理占用当前配置入站端口的本程序安装目录内的核心进程。
        /// 不按进程名全局结束进程，避免误杀其他目录中的独立 V2Ray/Xray。
        /// </summary>
        private bool StopManagedCoreProcessesOnInboundPorts(Config config, out string message)
        {
            message = String.Empty;
            if (config == null || config.inbound == null || config.inbound.Count == 0)
                return true;

            IPAddress listenAddress = config.allowLANConn ? IPAddress.Any : IPAddress.Loopback;
            Dictionary<int, bool> tcpPorts = new Dictionary<int, bool>();
            Dictionary<int, bool> udpPorts = new Dictionary<int, bool>();
            foreach (var inbound in config.inbound)
            {
                if (inbound == null || inbound.localPort <= 0 || inbound.localPort > 65535)
                    continue;
                tcpPorts[inbound.localPort] = true;
                if (inbound.udpEnabled)
                    udpPorts[inbound.localPort] = true;
            }
            if (config.inbound[0] != null)
            {
                int httpPort = config.inbound[0].localPort + 1;
                if (httpPort > 0 && httpPort <= 65535)
                    tcpPorts[httpPort] = true;
            }

            List<uint> ownerIds = new List<uint>();
            // 端口归属查询必须完整成功后才能执行清理；如果 TCP/UDP 表查询失败，
            // 不允许基于不完整信息继续启动，以免漏掉旧核心或误判端口空闲。
            try
            {
                foreach (TcpOwnerRow row in GetTcpListenerOwners())
                {
                    int port = GetHostPort(row.localPort);
                    IPAddress address = new IPAddress(BitConverter.GetBytes(row.localAddr));
                    if (tcpPorts.ContainsKey(port) && IsAddressConflict(listenAddress, address)
                        && !ownerIds.Contains(row.owningPid))
                        ownerIds.Add(row.owningPid);
                }

                foreach (UdpOwnerRow row in GetUdpOwners())
                {
                    int port = GetHostPort(row.localPort);
                    IPAddress address = new IPAddress(BitConverter.GetBytes(row.localAddr));
                    if (udpPorts.ContainsKey(port) && IsAddressConflict(listenAddress, address)
                        && !ownerIds.Contains(row.owningPid))
                        ownerIds.Add(row.owningPid);
                }
            }
            catch (Exception ex)
            {
                message = UIRes.I18N("PortTableReadFailedCleanup")
                    + ex.Message;
                Utils.SaveLog("[CoreCleanup] Port owner enumeration failed; startup aborted: " + ex.Message, ex);
                return false;
            }

            StringBuilder failures = new StringBuilder();
            foreach (uint ownerId in ownerIds)
            {
                Process owner = null;
                try
                {
                    owner = Process.GetProcessById((int)ownerId);
                    if (!IsManagedCoreProcess(owner))
                    {
                        Utils.SaveLog("[CoreCleanup] Keeping external port owner; PID=" + ownerId
                            + "; process=" + owner.ProcessName + ".");
                        continue;
                    }

                    string processName = owner.ProcessName;
                    if (!owner.HasExited)
                    {
                        try { owner.CloseMainWindow(); } catch { }
                        if (!owner.WaitForExit(500))
                        {
                            owner.Kill();
                            if (!owner.WaitForExit(5000))
                                throw new InvalidOperationException(UIRes.I18N("ForcedStopStillRunning"));
                        }
                    }
                    if (!owner.HasExited)
                        throw new InvalidOperationException(UIRes.I18N("ProcessStillRunning"));
                    Utils.SaveLog("[CoreCleanup] Terminated managed core occupying inbound port: "
                        + processName + "; PID=" + ownerId);
                }
                catch (ArgumentException)
                {
                    // The owner exited between the port-table snapshot and PID lookup.
                }
                catch (Exception ex)
                {
                    failures.AppendLine("PID=" + ownerId + ": " + ex.Message);
                }
                finally
                {
                    if (owner != null) try { owner.Dispose(); } catch { }
                }
            }

            if (failures.Length > 0)
            {
                message = UIRes.I18N("ManagedCoreStopFailed")
                    + Environment.NewLine + failures.ToString();
                return false;
            }
            return true;
        }

        private bool IsManagedCoreProcess(Process process)
        {
            if (process == null)
                return false;
            string name;
            try { name = process.ProcessName; }
            catch { return false; }
            if (!String.Equals(name, "v2ray", StringComparison.OrdinalIgnoreCase)
                && !String.Equals(name, "wv2ray", StringComparison.OrdinalIgnoreCase)
                && !String.Equals(name, "xray", StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                string actualPath = Path.GetFullPath(process.MainModule.FileName);
                string[] knownCoreFiles = new string[] { "v2ray.exe", "wv2ray.exe", "xray.exe" };
                foreach (string coreFile in knownCoreFiles)
                {
                    string expectedPath = Path.GetFullPath(Utils.GetPath(coreFile));
                    if (String.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch (Exception ex)
            {
                Utils.SaveLog("[CoreCleanup] Cannot verify executable path; process will not be terminated. PID="
                    + process.Id + "; " + ex.Message);
            }
            return false;
        }

        /// <summary>
        /// 检查配置端口的占用者。只报告未被安全清理的外部程序。
        /// </summary>
        private bool FindInboundPortConflict(Config config, out string message, out bool inspectionFailed)
        {
            message = String.Empty;
            inspectionFailed = false;
            if (config == null || config.inbound == null || config.inbound.Count == 0)
                return false;

            IPAddress listenAddress = config.allowLANConn ? IPAddress.Any : IPAddress.Loopback;
            Dictionary<int, bool> tcpPorts = new Dictionary<int, bool>();
            Dictionary<int, bool> udpPorts = new Dictionary<int, bool>();

            foreach (var inbound in config.inbound)
            {
                if (inbound == null || inbound.localPort <= 0 || inbound.localPort > 65535)
                    continue;
                tcpPorts[inbound.localPort] = true;
                if (inbound.udpEnabled)
                    udpPorts[inbound.localPort] = true;
            }

            // 当前 GUI 的 HTTP 入站端口按第一个 inbound 端口 + 1 生成。
            if (config.inbound[0] != null)
            {
                int httpPort = config.inbound[0].localPort + 1;
                if (httpPort > 0 && httpPort <= 65535)
                    tcpPorts[httpPort] = true;
            }

            StringBuilder conflicts = new StringBuilder();
            HashSet<string> reportedConflicts = new HashSet<string>();
            try
            {
                // TCP_TABLE_OWNER_PID_LISTENER 只返回 LISTENING，不把 TIME_WAIT
                // 等非监听状态当作端口冲突。
                foreach (TcpOwnerRow row in GetTcpListenerOwners())
                {
                    int port = GetHostPort(row.localPort);
                    if (!tcpPorts.ContainsKey(port))
                        continue;

                    IPAddress boundAddress = new IPAddress(BitConverter.GetBytes(row.localAddr));
                    if (!IsAddressConflict(listenAddress, boundAddress))
                        continue;

                    string ownerDetails = FormatPortOwner(port, row.owningPid);
                    if (reportedConflicts.Add(ownerDetails))
                    {
                        if (conflicts.Length > 0) conflicts.Append(" | ");
                        conflicts.Append(ownerDetails);
                    }
                }

                // UDP 没有 LISTENING 状态，按实际绑定端口及 owning PID 判断。
                foreach (UdpOwnerRow row in GetUdpOwners())
                {
                    int port = GetHostPort(row.localPort);
                    if (!udpPorts.ContainsKey(port))
                        continue;

                    IPAddress boundAddress = new IPAddress(BitConverter.GetBytes(row.localAddr));
                    if (!IsAddressConflict(listenAddress, boundAddress))
                        continue;

                    string ownerDetails = FormatPortOwner(port, row.owningPid);
                    if (reportedConflicts.Add(ownerDetails))
                    {
                        if (conflicts.Length > 0) conflicts.Append(" | ");
                        conflicts.Append(ownerDetails);
                    }
                }
            }
            catch (Exception ex)
            {
                inspectionFailed = true;
                message = UIRes.I18N("PortTableReadFailed")
                    + ex.Message;
                Utils.SaveLog("[PortCheck] Port owner inspection failed; startup aborted: " + ex.Message, ex);
                return true;
            }

            if (conflicts.Length == 0)
                return false;

            message = "[Error] " + conflicts.ToString();
            return true;
        }

        /// <summary>
        /// 内核进程退出后，等待 Windows 网络栈释放其 TCP/UDP 监听端口。
        /// 仅检查 TCP LISTENING 和 UDP 绑定，不把 TCP TIME_WAIT 当作端口占用。
        /// 每 100ms 检查一次，最多等待 1 秒；超时保留端口及占用进程详情。
        /// </summary>
        private bool WaitForInboundPortsRelease(Config config, out string message)
        {
            const int timeoutMs = 1000;
            const int retryIntervalMs = 100;
            Stopwatch waitWatch = Stopwatch.StartNew();
            string lastConflict = String.Empty;

            while (true)
            {
                bool inspectionFailed;
                if (!FindInboundPortConflict(config, out lastConflict, out inspectionFailed))
                {
                    if (waitWatch.ElapsedMilliseconds > 0)
                        Utils.SaveLog("[PortCheck] Configured inbound ports released after "
                            + waitWatch.ElapsedMilliseconds + " ms.");
                    message = String.Empty;
                    return false;
                }

                // 查询失败不是普通端口冲突：立即中止，不继续等待或启动。
                if (inspectionFailed)
                {
                    message = lastConflict;
                    return true;
                }

                if (waitWatch.ElapsedMilliseconds >= timeoutMs)
                    break;

                System.Threading.Thread.Sleep(retryIntervalMs);
            }

            // Keep the last owner snapshot: otherwise the user only sees a generic
            // timeout and cannot identify which port and process blocked startup.
            message = lastConflict;
            return true;
        }

        private bool IsAddressConflict(IPAddress targetAddress, IPAddress boundAddress)
        {
            return boundAddress.Equals(IPAddress.Any)
                || targetAddress.Equals(IPAddress.Any)
                || boundAddress.Equals(targetAddress);
        }

        private string FormatPortOwner(int port, uint processId)
        {
            string processName = UIRes.I18N("Unknown");
            try
            {
                using (Process owner = Process.GetProcessById((int)processId))
                {
                    processName = owner.ProcessName;
                    if (!processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && !String.Equals(processName, "System", StringComparison.OrdinalIgnoreCase))
                        processName += ".exe";
                }
            }
            catch
            {
                // 进程可能在枚举端口后立即退出，保留 PID 供用户排查。
            }

            // The combined conflict message adds one [Error] prefix for all ports.
            return String.Format(UIRes.I18N("PortOwnerDetails"),
                port, processName, processId);
        }

        private int GetHostPort(uint networkPort)
        {
            return (ushort)IPAddress.NetworkToHostOrder((short)(networkPort & 0xFFFF));
        }

        private List<TcpOwnerRow> GetTcpListenerOwners()
        {
            const int AF_INET = 2;
            const int TCP_TABLE_OWNER_PID_LISTENER = 3;
            int bufferLength = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref bufferLength, true, AF_INET,
                TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (bufferLength <= 0)
                return new List<TcpOwnerRow>();

            IntPtr buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                int result = GetExtendedTcpTable(buffer, ref bufferLength, true, AF_INET,
                    TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (result != 0)
                    throw new InvalidOperationException("GetExtendedTcpTable failed: " + result);

                uint count = (uint)Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf(typeof(TcpOwnerRow));
                IntPtr rowPointer = IntPtr.Add(buffer, sizeof(uint));
                List<TcpOwnerRow> rows = new List<TcpOwnerRow>();
                for (uint i = 0; i < count; i++)
                {
                    rows.Add((TcpOwnerRow)Marshal.PtrToStructure(rowPointer, typeof(TcpOwnerRow)));
                    rowPointer = IntPtr.Add(rowPointer, rowSize);
                }
                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private List<UdpOwnerRow> GetUdpOwners()
        {
            const int AF_INET = 2;
            const int UDP_TABLE_OWNER_PID = 1;
            int bufferLength = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref bufferLength, true, AF_INET,
                UDP_TABLE_OWNER_PID, 0);
            if (bufferLength <= 0)
                return new List<UdpOwnerRow>();

            IntPtr buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                int result = GetExtendedUdpTable(buffer, ref bufferLength, true, AF_INET,
                    UDP_TABLE_OWNER_PID, 0);
                if (result != 0)
                    throw new InvalidOperationException("GetExtendedUdpTable failed: " + result);

                uint count = (uint)Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf(typeof(UdpOwnerRow));
                IntPtr rowPointer = IntPtr.Add(buffer, sizeof(uint));
                List<UdpOwnerRow> rows = new List<UdpOwnerRow>();
                for (uint i = 0; i < count; i++)
                {
                    rows.Add((UdpOwnerRow)Marshal.PtrToStructure(rowPointer, typeof(UdpOwnerRow)));
                    rowPointer = IntPtr.Add(rowPointer, rowSize);
                }
                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TcpOwnerRow
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UdpOwnerRow
        {
            public uint localAddr;
            public uint localPort;
            public uint owningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedTcpTable(
            IntPtr tcpTable, ref int outBufferLength, bool sort, int ipVersion,
            int tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedUdpTable(
            IntPtr udpTable, ref int outBufferLength, bool sort, int ipVersion,
            int tableClass, uint reserved);

        public int LoadV2rayConfigString(Config config, List<int> selecteds,
            out Dictionary<int, int> speedTestPorts)
        {
            speedTestPorts = new Dictionary<int, int>();

            try
            {
                // Ask Windows for currently unused ephemeral ports instead of
                // deriving fixed ports from the SOCKS port (which can collide).
                foreach (int index in selecteds)
                {
                    if (index < 0 || index >= config.vmess.Count
                        || config.vmess[index].configType == (int)EConfigType.Custom)
                        continue;

                    int port = GetAvailablePort();
                    while (speedTestPorts.ContainsValue(port))
                        port = GetAvailablePort();

                    speedTestPorts[index] = port;
                }

                string msg;
                string configStr = V2rayConfigHandler.GenerateClientSpeedtestConfigString(
                    config, selecteds, speedTestPorts, out msg);
                if (configStr == "")
                {
                    ShowMsg(false, msg);
                    return -1;
                }

                ShowMsg(false, msg);
                return V2rayStartNew(configStr, new List<int>(speedTestPorts.Values));
            }
            catch (Exception ex)
            {
                Utils.SaveLog("Unable to allocate speed-test ports: " + ex.Message, ex);
                ShowMsg(false, UIRes.I18N("SpeedtestPortAllocateFailed") + ": " + ex.Message);
                return -1;
            }
        }

        private int GetAvailablePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        private void V2rayRestart()
        {
            V2rayStop();
            V2rayStart();
        }

        public void V2rayStop()
        {
            try
            {
                // 先停止由当前 V2rayHandler 启动并持有的核心进程；
                // 随后仅按入站端口定位并清理安装目录内的旧核心。
                if (_process != null)
                {
                    Process oldProcess = _process;
                    KillProcess(oldProcess);

                    // Never forget a process that is still alive: the next start
                    // must not race its UDP/TCP listeners being released.
                    if (!oldProcess.HasExited)
                        throw new InvalidOperationException(UIRes.I18N("OldCoreStillExitingWithPid") + oldProcess.Id);

                    oldProcess.Dispose();
                    _process = null;
                }
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }
        }

        public void V2rayStopPid(int pid)
        {
            try
            {
                Process p = Process.GetProcessById(pid);
                KillProcess(p);
                p.Dispose();
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }
        }

        private string V2rayFindexe()
        {
            if (lstV2ray == null)
            {
                lstV2ray = new List<string> { "wv2ray", "v2ray" };
                coreUrl = Global.v2flyCoreUrl;
            }

            foreach (string name in lstV2ray)
            {
                string fileName = Utils.GetPath(name + ".exe");
                if (File.Exists(fileName))
                    return fileName;
            }

            string msg = string.Format(UIRes.I18N("NotFoundCore"), coreUrl ?? Global.v2flyCoreUrl);
            ShowMsg(false, msg);
            return string.Empty;
        }

        // Wait for the core's own startup message, then briefly verify the SOCKS listener.
        // Both output streams are consumed asynchronously to avoid pipe deadlocks.
        private Process TryStartCore(string fileName, string arguments, List<int> ports, bool checkUdp,
            bool saveLogs, out string failure, out long startupElapsedMs, out string coreNameVersion,
            out bool canFallback)
        {
            failure = string.Empty;
            startupElapsedMs = 0;
            coreNameVersion = "V2Ray";
            canFallback = true;
            string detectedCoreNameVersion = "V2Ray";
            Stopwatch startupWatch = new Stopwatch();
            Process p = new Process();
            StringBuilder errorText = new StringBuilder();
            bool processStarted = false;
            p.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = Utils.StartupPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            DataReceivedEventHandler outputHandler = (sender, e) =>
            {
                if (String.IsNullOrEmpty(e.Data)) return;
                string banner = e.Data.Trim();
                if (banner.StartsWith("V2Ray ", StringComparison.OrdinalIgnoreCase)
                    || banner.StartsWith("Xray ", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = banner.Split(new char[] { ' ', '\t' },
                        StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                        detectedCoreNameVersion = parts[0] + " " + parts[1];
                }
                // Forward every core output line to the GUI. MainForm applies the
                // configured log level and display filtering. When requested, the
                // same output is also copied to Vaccess.log / Verror.log below.
                if (IsImportantCoreDiagnostic(e.Data))
                    Utils.SaveLogDetail("CORE", e.Data);
                if (saveLogs)
                    AppendCoreLog("Vaccess.log", e.Data);
                ShowMsg(false, e.Data + Environment.NewLine);
            };
            DataReceivedEventHandler errorHandler = (sender, e) =>
            {
                if (String.IsNullOrEmpty(e.Data)) return;
                lock (errorText) errorText.AppendLine(e.Data);
                // Keep stderr diagnostics visible through the same GUI filtering path.
                if (IsImportantCoreDiagnostic(e.Data))
                    Utils.SaveLogDetail("CORE", e.Data);
                if (saveLogs)
                    AppendCoreLog("Verror.log", e.Data);
                ShowMsg(false, e.Data + Environment.NewLine);
            };
            p.OutputDataReceived += outputHandler;
            p.ErrorDataReceived += errorHandler;

            try
            {
                if (!p.Start())
                    throw new Exception("Process.Start returned false.");
                processStarted = true;

                startupWatch.Start();
                Utils.SaveLog("[CoreStartup] Process created; PID=" + p.Id
                    + "; time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    + "; arguments=" + arguments);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                // Readiness is determined by the configured SOCKS listener, not by
                // an INFO-level "started" message which may be suppressed by loglevel.
                if (ports == null || ports.Count == 0)
                {
                    // There is no SOCKS listener to probe. Give the process a brief
                    // startup grace period, then accept it only if it remains alive.
                    if (!p.WaitForExit(500) && !p.HasExited)
                    {
                        startupElapsedMs = startupWatch.ElapsedMilliseconds;
                        coreNameVersion = detectedCoreNameVersion;
                        return p;
                    }
                }
                else
                {
                    // Poll the SOCKS listener from the beginning. This works with
                    // debug/info/warning/error/none because it does not depend on logs.
                    for (int elapsed = 0; elapsed < 10000; elapsed += 50)
                    {
                        if (p.HasExited) break;
                        if (IsAnyPortReady(ports, p.Id, checkUdp) && !p.HasExited)
                        {
                            startupElapsedMs = startupWatch.ElapsedMilliseconds;
                            coreNameVersion = detectedCoreNameVersion;
                            return p;
                        }
                        if (p.WaitForExit(50)) break;
                    }
                }

                if (!p.HasExited)
                {
                    startupElapsedMs = startupWatch.ElapsedMilliseconds;
                    failure = ports == null || ports.Count == 0
                        ? UIRes.I18N("CoreExitedAfterStart")
                        : UIRes.I18N("CoreSocksNotReady");

                    // Do not try the legacy command line until this process is
                    // confirmed stopped; otherwise both attempts may compete for ports.
                    try
                    {
                        p.Kill();
                        if (!p.WaitForExit(5000))
                        {
                            canFallback = false;
                            failure += Environment.NewLine + UIRes.I18N("FailedCoreStillRunningNoFallback")
                                + p.Id;
                        }
                    }
                    catch (Exception stopEx)
                    {
                        canFallback = false;
                        failure += Environment.NewLine + UIRes.I18N("CannotConfirmFailedCoreExitedNoFallback")
                            + stopEx.Message;
                    }
                    try { p.Dispose(); } catch { }
                    return null;
                }

                p.WaitForExit();
                startupElapsedMs = startupWatch.ElapsedMilliseconds;
                lock (errorText) failure = errorText.ToString();
                if (String.IsNullOrEmpty(failure))
                    failure = UIRes.I18N("CoreExitedDuringStartup") + p.ExitCode;
                p.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                lock (errorText) failure = errorText.ToString();
                failure = ex.Message + (String.IsNullOrEmpty(failure) ? "" : Environment.NewLine + failure);
                if (processStarted)
                {
                    try
                    {
                        if (!p.HasExited)
                        {
                            p.Kill();
                            if (!p.WaitForExit(5000))
                            {
                                canFallback = false;
                                failure += Environment.NewLine + UIRes.I18N("StartupExceptionProcessStillRunning")
                                    + p.Id;
                            }
                        }
                    }
                    catch (Exception stopEx)
                    {
                        canFallback = false;
                        failure += Environment.NewLine + UIRes.I18N("CannotConfirmStartupExceptionExited") + stopEx.Message;
                    }
                }
                try { p.Dispose(); } catch { }
                return null;
            }
        }

        /// <summary>
        /// Save the same core output that is forwarded to the GUI. Keeping this
        /// separate from the core's own file logging ensures logging is always
        /// visible in the main window, regardless of the save-log checkbox.
        /// </summary>
        private void AppendCoreLog(string fileName, string line)
        {
            try
            {
                string path = Utils.GetPath(fileName);
                lock (coreLogLock)
                {
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                Utils.SaveLog("[CoreLog] Unable to save " + fileName + ": " + ex.Message, ex);
            }
        }

        private bool IsCoreStartedMessage(string line)
        {
            if (String.IsNullOrEmpty(line)) return false;
            return line.IndexOf("started", StringComparison.OrdinalIgnoreCase) >= 0
                && (line.IndexOf("V2Ray", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Xray", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private bool IsImportantCoreDiagnostic(string line)
        {
            if (String.IsNullOrEmpty(line)) return false;
            string value = line.ToLowerInvariant();
            return value.IndexOf("[warning]", StringComparison.Ordinal) >= 0
                || value.IndexOf("[error]", StringComparison.Ordinal) >= 0
                || value.IndexOf("[fatal]", StringComparison.Ordinal) >= 0
                || value.IndexOf("panic:", StringComparison.Ordinal) >= 0
                || value.IndexOf("failed", StringComparison.Ordinal) >= 0;
        }

        private bool IsRoutineCoreOutput(string line)
        {
            if (String.IsNullOrEmpty(line)) return false;
            if (IsCoreStartedMessage(line)) return true;

            string value = line.Trim();
            if (String.Equals(value, "A unified platform for anti-censorship.",
                StringComparison.OrdinalIgnoreCase))
                return true;

            // Standard V2Ray/Xray version banners; do not suppress arbitrary warnings.
            if (value.StartsWith("V2Ray ", StringComparison.OrdinalIgnoreCase)
                && value.IndexOf("(V2Fly", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (value.StartsWith("Xray ", StringComparison.OrdinalIgnoreCase)
                && value.IndexOf("(Xray,", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        private bool IsAnyPortReady(List<int> ports, int processId, bool checkUdp)
        {
            if (ports == null || ports.Count == 0) return false;

            // PID 必须对应当前安装目录中的受管理核心程序；避免仅凭端口号
            // 或可连接状态，把其他进程误认为本次启动的内核。
            Process startedCore = null;
            try
            {
                startedCore = Process.GetProcessById(processId);
                if (!IsManagedCoreProcess(startedCore) || startedCore.HasExited)
                    return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (startedCore != null)
                    try { startedCore.Dispose(); } catch { }
            }

            List<TcpOwnerRow> tcpOwners;
            List<UdpOwnerRow> udpOwners;
            try
            {
                tcpOwners = GetTcpListenerOwners();
                udpOwners = checkUdp && readinessUdpPorts.Count > 0 ? GetUdpOwners() : new List<UdpOwnerRow>();
            }
            catch (Exception ex)
            {
                Utils.SaveLog("[CoreStartup] Unable to verify listener process ownership: " + ex.Message, ex);
                return false;
            }

            foreach (int port in ports)
            {
                bool ownedByStartedCore = false;
                foreach (TcpOwnerRow owner in tcpOwners)
                {
                    if (GetHostPort(owner.localPort) == port && owner.owningPid == (uint)processId)
                    {
                        ownedByStartedCore = true;
                        break;
                    }
                }
                if (!ownedByStartedCore)
                    continue;

                if (checkUdp)
                {
                    bool udpReady = true;
                    foreach (int udpPort in readinessUdpPorts)
                    {
                        bool udpOwnedByStartedCore = false;
                        foreach (UdpOwnerRow owner in udpOwners)
                        {
                            if (GetHostPort(owner.localPort) == udpPort && owner.owningPid == (uint)processId)
                            {
                                udpOwnedByStartedCore = true;
                                break;
                            }
                        }
                        if (!udpOwnedByStartedCore)
                        {
                            udpReady = false;
                            break;
                        }
                    }
                    if (!udpReady)
                        continue;
                }

                try
                {
                    using (TcpClient client = new TcpClient())
                    {
                        client.Connect(IPAddress.Loopback, port);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        /// <summary>
        /// 通过核心自身的 version 命令检测版本。优先使用现代子命令，
        /// 再兼容旧版 -version 参数；检测失败时返回 false，由启动逻辑保留兼容回退。
        /// </summary>
        /// <summary>
        /// Run the core's version command. A successful exit and a recognized
        /// version string indicate support for the modern "run -c" syntax.
        /// Otherwise use the legacy "-config" syntax.
        /// </summary>
        private bool TryDetectCoreVersion(string fileName, out string coreNameVersion,
            out bool useModernArguments)
        {
            coreNameVersion = String.Empty;
            useModernArguments = false;

            string[] versionArguments = new string[] { "version", "-version" };
            foreach (string arguments in versionArguments)
            {
                Process probe = new Process();
                StringBuilder output = new StringBuilder();
                object outputLock = new object();
                try
                {
                    probe.StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        WorkingDirectory = Utils.StartupPath(),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    };
                    DataReceivedEventHandler capture = (sender, e) =>
                    {
                        if (!String.IsNullOrEmpty(e.Data))
                        {
                            lock (outputLock) output.AppendLine(e.Data);
                        }
                    };
                    probe.OutputDataReceived += capture;
                    probe.ErrorDataReceived += capture;

                    if (!probe.Start())
                        continue;
                    probe.BeginOutputReadLine();
                    probe.BeginErrorReadLine();

                    if (!probe.WaitForExit(3000))
                    {
                        try { probe.Kill(); probe.WaitForExit(1000); } catch { }
                        continue;
                    }
                    probe.WaitForExit();

                    string versionOutput;
                    lock (outputLock) versionOutput = output.ToString();

                    Match match = Regex.Match(versionOutput,
                        @"(?im)\b(V2Ray|Xray)\s+v?(\d+)\.(\d+)(?:\.(\d+))?");
                    if (probe.ExitCode != 0 || !match.Success)
                        continue;

                    coreNameVersion = match.Groups[1].Value + " "
                        + match.Groups[2].Value + "." + match.Groups[3].Value
                        + (match.Groups[4].Success ? "." + match.Groups[4].Value : String.Empty);
                    useModernArguments = true;
                    Utils.SaveLog("[CoreStartup] Version query succeeded: " + coreNameVersion
                        + "; using modern config argument -c.");
                    return true;
                }
                catch (Exception ex)
                {
                    Utils.SaveLog("[CoreStartup] Version query '" + arguments
                        + "' failed: " + ex.Message);
                }
                finally
                {
                    try { probe.Dispose(); } catch { }
                }
            }

            Utils.SaveLog("[CoreStartup] Version query failed or returned no valid version; "
                + "using legacy config argument -config.");
            return false;
        }

        private bool IsArgumentCompatibilityError(string error)
        {
            if (String.IsNullOrEmpty(error)) return false;
            string value = error.ToLowerInvariant();
            return value.Contains("unknown command")
                || value.Contains("unknown flag")
                || value.Contains("unknown shorthand flag")
                || value.Contains("flag provided but not defined")
                || value.Contains("unknown option");
        }

        private Process StartCoreWithFallback(string fileName, string configFile, List<int> ports, bool checkUdp,
            bool saveLogs, out string failure, out long startupElapsedMs, out bool usedLegacyArguments,
            out string coreNameVersion)
        {
            Stopwatch totalWatch = Stopwatch.StartNew();
            startupElapsedMs = 0;
            usedLegacyArguments = false;
            coreNameVersion = "V2Ray";

            string detectedVersion;
            bool useModernArguments;
            bool versionDetected = TryDetectCoreVersion(fileName, out detectedVersion,
                out useModernArguments);
            string primaryArguments = (versionDetected && !useModernArguments ? "-config \"" :
                "run -c \"") + configFile + "\"";
            string primaryError;
            long primaryElapsed;
            string startedVersion;
            bool canFallback;

            Process p = TryStartCore(fileName, primaryArguments, ports, checkUdp, saveLogs,
                out primaryError, out primaryElapsed, out startedVersion, out canFallback);
            if (p != null)
            {
                totalWatch.Stop();
                startupElapsedMs = totalWatch.ElapsedMilliseconds;
                coreNameVersion = String.IsNullOrEmpty(detectedVersion) ? startedVersion : detectedVersion;
                usedLegacyArguments = versionDetected && !useModernArguments;
                failure = String.Empty;
                Utils.SaveLog("[CoreStartup] PID=" + p.Id
                    + "; core=" + coreNameVersion
                    + "; config-arg=" + (usedLegacyArguments ? "-config" : "-c")
                    + "; elapsed=" + startupElapsedMs + " ms; SOCKS ready.");
                return p;
            }

            if (!canFallback)
            {
                totalWatch.Stop();
                startupElapsedMs = totalWatch.ElapsedMilliseconds;
                failure = primaryError;
                return null;
            }

            // 版本查询成功时，参数选择已有明确依据；仅在明确的 CLI 不兼容错误下回退。
            // 版本查询失败时已按旧参数启动，不再反复尝试新参数。
            bool shouldFallback = versionDetected
                && IsArgumentCompatibilityError(primaryError);
            if (!shouldFallback)
            {
                totalWatch.Stop();
                startupElapsedMs = totalWatch.ElapsedMilliseconds;
                failure = primaryError;
                Utils.SaveLog("[CoreStartup] Startup failed; no argument fallback because the error "
                    + "does not indicate CLI incompatibility. " + primaryError);
                return null;
            }

            string fallbackArguments = (versionDetected && useModernArguments ? "-config \"" :
                "run -c \"") + configFile + "\"";
            Utils.SaveLog("[CoreStartup] Primary config argument failed after " + primaryElapsed
                + " ms; trying compatibility argument. " + primaryError);

            string fallbackError;
            long fallbackElapsed;
            string fallbackVersion;
            bool unusedCanFallback;
            p = TryStartCore(fileName, fallbackArguments, ports, checkUdp, saveLogs,
                out fallbackError, out fallbackElapsed, out fallbackVersion, out unusedCanFallback);
            totalWatch.Stop();
            startupElapsedMs = totalWatch.ElapsedMilliseconds;
            if (p != null)
            {
                usedLegacyArguments = fallbackArguments.StartsWith("-config ",
                    StringComparison.Ordinal);
                coreNameVersion = String.IsNullOrEmpty(detectedVersion) ? fallbackVersion : detectedVersion;
                failure = String.Empty;
                Utils.SaveLog("[CoreStartup] PID=" + p.Id
                    + "; core=" + coreNameVersion
                    + "; config-arg=" + (usedLegacyArguments ? "-config" : "-c")
                    + "; elapsed=" + startupElapsedMs + " ms; SOCKS ready.");
                return p;
            }

            failure = UIRes.I18N("CoreStartupFailedBothArgs")
                + Environment.NewLine + UIRes.I18N("NewArgsLabel") + Environment.NewLine
                + (primaryArguments.StartsWith("-config ", StringComparison.Ordinal)
                    ? fallbackError : primaryError)
                + Environment.NewLine + UIRes.I18N("LegacyArgsLabel") + Environment.NewLine
                + (primaryArguments.StartsWith("-config ", StringComparison.Ordinal)
                    ? primaryError : fallbackError);
            return null;
        }

        private void V2rayStart()
        {
            ShowMsg(false, UIRes.I18N("StartService"));
            try
            {
                string fileName = V2rayFindexe();
                if (fileName == "") return;

                string failure;
                long startupElapsedMs;
                bool usedLegacyArguments;
                string coreNameVersion;
                Process p = StartCoreWithFallback(fileName,
                    Utils.GetPath(v2rayConfigRes), readinessPorts, true, saveCoreLogs, out failure,
                    out startupElapsedMs, out usedLegacyArguments, out coreNameVersion);
                if (p == null)
                    throw new Exception(failure);

                try { p.PriorityClass = ProcessPriorityClass.High; } catch { }
                _process = p;
                Global.processJob.AddProcess(p.Handle);

            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
                ShowMsg(true, ex.Message);
            }
        }

        private int V2rayStartNew(string configStr, List<int> ports)
        {
            ShowMsg(false, UIRes.I18N("StartService"));
            string tempConfigFile = null;
            Process p = null;
            try
            {
                string fileName = V2rayFindexe();
                if (fileName == "") return -1;

                tempConfigFile = Path.Combine(Path.GetTempPath(),
                    "v2rayN-speedtest-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(tempConfigFile, configStr, new UTF8Encoding(false));

                string failure;
                long startupElapsedMs;
                bool usedLegacyArguments;
                string coreNameVersion;
                p = StartCoreWithFallback(fileName, tempConfigFile, ports, false, false, out failure,
                    out startupElapsedMs, out usedLegacyArguments, out coreNameVersion);
                if (p == null)
                    throw new Exception(UIRes.I18N("SpeedtestCoreStartFailed") + failure);

                // The core has loaded its configuration; the temporary file is no longer needed.
                try { File.Delete(tempConfigFile); } catch { }
                tempConfigFile = null;

                Global.processJob.AddProcess(p.Handle);
                return p.Id;
            }
            catch (Exception ex)
            {
                if (p != null)
                {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                    try { p.Dispose(); } catch { }
                }
                if (!String.IsNullOrEmpty(tempConfigFile))
                {
                    try { File.Delete(tempConfigFile); } catch { }
                }
                Utils.SaveLog(ex.Message, ex);
                ShowMsg(false, ex.Message);
                return -1;
            }
        }

        private void ShowMsg(bool updateToTrayTooltip, string msg)
        {
            ProcessEvent?.Invoke(updateToTrayTooltip, msg);
        }

        private void KillProcess(Process p)
        {
            try
            {
                if (p == null || p.HasExited) return;
                p.CloseMainWindow();
                if (!p.WaitForExit(500))
                {
                    p.Kill();
                    if (!p.WaitForExit(5000))
                        Utils.SaveLog("Core process did not exit after Kill. PID=" + p.Id);
                }
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }
        }
    }
}