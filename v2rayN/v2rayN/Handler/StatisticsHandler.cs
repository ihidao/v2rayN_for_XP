using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using v2rayN.Mode;

namespace v2rayN.Handler
{
    // Uses the core built-in statistics CLI instead of
    // Grpc.Core. V2Ray and Xray have different CLI syntax. This avoids the native gRPC runtime that is unsuitable for
    // the Windows XP / .NET Framework 4.0 target.
    class StatisticsHandler
    {
        private readonly Config config_;
        private readonly Action<ulong, ulong, List<ServerStatItem>> updateFunc_;
        private readonly ServerStatistics serverStatistics_;
        private Thread worker_;
        private volatile bool stopping_;
        private string lastError_ = string.Empty;
        private ulong lastUp_;
        private ulong lastDown_;
        private string lastItemId_ = string.Empty;
        private bool hasLastSample_;

        public bool Enable { get; private set; }
        public bool UpdateUI { get; set; }

        public List<ServerStatItem> Statistic
        {
            get { return serverStatistics_.server; }
        }

        public StatisticsHandler(Config config, Action<ulong, ulong, List<ServerStatItem>> update)
        {
            config_ = config;
            updateFunc_ = update;
            Enable = config != null && config.enableStatistics;
            UpdateUI = false;
            serverStatistics_ = LoadStatisticsFromFile();
        }

        public void Run()
        {
            if (!Enable || worker_ != null)
                return;

            stopping_ = false;
            worker_ = new Thread(StatisticsLoop);
            worker_.IsBackground = true;
            worker_.Name = "v2rayN traffic statistics";
            worker_.Start();
        }

        private void StatisticsLoop()
        {
            while (!stopping_)
            {
                try
                {
                    PollStatistics();
                }
                catch (Exception ex)
                {
                    if (lastError_ != ex.Message)
                    {
                        lastError_ = ex.Message;
                        Utils.SaveLog("Statistics polling failed: " + ex.Message, ex);
                    }
                }

                int delay = config_.statisticsFreshRate;
                if (delay < 1000 || delay > 10000)
                    delay = 1000;
                int slept = 0;
                while (!stopping_ && slept < delay)
                {
                    int part = Math.Min(200, delay - slept);
                    Thread.Sleep(part);
                    slept += part;
                }
            }
        }

        private void PollStatistics()
        {
            if (config_.index < 0 || config_.index >= config_.vmess.Count)
                return;

            bool isV2RayCore = config_.coreType == ECoreType.v2ray_core_5_or_above
                || config_.coreType == ECoreType.v2ray_core_below_5;
            bool usesModernArguments = config_.coreType == ECoreType.v2ray_core_5_or_above
                || config_.coreType == ECoreType.xray_core_1_or_above;
            string coreName = isV2RayCore ? "v2ray.exe" : "xray.exe";
            string corePath = Utils.GetPath(coreName);
            if (!System.IO.File.Exists(corePath) && isV2RayCore)
            {
                string alternate = Utils.GetPath("wv2ray.exe");
                if (System.IO.File.Exists(alternate))
                    corePath = alternate;
            }
            if (!System.IO.File.Exists(corePath))
                throw new Exception(UIRes.I18N("CoreNotFound") + coreName);

            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = corePath;
            string server = "--server=127.0.0.1:" + Global.statePort.ToString(CultureInfo.InvariantCulture);
            // Modern selected cores use "stats -json"; legacy cores use "statsquery".
            info.Arguments = usesModernArguments
                ? "api stats -json " + server
                : "api statsquery " + server;
            info.WorkingDirectory = Utils.StartupPath();
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;

            StringBuilder output = new StringBuilder();
            StringBuilder error = new StringBuilder();
            using (Process process = new Process())
            {
                process.StartInfo = info;
                process.OutputDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                        lock (output) output.AppendLine(args.Data);
                };
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                        lock (error) error.AppendLine(args.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException(UIRes.I18N("StatisticsApiTimeout"));
                }
                process.WaitForExit();
            }

