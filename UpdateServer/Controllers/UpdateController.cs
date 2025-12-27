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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UpdateServer.Services;

namespace UpdateServer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class UpdateController : ControllerBase
    {

        private readonly ILogger<UpdateController> _logger;
        private readonly UpdateManager manager;
        private readonly UpdaterUpdateService updaterUpdateService;
        private readonly UpgradeService upgradeService;
        private readonly IOptionsMonitor<Dictionary<string, string>> appFolderMapping;

        public UpdateController(
            ILogger<UpdateController> logger, 
            UpdateManager manager,
            UpdaterUpdateService updaterUpdateService,
            UpgradeService upgradeService,
            IOptionsMonitor<Dictionary<string, string>> appFolderMapping)
        {
            _logger = logger;
            this.manager = manager;
            this.updaterUpdateService = updaterUpdateService;
            this.upgradeService = upgradeService;
            this.appFolderMapping = appFolderMapping;
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

            // Detect if this is an old updater client
            bool isOldUpdater = IsOldUpdaterClient(Request);
            
            _logger.LogInformation("{app} Checking... user version: {version}, old updater: {isOld}", 
                app, check?.Version ?? "Unknown", isOldUpdater);

            bool upToDate = isOldUpdater ? false : manager.CheckVersion(appFolder, check, includePrerelease);

            _logger.LogInformation("{app} is update to date: {upToDate}", app, upToDate);

            return Ok(upToDate);
        }

        [HttpPost("{app}/check-upgrades")]
        public IActionResult CheckUpgrades(string app, [FromBody] CheckRequest request, [FromQuery] bool includePrerelease = false)
        {
            if (string.IsNullOrEmpty(request?.Version))
            {
                 return BadRequest("Version is required");
            }

            var clientVersion = AppVersion.Parse(request.Version);
            if (clientVersion == null)
            {
                return BadRequest("Invalid version format");
            }

            var result = upgradeService.GetApplicableUpgrades(app, clientVersion, includePrerelease);
            if (result == null)
            {
                return NotFound();
            }

            // If no upgrades found, returning up-to-date or similar
            if (result.Upgrades.Count == 0)
            {
                // Return 204 No Content to indicate up to date, or empty list
                // Design doc says: "Result: No upgrades needed, returns 204 No Content"
                return NoContent();
            }

            return Ok(new UpgradeInfoWrapper
            {
                CurrentVersion = request.Version,
                TargetVersion = result.TargetVersion,
                Upgrades = result.Upgrades.Select(u => new UpgradeSummary
                {
                    Id = u.Id,
                    Name = u.Name,
                    Priority = u.Priority
                }).ToList(),
                PackageSize = result.EstimatedSize,
                RequiresDownload = true
            });
        }

        [HttpGet("{app}/download-upgrade")]
        public async Task<IActionResult> DownloadUpgrade(string app, [FromQuery] string fromVersion, [FromQuery] bool includePrerelease = false)
        {
            if (string.IsNullOrEmpty(fromVersion))
                return BadRequest("fromVersion is required");

            var clientVersion = AppVersion.Parse(fromVersion);
            if (clientVersion == null)
                return BadRequest("Invalid version format");

            try
            {
                var packagePath = await upgradeService.BuildUpgradePackage(app, clientVersion, includePrerelease);
                
                if (packagePath == null)
                    return NotFound();
                
                return await ServePackage(packagePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download upgrade package");
                return StatusCode(500, "Internal server error during package generation");
            }
        }

        [HttpGet("{app}/download")]
        public async Task<IActionResult> DownloadFile(string app, [FromQuery] bool includePrerelease = true)
        {
            // Detect if this is an old updater client
            bool isOldUpdater = IsOldUpdaterClient(Request);
            
            string localFilePath = manager.GetUpdateFileForApp(app, includePrerelease);
            if (string.IsNullOrEmpty(localFilePath))
            {
                _logger.LogError("No update file found for app: {app}", app);
                return NotFound();
            }

            if (isOldUpdater)
            {
                // Only embed updater update if app file is a tar.gz (not .exe)
                // We need to extract and repackage, which only works with tar.gz files
                bool isTarGz = Path.GetExtension(localFilePath) == ".gz" || 
                               localFilePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);
                
                if (isTarGz)
                {
                    // Package app update with embedded updater update
                    _logger.LogInformation("Old updater detected. Packaging app update with updater update for app: {app}", app);
                    
                    // Get app folder name for script generation
                    string appFolderName = GetAppFolderName(app);
                    
                    var packagePath = await updaterUpdateService.PackageAppUpdateWithUpdater(app, localFilePath, appFolderName);
                    if (packagePath != null && System.IO.File.Exists(packagePath))
                    {
                        return await ServePackage(packagePath);
                    }
                    // Fall through to serve normal update if packaging fails
                    _logger.LogWarning("Failed to package with updater, serving normal update");
                }
                else
                {
                    _logger.LogInformation("Old updater detected but app file is not tar.gz (extension: {ext}), serving normal update without embedding updater", 
                        Path.GetExtension(localFilePath));
                }
            }

            // Serve normal app update
            return ServeFile(localFilePath);
        }

        [HttpGet("{app}/latest-info")]
        public IActionResult GetLatestInfo(string app, [FromQuery] bool includePreRelease = false)
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
                prerelease = includePreRelease && info.LatestPreRelease != null ? new
                {
                    version = info.LatestPreRelease.Version?.ToString(),
                    file = Path.GetFileName(info.LatestPreRelease.FilePath),
                    lastModified = info.LatestPreRelease.LastModified
                } : null
            });
        }

        public static string MakeEtag(long lastMod, long size)
        {
            string etag = '"' + lastMod.ToString("x") + '-' + size.ToString("x") + '"';
            return etag;
        }

        /// <summary>
        /// Detect if the request is from an old updater client (version < 2.0.0)
        /// </summary>
        private bool IsOldUpdaterClient(Microsoft.AspNetCore.Http.HttpRequest request)
        {
            // Method 1: Check User-Agent header
            var userAgent = request.Headers["User-Agent"].ToString();
            if (!string.IsNullOrEmpty(userAgent))
            {
                var match = Regex.Match(userAgent, @"AppUpdater/(\d+\.\d+\.\d+)");
                if (match.Success)
                {
                    try
                    {
                        var version = Version.Parse(match.Groups[1].Value);
                        bool isOld = version < new Version(2, 0, 0);
                        _logger.LogDebug("Detected updater version {version} from User-Agent. Is old: {isOld}", 
                            version, isOld);
                        return isOld;
                    }
                    catch
                    {
                        // Invalid version format
                    }
                }
            }

            // Method 2: Check custom header
            var updaterVersion = request.Headers["X-Updater-Version"].ToString();
            if (!string.IsNullOrEmpty(updaterVersion))
            {
                try
                {
                    var version = Version.Parse(updaterVersion);
                    bool isOld = version < new Version(2, 0, 0);
                    _logger.LogDebug("Detected updater version {version} from header. Is old: {isOld}", 
                        version, isOld);
                    return isOld;
                }
                catch
                {
                    // Invalid version format
                }
            }

            // Method 3: Default - if using old endpoint, assume old updater
            // This is the fallback if no version info available
            _logger.LogDebug("No updater version detected. Assuming old updater (using old endpoint)");
            return true;
        }

        /// <summary>
        /// Serve a file (normal app update)
        /// </summary>
        private IActionResult ServeFile(string filePath)
        {
            bool isExecutable = Path.GetExtension(filePath) == ".exe";
            if (!isExecutable && Path.GetExtension(filePath) != ".gz")
            {
                _logger.LogError("File is not an executable nor tarball file. [Path: {path}]", filePath);
                return Problem(detail: "File is corrupted. Please contact administrator.", title: "File Corrupted");
            }

            var fileInfo = new FileInfo(filePath);
            var lastWriteTime = fileInfo.LastWriteTimeUtc;
            var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);

            var filename = Path.GetFileName(filePath);
            bool useOriginalFilename = filename.Split("-").Length == 2;
            string contentType = isExecutable ? "application/vnd.microsoft.portable-executable" : "application/gzip";
            string extension = isExecutable ? ".exe" : ".tar.gz";

            var result = File(fileStream, contentType, useOriginalFilename ? filename : $"update{extension}");
            result.LastModified = new DateTimeOffset(lastWriteTime);

            return result;
        }

        /// <summary>
        /// Serve a package (app update with embedded updater)
        /// </summary>
        private async Task<IActionResult> ServePackage(string packagePath)
        {
            var fileInfo = new FileInfo(packagePath);
            var fileStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read);
            var filename = Path.GetFileName(packagePath);

            _logger.LogInformation("Serving combined package: {file} ({size} bytes)", 
                filename, fileInfo.Length);

            var result = File(fileStream, "application/gzip", filename);
            result.LastModified = new DateTimeOffset(fileInfo.LastWriteTimeUtc);

            return result;
        }

        /// <summary>
        /// Get app folder name from app name mapping (configurable via appsettings.json)
        /// </summary>
        private string GetAppFolderName(string appName)
        {
            var mapping = appFolderMapping.Get("AppFolderMapping");
            if (mapping != null && mapping.TryGetValue(appName, out var folderName))
            {
                _logger.LogDebug("Mapped app '{appName}' to folder '{folderName}'", appName, folderName);
                return folderName;
            }

            // Default fallback: use lowercase app name
            _logger.LogWarning("Unknown app name '{appName}' in AppFolderMapping, using default folder name: {default}", 
                appName, appName.ToLowerInvariant());
            return appName.ToLowerInvariant();
        }
    }
}
