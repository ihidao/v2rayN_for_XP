using System.Collections.Generic;
using System.Reflection;
using System.Resources;
using System.Globalization;
using System.Threading;

namespace v2rayN
{
    public class UIRes
    {
        static ResourceManager res = new ResourceManager("v2rayN.Resx.ResUI", Assembly.GetExecutingAssembly());

        private static CultureInfo uiCulture = new CultureInfo("zh-Hans");

        public static void SetCulture(string cultureName)
        {
            uiCulture = new CultureInfo(cultureName);
        }

        static string LoadString(ResourceManager resMgr, string key)
        {
            string value = resMgr.GetString(key, uiCulture);
            if (value == null)
            {
                throw new KeyNotFoundException($"key: {key}");
            }
            return value;
        }

        private static readonly Dictionary<string, string[]> extraStrings = new Dictionary<string, string[]>
        {
            { "AppStarting", new[] { "v2rayN is starting", "v2rayN 正在启动" } },
            { "StartupSnapshotFailed", new[] { "Failed to create the startup configuration snapshot. Startup was cancelled.", "启动配置快照创建失败，已取消后台启动。" } },
            { "CoreNotReadyProxyStatsDisabled", new[] { "The core is not ready. System proxy and traffic statistics will remain disabled.", "核心未就绪，暂不启用系统代理和流量统计。" } },
            { "RestartSnapshotFailed", new[] { "Failed to create the restart configuration snapshot. Core restart was cancelled.", "重启配置快照创建失败，已取消内核重启。" } },
            { "CoreRestartFailed", new[] { "Core restart failed: ", "内核重启失败: " } },
            { "CoreNotReadyProxyStatsNotUpdated", new[] { "The core is not ready. System proxy and traffic statistics were not updated.", "核心未就绪，未更新系统代理和流量统计。" } },
            { "OldCoreStillExiting", new[] { "The previous core process has not exited. Startup was cancelled. Please try again shortly.", "旧内核进程尚未完全退出，已取消本次启动。请稍后重试。" } },
            { "PortTableReadFailedCleanup", new[] { "Unable to read the Windows TCP/UDP port table completely. Core startup was cancelled: ", "无法完整读取 Windows TCP/UDP 端口占用信息，已取消内核启动，避免误判端口状态：" } },
            { "ForcedStopStillRunning", new[] { "The process is still running after termination was requested.", "强制结束后仍未退出。" } },
            { "ProcessStillRunning", new[] { "The process is still running.", "进程仍在运行。" } },
            { "ManagedCoreStopFailed", new[] { "Unable to stop the managed core process occupying an inbound port. Startup was cancelled: ", "无法结束占用入站端口的受管理内核进程，已取消启动：" } },
            { "PortTableReadFailed", new[] { "Unable to read the Windows TCP/UDP port table. Core startup was cancelled: ", "无法读取 Windows TCP/UDP 端口占用信息，已取消内核启动，避免把未知状态误判为空闲端口：" } },
            { "InboundPortConflictPrefix", new[] { "A local inbound port configured in v2rayN is still in use by another process:", "检测到 v2rayN 配置的本地端口仍被其他进程占用：" } },
            { "InboundPortConflictSuffix", new[] { "Close the listed program or change the local inbound port in v2rayN. To avoid terminating unrelated software, v2rayN will not stop external processes.", "请关闭对应程序或修改 v2rayN 的本地入站端口。为避免误杀其他软件，v2rayN 不会结束这些外部进程。" } },
            { "PortReleaseTimeout", new[] { "Timed out waiting for the local port to be released. Check whether another program is using it.", "本地端口释放超时，请检查是否有其他程序占用端口。" } },
            { "Unknown", new[] { "Unknown", "未知" } },
            { "PortOwnerDetails", new[] { "Local port {0} is occupied by process {1} (PID: {2})", "本地端口 {0} 已被进程 {1} (PID: {2}) 占用" } },
            { "SpeedtestPortAllocateFailed", new[] { "Unable to allocate a speed-test port", "无法分配测速端口" } },
            { "OldCoreStillExitingWithPid", new[] { "The previous core process is still exiting. Startup was cancelled. PID=", "旧内核进程仍在退出，已取消本次启动。PID=" } },
            { "CoreExitedAfterStart", new[] { "The core process exited shortly after startup.", "内核进程启动后未能保持运行。" } },
            { "CoreSocksNotReady", new[] { "The core did not establish a usable SOCKS inbound within 10 seconds.", "内核进程在 10 秒内未能建立可用的 SOCKS 入站端口。" } },
            { "FailedCoreStillRunningNoFallback", new[] { "The failed core process is still running after termination was requested. The alternate startup mode will not be tried. PID=", "启动失败的内核进程在结束请求后仍未退出，已禁止尝试第二种启动参数。PID=" } },
            { "CannotConfirmFailedCoreExitedNoFallback", new[] { "Unable to confirm that the failed core process has exited. The alternate startup mode will not be tried: ", "无法确认失败的内核进程已退出，已禁止尝试第二种启动参数: " } },
            { "CoreExitedDuringStartup", new[] { "The core process exited during startup checks. Exit code: ", "内核进程在启动检查期间退出，退出代码: " } },
            { "StartupExceptionProcessStillRunning", new[] { "The core process is still running after a startup error. The alternate startup mode will not be tried. PID=", "启动异常后的内核进程仍未退出，已禁止尝试第二种启动参数。PID=" } },
            { "CannotConfirmStartupExceptionExited", new[] { "Unable to confirm that the core process exited after the startup error: ", "无法确认启动异常进程已退出: " } },
            { "NewCoreStartupFailedNoFallback", new[] { "Startup with the new arguments failed, and the process exit could not be confirmed. Compatibility retry was cancelled to avoid a port conflict.", "内核新参数启动失败，且无法确认失败进程已退出；为避免端口冲突，已取消兼容参数重试。" } },
            { "CoreStartupFailedBothArgs", new[] { "Core startup failed: neither the new nor the legacy command-line arguments worked.", "内核启动失败：新旧两种命令行参数均未能启动核心。" } },
            { "SpeedtestCoreStartFailed", new[] { "Failed to start the real-connection speed-test core. ", "真连接测速内核启动失败。" } },
            { "CoreNotFound", new[] { "Core executable not found: ", "未找到核心程序: " } },
            { "StatisticsApiTimeout", new[] { "Timed out while querying the V2Ray Statistics API", "查询 V2Ray 统计 API 超时" } },
            { "StatisticsApiNoData", new[] { "The Statistics API returned no data", "统计 API 未返回数据" } },
            { "StatisticsApiInvalidFormat", new[] { "The Statistics API returned an invalid response format", "统计 API 返回格式不正确" } },
            { "DownloadTimeoutCancelled", new[] { "Download timed out or was cancelled", "下载超时或已取消" } },
            { "SpeedtestNoData", new[] { "Speed test failed: no data received", "测速失败：未收到数据" } },
            { "SpeedtestNoValidData", new[] { "Speed test failed: no valid data received", "测速失败：未收到有效数据" } },
            { "SpeedtestCalculationError", new[] { "Speed test failed: error while calculating the speed", "测速失败：速度计算异常" } },
            { "SpeedtestGeneratingConfigFormat", new[] { "Generating speed-test configuration for {0} selected nodes.", "正在生成测速配置，共选择 {0} 个节点。" } },
            { "SpeedtestStartingCore", new[] { "Starting temporary speed-test core...", "正在启动临时测速内核……" } },
            { "SpeedtestCoreStartFailedLog", new[] { "Failed to start speed-test core: ", "测速内核启动失败：" } },
            { "SpeedtestCoreStarted", new[] { "Speed-test core started successfully. PID=", "测速内核启动成功，PID=" } },
            { "SpeedtestSkipCustomConfig", new[] { "Skipping custom configuration: ", "跳过自定义配置：" } },
            { "SpeedtestTestingNode", new[] { "Testing node: ", "开始测试节点：" } },
            { "SpeedtestPortAllocationFailed", new[] { "Speed-test port allocation failed", "测速端口分配失败" } },
            { "SpeedtestNodeFailed", new[] { "Node test failed: ", "节点 " } },
            { "SpeedtestElapsedSuffix", new[] { ", elapsed {0} ms.", "，耗时 {0} ms。" } },
            { "SpeedtestFailureReasonSuffix", new[] { ", failure reason: {0}.", "，失败原因：{0}。" } },
            { "SpeedtestError", new[] { "Speed-test error: ", "测速异常：" } },
            { "SpeedtestRequestSentFormat", new[] { "Speed-test requests sent. Waiting for {0} node(s) to respond.", "测速请求已发出，等待 {0} 个节点返回结果。" } },
            { "SpeedtestStoppingCore", new[] { "Stopping temporary speed-test core. PID=", "正在停止临时测速内核，PID=" } },
            { "SpeedtestCompletedFormat", new[] { "Real-connection speed test completed. Total time: {0} ms.", "本次真连接测速结束，总耗时 {0} ms。" } },
            { "SpeedtestNodePortFailed", new[] { "Node {0} test failed: speed-test port allocation failed.", "节点 {0} 测速失败：测速端口分配失败。" } },
            { "SpeedtestNodeTestResultPrefix", new[] { "Node {0} test result: ", "节点 {0} 测试结果：" } },
            { "SpeedtestErrorShort", new[] { "Speed-test error", "测速异常" } },
            { "SpeedtestNodeError", new[] { "Node {0} test error: {1}", "节点 {0} 测速异常：{1}" } },
            { "SpeedtestUnexpectedError", new[] { "An error occurred during the speed test: ", "测速过程发生异常：" } },
            { "NewArgsLabel", new[] { "[New arguments -c]", "[新参数 -c]" } },
            { "LegacyArgsLabel", new[] { "[Legacy arguments -config]", "[兼容参数 -config]" } },
            { "SpeedtestFailurePrefix", new[] { "Speed test failed", "测速失败" } },
            { "DownloadTimeoutPrefix", new[] { "Download timed out", "下载超时" } },
            { "GeoProxyDownloadFailedRetryDirect", new[] { "Geo file download through the local proxy failed. Retrying with a direct connection...", "通过本地代理下载 Geo 文件失败，正在尝试直连..." } },
            { "GeoDownloadFailedAllMethods", new[] { "Geo file download failed using both the proxy and direct connection: ", "Geo 文件下载失败（代理和直连均未成功）：" } },
            { "AlreadyRunning", new[] { "v2rayN is already running", "v2rayN已经运行" } },
            { "DefaultGlobal", new[] { "Global (Global)", "全局(Global)" } },
            { "DefaultBypass", new[] { "Bypass Mainland (Whitelist)", "绕过大陆(Whitelist)" } },
            { "CoreTypeV2Ray5OrAbove", new[] { "v2ray_core (5.0 and later)", "v2ray_core (5.0及以上)" } },
            { "CoreTypeV2RayBelow5", new[] { "v2ray_core (below 5.0)", "v2ray_core (5.0以下)" } },
            { "CoreTypeXray1OrAbove", new[] { "xray_core (1.0 and later)", "xray_core (1.0及以上)" } },
            { "CoreTypeXrayBelow1", new[] { "xray_core (below 1.0)", "xray_core (1.0以下)" } }
        };

        public static string I18N(string key)
        {
            string[] values;
            if (extraStrings.TryGetValue(key, out values))
            {
                return uiCulture.TwoLetterISOLanguageName == "zh"
                    ? values[1] : values[0];
            }
            return LoadString(res, key);
        }
    }
}
