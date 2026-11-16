using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using v2rayN.Handler;
using v2rayN.HttpProxyHandler;
using v2rayN.Mode;
using v2rayN.Base;
using v2rayN.Tool;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace v2rayN.Forms
{
    public partial class MainForm : BaseForm
    {
        private V2rayHandler v2rayHandler;
        private List<int> lvSelecteds = new List<int>();
        private StatisticsHandler statistics = null;
        private string MsgFilter = string.Empty;
        private volatile bool startupExiting;
        // Serializes initial startup and later core restarts without blocking the UI thread.
        private bool coreRestartInProgress;
        private bool coreRestartPending;
        private bool pendingReloadConfig;
        // Do not let an early tray click race the first-time window/UI initialization.
        private bool startupUiReady;
        private bool trayShowPending;

        #region Window 事件

        public MainForm()
        {
            InitializeComponent();

            // Save the user's complete proxy state before startup logic can change it.

            // Show a usable tray icon immediately; do not wait for the core to become ready.
            notifyMain.Icon = this.Icon ?? SystemIcons.Application;
            notifyMain.Text = UIRes.I18N("AppStarting");
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;
            notifyMain.Visible = true;
            this.Text = Utils.GetVersion();

            // Disable update checks for this Windows XP build.
            tsbCheckUpdateN.Enabled = false;
            tsbCheckUpdateCore.Enabled = false;
            tsbCheckUpdateXrayCore.Enabled = false;

            // Remove the promotion button and its separator from the toolbar.
            tsbPromotion.Visible = false;
            toolStripSeparator11.Visible = false;

            Global.processJob = new Job();

            Application.ApplicationExit += (sender, args) =>
            {
                MyAppExit(false);
            };
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            Stopwatch startupWatch = Stopwatch.StartNew();
            ConfigHandler.LoadConfig(ref config);
            Utils.SaveLog("[Startup] Config.LoadConfig: " + startupWatch.ElapsedMilliseconds + " ms");

            startupWatch.Restart();
            ConfigHandler.InitBuiltinRouting(ref config);
            Utils.SaveLog("[Startup] Built-in routing initialization: " + startupWatch.ElapsedMilliseconds + " ms");

            v2rayHandler = new V2rayHandler();
            v2rayHandler.ProcessEvent += v2rayHandler_ProcessEvent;

        }

        private void MainForm_VisibleChanged(object sender, EventArgs e)
        {
            if (statistics == null || !statistics.Enable) return;
            if ((sender as Form).Visible)
            {
                statistics.UpdateUI = true;
            }
            else
            {
                statistics.UpdateUI = false;
            }
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            try
            {
                // Hide the initial Application.Run() display before accepting tray requests.
                // The tray icon is already available, and core startup remains independent.
                HideForm();

                // A core-startup exception must not prevent the main window from becoming usable.
                try
                {
                    StartInitialV2ray();
                }
                catch (Exception ex)
                {
                    Utils.SaveLog("Initial core startup scheduling failed: " + ex.Message, ex);
                }

                Stopwatch sw = Stopwatch.StartNew();
                try
                {
                    InitServersView();
                    RefreshServers();
                    RefreshRoutingsMenu();
                    RestoreUI();
                    sw.Stop();
                    Utils.SaveLog("[Startup] UI server/routing initialization: "
                        + sw.ElapsedMilliseconds + " ms");
                }
                catch (Exception ex)
                {
                    Utils.SaveLog("UI initialization failed: " + ex.Message, ex);
                }
            }
            catch (Exception ex)
            {
                Utils.SaveLog("Main window startup failed: " + ex.Message, ex);
            }
            finally
            {
                // Always release queued tray requests, even if core startup or UI setup fails.
                startupUiReady = true;
                if (trayShowPending && !startupExiting && !IsDisposed && !Disposing)
                {
                    trayShowPending = false;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (!startupExiting && !IsDisposed && !Disposing)
                                ShowForm();
                        });
                    }
                    catch (InvalidOperationException) { }
                }
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            switch (e.CloseReason)
            {
                case CloseReason.UserClosing:
                    StorageUI();
                    e.Cancel = true;
                    HideForm();
                    break;
                case CloseReason.ApplicationExitCall:
                case CloseReason.FormOwnerClosing:
                case CloseReason.TaskManagerClosing:
                    MyAppExit(false);
                    break;
                case CloseReason.WindowsShutDown:
                    MyAppExit(true);
                    break;
            }
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            //if (this.WindowState == FormWindowState.Minimized)
            //{
            //    HideForm();
            //}
            //else
            //{

            //}
        }
        private void MyAppExit(bool blWindowsShutDown)
        {
            startupExiting = true;

            // Stop the core independently so a shutdown error cannot skip proxy cleanup.
            try
            {
                // 不在 UI 线程等待后台启动；若启动过程随后完成，worker 的 finally 会再次停止核心。
                if (v2rayHandler != null)
                    v2rayHandler.V2rayStop();
            }
            catch (Exception ex)
            {
                Utils.SaveLog("[Exit] Stop core failed: " + ex.Message, ex);
            }

            // Always attempt proxy cleanup, even if stopping the core failed.
            try
            {
                if (blWindowsShutDown)
                {
                    HttpProxyHandle.ResetIEProxy4WindowsShutDown();
                }
                else
                {
                    HttpProxyHandle.UpdateSysProxy(config, true);
                }
            }
            catch (Exception ex)
            {
                Utils.SaveLog("[Exit] Clear system proxy failed: " + ex.Message, ex);
            }

            try
            {
                ConfigHandler.SaveConfig(ref config);
            }
            catch (Exception ex)
            {
                Utils.SaveLog("[Exit] Save configuration failed: " + ex.Message, ex);
            }

            try
            {
                statistics?.SaveToFile();
            }
            catch (Exception ex)
            {
                Utils.SaveLog("[Exit] Save statistics failed: " + ex.Message, ex);
            }

            try
            {
                statistics?.Close();
            }
            catch (Exception ex)
            {
                Utils.SaveLog("[Exit] Close statistics failed: " + ex.Message, ex);
            }
        }

        private void RestoreUI()
        {
            scMain.Panel2Collapsed = true;

            if (!config.uiItem.mainSize.IsEmpty)
            {
                this.Width = config.uiItem.mainSize.Width;
                this.Height = config.uiItem.mainSize.Height;
            }

            for (int k = 0; k < lvServers.Columns.Count; k++)
            {
                var width = ConfigHandler.GetformMainLvColWidth(ref config, ((EServerColName)k).ToString(), lvServers.Columns[k].Width);
                lvServers.Columns[k].Width = width;
            }
        }

        private void StorageUI()
        {
            config.uiItem.mainSize = new Size(this.Width, this.Height);

            for (int k = 0; k < lvServers.Columns.Count; k++)
            {
                ConfigHandler.AddformMainLvColWidth(ref config, ((EServerColName)k).ToString(), lvServers.Columns[k].Width);
            }
        }

        #endregion

        #region 显示服务器 listview 和 menu

        /// <summary>
        /// 刷新服务器
        /// </summary>
        private void RefreshServers()
        {
            RefreshServersView();
            //lvServers.AutoResizeColumns();
            RefreshServersMenu();
        }

        /// <summary>
        /// 初始化服务器列表
        /// </summary>
        private void InitServersView()
        {
            lvServers.BeginUpdate();
            lvServers.Items.Clear();

            lvServers.GridLines = true;
            lvServers.FullRowSelect = true;
            lvServers.View = View.Details;
            lvServers.Scrollable = true;
            lvServers.MultiSelect = true;
            lvServers.HeaderStyle = ColumnHeaderStyle.Clickable;

            lvServers.Columns.Add("", 30);
            lvServers.Columns.Add(UIRes.I18N("LvServiceType"), 80);
            lvServers.Columns.Add(UIRes.I18N("LvAlias"), 100);
            lvServers.Columns.Add(UIRes.I18N("LvAddress"), 120);
            lvServers.Columns.Add(UIRes.I18N("LvPort"), 50);
            lvServers.Columns.Add(UIRes.I18N("LvEncryptionMethod"), 90);
            lvServers.Columns.Add(UIRes.I18N("LvTransportProtocol"), 70);
            lvServers.Columns.Add(UIRes.I18N("LvSubscription"), 50);
            lvServers.Columns.Add(UIRes.I18N("LvTestResults"), 70, HorizontalAlignment.Right);

            if (config.enableStatistics)
            {
                // 统计列先按配置创建；统计数据文件在核心启动后再后台读取。
                lvServers.Columns.Add(UIRes.I18N("LvTodayDownloadDataAmount"), 70);
                lvServers.Columns.Add(UIRes.I18N("LvTodayUploadDataAmount"), 70);
                lvServers.Columns.Add(UIRes.I18N("LvTotalDownloadDataAmount"), 70);
                lvServers.Columns.Add(UIRes.I18N("LvTotalUploadDataAmount"), 70);
            }
            lvServers.EndUpdate();
        }

        /// <summary>
        /// 刷新服务器列表
        /// </summary>
        private void RefreshServersView()
        {
            int index = lvServers.SelectedIndices.Count > 0 ? lvServers.SelectedIndices[0] : -1;

            lvServers.BeginUpdate();
            lvServers.Items.Clear();

            for (int k = 0; k < config.vmess.Count; k++)
            {
                string def = string.Empty;
                string totalUp = string.Empty,
                        totalDown = string.Empty,
                        todayUp = string.Empty,
                        todayDown = string.Empty;
                if (config.index.Equals(k))
                {
                    def = "√";
                }

                VmessItem item = config.vmess[k];

                bool stats = statistics != null && statistics.Enable;
                if (stats)
                {
                    ServerStatItem sItem = statistics.Statistic.Find(item_ => item_.itemId == item.getItemId());
                    if (sItem != null)
                    {
                        totalUp = Utils.HumanFy(sItem.totalUp);
                        totalDown = Utils.HumanFy(sItem.totalDown);
                        todayUp = Utils.HumanFy(sItem.todayUp);
                        todayDown = Utils.HumanFy(sItem.todayDown);
                    }
                }
                ListViewItem lvItem = new ListViewItem(def);
                Utils.AddSubItem(lvItem, EServerColName.configType.ToString(), ((EConfigType)item.configType).ToString());
                Utils.AddSubItem(lvItem, EServerColName.remarks.ToString(), item.remarks);
                Utils.AddSubItem(lvItem, EServerColName.address.ToString(), item.address);
                Utils.AddSubItem(lvItem, EServerColName.port.ToString(), item.port.ToString());
                Utils.AddSubItem(lvItem, EServerColName.security.ToString(), item.security);
                Utils.AddSubItem(lvItem, EServerColName.network.ToString(), item.network);
                Utils.AddSubItem(lvItem, EServerColName.subRemarks.ToString(), item.getSubRemarks(config));
                Utils.AddSubItem(lvItem, EServerColName.testResult.ToString(), item.testResult);
                if (stats)
                {
                    Utils.AddSubItem(lvItem, EServerColName.todayDown.ToString(), todayDown);
                    Utils.AddSubItem(lvItem, EServerColName.todayUp.ToString(), todayUp);
                    Utils.AddSubItem(lvItem, EServerColName.totalDown.ToString(), totalDown);
                    Utils.AddSubItem(lvItem, EServerColName.totalUp.ToString(), totalUp);
                }

                if (k % 2 == 1) // 隔行着色
                {
                    lvItem.BackColor = Color.WhiteSmoke;
                }
                if (config.index.Equals(k))
                {
                    //lvItem.Checked = true;
                    lvItem.ForeColor = Color.DodgerBlue;
                    lvItem.Font = new Font(lvItem.Font, FontStyle.Bold);
                }

                if (lvItem != null) lvServers.Items.Add(lvItem);
            }
            lvServers.EndUpdate();

            if (index >= 0 && index < lvServers.Items.Count && lvServers.Items.Count > 0)
            {
                lvServers.Items[index].Selected = true;
                lvServers.EnsureVisible(index); // workaround
            }
        }

        /// <summary>
        /// 刷新托盘服务器菜单
        /// </summary>
        private void RefreshServersMenu()
        {
            menuServers.DropDownItems.Clear();

            List<ToolStripMenuItem> lst = new List<ToolStripMenuItem>();
            for (int k = 0; k < config.vmess.Count; k++)
            {
                VmessItem item = config.vmess[k];
                string name = item.getSummary();

                ToolStripMenuItem ts = new ToolStripMenuItem(name)
                {
                    Tag = k
                };
                if (config.index.Equals(k))
                {
                    ts.Checked = true;
                }
                ts.Click += new EventHandler(ts_Click);
                lst.Add(ts);
            }
            menuServers.DropDownItems.AddRange(lst.ToArray());
        }

        private void ts_Click(object sender, EventArgs e)
        {
            try
            {
                ToolStripItem ts = (ToolStripItem)sender;
                int index = Utils.ToInt(ts.Tag);
                SetDefaultServer(index);
            }
            catch
            {
            }
        }

        private void lvServers_SelectedIndexChanged(object sender, EventArgs e)
        {
            int index = -1;
            try
            {
                if (lvServers.SelectedIndices.Count > 0)
                {
                    index = lvServers.SelectedIndices[0];
                }
            }
            catch
            {
            }
            if (index < 0)
            {
                return;
            }
            //qrCodeControl.showQRCode(index, config);
        }

        private void DisplayToolStatus()
        {
            toolSslSocksPort.Text = $"{Global.Loopback}:{config.inbound[0].localPort}";
            toolSslHttpPort.Text = $"{Global.Loopback}:{Global.httpPort}";

            notifyMain.Icon = MainFormHandler.Instance.GetNotifyIcon(config, this.Icon);
        }
        private void ssMain_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            if (!Utils.IsNullOrEmpty(e.ClickedItem.Text))
            {
                Utils.SetClipboardData(e.ClickedItem.Text);
            }
        }

        private void lvServers_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (e.Column < 0)
            {
                return;
            }

            try
            {
                var tag = lvServers.Columns[e.Column].Tag?.ToString();
                bool asc = Utils.IsNullOrEmpty(tag) ? true : !Convert.ToBoolean(tag);
                if (ConfigHandler.SortServers(ref config, (EServerColName)e.Column, asc) != 0)
                {
                    return;
                }
                lvServers.Columns[e.Column].Tag = Convert.ToString(asc);
                RefreshServers();
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }

            if (e.Column < 0)
            {
                return;
            }

        }
        #endregion

        #region v2ray 操作

        /// <summary>
        /// 首次启动放到线程池执行，避免内核启动检查阻塞主窗体消息循环。
        /// </summary>
        private void StartInitialV2ray()
        {
            tsbReload.Enabled = false;
            startupExiting = false;
            coreRestartInProgress = true;
            bool reloadConfig = Global.reloadV2ray;
            Global.reloadV2ray = false;

            if (reloadConfig)
            {
                ClearMsg();
            }

            // 后台线程只读取启动快照，避免与窗体上的服务器/路由编辑操作并发访问同一 Config。
            Config startupConfig = null;
            try
            {
                startupConfig = Utils.FromJson<Config>(Utils.ToJson(config));
            }
            catch (Exception ex)
            {
                Utils.SaveLog("Unable to create startup config snapshot: " + ex.Message, ex);
            }
            if (startupConfig == null)
            {
                Utils.SaveLog("Initial core startup cancelled: config snapshot could not be created.");
                AppendText(false, UIRes.I18N("StartupSnapshotFailed"));
                coreRestartInProgress = false;
                tsbReload.Enabled = true;
                return;
            }

            ThreadStart startupAction = delegate
            {
                bool coreReady = false;
                StatisticsHandler startupStatistics = null;
                Stopwatch sw = Stopwatch.StartNew();
                try
                {
                    if (!startupExiting)
                    {
                        v2rayHandler.LoadV2ray(startupConfig, reloadConfig);
                        coreReady = v2rayHandler.IsCoreRunning;

                        // 统计数据文件读取和轮询线程都放到核心启动之后。
                        if (coreReady && startupConfig.enableStatistics)
                        {
                            Stopwatch statsWatch = Stopwatch.StartNew();
                            try
                            {
                                startupStatistics = new StatisticsHandler(startupConfig, UpdateStatisticsHandler);
                            }
                            catch (Exception statsEx)
                            {
                                Utils.SaveLog("Statistics initialization failed: " + statsEx.Message, statsEx);
                            }
                            statsWatch.Stop();
                            Utils.SaveLog("[Startup] Statistics data initialization: "
                                + statsWatch.ElapsedMilliseconds + " ms");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Utils.SaveLog("Initial core startup failed: " + ex.Message, ex);
                    v2rayHandler_ProcessEvent(false, ex.Message);
                }
                finally
                {
                    sw.Stop();
                    Utils.SaveLog("[Startup] background core initialization: "
                        + sw.ElapsedMilliseconds + " ms; ready=" + coreReady);

                    if (startupExiting)
                    {
                        // 处理退出与 Process.Start 同时发生的竞态，确保不会遗留刚启动的核心。
                        try { v2rayHandler.V2rayStop(); } catch { }
                    }

                    try
                    {
                        if (!startupExiting && !IsDisposed && IsHandleCreated)
                        {
                            BeginInvoke((MethodInvoker)delegate
                            {
                                if (startupExiting || IsDisposed) return;

                                coreRestartInProgress = false;
                                ConfigHandler.SaveConfig(ref config, false);

                                if (coreReady && startupStatistics != null)
                                {
                                    // 核心监听就绪后才交接统计对象。
                                    statistics = startupStatistics;
                                    statistics.UpdateUI = Visible;
                                    RefreshServersView();
                                    statistics.Run();
                                }
                                else if (!coreReady)
                                {
                                    // Keep the original proxy/statistics safety behavior silent.
                                }

                                tsbReload.Enabled = true;
                                if (coreRestartPending)
                                {
                                    bool reloadAgain = pendingReloadConfig;
                                    coreRestartPending = false;
                                    pendingReloadConfig = false;
                                    Global.reloadV2ray = Global.reloadV2ray || reloadAgain;
                                    LoadV2ray();
                                    return;
                                }

                                if (coreReady)
                                {
                                    ChangePACButtonStatus(config.sysProxyType);
                                }
                            });
                        }
                    }
                    catch { }
                }
            };
            Thread startupThread = new Thread(startupAction);
            startupThread.IsBackground = true;
            startupThread.Priority = ThreadPriority.AboveNormal;
            startupThread.Name = "v2rayN-CoreStartup";
            startupThread.Start();
            Utils.SaveLog("[Startup] Core startup worker started (AboveNormal).");
        }

        /// <summary>
        /// Reload the core on a worker thread so core shutdown/startup never blocks
        /// the WinForms message loop. Configuration is snapshotted on the UI thread.
        /// </summary>
        private void LoadV2ray()
        {
            if (InvokeRequired)
            {
                try { BeginInvoke((MethodInvoker)delegate { LoadV2ray(); }); } catch { }
                return;
            }
            if (startupExiting || IsDisposed) return;

            // Initial startup and reloads share one handler/process instance. Queue a
            // single follow-up reload instead of allowing concurrent process operations.
            if (coreRestartInProgress)
            {
                coreRestartPending = true;
                pendingReloadConfig = pendingReloadConfig || Global.reloadV2ray;
                return;
            }

            coreRestartInProgress = true;
            tsbReload.Enabled = false;
            bool reloadConfig = Global.reloadV2ray;
            Global.reloadV2ray = false;
            if (reloadConfig) ClearMsg();

            Config restartConfig = null;
            try
            {
                restartConfig = Utils.FromJson<Config>(Utils.ToJson(config));
            }
            catch (Exception ex)
            {
                Utils.SaveLog("Unable to create core restart config snapshot: " + ex.Message, ex);
            }

            if (restartConfig == null)
            {
                coreRestartInProgress = false;
                tsbReload.Enabled = true;
                AppendText(false, UIRes.I18N("RestartSnapshotFailed"));
                return;
            }

            ConfigHandler.SaveConfig(ref config, false);
            StatisticsHandler previousStatistics = statistics;
            statistics = null;
            toolSslServerSpeed.Text = "0/s↑ | 0/s↓";

            Thread restartThread = new Thread(new ThreadStart(delegate
            {
                bool coreReady = false;
                StatisticsHandler restartedStatistics = null;
                Stopwatch sw = Stopwatch.StartNew();
                try
                {
                    // Persist and stop the old poller before replacing the core.
                    if (previousStatistics != null)
                        previousStatistics.Close();

                    if (!startupExiting)
                    {
                        v2rayHandler.LoadV2ray(restartConfig, reloadConfig);
                        coreReady = v2rayHandler.IsCoreRunning;

                        // A new core starts with fresh counters; use a new handler so
                        // the first sample establishes a fresh baseline.
                        if (coreReady && restartConfig.enableStatistics)
                        {
                            try
                            {
                                restartedStatistics = new StatisticsHandler(
                                    restartConfig, UpdateStatisticsHandler);
                            }
                            catch (Exception statsEx)
                            {
                                Utils.SaveLog("Statistics reinitialization failed: "
                                    + statsEx.Message, statsEx);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Utils.SaveLog("Core restart failed: " + ex.Message, ex);
                    AppendText(false, UIRes.I18N("CoreRestartFailed") + ex.Message);
                }
                finally
                {
                    sw.Stop();
                    Utils.SaveLog("[CoreRestart] background restart: "
                        + sw.ElapsedMilliseconds + " ms; ready=" + coreReady);

                    if (startupExiting)
                    {
                        // A return statement is not legal inside a finally clause.
                        // Stop the core and skip posting UI work while the app exits.
                        try { v2rayHandler.V2rayStop(); } catch { }
                    }
                    else
                    {
                        try
                        {
                            if (!IsDisposed && IsHandleCreated)
                            {
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    if (startupExiting || IsDisposed) return;

                                    coreRestartInProgress = false;
                                    ConfigHandler.SaveConfig(ref config, false);

                                    // If another operation changed settings while this restart
                                    // was running, restart once more using the newest snapshot.
                                    if (coreRestartPending)
                                    {
                                        bool reloadAgain = pendingReloadConfig;
                                        coreRestartPending = false;
                                        pendingReloadConfig = false;
                                        Global.reloadV2ray = Global.reloadV2ray || reloadAgain;
                                        LoadV2ray();
                                        return;
                                    }

                                    if (coreReady)
                                    {
                                        if (restartedStatistics != null)
                                        {
                                            statistics = restartedStatistics;
                                            statistics.UpdateUI = Visible;
                                            RefreshServersView();
                                            statistics.Run();
                                        }
                                        ChangePACButtonStatus(config.sysProxyType);
                                    }
                                    else
                                    {
                                        statistics = null;
                                        // Keep the original proxy/statistics safety behavior silent.
                                    }

                                    tsbReload.Enabled = true;
                                    Invalidate(true);
                                    Update();
                                });
                            }
                        }
                        catch { }
                    }
                }
            }));
            restartThread.IsBackground = true;
            restartThread.Priority = ThreadPriority.AboveNormal;
            restartThread.Name = "v2rayN-CoreRestart";
            restartThread.Start();
            Utils.SaveLog("[CoreRestart] Core restart worker started (AboveNormal).");
        }

        /// <summary>
        /// 关闭V2ray
        /// </summary>
        private void CloseV2ray()
        {
            ConfigHandler.SaveConfig(ref config, false);
            statistics?.SaveToFile();

            ChangePACButtonStatus(0);

            v2rayHandler.V2rayStop();
        }

        #endregion

        #region 功能按钮

        private void lvServers_Click(object sender, EventArgs e)
        {
            int index = -1;
            try
            {
                if (lvServers.SelectedIndices.Count > 0)
                {
                    index = lvServers.SelectedIndices[0];
                }
            }
            catch
            {
            }
            if (index < 0)
            {
                return;
            }
            qrCodeControl.showQRCode(index, config);
        }

        private void lvServers_DoubleClick(object sender, EventArgs e)
        {
            int index = GetLvSelectedIndex();
            if (index < 0)
            {
                return;
            }
            ShowServerForm(config.vmess[index].configType, index);
        }
        private void ShowServerForm(int configType, int index)
        {
            BaseServerForm fm;
            switch (configType)
            {
                case (int)EConfigType.Vmess:
                    fm = new AddServerForm();
                    break;
                case (int)EConfigType.Shadowsocks:
                    fm = new AddServer3Form();
                    break;
                case (int)EConfigType.Socks:
                    fm = new AddServer4Form();
                    break;
                case (int)EConfigType.VLESS:
                    fm = new AddServer5Form();
                    break;
                case (int)EConfigType.Trojan:
                    fm = new AddServer6Form();
                    break;
                default:
                    fm = new AddServer2Form();
                    break;
            }
            fm.EditIndex = index;
            if (fm.ShowDialog() == DialogResult.OK)
            {
                RefreshServers();
                LoadV2ray();
            }
        }


        private void lvServers_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control)
            {
                switch (e.KeyCode)
                {
                    case Keys.A:
                        menuSelectAll_Click(null, null);
                        break;
                    case Keys.C:
                        menuExport2ShareUrl_Click(null, null);
                        break;
                    case Keys.V:
                        menuAddServers_Click(null, null);
                        break;
                    case Keys.P:
                        menuPingServer_Click(null, null);
                        break;
                    case Keys.O:
                        menuTcpingServer_Click(null, null);
                        break;
                    case Keys.R:
                        menuRealPingServer_Click(null, null);
                        break;
                    case Keys.S:
                        menuScanScreen_Click(null, null);
                        break;
                }
            }
            else
            {
                switch (e.KeyCode)
                {
                    case Keys.Enter:
                        menuSetDefaultServer_Click(null, null);
                        break;
                    case Keys.Delete:
                        menuRemoveServer_Click(null, null);
                        break;
                    case Keys.T:
                        menuMoveTop_Click(null, null);
                        break;
                    case Keys.B:
                        menuMoveBottom_Click(null, null);
                        break;
                    case Keys.U:
                        menuMoveUp_Click(null, null);
                        break;
                    case Keys.D:
                        menuMoveDown_Click(null, null);
                        break;
                }
            }
        }

        private void menuAddVmessServer_Click(object sender, EventArgs e)
        {
            ShowServerForm((int)EConfigType.Vmess, -1);
        }

        private void menuAddVlessServer_Click(object sender, EventArgs e)
        {
            ShowServerForm((int)EConfigType.VLESS, -1);
        }

        private void menuRemoveServer_Click(object sender, EventArgs e)
        {

            int index = GetLvSelectedIndex();
            if (index < 0)
            {
                return;
            }
            if (UI.ShowYesNo(UIRes.I18N("RemoveServer")) == DialogResult.No)
            {
                return;
            }
            for (int k = lvSelecteds.Count - 1; k >= 0; k--)
            {
                ConfigHandler.RemoveServer(ref config, lvSelecteds[k]);
            }
            RefreshServers();
            LoadV2ray();

        }

        private void menuRemoveDuplicateServer_Click(object sender, EventArgs e)
        {
            Utils.DedupServerList(config.vmess, out List<VmessItem> servers, config.keepOlderDedupl);
            int oldCount = config.vmess.Count;
            int newCount = servers.Count;
            if (servers != null)
            {
                config.vmess = servers;
            }
            RefreshServers();
            LoadV2ray();
            UI.Show(string.Format(UIRes.I18N("RemoveDuplicateServerResult"), oldCount, newCount));
        }

        private void menuCopyServer_Click(object sender, EventArgs e)
        {
            int index = GetLvSelectedIndex();
            if (index < 0)
            {
                return;
            }
            if (ConfigHandler.CopyServer(ref config, index) == 0)
            {
                RefreshServers();
            }
        }

        private void menuSetDefaultServer_Click(object sender, EventArgs e)
        {
            int index = GetLvSelectedIndex();
            if (index < 0)
            {
                return;
            }
            SetDefaultServer(index);
        }


        private void menuPingServer_Click(object sender, EventArgs e)
        {
            Speedtest("ping");
        }
        private void menuTcpingServer_Click(object sender, EventArgs e)
        {
            Speedtest("tcping");
        }

        private void menuRealPingServer_Click(object sender, EventArgs e)
        {
            //if (!config.sysAgentEnabled)
            //{
            //    UI.Show(UIRes.I18N("NeedHttpGlobalProxy"));
            //    return;
            //}

            //UI.Show(UIRes.I18N("SpeedServerTips"));

            Speedtest("realping");
        }
        private void Speedtest(string actionType)
        {
            if (GetLvSelectedIndex() < 0) return;
            ClearTestResult();
            SpeedtestHandler statistics = new SpeedtestHandler(ref config, ref v2rayHandler, lvSelecteds, actionType,
                UpdateSpeedtestHandler);
        }

        private void tsbTestMe_Click(object sender, EventArgs e)
        {
            SpeedtestHandler statistics = new SpeedtestHandler(ref config);
            string result = statistics.RunAvailabilityCheck() + "ms";
            AppendText(false, string.Format(UIRes.I18N("TestMeOutput"), result));
        }

        private void menuClearStatistic_Click(object sender, EventArgs e)
        {
            if (statistics != null)
            {
                statistics.ClearAllServerStatistics();
            }
        }

        private void menuExport2ClientConfig_Click(object sender, EventArgs e)
        {
            int index = GetLvSelectedIndex();
            MainFormHandler.Instance.Export2ClientConfig(index, config);
        }

        private void menuExport2ServerConfig_Click(object sender, EventArgs e)
        {
            int index = GetLvSelectedIndex();
            MainFormHandler.Instance.Export2ServerConfig(index, config);
        }

        private void menuExport2ShareUrl_Click(object sender, EventArgs e)
        {
            GetLvSelectedIndex();

            StringBuilder sb = new StringBuilder();
            foreach (int v in lvSelecteds)
            {
                string url = ShareHandler.GetShareUrl(config, v);
                if (Utils.IsNullOrEmpty(url))
                {
                    continue;
                }
                sb.Append(url);
                sb.AppendLine();
            }
            if (sb.Length > 0)
            {
                Utils.SetClipboardData(sb.ToString());
                AppendText(false, UIRes.I18N("BatchExportURLSuccessfully"));
                //UI.Show(UIRes.I18N("BatchExportURLSuccessfully"));
            }
        }

        private void menuExport2SubContent_Click(object sender, EventArgs e)
        {
            GetLvSelectedIndex();

            StringBuilder sb = new StringBuilder();
            foreach (int v in lvSelecteds)
            {
                string url = ShareHandler.GetShareUrl(config, v);
                if (Utils.IsNullOrEmpty(url))
                {
                    continue;
                }
                sb.Append(url);
                sb.AppendLine();
            }
            if (sb.Length > 0)
            {
                Utils.SetClipboardData(Utils.Base64Encode(sb.ToString()));
                UI.Show(UIRes.I18N("BatchExportSubscriptionSuccessfully"));
            }
        }

        private void tsbOptionSetting_Click(object sender, EventArgs e)
        {
            Config configBefore = null;
            try
            {
                configBefore = Utils.FromJson<Config>(Utils.ToJson(config));
            }
            catch (Exception ex)
            {
                Utils.SaveLog("Unable to snapshot options before editing: " + ex.Message, ex);
            }

            OptionSettingForm fm = new OptionSettingForm();
            if (fm.ShowDialog() == DialogResult.OK)
            {
                RefreshServers();

                // Avoid restarting when the options dialog was saved without any changes.
                // Core logging changes are compared too because the core reads them at startup.
                if (configBefore != null && CoreSettingsUnchanged(configBefore, config))
                {
                    Global.reloadV2ray = false;
                    Utils.SaveLog("[Settings] No configuration changes; core restart skipped.");
                    return;
                }

                LoadV2ray();
            }
        }

        private bool CoreSettingsUnchanged(Config before, Config after)
        {
            if (before == null || after == null)
                return false;

            try
            {
                Config normalizedBefore = Utils.FromJson<Config>(Utils.ToJson(before));
                Config normalizedAfter = Utils.FromJson<Config>(Utils.ToJson(after));
                if (normalizedBefore == null || normalizedAfter == null)
                    return false;

                // Core log level and log output are part of the generated core
                // configuration, so a real logging change must still restart the core.
                return String.Equals(Utils.ToJson(normalizedBefore),
                    Utils.ToJson(normalizedAfter), StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                Utils.SaveLog("Unable to compare core configuration after options change: " + ex.Message, ex);
                return false;
            }
        }

        private void tsbRoutingSetting_Click(object sender, EventArgs e)
        {
            var fm = new RoutingSettingForm();
            if (fm.ShowDialog() == DialogResult.OK)
            {
                RefreshRoutingsMenu();
                RefreshServers();
                LoadV2ray();
            }
        }

        private void tsbReload_Click(object sender, EventArgs e)
        {
            Global.reloadV2ray = true;
            LoadV2ray();
        }

        private void tsbClose_Click(object sender, EventArgs e)
        {
            HideForm();
            //this.WindowState = FormWindowState.Minimized;
        }

        /// <summary>
        /// 设置活动服务器
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        private int SetDefaultServer(int index)
        {
            if (index < 0)
            {
                UI.Show(UIRes.I18N("PleaseSelectServer"));
                return -1;
            }
            if (ConfigHandler.SetDefaultServer(ref config, index) == 0)
            {
                RefreshServers();
                LoadV2ray();
            }
            return 0;
        }

        /// <summary>
        /// 取得ListView选中的行
        /// </summary>
        /// <returns></returns>
        private int GetLvSelectedIndex()
        {
            int index = -1;
            lvSelecteds.Clear();
            try
            {
                if (lvServers.SelectedIndices.Count <= 0)
                {
                    UI.Show(UIRes.I18N("PleaseSelectServer"));
                    return index;
                }

                index = lvServers.SelectedIndices[0];
                foreach (int i in lvServers.SelectedIndices)
                {
                    lvSelecteds.Add(i);
                }
                return index;
            }
            catch
            {
                return index;
            }
        }

        private void menuAddCustomServer_Click(object sender, EventArgs e)
        {
            UI.Show(UIRes.I18N("CustomServerTips"));

            OpenFileDialog fileDialog = new OpenFileDialog
            {
                Multiselect = false,
                Filter = "Config|*.json|All|*.*"
            };
            if (fileDialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }
            string fileName = fileDialog.FileName;
            if (Utils.IsNullOrEmpty(fileName))
            {
                return;
            }

            if (ConfigHandler.AddCustomServer(ref config, fileName) == 0)
            {
                RefreshServers();
                //LoadV2ray();
                UI.Show(UIRes.I18N("SuccessfullyImportedCustomServer"));
            }
            else
            {
                UI.ShowWarning(UIRes.I18N("FailedImportedCustomServer"));
            }
        }

        private void menuAddShadowsocksServer_Click(object sender, EventArgs e)
        {
            ShowServerForm((int)EConfigType.Shadowsocks, -1);
            ShowForm();
        }

        private void menuAddSocksServer_Click(object sender, EventArgs e)
        {
            ShowServerForm((int)EConfigType.Socks, -1);
            ShowForm();
        }

        private void menuAddTrojanServer_Click(object sender, EventArgs e)
        {
            ShowServerForm((int)EConfigType.Trojan, -1);
            ShowForm();
        }

        private void menuAddServers_Click(object sender, EventArgs e)
        {
            string clipboardData = Utils.GetClipboardData();
            int ret = MainFormHandler.Instance.AddBatchServers(config, clipboardData);
            if (ret > 0)
            {
                RefreshServers();
                UI.Show(string.Format(UIRes.I18N("SuccessfullyImportedServerViaClipboard"), ret));
            }
        }

        private void menuScanScreen_Click(object sender, EventArgs e)
        {
            HideForm();
            bgwScan.RunWorkerAsync();
        }

        private void menuUpdateSubscriptions_Click(object sender, EventArgs e)
        {
            UpdateSubscriptionProcess();
        }

        private void tsbBackupGuiNConfig_Click(object sender, EventArgs e)
        {
            MainFormHandler.Instance.BackupGuiNConfig(config);
        }
        #endregion


        #region 提示信息

        /// <summary>
        /// 消息委托
        /// </summary>
        /// <param name="notify"></param>
        /// <param name="msg"></param>
        void v2rayHandler_ProcessEvent(bool notify, string msg)
        {
            AppendText(notify, msg);
        }

        delegate void AppendTextDelegate(string text);
        void AppendText(bool notify, string msg)
        {
            try
            {
                if (txtMsgBox.InvokeRequired)
                {
                    // 不让核心输出线程同步等待 UI，避免启动/退出期间互相阻塞。
                    BeginInvoke((MethodInvoker)delegate { AppendText(notify, msg); });
                    return;
                }

                AppendText(msg);
                if (notify)
                {
                    notifyMsg(msg);
                }
            }
            catch
            {
            }
        }

        void AppendText(string text)
        {
            if (this.txtMsgBox.InvokeRequired)
            {
                BeginInvoke(new AppendTextDelegate(AppendText), new object[] { text });
            }
            else
            {
                if (!Utils.IsNullOrEmpty(MsgFilter))
                {
                    if (!Regex.IsMatch(text, MsgFilter))
                    {
                        return;
                    }
                }
                ShowMsg(text);
            }
        }

        /// <summary>
        /// 提示信息
        /// </summary>
        /// <param name="msg"></param>
        private void ShowMsg(string msg)
        {
            if (txtMsgBox.Lines.Length > 999)
            {
                ClearMsg();
            }
            this.txtMsgBox.AppendText(msg);
            if (!msg.EndsWith(Environment.NewLine))
            {
                this.txtMsgBox.AppendText(Environment.NewLine);
            }
        }

        /// <summary>
        /// 清除信息
        /// </summary>
        private void ClearMsg()
        {
            this.txtMsgBox.Clear();
        }

        /// <summary>
        /// 托盘信息
        /// </summary>
        /// <param name="msg"></param>
        private void notifyMsg(string msg)
        {
            notifyMain.Text = msg;
        }

        #endregion


        #region 托盘事件

        private void notifyMain_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || startupExiting)
                return;

            // A tray click received during the first show/hide transition is queued.
            // This prevents an early click from being overwritten by startup hiding.
            if (!startupUiReady)
            {
                trayShowPending = true;
                return;
            }

            // Defer the actual show until the NotifyIcon mouse message has completed.
            if (!IsDisposed && !Disposing && IsHandleCreated)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (!startupExiting && !IsDisposed && !Disposing)
                            ShowForm();
                    });
                }
                catch (InvalidOperationException) { }
            }
        }

        private void menuExit_Click(object sender, EventArgs e)
        {
            this.Visible = false;
            this.Close();

            Application.Exit();
        }


        private void ShowForm()
        {
            if (IsDisposed || Disposing || !IsHandleCreated || !startupUiReady)
                return;

            // Restore the taskbar entry, show the form, then restore its state.
            // On Windows XP, restoring a hidden form before Show() can leave it
            // logically minimized; the taskbar button then appears but the form does not.
            ShowInTaskbar = true;
            if (!Visible)
                Show();

            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;

            // Finish layout and paint the form and its child controls before
            // activating it, reducing the blank-client-area flash on Windows XP.
            PerformLayout();
            txtMsgBox.ScrollToCaret();
            Invalidate(true);
            Update();

            // Activate only after the window contents have been synchronously redrawn.
            Activate();
            BringToFront();
        }

        private void HideForm()
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            // Hiding the form is enough to remove its taskbar button.
            // Do not change ShowInTaskbar here: changing it after handle creation
            // recreates the native window handle and can cause visible flicker on Windows XP.
            if (!this.notifyMain.Visible)
            {
                this.notifyMain.Visible = true;
            }
            if (this.Visible)
            {
                this.Hide();
            }
        }

        #endregion

        #region 后台测速

        private void SetTestResult(int k, string txt)
        {
            if (k < lvServers.Items.Count)
            {
                config.vmess[k].testResult = txt;
                lvServers.Items[k].SubItems["testResult"].Text = txt;
            }
        }
        private void ClearTestResult()
        {
            foreach (int s in lvSelecteds)
            {
                SetTestResult(s, "");
            }
        }
        private void UpdateSpeedtestHandler(int index, string msg)
        {
            lvServers.Invoke((MethodInvoker)delegate
            {
                SetTestResult(index, msg);
            });
        }

        private void UpdateStatisticsHandler(ulong up, ulong down, List<ServerStatItem> statisticsData)
        {
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        UpdateStatisticsHandler(up, down, statisticsData);
                    });
                }
                catch { }
                return;
            }

            // Ignore a queued callback from a worker that has already been replaced.
            if (statistics == null
                || !Object.ReferenceEquals(statistics.Statistic, statisticsData))
                return;

            try
            {
                int refreshRate = config.statisticsFreshRate;
                if (refreshRate < 1000)
                    refreshRate = 1000;
                up = (ulong)(up / (refreshRate / 1000f));
                down = (ulong)(down / (refreshRate / 1000f));
                toolSslServerSpeed.Text = string.Format("{0}/s↑ | {1}/s↓",
                    Utils.HumanFy(up), Utils.HumanFy(down));

                for (int i = 0; i < config.vmess.Count && i < lvServers.Items.Count; i++)
                {
                    int index = statisticsData.FindIndex(item_ =>
                        item_.itemId == config.vmess[i].getItemId());
                    if (index != -1)
                    {
                        lvServers.BeginUpdate();
                        lvServers.Items[i].SubItems["todayDown"].Text =
                            Utils.HumanFy(statisticsData[index].todayDown);
                        lvServers.Items[i].SubItems["todayUp"].Text =
                            Utils.HumanFy(statisticsData[index].todayUp);
                        lvServers.Items[i].SubItems["totalDown"].Text =
                            Utils.HumanFy(statisticsData[index].totalDown);
                        lvServers.Items[i].SubItems["totalUp"].Text =
                            Utils.HumanFy(statisticsData[index].totalUp);
                        lvServers.EndUpdate();
                    }
                }
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }
        }

        #endregion

        #region 移动服务器

        private void menuMoveTop_Click(object sender, EventArgs e)
        {
            MoveServer(EMove.Top);
        }

        private void menuMoveUp_Click(object sender, EventArgs e)
        {
            MoveServer(EMove.Up);
        }

        private void menuMoveDown_Click(object sender, EventArgs e)
        {
            MoveServer(EMove.Down);
        }

        private void menuMoveBottom_Click(object sender, EventArgs e)
        {
            MoveServer(EMove.Bottom);
        }

        private void MoveServer(EMove eMove)
        {
            int index = GetLvSelectedIndex();
            if (index < 0)
            {
                UI.Show(UIRes.I18N("PleaseSelectServer"));
                return;
            }
            if (ConfigHandler.MoveServer(ref config, index, eMove) == 0)
            {
                //TODO: reload is not good.
                RefreshServers();
                //LoadV2ray();
            }
        }
        private void menuSelectAll_Click(object sender, EventArgs e)
        {
            foreach (ListViewItem item in lvServers.Items)
            {
                item.Selected = true;
            }
        }

        #endregion

        #region 系统代理相关
        private void menuKeepClear_Click(object sender, EventArgs e)
        {
            SetListenerType(ESysProxyType.ForcedClear);
        }
        private void menuGlobal_Click(object sender, EventArgs e)
        {
            SetListenerType(ESysProxyType.ForcedChange);
        }

        private void menuKeepNothing_Click(object sender, EventArgs e)
        {
            SetListenerType(ESysProxyType.Unchanged);
        }
        private void SetListenerType(ESysProxyType type)
        {
            config.sysProxyType = type;
            ChangePACButtonStatus(type);
        }

        private void ChangePACButtonStatus(ESysProxyType type)
        {
            HttpProxyHandle.UpdateSysProxy(config, false);
            //if (type != ListenerType.noHttpProxy)
            //{
            //    HttpProxyHandle.RestartHttpAgent(config, false);
            //}
            //else
            //{
            //    HttpProxyHandle.CloseHttpAgent(config);
            //}

            for (int k = 0; k < menuSysAgentMode.DropDownItems.Count; k++)
            {
                ToolStripMenuItem item = ((ToolStripMenuItem)menuSysAgentMode.DropDownItems[k]);
                item.Checked = ((int)type == k);
            }

            ConfigHandler.SaveConfig(ref config, false);
            DisplayToolStatus();
        }

        #endregion


        #region CheckUpdate

        private void tsbCheckUpdateN_Click(object sender, EventArgs e)
        {
            void _updateUI(bool success, string msg)
            {
                AppendText(false, msg);
                if (success)
                {
                    menuExit_Click(null, null);
                }
            };
            (new UpdateHandle()).CheckUpdateGuiN(config, _updateUI);
        }

        private void tsbCheckUpdateCore_Click(object sender, EventArgs e)
        {
            CheckUpdateCore("v2fly");
        }

        private void tsbCheckUpdateXrayCore_Click(object sender, EventArgs e)
        {
            CheckUpdateCore("xray");
        }

        private void CheckUpdateCore(string type)
        {
            void _updateUI(bool success, string msg)
            {
                AppendText(false, msg);
                if (success)
                {
                    CloseV2ray();

                    string fileName = Global.DownloadFileName;
                    fileName = Utils.GetPath(fileName);
                    FileManager.ZipExtractToFile(fileName, config.ignoreGeoUpdateCore ? "geo" : "");

                    AppendText(false, UIRes.I18N("MsgUpdateV2rayCoreSuccessfullyMore"));

                    Global.reloadV2ray = true;
                    LoadV2ray();

                    AppendText(false, UIRes.I18N("MsgUpdateV2rayCoreSuccessfully"));
                }
            };
            (new UpdateHandle()).CheckUpdateCore(type, config, _updateUI);
        }

        private void tsbCheckUpdateGeoSite_Click(object sender, EventArgs e)
        {
            (new UpdateHandle()).UpdateGeoFile("geosite", config, (bool success, string msg) =>
            {
                AppendText(false, msg);
                if (success)
                {
                    Global.reloadV2ray = true;
                    LoadV2ray();
                }
            });
        }

        private void tsbCheckUpdateGeoIP_Click(object sender, EventArgs e)
        {
            (new UpdateHandle()).UpdateGeoFile("geoip", config, (bool success, string msg) =>
            {
                AppendText(false, msg);
                if (success)
                {
                    Global.reloadV2ray = true;
                    LoadV2ray();
                }
            });
        }
        #endregion

        #region Help


        private void tsbAbout_Click(object sender, EventArgs e)
        {
            Process.Start(Global.AboutUrl);
        }

        private void tsbV2rayWebsite_Click(object sender, EventArgs e)
        {
            Process.Start(Global.v2rayWebsiteUrl);
        }

        private void tsbPromotion_Click(object sender, EventArgs e)
        {
            Process.Start($"{Utils.Base64Decode(Global.PromotionUrl)}?t={DateTime.Now.Ticks}");
        }
        #endregion

        #region ScanScreen


        private void bgwScan_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
        {
            string ret = Utils.ScanScreen();
            bgwScan.ReportProgress(0, ret);
        }

        private void bgwScan_ProgressChanged(object sender, System.ComponentModel.ProgressChangedEventArgs e)
        {
            ShowForm();

            string result = Convert.ToString(e.UserState);
            if (Utils.IsNullOrEmpty(result))
            {
                UI.ShowWarning(UIRes.I18N("NoValidQRcodeFound"));
            }
            else
            {
                int ret = MainFormHandler.Instance.AddBatchServers(config, result);
                if (ret > 0)
                {
                    RefreshServers();
                    UI.Show(UIRes.I18N("SuccessfullyImportedServerViaScan"));
                }
            }
        }

        #endregion

        #region 订阅
        private void tsbSubSetting_Click(object sender, EventArgs e)
        {
            SubSettingForm fm = new SubSettingForm();
            if (fm.ShowDialog() == DialogResult.OK)
            {
                RefreshServers();
            }
        }

        private void tsbSubUpdate_Click(object sender, EventArgs e)
        {
            UpdateSubscriptionProcess();
        }

        /// <summary>
        /// the subscription update process
        /// </summary>
        private void UpdateSubscriptionProcess()
        {
            void _updateUI(bool success, string msg)
            {
                AppendText(false, msg);
                if (success)
                {
                    RefreshServers();
                }
            };

            (new UpdateHandle()).UpdateSubscriptionProcess(config, _updateUI);
        }

        private void tsbQRCodeSwitch_CheckedChanged(object sender, EventArgs e)
        {
            bool bShow = tsbQRCodeSwitch.Checked;
            scMain.Panel2Collapsed = !bShow;
        }
        #endregion

        #region Language

        private void tsbLanguageDef_Click(object sender, EventArgs e)
        {
            SetCurrentLanguage("en");
        }

        private void tsbLanguageZhHans_Click(object sender, EventArgs e)
        {
            SetCurrentLanguage("zh-Hans");
        }
        private void SetCurrentLanguage(string value)
        {
            Utils.RegWriteValue(Global.MyRegPath, Global.MyRegKeyLanguage, value);
            Program.RestartForLanguageChange = true;
            Application.Exit();
        }





        #endregion


        #region RoutingsMenu

        /// <summary>
        ///  
        /// </summary>
        private void RefreshRoutingsMenu()
        {
            menuRoutings.Visible = config.enableRoutingAdvanced;
            if (!config.enableRoutingAdvanced)
            {
                return;
            }

            menuRoutings.DropDownItems.Clear();

            List<ToolStripMenuItem> lst = new List<ToolStripMenuItem>();
            for (int k = 0; k < config.routings.Count; k++)
            {
                var item = config.routings[k];
                if (item.locked == true)
                {
                    continue;
                }
                string name = item.remarks;

                ToolStripMenuItem ts = new ToolStripMenuItem(name)
                {
                    Tag = k
                };
                if (config.routingIndex.Equals(k))
                {
                    ts.Checked = true;
                }
                ts.Click += new EventHandler(ts_Routing_Click);
                lst.Add(ts);
            }
            menuRoutings.DropDownItems.AddRange(lst.ToArray());
        }

        private void ts_Routing_Click(object sender, EventArgs e)
        {
            try
            {
                ToolStripItem ts = (ToolStripItem)sender;
                int index = Utils.ToInt(ts.Tag);

                if (ConfigHandler.SetDefaultRouting(ref config, index) == 0)
                {
                    RefreshRoutingsMenu();
                    LoadV2ray();
                }
            }
            catch
            {
            }
        }
        #endregion

        #region MsgBoxMenu
        private void menuMsgBoxSelectAll_Click(object sender, EventArgs e)
        {
            this.txtMsgBox.Focus();
            this.txtMsgBox.SelectAll();
        }

        private void menuMsgBoxCopy_Click(object sender, EventArgs e)
        {
            var data = this.txtMsgBox.SelectedText.TrimEx();
            Utils.SetClipboardData(data);
        }

        private void menuMsgBoxCopyAll_Click(object sender, EventArgs e)
        {
            var data = this.txtMsgBox.Text;
            Utils.SetClipboardData(data);
        }
        private void menuMsgBoxAddRoutingRule_Click(object sender, EventArgs e)
        {
            menuMsgBoxCopy_Click(null, null);
            tsbRoutingSetting_Click(null, null);
        }

        private void txtMsgBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control)
            {
                switch (e.KeyCode)
                {
                    case Keys.A:
                        menuMsgBoxSelectAll_Click(null, null);
                        break;
                    case Keys.C:
                        menuMsgBoxCopy_Click(null, null);
                        break;
                    case Keys.V:
                        menuMsgBoxAddRoutingRule_Click(null, null);
                        break;

                }
            }

        }
        private void menuMsgBoxFilter_Click(object sender, EventArgs e)
        {
            var fm = new MsgFilterSetForm();
            fm.MsgFilter = MsgFilter;
            if (fm.ShowDialog() == DialogResult.OK)
            {
                MsgFilter = fm.MsgFilter;
                gbMsgTitle.Text = string.Format(UIRes.I18N("MsgInformationTitle"), MsgFilter);
            }
        }
        #endregion

    }
}
