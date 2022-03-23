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
    }
}
