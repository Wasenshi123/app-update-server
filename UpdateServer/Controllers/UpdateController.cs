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
        public IActionResult CheckUpdate(string app, [FromBody] CheckRequest check = null, [FromQuery] bool includePrerelease = true)
        {
            string appFolder = manager.GetFolder(app);
            if (appFolder == null)
            {
                _logger.LogInformation("Trying to check update for non-existing app: {app}", app);
                return NotFound();
            }

            _logger.LogInformation("{app} Checking... user version: {version}", app, check.Version ?? "Unknown");

            bool upToDate = manager.CheckVersion(appFolder, check, includePrerelease);

            _logger.LogInformation("{app} is update to date: {upToDate}", app, upToDate);

            return Ok(upToDate);
        }

        [HttpGet("{app}/download")]
        public IActionResult DownloadFile(string app, [FromQuery] bool includePrerelease = true)
        {
            string localFilePath = manager.GetUpdateFileForApp(app, includePrerelease);
            if (string.IsNullOrEmpty(localFilePath))
            {
                _logger.LogError("No update file found for app: {app}", app);
                return NotFound();
            }
            bool isExecutable = Path.GetExtension(localFilePath) == ".exe";
            if (!isExecutable && Path.GetExtension(localFilePath) != ".gz")
            {
                _logger.LogError("File is not an executable nor tarball file. [Appname: {app}]", app);
                return Problem(detail: "File is corrupted. Please contact administrator.", title: "File Corrupted");
            }

            var fileInfo = new FileInfo(localFilePath);
            var lastWriteTime = fileInfo.LastWriteTimeUtc;
            var fileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read);

            var filename = Path.GetFileName(localFilePath);
            bool useOriginalFilename = filename.Split("-").Length == 2;
            string contentType = isExecutable ? "application/vnd.microsoft.portable-executable" : "application/gzip";
            string extension = isExecutable ? ".exe" : ".tar.gz";

            var result = File(fileStream, contentType, useOriginalFilename ? filename : $"update{extension}");
            result.LastModified = new DateTimeOffset(lastWriteTime);

            return result;
        }

        [HttpGet("{app}/latest-info")]
        public IActionResult GetLatestInfo(string app)
        {
            string appFolder = manager.GetFolder(app);
            if (appFolder == null)
            {
                _logger.LogInformation("Trying to get latest info for non-existing app: {app}", app);
                return NotFound();
            }

            var info = manager.GetLatestUpdateInfo(appFolder);
            if (info == null)
            {
                return NotFound();
            }

            // Return a simplified DTO for the client
            return Ok(new
            {
                stable = info.LatestStable == null ? null : new
                {
                    version = info.LatestStable.Version?.ToString(),
                    file = Path.GetFileName(info.LatestStable.FilePath),
                    lastModified = info.LatestStable.LastModified
                },
                prerelease = info.LatestPreRelease == null ? null : new
                {
                    version = info.LatestPreRelease.Version?.ToString(),
                    file = Path.GetFileName(info.LatestPreRelease.FilePath),
                    lastModified = info.LatestPreRelease.LastModified
                }
            });
        }

        public static string MakeEtag(long lastMod, long size)
        {
            string etag = '"' + lastMod.ToString("x") + '-' + size.ToString("x") + '"';
            return etag;
        }
    }
}
