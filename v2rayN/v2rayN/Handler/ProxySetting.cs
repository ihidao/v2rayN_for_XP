using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace v2rayN.Handler
{
    /// <summary>
    /// Sets WinINet per-connection proxy options. This API is supported by Windows XP
    /// and updates the active LAN connection rather than only editing registry values.
    /// </summary>
    internal static class ProxySetting
    {
        public static bool UnsetProxy()
        {
            return SetProxy(null, null, 1);
        }

        public static bool SetProxy(string proxyServer, string exceptions, int type)
        {
            if (type != 1 && type != 2 && type != 4)
            {
                throw new ArgumentOutOfRangeException("type");
            }

            int optionCount = type == 1 ? 1 : (type == 2 ? 3 : (String.IsNullOrEmpty(exceptions) ? 2 : 3));
            InternetConnectionOption[] options = new InternetConnectionOption[optionCount];
            IntPtr[] allocatedStrings = new IntPtr[2];
            IntPtr optionsPtr = IntPtr.Zero;
            IntPtr listPtr = IntPtr.Zero;

            try
            {
                int flags;
                PerConnOption valueOption;

                if (type == 1)
                {
                    flags = (int)PerConnFlags.PROXY_TYPE_DIRECT;
                    valueOption = PerConnOption.INTERNET_PER_CONN_FLAGS;
                }
                else if (type == 2)
                {
                    flags = (int)(PerConnFlags.PROXY_TYPE_DIRECT | PerConnFlags.PROXY_TYPE_PROXY);
                    valueOption = PerConnOption.INTERNET_PER_CONN_PROXY_SERVER;
                }
                else
                {
                    flags = (int)(PerConnFlags.PROXY_TYPE_DIRECT | PerConnFlags.PROXY_TYPE_AUTO_PROXY_URL);
                    valueOption = PerConnOption.INTERNET_PER_CONN_AUTOCONFIG_URL;
                }

                options[0].m_Option = PerConnOption.INTERNET_PER_CONN_FLAGS;
                options[0].m_Value.m_Int = flags;

                if (optionCount > 1)
                {
                    allocatedStrings[0] = Marshal.StringToHGlobalAuto(proxyServer ?? String.Empty);
                    options[1].m_Option = valueOption;
                    options[1].m_Value.m_StringPtr = allocatedStrings[0];

                    if (optionCount > 2)
                    {
                        allocatedStrings[1] = Marshal.StringToHGlobalAuto(exceptions);
                        options[2].m_Option = PerConnOption.INTERNET_PER_CONN_PROXY_BYPASS;
                        options[2].m_Value.m_StringPtr = allocatedStrings[1];
                    }
                }

                int optionSize = Marshal.SizeOf(typeof(InternetConnectionOption));
                optionsPtr = Marshal.AllocCoTaskMem(optionSize * options.Length);
                for (int i = 0; i < options.Length; i++)
                {
                    IntPtr optionPtr = new IntPtr(optionsPtr.ToInt64() + (i * optionSize));
                    Marshal.StructureToPtr(options[i], optionPtr, false);
                }

                InternetPerConnOptionList list = new InternetPerConnOptionList();
                list.dwSize = Marshal.SizeOf(typeof(InternetPerConnOptionList));
                list.szConnection = IntPtr.Zero; // Default LAN connection
                list.dwOptionCount = options.Length;
                list.dwOptionError = 0;
                list.options = optionsPtr;

                listPtr = Marshal.AllocCoTaskMem(list.dwSize);
                Marshal.StructureToPtr(list, listPtr, false);

                if (!NativeMethods.InternetSetOption(
                    IntPtr.Zero,
                    InternetOption.INTERNET_OPTION_PER_CONNECTION_OPTION,
                    listPtr,
                    list.dwSize))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                NotifySettingsChanged();
                return true;
            }
            finally
            {
                if (listPtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(listPtr);
                }
                if (optionsPtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(optionsPtr);
                }
                for (int i = 0; i < allocatedStrings.Length; i++)
                {
                    if (allocatedStrings[i] != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(allocatedStrings[i]);
                    }
                }
            }
        }


        public static bool UsedProxy()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", false))
            {
                return key != null && Convert.ToInt32(key.GetValue("ProxyEnable", 0)) == 1;
            }
        }

        public static string GetProxyProxyServer()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", false))
            {
                return key == null ? String.Empty : Convert.ToString(key.GetValue("ProxyServer", String.Empty));
            }
        }

        private static void NotifySettingsChanged()
        {
            if (!NativeMethods.InternetSetOption(IntPtr.Zero, InternetOption.INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!NativeMethods.InternetSetOption(IntPtr.Zero, InternetOption.INTERNET_OPTION_REFRESH, IntPtr.Zero, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct InternetPerConnOptionList
        {
            public int dwSize;
            public IntPtr szConnection;
            public int dwOptionCount;
            public int dwOptionError;
            public IntPtr options;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct InternetConnectionOption
        {
            public PerConnOption m_Option;
            public InternetConnectionOptionValue m_Value;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InternetConnectionOptionValue
        {
            [FieldOffset(0)]
            public int m_Int;

            [FieldOffset(0)]
            public IntPtr m_StringPtr;
        }

        private enum InternetOption : int
        {
            INTERNET_OPTION_PER_CONNECTION_OPTION = 75,
            INTERNET_OPTION_REFRESH = 37,
            INTERNET_OPTION_SETTINGS_CHANGED = 39
        }

        private enum PerConnOption : int
        {
            INTERNET_PER_CONN_FLAGS = 1,
            INTERNET_PER_CONN_PROXY_SERVER = 2,
            INTERNET_PER_CONN_PROXY_BYPASS = 3,
            INTERNET_PER_CONN_AUTOCONFIG_URL = 4
        }

        [Flags]
        private enum PerConnFlags : int
        {
            PROXY_TYPE_DIRECT = 0x00000001,
            PROXY_TYPE_PROXY = 0x00000002,
            PROXY_TYPE_AUTO_PROXY_URL = 0x00000004
        }

        private static class NativeMethods
        {
            [DllImport("wininet.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool InternetSetOption(
                IntPtr hInternet,
                InternetOption dwOption,
                IntPtr lpBuffer,
                int dwBufferLength);
        }
    }
}
