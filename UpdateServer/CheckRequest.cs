using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace UpdateServer
{
    public class CheckRequest
    {
        public string Version { get; set; }
        public DateTimeOffset? Modified { get; set; }
        public string Checksum { get; set; }

        /// <summary>
        /// When true (default), the server may include an updater self-update in check-upgrades
        /// and treat the client as not up-to-date when only the updater package is newer.
        /// </summary>
        public bool? IncludeSelfUpdate { get; set; }
    }
}
