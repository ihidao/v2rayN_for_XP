using System;
using System.Collections.Generic;
using v2rayN.Mode;

namespace v2rayN.Handler
{
    // XP/.NET 4.0 build: gRPC-based traffic statistics are intentionally disabled.
    // Keep this compatibility shell so the rest of the 5.39 UI code remains intact.
    class StatisticsHandler
    {
        private readonly ServerStatistics serverStatistics_ = new ServerStatistics
        {
            server = new List<ServerStatItem>()
        };

        public bool Enable { get; set; }
        public bool UpdateUI { get; set; }
        public List<ServerStatItem> Statistic { get { return serverStatistics_.server; } }

        public StatisticsHandler(Config config, Action<ulong, ulong, List<ServerStatItem>> update)
        {
            Enable = false;
            UpdateUI = false;
        }

        public void Close() { }
        public void SaveToFile() { }
        public void ClearAllServerStatistics() { }
    }
}
