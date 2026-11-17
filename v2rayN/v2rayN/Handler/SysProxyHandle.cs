using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using v2rayN.Mode;
using v2rayN.Tool;

namespace v2rayN.Handler
{
    /// <summary>
    /// Updates the current user's WinINet system proxy settings.
    /// Use the WinINet API directly so this also works on Windows XP,
    /// without depending on the bundled sysproxy.exe helper.
    /// </summary>
    public static class SysProxyHandle
    {
        private const string InternetSettingsKey =
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        private const int InternetOptionRefresh = 37;
        private const int InternetOptionSettingsChanged = 39;

        public static bool UpdateSysProxy(Config config, bool forceDisable)
        {
            if (config == null)
            {
                return false;
            }

            ESysProxyType type = config.sysProxyType;
            if (forceDisable && type == ESysProxyType.ForcedChange)
            {
                type = ESysProxyType.ForcedClear;
            }

            try
            {
                if (type == ESysProxyType.ForcedClear)
                {
                    return ResetIEProxy();
                }

                if (type == ESysProxyType.Unchanged)
                {
                    // This mode deliberately leaves the current Windows proxy untouched.
                    return true;
                }

                if (type != ESysProxyType.ForcedChange)
                {
                    return false;
                }

                int socksPort = GetSocksPort(config);
                int httpPort = socksPort + 1;
                if (socksPort <= 0 || httpPort > 65535)
                {
                    throw new InvalidOperationException("The local SOCKS/HTTP inbound ports are invalid.");
                }

                string exceptions = string.Join(";",
                    new string[]
                    {
                        config.constItem == null ? string.Empty : config.constItem.defIEProxyExceptions,
                        config.systemProxyExceptions
                    });

                string proxy;
                if (Utils.IsNullOrEmpty(config.systemProxyAdvancedProtocol))
                {
                    proxy = Global.Loopback + ":" + httpPort;
                }
                else
                {
                    proxy = config.systemProxyAdvancedProtocol
                        .Replace("{ip}", Global.Loopback)
                        .Replace("{http_port}", httpPort.ToString())
                        .Replace("{socks_port}", socksPort.ToString());
                }

                SetIEProxy(true, proxy, exceptions);
                return true;
            }
            catch (Exception ex)
            {
                Utils.SaveLog("Failed to update Windows system proxy: " + ex.Message, ex);
                return false;
            }
        }

        private static int GetSocksPort(Config config)
        {
            if (config.inbound == null)
            {
                return 0;
            }

            foreach (var inbound in config.inbound)
            {
                if (inbound != null && inbound.protocol == Global.InboundSocks)
                {
                    return inbound.localPort;
                }
            }

            return 0;
        }

        public static void ResetIEProxy4WindowsShutDown()
        {
            ResetIEProxy();
        }

        public static void SetIEProxy(bool global, string strProxy, string strExceptions)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, true))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("Cannot open the current user's Internet Settings registry key.");
                }

                if (global)
                {
                    key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                    key.SetValue("ProxyServer", strProxy ?? string.Empty, RegistryValueKind.String);
                    key.SetValue("ProxyOverride", strExceptions ?? string.Empty, RegistryValueKind.String);
                }
                else
                {
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    key.SetValue("AutoConfigURL", strProxy ?? string.Empty, RegistryValueKind.String);
                }

                key.Flush();
            }

            NotifyWinInetSettingsChanged();
        }

        public static bool ResetIEProxy()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, true))
                {
                    if (key == null)
                    {
                        throw new InvalidOperationException("Cannot open the current user's Internet Settings registry key.");
                    }

                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    key.Flush();
                }

                NotifyWinInetSettingsChanged();
                return true;
            }
            catch (Exception ex)
            {
                Utils.SaveLog("Failed to clear Windows system proxy: " + ex.Message, ex);
                return false;
            }
        }

        private static void NotifyWinInetSettingsChanged()
        {
            if (!NativeMethods.InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!NativeMethods.InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        private static class NativeMethods
        {
            [DllImport("wininet.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool InternetSetOption(
                IntPtr hInternet,
                int dwOption,
                IntPtr lpBuffer,
                int dwBufferLength);
        }
    }
}
