using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using v2rayN.Forms;
using v2rayN.Resx;
using v2rayN.Tool;

namespace v2rayN
{
    static class Program
    {
        // Restart after the current message loop and shutdown cleanup complete.
        public static bool RestartForLanguageChange;

        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        static void Main()
        {
            if (Environment.OSVersion.Version.Major >= 6)
            {
                Utils.SetProcessDPIAware();
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += Application_ThreadException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;


            //AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

            if (!IsDuplicateInstance())
            {
                Logging.Setup();
                Utils.SaveLog($"v2rayN start up | {Utils.GetVersion()} | {Utils.GetExePath()}");
                Logging.ClearLogs();

                //设置语言环境
                string lang = Utils.RegReadValue(Global.MyRegPath, Global.MyRegKeyLanguage, "zh-Hans");
                var languageCulture = new System.Globalization.CultureInfo(lang);
                Thread.CurrentThread.CurrentUICulture = languageCulture;

                // Pin resource lookup to the selected UI language for all threads.
                // Background core/process callbacks may run on threads that do not
                // inherit CurrentUICulture on .NET Framework 4.0.
                ResUI.Culture = languageCulture;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());

                // Release the single-instance mutex before starting the new process.
                if (RestartForLanguageChange)
                {
                    try
                    {
                        Global.mutexObj?.Dispose();
                        Global.mutexObj = null;

                        Process.Start(new ProcessStartInfo
                        {
                            FileName = Application.ExecutablePath,
                            WorkingDirectory = Application.StartupPath,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        Utils.SaveLog("Unable to restart v2rayN after language change: " + ex.Message, ex);
                        UI.ShowWarning("Unable to restart v2rayN. Please start it manually.");
                    }
                }
            }
            else
            {
                try
                {
                    //read handle from reg and show the window
                    long.TryParse(Utils.RegReadValue(Global.MyRegPath, Utils.WindowHwndKey, ""), out long llong);
                    if (llong > 0)
                    {
                        var hwnd = (IntPtr)llong;
                        if (Utils.IsWindow(hwnd))
                        {
                            Utils.ShowWindow(hwnd, 4);
                            Utils.SwitchToThisWindow(hwnd, true);
                            return;
                        }
                    }
                }
                catch { }
                UI.ShowWarning($"v2rayN is already running(v2rayN已经运行)");
            }
        }

        //private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        //{
        //    try
        //    {
        //        string resourceName = "v2rayN.LIB." + new AssemblyName(args.Name).Name + ".dll";
        //        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
        //        {
        //            if (stream == null)
        //            {
        //                return null;
        //            }
        //            byte[] assemblyData = new byte[stream.Length];
        //            stream.Read(assemblyData, 0, assemblyData.Length);
        //            return Assembly.Load(assemblyData);
        //        }
        //    }
        //    catch
        //    {
        //        return null;
        //    }
        //}

        /// <summary> 
        /// 检查是否已在运行
        /// </summary> 
        public static bool IsDuplicateInstance()
        {
            //string name = "v2rayN";

            string name = Utils.GetExePath(); // Allow different locations to run
            name = name.Replace("\\", "/"); // https://stackoverflow.com/questions/20714120/could-not-find-a-part-of-the-path-error-while-creating-mutex

            Global.mutexObj = new Mutex(false, name, out bool bCreatedNew);
            return !bCreatedNew;
        }

        static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Utils.SaveLog("Application_ThreadException", e.Exception);
        }

        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Utils.SaveLog("CurrentDomain_UnhandledException", (Exception)e.ExceptionObject);
        }
    }
}
