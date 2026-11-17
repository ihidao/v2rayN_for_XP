using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using v2rayN.Mode;
using v2rayN.Tool;

namespace v2rayN.Handler
{
    /// <summary>
    /// Manages the current user's Windows Internet (WinINet) proxy settings.
    /// Uses the same registry-based approach as the stable 4.20 XP branch.
    /// </summary>
    public static class SysProxyHandle
    {
        private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private static RegistryKey OpenSettingsKey()
        {
            RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, true);
            if (key == null)
                key = Registry.CurrentUser.CreateSubKey(InternetSettingsPath);

            if (key == null)
                throw new InvalidOperationException("Cannot open the current user's Internet Settings registry key.");

            return key;
        }

        private static void NotifyInternetSettingsChanged()
        {
            bool changed = InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            bool refreshed = InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
            if (!changed || !refreshed)
                Utils.SaveLog("[SysProxy] InternetSetOption notification failed. Win32 error: " + Marshal.GetLastWin32Error());
        }

        private static void ApplyGlobalProxy(string proxyServer, string bypassList)
        {
            if (String.IsNullOrEmpty(proxyServer))
                throw new ArgumentException("Proxy server address is empty.", "proxyServer");

            using (RegistryKey key = OpenSettingsKey())
            {
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", proxyServer, RegistryValueKind.String);
                key.SetValue("ProxyOverride", bypassList ?? String.Empty, RegistryValueKind.String);
                key.DeleteValue("AutoConfigURL", false);
            }

            NotifyInternetSettingsChanged();
        }

        private static void ApplyPac(string pacUrl)
        {
            if (String.IsNullOrEmpty(pacUrl))
                throw new ArgumentException("PAC URL is empty.", "pacUrl");

            using (RegistryKey key = OpenSettingsKey())
            {
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                key.SetValue("AutoConfigURL", pacUrl, RegistryValueKind.String);
            }

            NotifyInternetSettingsChanged();
        }

        private static string BuildBypassList(Config config)
        {
            string defaults = config.constItem == null
                ? Global.IEProxyExceptions
                : config.constItem.defIEProxyExceptions;
            string configured = config.systemProxyExceptions ?? String.Empty;
            string combined = String.Join(";", new string[] { defaults ?? String.Empty, configured, "<local>" });
            string[] entries = combined.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            System.Collections.Generic.List<string> unique = new System.Collections.Generic.List<string>();
            foreach (string entry in entries)
            {
                string value = entry.Trim();
                if (value.Length == 0)
                    continue;
                bool exists = false;
                foreach (string current in unique)
                {
                    if (String.Equals(current, value, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                    unique.Add(value);
            }
            return String.Join(";", unique.ToArray());
        }

        public static bool UpdateSysProxy(Config config, bool forceDisable)
        {
            if (config == null)
                return false;

            ESysProxyType type = config.sysProxyType;
            if (forceDisable && type == ESysProxyType.ForcedChange)
                type = ESysProxyType.ForcedClear;

            try
            {
                if (type == ESysProxyType.ForcedClear)
                    return ResetIEProxy();

                if (type == ESysProxyType.Unchanged)
                    return true;

                if (type != ESysProxyType.ForcedChange)
                    return false;

                int httpPort = config.GetLocalPort(Global.InboundHttp);
                if (httpPort <= 0 || httpPort > 65535)
                    throw new InvalidOperationException("The local HTTP inbound port is invalid.");

                string proxyServer = Global.Loopback + ":" + httpPort.ToString();
                if (!Utils.IsNullOrEmpty(config.systemProxyAdvancedProtocol))
                {
                    int socksPort = config.GetLocalPort(Global.InboundSocks);
                    proxyServer = config.systemProxyAdvancedProtocol
                        .Replace("{ip}", Global.Loopback)
                        .Replace("{http_port}", httpPort.ToString())
                        .Replace("{socks_port}", socksPort.ToString());
                }

                ApplyGlobalProxy(proxyServer, BuildBypassList(config));
                return true;
            }
            catch (Exception ex)
            {
                Utils.SaveLog("[SysProxy] Failed to update system proxy: " + ex.Message, ex);
                return false;
            }
        }

        public static void ResetIEProxy4WindowsShutDown()
        {
            ResetIEProxy();
        }

        public static void SetIEProxy(bool global, string strProxy, string strExceptions)
        {
            try
            {
                if (global)
                    ApplyGlobalProxy(strProxy, String.IsNullOrEmpty(strExceptions)
                        ? Global.IEProxyExceptions + ";<local>" : strExceptions);
                else
                    ApplyPac(strProxy);
            }
            catch (Exception ex)
            {
                Utils.SaveLog("[SysProxy] Failed to set system proxy: " + ex.Message, ex);
                throw;
            }
        }

        public static bool ResetIEProxy()
        {
            try
            {
                using (RegistryKey key = OpenSettingsKey())
                {
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    key.DeleteValue("ProxyServer", false);
                    key.DeleteValue("ProxyOverride", false);
                    key.DeleteValue("AutoConfigURL", false);
                }

                NotifyInternetSettingsChanged();
                return true;
            }
            catch (Exception ex)
            {
                Utils.SaveLog("[SysProxy] Reset system proxy failed: " + ex.Message, ex);
                return false;
            }
        }
    }
}