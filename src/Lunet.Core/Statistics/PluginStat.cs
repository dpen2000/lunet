using System;
using Lunet.Core;

namespace Lunet.Statistics
{
    public class PluginStat
    {
        public PluginStat(ISitePluginCore plugin)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            Plugin = plugin;
        }

        public ISitePluginCore Plugin { get; }

        public int Order { get; set; }

        public int PageCount { get; set; }

        public TimeSpan InitializeTime { get; set; }

        public TimeSpan BeginProcessTime { get; set; }

        public TimeSpan ContentProcessTime { get; set; }

        public TimeSpan EndProcessTime { get; set; }
    }
}