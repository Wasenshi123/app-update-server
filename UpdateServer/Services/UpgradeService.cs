using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace UpdateServer.Services
{
    public class UpgradeService
    {
        private readonly ILogger<UpgradeService> _logger;
        private readonly UpdateManager _updateManager;
        private readonly CompressionService _compressionService;
        private readonly IOptionsMonitor<Dictionary<string, string>> _appFolderMapping;
        private readonly UpdaterUpdateService _updaterUpdateService;
        
        // Relative path for upgrade files (consistent with UpdateManager.PREFIX_FOLDER pattern)
        // Combined with AppDomain.CurrentDomain.BaseDirectory to get full path
        public const string UPGRADE_FOLDER = "upgrade"; 

        public UpgradeService(
            ILogger<UpgradeService> logger,
            UpdateManager updateManager,
            CompressionService compressionService,
            IOptionsMonitor<Dictionary<string, string>> appFolderMapping,
            UpdaterUpdateService updaterUpdateService)
        {
            _logger = logger;
            _updateManager = updateManager;
            _compressionService = compressionService;
            _appFolderMapping = appFolderMapping;
            _updaterUpdateService = updaterUpdateService;
        }

        public ApplicableUpgradesResult GetApplicableUpgrades(string appName, AppVersion clientVersion, bool includePrerelease, string? updaterVersion = null)
        {
            var appFolder = _updateManager.GetFolder(appName);
            if (string.IsNullOrEmpty(appFolder))
            {
                _logger.LogWarning("App folder not found for {appName}", appName);
                return null;
            }

            var latestAppUpdate = _updateManager.GetLatestUpdateInfo(appFolder);
            var latestVersion = (includePrerelease && latestAppUpdate?.LatestPreRelease != null) 
                ? latestAppUpdate.LatestPreRelease.Version 
                : latestAppUpdate?.LatestStable?.Version;
            
            if (includePrerelease && latestAppUpdate?.LatestStable?.Version != null && latestAppUpdate?.LatestPreRelease?.Version != null)
            {
                if (latestAppUpdate.LatestStable.Version.CompareTo(latestAppUpdate.LatestPreRelease.Version) > 0)
                {
                    latestVersion = latestAppUpdate.LatestStable.Version;
                }
            }
            
            if (latestVersion == null)
            {
                _logger.LogWarning("No latest version found for {appName}", appName);
                return null;
            }

            // Load manifests
            var manifests = LoadManifests(appFolder);
            
            // Filter
            var applicable = FilterApplicableUpgrades(manifests, clientVersion, latestVersion);

            // Sort/Resolve Deps
            var ordered = ResolveUpgradeOrder(applicable);

            // Check if App Update is needed and add it as a virtual manifest
            if (latestVersion != null && clientVersion != null && latestVersion.CompareTo(clientVersion) > 0)
            {
                var appUpdateManifest = new UpgradeManifest
                {
                    Id = "app-update-" + latestVersion.ToString(),
                    Name = $"Application Update {latestVersion}",
                    Description = "Main application update",
                    Version = latestVersion.ToString(),
                    Priority = 100, // High priority
                    Files = new List<UpgradeFileParams>(), // Will be populated during packaging or handled specially
                    Metadata = new Dictionary<string, object> { { "Type", "AppUpdate" } },
                    AppliesTo = new VersionRange
                    {
                        // Apply to all versions up to the target version
                        MaxVersion = latestVersion.ToString()
                    }
                };
                
                ordered.Add(appUpdateManifest);
            }

            // Check if Updater Self-Update is needed
            if (!string.IsNullOrEmpty(updaterVersion))
            {
                var selfUpdateManifest = _updaterUpdateService.GenerateSelfUpdateManifest(updaterVersion);
                if (selfUpdateManifest != null)
                {
                    _logger.LogInformation("Injecting Updater Self-Update manifest {id}", selfUpdateManifest.Id);
                    ordered.Add(selfUpdateManifest);
                }
            }

            var estimatedSize = ordered.Sum(u => u.Files?.Sum(f => f.Size) ?? 0);

            return new ApplicableUpgradesResult
            {
                TargetVersion = latestVersion.ToString(),
                Upgrades = ordered,
                EstimatedSize = estimatedSize
            };
        }

        public async Task<string> BuildUpgradePackage(string appName, AppVersion clientVersion, bool includePrerelease, string? updaterVersion = null)
        {
            var result = GetApplicableUpgrades(appName, clientVersion, includePrerelease, updaterVersion);
            if (result == null || !result.Upgrades.Any())
            {
                return null;
            }

            var upgradePackageId = GenerateCacheKey(appName, clientVersion, result.Upgrades);
            
            // Try to use cache in app folder first, fall back to writable cache location if needed
            var appFolder = _updateManager.GetFolder(appName);
            string cachePath = null;
            
            if (!string.IsNullOrEmpty(appFolder))
            {
                var preferredCachePath = Path.Combine(appFolder, "cache", $"{upgradePackageId}.tar.gz");
                
                // Check if we can write to the app folder by trying to create the cache directory
                var cacheDir = Path.GetDirectoryName(preferredCachePath);
                if (!string.IsNullOrEmpty(cacheDir))
                {
                    try
                    {
                        if (!Directory.Exists(cacheDir))
                        {
                            Directory.CreateDirectory(cacheDir);
                        }
                        // Test write permission by checking if we can create a file
                        var testFile = Path.Combine(cacheDir, ".write-test");
                        try
                        {
                            File.WriteAllText(testFile, "test");
                            File.Delete(testFile);
                            cachePath = preferredCachePath;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            _logger.LogWarning("Cannot write to app folder cache directory {cacheDir}, using fallback location", cacheDir);
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        _logger.LogWarning("Cannot create cache directory in app folder {cacheDir}, using fallback location", cacheDir);
                    }
                }
            }
            
            // Fallback to writable cache location (under base directory or temp)
            if (string.IsNullOrEmpty(cachePath))
            {
                var fallbackCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", appName);
                Directory.CreateDirectory(fallbackCacheDir);
                cachePath = Path.Combine(fallbackCacheDir, $"{upgradePackageId}.tar.gz");
                _logger.LogInformation("Using fallback cache location: {cachePath}", cachePath);
            }

            if (File.Exists(cachePath))
            {
                _logger.LogInformation("Returning cached package: {path}", cachePath);
                return cachePath;
            }

            // Get fromVersion - use AppliesTo.MinVersion if available, otherwise use clientVersion
            var firstUpgrade = result.Upgrades.First();
            var fromVersion = firstUpgrade.AppliesTo?.MinVersion ?? clientVersion.ToString();
            
            return await PackageUpgrades(appName, result.Upgrades, cachePath, fromVersion, result.TargetVersion, includePrerelease);
        }

        private List<UpgradeManifest> LoadManifests(string appFolder)
        {
            var manifestsDir = Path.Combine(appFolder, "upgrade-manifests");
            if (!Directory.Exists(manifestsDir))
            {
                return new List<UpgradeManifest>();
            }

            var manifests = new List<UpgradeManifest>();
            foreach (var file in Directory.GetFiles(manifestsDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var manifest = JsonSerializer.Deserialize(json, UpdateServerJsonContext.Default.UpgradeManifest);
                    if (manifest != null)
                    {
                        manifests.Add(manifest);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse manifest: {file}", file);
                }
            }
            return manifests;
        }

        private List<UpgradeManifest> FilterApplicableUpgrades(List<UpgradeManifest> manifests, AppVersion clientVersion, AppVersion targetVersion)
        {
            return manifests
                .Where(m => IsVersionInRange(clientVersion, m.AppliesTo))
                .Where(m => m.TargetVersion == null || AppVersion.Parse(m.TargetVersion).CompareTo(targetVersion) <= 0)
                .ToList();
        }

        private bool IsVersionInRange(AppVersion version, VersionRange range)
        {
            if (range == null) return true;

            if (range.MinVersion != null)
            {
                var min = AppVersion.Parse(range.MinVersion);
                if (version.CompareTo(min) < 0) return false;
            }

            if (range.MaxVersion != null)
            {
                var max = AppVersion.Parse(range.MaxVersion);
                if (version.CompareTo(max) >= 0) return false;
            }

            if (range.ExcludeVersions != null && range.ExcludeVersions.Contains(version.ToString()))
            {
                return false;
            }

            return true;
        }

        private List<UpgradeManifest> ResolveUpgradeOrder(List<UpgradeManifest> upgrades)
        {
            var ordered = new List<UpgradeManifest>();
            var processed = new HashSet<string>();
            var processing = new HashSet<string>();

            // Simplistic topological sort based on dependencies
            // Also respect priority
            
            var upgradeMap = upgrades.ToDictionary(u => u.Id);

            void Visit(UpgradeManifest u)
            {
                if (processed.Contains(u.Id)) return;
                if (processing.Contains(u.Id)) throw new Exception("Circular dependency detected");

                processing.Add(u.Id);

                foreach (var depId in u.Dependencies)
                {
                    if (upgradeMap.ContainsKey(depId))
                    {
                        Visit(upgradeMap[depId]);
                    }
                }

                // also check priority? Actually doc says "Topological sort based on dependencies and priority"
                // Usually priority just dictates order among independent items.
                // But dependencies enforce strict order.

                processing.Remove(u.Id);
                processed.Add(u.Id);
                ordered.Add(u);
            }

            foreach (var upgrade in upgrades.OrderBy(u => u.Priority))
            {
                Visit(upgrade);
            }

            return ordered;
        }

        private string GenerateCacheKey(string appName, AppVersion clientVersion, List<UpgradeManifest> upgrades)
        {
            // Simple hash of upgrade IDs
            var ids = string.Join("-", upgrades.Select(u => u.Id).OrderBy(x => x));
            return $"upgrade-{clientVersion}-{ids.GetHashCode():X}";
        }

        private string GetUpgradeSourcePath(string appName, UpgradeManifest manifest)
        {
            // Follows same pattern as UpdateManager.GetFolder():
            // - Uses relative path constant (UPGRADE_FOLDER)
            // - Combines with AppDomain.CurrentDomain.BaseDirectory
            // Result: /app/upgrade/Box/driver-v2-upgrade/ or /app/upgrade/shared/common-driver/
            //
            // Note: basePath in manifest should be from UpdateServer's perspective (/app/upgrade),
            // not from NFS server's perspective (/exports/upgrade). If omitted, defaults to /app/upgrade.
            
            string basePath;
            if (!string.IsNullOrEmpty(manifest.Storage?.BasePath))
            {
                // Use explicit basePath from manifest if provided
                // Should be /app/upgrade (UpdateServer mount point), not /exports/upgrade (NFS server path)
                basePath = manifest.Storage.BasePath;
            }
            else
            {
                // Use same pattern as UpdateManager: combine base directory with relative folder
                // Defaults to /app/upgrade (AppDomain.CurrentDomain.BaseDirectory + "upgrade")
                basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, UPGRADE_FOLDER);
            }
            
            var path = manifest.Storage?.Path ?? ""; // e.g. "Box/driver-v2-upgrade/"
            
            return Path.Combine(basePath, path);
        }

        private async Task<string> PackageUpgrades(string appName, List<UpgradeManifest> upgrades, string outputPath, string fromVersion, string toVersion, bool includePrerelease)
        {
            // Ensure cache directory exists early (fail fast if permissions are insufficient)
            var cacheDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(cacheDir))
            {
                try
                {
                    if (!Directory.Exists(cacheDir))
                    {
                        _logger.LogInformation("Creating cache directory: {cacheDir}", cacheDir);
                        Directory.CreateDirectory(cacheDir);
                    }
                    
                    // Verify write permissions by attempting to create a test file
                    var testFile = Path.Combine(cacheDir, ".write-test");
                    try
                    {
                        File.WriteAllText(testFile, "test");
                        File.Delete(testFile);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        _logger.LogError("Cache directory exists but is not writable: {cacheDir}", cacheDir);
                        throw new UnauthorizedAccessException($"Cache directory exists but is not writable: {cacheDir}. Please ensure the application has write permissions.");
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    _logger.LogError("Failed to create cache directory. Ensure the application has write permissions to the cache directory path: {cacheDir}", cacheDir);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error creating cache directory: {cacheDir}", cacheDir);
                    throw;
                }
            }

            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try
            {
                var upgradePkgDir = Path.Combine(tempDir, "upgrade"); // Root of package
                Directory.CreateDirectory(upgradePkgDir);

                // Create manifest
                var packageManifest = new UpgradePackageManifest
                {
                    FromVersion = fromVersion,
                    ToVersion = toVersion,
                    Upgrades = upgrades.Select(u => u.Id).ToList()
                };

                File.WriteAllText(
                    Path.Combine(upgradePkgDir, "package-manifest.json"), 
                    JsonSerializer.Serialize(packageManifest, UpdateServerJsonContext.Default.UpgradePackageManifest));

                var upgradesDir = Path.Combine(upgradePkgDir, "upgrades");
                Directory.CreateDirectory(upgradesDir);

                foreach (var upgrade in upgrades)
                {
                    var destPath = Path.Combine(upgradesDir, upgrade.Id);
                    Directory.CreateDirectory(destPath);

                    if (upgrade.Metadata != null && upgrade.Metadata.ContainsKey("Type") && upgrade.Metadata["Type"].ToString() == "AppUpdate")
                    {
                        // Handle App Update - Extract from tarball
                        var appUpdatePath = _updateManager.GetUpdateFileForApp(appName, includePrerelease);
                        // Ideally we should verify the version matches 'toVersion'.
                        
                        if (!string.IsNullOrEmpty(appUpdatePath) && File.Exists(appUpdatePath))
                        {
                            _logger.LogInformation("Packaging App Update from {path}", appUpdatePath);
                            var fileName = Path.GetFileName(appUpdatePath);
                            File.Copy(appUpdatePath, Path.Combine(destPath, fileName), true);
                            
                            // Add single file entry for the tarball
                            upgrade.Files = new List<UpgradeFileParams>
                            {
                                new UpgradeFileParams
                                {
                                    Path = fileName,
                                    Target = null, // Extract to root of app folder
                                    Explode = true,
                                    Size = new FileInfo(appUpdatePath).Length
                                }
                            };
                        }
                        else
                        {
                            _logger.LogWarning("App update file not found");
                        }

                    }
                    else if (upgrade.Metadata != null && upgrade.Metadata.ContainsKey("Type") && upgrade.Metadata["Type"].ToString() == "UpdaterSelfUpdate")
                    {
                        // Handle Updater Self-Update
                        var updaterFolder = _updateManager.GetFolder("Updater");
                        var latestUpdater = _updateManager.GetLatestUpdateInfo(updaterFolder);
                        var updaterFilePath = latestUpdater?.LatestStable?.FilePath;

                        if (!string.IsNullOrEmpty(updaterFilePath) && File.Exists(updaterFilePath))
                        {
                            _logger.LogInformation("Packaging Updater Self-Update from {path}", updaterFilePath);
                            var fileName = Path.GetFileName(updaterFilePath);
                            var destFilePath = Path.Combine(destPath, fileName);
                            
                            // Get file size before copying to verify source file
                            var sourceFileInfo = new FileInfo(updaterFilePath);
                            var sourceSize = sourceFileInfo.Length;
                            _logger.LogInformation("Source updater file size: {size} bytes", sourceSize);
                            
                            // Copy the file
                            File.Copy(updaterFilePath, destFilePath, true);
                            
                            // Verify the copied file exists and has correct size
                            var destFileInfo = new FileInfo(destFilePath);
                            if (!destFileInfo.Exists)
                            {
                                var errorMsg = $"Failed to copy updater file: destination file does not exist at {destFilePath}";
                                _logger.LogError(errorMsg);
                                throw new FileNotFoundException(errorMsg);
                            }
                            
                            if (destFileInfo.Length != sourceSize)
                            {
                                var errorMsg = $"File copy size mismatch: source={sourceSize} bytes, destination={destFileInfo.Length} bytes";
                                _logger.LogError(errorMsg);
                                throw new InvalidOperationException(errorMsg);
                            }
                            
                            _logger.LogInformation("Successfully copied updater file: {fileName} ({size} bytes)", fileName, destFileInfo.Length);
                            
                            // Populate Files in manifest - use the copied file's size for accuracy
                            var targetDir = $"/home/hemo/updater/pending-update/updater-{upgrade.Version}";
                            upgrade.Files = new List<UpgradeFileParams>
                            {
                                new UpgradeFileParams
                                {
                                    Path = fileName,
                                    Target = targetDir,
                                    Explode = true,
                                    Size = destFileInfo.Length
                                }
                            };
                            
                            // Post install script is already set in GenerateSelfUpdateManifest but we might want to ensure it works?
                            // It's `echo "update-staged" > .pending-update`
                            // We need it to be executed relative to something?
                            // The generic installer runs it.
                        }
                        else
                        {
                             _logger.LogWarning("Updater update file not found");
                             throw new FileNotFoundException("Updater update file not found");
                        }
                    }
                    else
                    {
                        // Standard Upgrade
                        var sourcePath = GetUpgradeSourcePath(appName, upgrade);
                        
                        if (Directory.Exists(sourcePath))
                        {
                            CopyDirectory(sourcePath, destPath);
                        }
                        else
                        {
                            _logger.LogWarning("Upgrade source path not found: {path}", sourcePath);
                            throw new DirectoryNotFoundException($"Upgrade source path not found: {sourcePath}");
                        }
                    }

                    // Also write the manifest itself so client knows what to do
                    File.WriteAllText(
                        Path.Combine(destPath, "manifest.json"), 
                        JsonSerializer.Serialize(upgrade, UpdateServerJsonContext.Default.UpgradeManifest));
                }
                
                // Cache directory should already exist (created at start of method)
                // Double-check it exists before writing
                cacheDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
                {
                    _logger.LogWarning("Cache directory was removed after creation. Recreating: {cacheDir}", cacheDir);
                    Directory.CreateDirectory(cacheDir);
                }

                await _compressionService.CreateTarGz(upgradePkgDir, outputPath);

                return outputPath;
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        private void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
            }
            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
            }
        }
    }
}