            string json;
            string errorText;
            lock (output) json = output.ToString().Trim();
            lock (error) errorText = error.ToString().Trim();
            if (json.Length == 0)
                throw new Exception(errorText.Length == 0 ? UIRes.I18N("StatisticsApiNoData") : errorText);

            JObject result = JObject.Parse(json);
            JArray values = result["stat"] as JArray;
            if (values == null)
                throw new Exception(UIRes.I18N("StatisticsApiInvalidFormat"));

            ulong currentUp = 0;
            ulong currentDown = 0;
            foreach (JToken value in values)
            {
                string name = value["name"] == null ? string.Empty : value["name"].ToString();
                ulong count;
                if (!UInt64.TryParse(value["value"] == null ? "0" : value["value"].ToString(),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
                    continue;

                if (name == "outbound>>>proxy>>>traffic>>>uplink")
                    currentUp = count;
                else if (name == "outbound>>>proxy>>>traffic>>>downlink")
                    currentDown = count;
            }

            string itemId = config_.vmess[config_.index].getItemId();
            ulong up = 0;
            ulong down = 0;
            if (hasLastSample_ && itemId == lastItemId_)
            {
                up = currentUp >= lastUp_ ? currentUp - lastUp_ : currentUp;
                down = currentDown >= lastDown_ ? currentDown - lastDown_ : currentDown;
            }

            lastUp_ = currentUp;
            lastDown_ = currentDown;
            lastItemId_ = itemId;
            hasLastSample_ = true;
            lastError_ = string.Empty;

            ServerStatItem item = serverStatistics_.server.Find(x => x.itemId == itemId);
            if (item == null)
            {
                item = new ServerStatItem();
                item.itemId = itemId;
                item.dateNow = DateTime.Now.Date.Ticks;
                serverStatistics_.server.Add(item);
            }

            long today = DateTime.Now.Date.Ticks;
            if (item.dateNow != today)
            {
                item.todayUp = 0;
                item.todayDown = 0;
                item.dateNow = today;
            }

            item.totalUp += up;
            item.totalDown += down;
            item.todayUp += up;
            item.todayDown += down;

            if (UpdateUI && updateFunc_ != null)
                updateFunc_(up, down, serverStatistics_.server);
        }

        public void Close()
        {
            stopping_ = true;
            if (worker_ != null && worker_.IsAlive)
            {
                // PollStatistics waits at most five seconds for the core CLI.
                // Wait for that bounded query to finish before a replacement worker starts.
                try { worker_.Join(6000); } catch { }
                if (worker_.IsAlive)
                    Utils.SaveLog("Statistics worker did not stop within the expected timeout.");
            }
            SaveToFile();
        }

        private ServerStatistics LoadStatisticsFromFile()
        {
            ServerStatistics data = null;
            try
            {
                string result = Utils.LoadResource(Utils.GetPath(Global.StatisticLogOverall));
                if (!Utils.IsNullOrEmpty(result))
                    data = Utils.FromJson<ServerStatistics>(result);
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }

            if (data == null)
                data = new ServerStatistics();
            if (data.server == null)
                data.server = new List<ServerStatItem>();

            long ticks = DateTime.Now.Date.Ticks;
            foreach (ServerStatItem item in data.server)
            {
                if (item.dateNow != ticks)
                {
                    item.todayUp = 0;
                    item.todayDown = 0;
                    item.dateNow = ticks;
                }
            }
            return data;
        }

        public void SaveToFile()
        {
            try
            {
                Utils.ToJsonFile(serverStatistics_, Utils.GetPath(Global.StatisticLogOverall));
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }
        }

        public void ClearAllServerStatistics()
        {
            foreach (ServerStatItem item in serverStatistics_.server)
            {
                item.todayUp = 0;
                item.todayDown = 0;
                item.totalUp = 0;
                item.totalDown = 0;
            }
            SaveToFile();
        }
    }
}