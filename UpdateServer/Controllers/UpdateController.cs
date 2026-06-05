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

            var includeSelfUpdate = check?.IncludeSelfUpdate ?? true;
            if (upToDate && includeSelfUpdate && !isOldUpdater)
            {
                var updaterVersion = GetUpdaterVersionFromRequest(Request);
                if (!string.IsNullOrEmpty(updaterVersion)
                    && updaterUpdateService.IsUpdaterUpdateNeeded(updaterVersion))
                {
                    _logger.LogInformation("{app} app is up to date but updater self-update is available", app);
                    upToDate = false;
                }
            }

            _logger.LogInformation("{app} is update to date: {upToDate}", app, upToDate);

            return Ok(upToDate);
        }

        [HttpPost("{app}/check-upgrades")]
        public IActionResult CheckUpgrades(string app, [FromBody] CheckRequest request, [FromQuery] bool includePrerelease = false)
        {
            var versionRaw = string.IsNullOrWhiteSpace(request?.Version)
                ? UpgradeService.UnknownClientAppVersionSentinel
                : request.Version.Trim();

            var unknownClientAppVersion = string.Equals(
                versionRaw,
                UpgradeService.UnknownClientAppVersionSentinel,
                StringComparison.OrdinalIgnoreCase);

            var clientVersion = AppVersion.Parse(versionRaw);
            if (clientVersion == null)
            {
                return BadRequest("Invalid version format");
            }

            var includeSelfUpdate = request.IncludeSelfUpdate ?? true;
            var updaterVersion = GetUpdaterVersionFromRequest(Request);
            var result = upgradeService.GetApplicableUpgrades(
                app, clientVersion, includePrerelease, updaterVersion, includeSelfUpdate, unknownClientAppVersion);
            if (result == null)
            {
                return NotFound();
            }

            var selfUpdate = includeSelfUpdate
                ? updaterUpdateService.GetSelfUpdateCheckInfo(updaterVersion, includePrerelease)
                : null;

            if (result.Upgrades.Count == 0 && selfUpdate?.Available != true)
            {
                return NoContent();
            }

            return Ok(BuildUpgradeInfoResponse(versionRaw, result, selfUpdate));
        }

        private static UpgradeInfoWrapper BuildUpgradeInfoResponse(
            string currentVersion,
            ApplicableUpgradesResult result,
            SelfUpdateCheckInfo selfUpdate)
        {
            return new UpgradeInfoWrapper
            {
                CurrentVersion = currentVersion,
                TargetVersion = result.TargetVersion,
                Upgrades = result.Upgrades.Select(u => new UpgradeSummary
                {
                    Id = u.Id,
                    Name = u.Name,
                    Priority = u.Priority
                }).ToList(),
                PackageSize = result.EstimatedSize,
                RequiresDownload = true,
                SelfUpdate = selfUpdate
            };
        }

        [HttpGet("{app}/download-upgrade")]
        public async Task<IActionResult> DownloadUpgrade(
            string app,
            [FromQuery] string fromVersion,
            [FromQuery] bool includePrerelease = false,
            [FromQuery] bool includeSelfUpdate = true)
        {
            if (string.IsNullOrEmpty(fromVersion))
                return BadRequest("fromVersion is required");

            var fromRaw = fromVersion.Trim();
            var unknownClientAppVersion = string.Equals(
                fromRaw,
                UpgradeService.UnknownClientAppVersionSentinel,
                StringComparison.OrdinalIgnoreCase);

            var clientVersion = AppVersion.Parse(fromRaw);
            if (clientVersion == null)
                return BadRequest("Invalid version format");

            try
            {
                var updaterVersion = GetUpdaterVersionFromRequest(Request);
                if (includeSelfUpdate && string.IsNullOrEmpty(updaterVersion))
                {
                    var userAgent = Request.Headers["User-Agent"].ToString();
                    _logger.LogWarning(
                        "download-upgrade for {app}: no parseable updater version in User-Agent or X-Updater-Version (User-Agent: {userAgent})",
                        app,
                        string.IsNullOrEmpty(userAgent) ? "(missing)" : userAgent);
                }

                var packagePath = await upgradeService.BuildUpgradePackage(
                    app, clientVersion, includePrerelease, updaterVersion, includeSelfUpdate, unknownClientAppVersion);
                
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
        public async Task<IActionResult> DownloadFile(string app, [FromQuery] bool includePrerelease = false)
        {
            // Detect if this is an old updater client
            bool isOldUpdater = IsOldUpdaterClient(Request);
            
            string appFolder = manager.GetFolder(app);
            if (appFolder == null)
            {
                _logger.LogWarning("App folder not found for app: {app}. Check AppNames configuration and ensure the folder exists.", app);
                return NotFound(new { 
                    error = "App not found", 
                    app = app, 
                    message = $"No folder configured for app '{app}'. Please check AppNames configuration in appsettings.json and ensure the folder exists in the apps directory." 
                });
            }
            
            if (!Directory.Exists(appFolder))
            {
                _logger.LogWarning("App folder does not exist: {appFolder} for app: {app}", appFolder, app);
                return NotFound(new { 
                    error = "App folder not found", 
                    app = app, 
                    folder = appFolder,
                    message = $"The folder '{appFolder}' does not exist. Please ensure the app files are deployed to this location." 
                });
            }
            
            string localFilePath = manager.GetUpdateFileForApp(app, includePrerelease);
            if (string.IsNullOrEmpty(localFilePath))
            {
                _logger.LogError("No update file found for app: {app} in folder: {appFolder}", app, appFolder);
                return NotFound(new { 
                    error = "No update file found", 
                    app = app, 
                    folder = appFolder,
                    message = $"No update files found in '{appFolder}'. Please ensure update files are present and follow the naming pattern: app-version.exe or app-version.tar.gz" 
                });
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
        public IActionResult GetLatestInfo(
            string app,
            [FromQuery] bool? includePrerelease = null,
            [FromQuery] bool? includePreRelease = null)
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

            var includePre = includePrerelease ?? includePreRelease ?? false;

            // Return a simplified DTO for the client
            return Ok(new
            {
                stable = info.LatestStable == null ? null : new
                {
                    version = info.LatestStable.Version?.ToString(),
                    file = Path.GetFileName(info.LatestStable.FilePath),
                    lastModified = info.LatestStable.LastModified
                },
                prerelease = includePre && info.LatestPreRelease != null ? new
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

        private string? GetUpdaterVersionFromRequest(Microsoft.AspNetCore.Http.HttpRequest request)
        {
            // Check User-Agent header
            var userAgent = request.Headers["User-Agent"].ToString();
            if (!string.IsNullOrEmpty(userAgent))
            {
                var match = Regex.Match(userAgent, @"AppUpdater/(\d+\.\d+\.\d+)");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }

            // Check custom header
            var updaterVersion = request.Headers["X-Updater-Version"].ToString();
            if (!string.IsNullOrEmpty(updaterVersion))
            {
                return updaterVersion;
            }

            return null;
        }

        /// <summary>
        /// Detect if the request is from an old updater client (version < 2.0.0)
        /// </summary>
        private bool IsOldUpdaterClient(Microsoft.AspNetCore.Http.HttpRequest request)
        {
            var versionStr = GetUpdaterVersionFromRequest(request);
            if (versionStr != null)
            {
                try
                {
                    var version = Version.Parse(versionStr);
                    bool isOld = version < new Version(2, 0, 0);
                    _logger.LogDebug("Detected updater version {version}. Is old: {isOld}", version, isOld);
                    return isOld;
                }
                catch
                {
                    // Invalid format
                }
            }

            // Default - if using old endpoint (which this logic is mostly used for?), or no header
            _logger.LogDebug("No updater version detected. Assuming old updater.");
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
            if (string.IsNullOrEmpty(packagePath) || !System.IO.File.Exists(packagePath))
            {
                _logger.LogError("Package file not found: {path}", packagePath);
                return NotFound("Package file not found");
            }

            var fileInfo = new FileInfo(packagePath);
            
            if (fileInfo.Length == 0)
            {
                _logger.LogError("Package file is empty: {path}", packagePath);
                return Problem(detail: "Package file is empty", title: "Invalid Package");
            }

            _logger.LogInformation("Serving combined package: {file} ({size} bytes)", 
                Path.GetFileName(packagePath), fileInfo.Length);

            var fileStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var filename = Path.GetFileName(packagePath);

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
