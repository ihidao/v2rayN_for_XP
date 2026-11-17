using System;
using v2rayN.Mode;
using v2rayN.Tool;

namespace v2rayN.Handler
{
    public static class SysProxyHandle
    {
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
            if (global)
            {
                if (!ProxySetting.SetProxy(strProxy, strExceptions, 2))
                {
                    throw new InvalidOperationException("WinINet rejected the system proxy settings.");
                }
            }
            else
            {
                if (!ProxySetting.SetProxy(strProxy, null, 4))
                {
                    throw new InvalidOperationException("WinINet rejected the automatic proxy configuration.");
                }
            }
        }

        public static bool ResetIEProxy()
        {
            try
            {
                return ProxySetting.UnsetProxy();
            }
            catch (Exception ex)
            {
                Utils.SaveLog("Failed to clear Windows system proxy: " + ex.Message, ex);
                return false;
            }
        }
    }
}
