using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
            string version = assembly.GetName().Version.ToString();

            return Ok($"server version: {version}");
        }

        [HttpPost("{app}/check")]
        public IActionResult CheckUpdate(string app, [FromBody] CheckRequest check = null)
        {
            string appFolder = manager.GetFolder(app);
            if (appFolder == null)
            {
                _logger.LogInformation($"Trying to check update for non-existing app: {app}");
                return NotFound();
            }

            bool upToDate = manager.CheckVersion(appFolder, check);

            return Ok(upToDate);
        }

        [HttpGet("{app}/download")]
        public IActionResult DownloadFile(string app)
        {
            string localFilePath = manager.GetUpdateFileForApp(app);
            if (Path.GetExtension(localFilePath) != ".gz")
            {
                _logger.LogError($"File is not a tarball file. [Appname: {app}]");
                return Problem(title: "File Corrupted", detail: "File is corrupted. Please contact administrator.");
            }

            var lastWriteTime = new FileInfo(localFilePath).LastWriteTimeUtc;
            var fileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read);

            var filename = Path.GetFileName(localFilePath);
            bool useOriginalFilename = filename.Split("-").Length == 2;
            var result = File(fileStream, "application/gzip", useOriginalFilename ? filename : "update.tar.gz");
            result.LastModified = new DateTimeOffset(lastWriteTime);

            return result;
        }

        public static string MakeEtag(long lastMod, long size)
        {
            string etag = '"' + lastMod.ToString("x") + '-' + size.ToString("x") + '"';
            return etag;
        }
        
    }
}
