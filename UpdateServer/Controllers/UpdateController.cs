using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace UpdateServer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class UpdateController : ControllerBase
    {

        private readonly ILogger<UpdateController> _logger;
        private readonly UpdateManager manager;

        public UpdateController(ILogger<UpdateController> logger, UpdateManager manager)
        {
            _logger = logger;
            this.manager = manager;
        }

        [HttpGet]
        public IActionResult Get()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
            string version = fvi.FileVersion;

            return Ok($"server version: {version}");
        }

        [HttpPost("{app}/check")]
        public IActionResult CheckUpdate(string app, [FromBody] CheckRequest check = null)
        {
            string appFolder = manager.GetFolder(app);
            if (appFolder == null)
            {
                return NotFound();
            }

            bool upToDate = manager.CheckVersion(appFolder, check);

            return Ok(upToDate);
        }
    }
}
