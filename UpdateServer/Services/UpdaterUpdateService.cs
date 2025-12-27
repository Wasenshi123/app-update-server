using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace UpdateServer.Services
{
    public class UpdaterUpdateService
    {
        private readonly ILogger<UpdaterUpdateService> logger;
        private readonly UpdateManager updateManager;
        private readonly IOptionsMonitor<Dictionary<string, string>> appFolderMapping;
        private const string MIN_UPDATER_VERSION = "2.0.0";

        public UpdaterUpdateService(
            ILogger<UpdaterUpdateService> logger, 
            UpdateManager updateManager,
            IOptionsMonitor<Dictionary<string, string>> appFolderMapping)
        {
            this.logger = logger;
            this.updateManager = updateManager;
            this.appFolderMapping = appFolderMapping;
        }

        /// <summary>
        /// Check if an updater update is needed (if latest updater version >= 2.0.0)
        /// </summary>
        public bool IsUpdaterUpdateNeeded()
        {
            var updaterFolder = updateManager.GetFolder("Updater");
            if (updaterFolder == null)
            {
                logger.LogDebug("Updater folder not found");
                return false;
            }

            var latestUpdater = updateManager.GetLatestUpdateInfo(updaterFolder);
            if (latestUpdater?.LatestStable == null)
            {
                logger.LogDebug("No stable updater version found");
                return false;
            }

            // Check if latest updater is >= 2.0.0
            var latestVersion = latestUpdater.LatestStable.Version;
            if (latestVersion != null)
            {
                var minVersion = AppVersion.Parse(MIN_UPDATER_VERSION);
                bool needed = latestVersion.CompareTo(minVersion) >= 0;
                logger.LogInformation("Updater update needed: {needed} (latest: {version}, min: {min})", 
                    needed, latestVersion, MIN_UPDATER_VERSION);
                return needed;
            }

            return false;
        }

        /// <summary>
        /// Package app update with embedded updater update if needed
        /// </summary>
        public async Task<string> PackageAppUpdateWithUpdater(string appName, string appUpdatePath, string appFolderName = null)
        {
            if (!IsUpdaterUpdateNeeded())
            {
                logger.LogInformation("No updater update needed, serving app update as-is");
                return null;
            }

            var updaterFolder = updateManager.GetFolder("Updater");
            if (updaterFolder == null)
            {
                logger.LogWarning("Updater folder not found");
                return null;
            }

            var latestUpdater = updateManager.GetLatestUpdateInfo(updaterFolder);
            var updaterFilePath = latestUpdater?.LatestStable?.FilePath;
            if (string.IsNullOrEmpty(updaterFilePath) || !File.Exists(updaterFilePath))
            {
                logger.LogWarning("Latest updater file not found: {path}", updaterFilePath);
                return null;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try
            {
                logger.LogInformation("Packaging app update with embedded updater update...");

                // Extract app update
                var appExtractDir = Path.Combine(tempDir, "app");
                Directory.CreateDirectory(appExtractDir);
                await ExtractTarGz(appUpdatePath, appExtractDir);

                // Add updater update to upgrade folder (existing pattern)
                var upgradeDir = Path.Combine(appExtractDir, "upgrade");
                Directory.CreateDirectory(upgradeDir);

                // Copy updater archive to upgrade folder with standard name (matching existing pattern)
                var updaterFileName = Path.GetFileName(updaterFilePath);
                var updaterDest = Path.Combine(upgradeDir, "updater-new.tar.gz"); // Standard name matching existing pattern
                File.Copy(updaterFilePath, updaterDest, overwrite: true);
                logger.LogInformation("Copied updater archive to upgrade folder as updater-new.tar.gz (original: {file})", updaterFileName);

                // Get app folder name from appName mapping or use default
                if (string.IsNullOrEmpty(appFolderName))
                {
                    appFolderName = GetAppFolderName(appName);
                }

                // Create run.sh script to extract updater to correct place
                var runScriptPath = Path.Combine(upgradeDir, "run.sh");
                var runScriptContent = GenerateRunScript("updater-new.tar.gz", appFolderName);
                File.WriteAllText(runScriptPath, runScriptContent);
                
                // Make run.sh executable
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
                {
                    var chmodProcess = System.Diagnostics.Process.Start("chmod", $"+x {runScriptPath}");
                    chmodProcess?.WaitForExit();
                }
                
                logger.LogInformation("Created run.sh script in upgrade folder");

                // Create new package
                var packagePath = Path.Combine(Path.GetTempPath(), $"app-with-updater-{Guid.NewGuid()}.tar.gz");
                await CreateTarGz(appExtractDir, packagePath);

                logger.LogInformation("Packaged app update with updater update: {package} ({size} bytes)", 
                    Path.GetFileName(packagePath), new FileInfo(packagePath).Length);

                return packagePath;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to package app update with updater");
                return null;
            }
            finally
            {
                // Cleanup temp directory
                if (Directory.Exists(tempDir))
                {
                    try
                    {
                        Directory.Delete(tempDir, true);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to cleanup temp directory: {path}", tempDir);
                    }
                }
            }
        }

        /// <summary>
        /// Extract tar.gz archive
        /// </summary>
        private async Task ExtractTarGz(string archivePath, string extractPath)
        {
            logger.LogDebug("Extracting {archive} to {path}", archivePath, extractPath);

            using (var fileStream = File.OpenRead(archivePath))
            using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
            {
                await ExtractTar(gzipStream, extractPath);
            }
        }

        /// <summary>
        /// Extract tar stream
        /// </summary>
        private async Task ExtractTar(Stream tarStream, string extractPath)
        {
            var buffer = new byte[512];
            Directory.CreateDirectory(extractPath);

            while (true)
            {
                // Read header (512 bytes)
                int bytesRead = await tarStream.ReadAsync(buffer, 0, 512);
                if (bytesRead == 0 || buffer.All(b => b == 0))
                    break;

                // Parse filename (first 100 bytes)
                string fileName = System.Text.Encoding.ASCII.GetString(buffer, 0, 100).TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(fileName))
                    break;

                // Parse file size (octal, bytes 124-135)
                string sizeStr = System.Text.Encoding.ASCII.GetString(buffer, 124, 12).TrimEnd('\0', ' ');
                if (!long.TryParse(sizeStr, System.Globalization.NumberStyles.Integer, null, out long fileSize))
                {
                    fileSize = 0;
                }

                // Skip to file data
                tarStream.Seek(376 - 512, SeekOrigin.Current);

                if (fileSize > 0)
                {
                    var filePath = Path.Combine(extractPath, fileName);
                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    using (var fileStream = File.Create(filePath))
                    {
                        long remaining = fileSize;
                        while (remaining > 0)
                        {
                            int toRead = (int)Math.Min(remaining, buffer.Length);
                            int read = await tarStream.ReadAsync(buffer, 0, toRead);
                            if (read == 0)
                                break;
                            await fileStream.WriteAsync(buffer, 0, read);
                            remaining -= read;
                        }
                    }
                }

                // Skip padding to next 512-byte boundary
                long padding = (512 - (fileSize % 512)) % 512;
                if (padding > 0)
                {
                    tarStream.Seek(padding, SeekOrigin.Current);
                }
            }
        }

        /// <summary>
        /// Create tar.gz archive
        /// </summary>
        private async Task CreateTarGz(string sourceDir, string outputPath)
        {
            logger.LogDebug("Creating tar.gz archive: {output}", outputPath);

            using (var fileStream = File.Create(outputPath))
            using (var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal))
            {
                await CreateTar(sourceDir, gzipStream);
            }
        }

        /// <summary>
        /// Create tar archive
        /// </summary>
        private async Task CreateTar(string sourceDir, Stream tarStream)
        {
            var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            var buffer = new byte[512];

            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                var fileInfo = new FileInfo(file);

                // Write header
                Array.Clear(buffer, 0, 512);

                // Filename (100 bytes)
                var nameBytes = System.Text.Encoding.ASCII.GetBytes(relativePath);
                Array.Copy(nameBytes, 0, buffer, 0, Math.Min(nameBytes.Length, 100));

                // File mode (8 bytes) - 0644
                System.Text.Encoding.ASCII.GetBytes("0000644").CopyTo(buffer, 100);

                // UID/GID (16 bytes) - 0
                System.Text.Encoding.ASCII.GetBytes("0000000").CopyTo(buffer, 108);
                System.Text.Encoding.ASCII.GetBytes("0000000").CopyTo(buffer, 116);

                // File size (12 bytes, octal)
                string sizeOctal = Convert.ToString(fileInfo.Length, 8).PadLeft(11, '0') + " ";
                System.Text.Encoding.ASCII.GetBytes(sizeOctal).CopyTo(buffer, 124);

                // Modification time (12 bytes, octal)
                long unixTime = ((DateTimeOffset)fileInfo.LastWriteTimeUtc).ToUnixTimeSeconds();
                string timeOctal = Convert.ToString(unixTime, 8).PadLeft(11, '0') + " ";
                System.Text.Encoding.ASCII.GetBytes(timeOctal).CopyTo(buffer, 136);

                // Type flag (1 byte) - regular file
                buffer[156] = (byte)'0';

                // Write header
                await tarStream.WriteAsync(buffer, 0, 512);

                // Write file content
                using (var fileStream = File.OpenRead(file))
                {
                    await fileStream.CopyToAsync(tarStream);
                }

                // Write padding to 512-byte boundary
                long padding = (512 - (fileInfo.Length % 512)) % 512;
                if (padding > 0)
                {
                    Array.Clear(buffer, 0, (int)padding);
                    await tarStream.WriteAsync(buffer, 0, (int)padding);
                }
            }

            // Write two empty blocks at end
            Array.Clear(buffer, 0, 512);
            await tarStream.WriteAsync(buffer, 0, 512);
            await tarStream.WriteAsync(buffer, 0, 512);
        }

        /// <summary>
        /// Get app folder name from app name mapping (configurable via appsettings.json)
        /// </summary>
        private string GetAppFolderName(string appName)
        {
            var mapping = appFolderMapping.Get("AppFolderMapping");
            if (mapping != null && mapping.TryGetValue(appName, out var folderName))
            {
                logger.LogDebug("Mapped app '{appName}' to folder '{folderName}'", appName, folderName);
                return folderName;
            }

            // Default fallback: use lowercase app name
            logger.LogWarning("Unknown app name '{appName}' in AppFolderMapping, using default folder name: {default}", 
                appName, appName.ToLowerInvariant());
            return appName.ToLowerInvariant();
        }

        /// <summary>
        /// Generate run.sh script to extract updater to Updater folder
        /// Matches the existing upgrade pattern used on IoT devices
        /// </summary>
        private string GenerateRunScript(string updaterFileName, string appFolderName)
        {
            return @"#!/bin/bash

# Upgrade script to update appUpdater
# This script is automatically generated by UpdateServer
# Matches existing upgrade pattern: extracts updater from upgrade/updater-new.tar.gz

# Define variables (matching existing pattern)
# App folder name is dynamically determined based on app (hemopro for HemoBox, checkinapp for HemoCheckIn)
APP_FOLDER=""" + appFolderName + @"""
UPDATER_PATH=""/home/hemo/$APP_FOLDER/upgrade/" + updaterFileName + @"""
DESTINATION_DIR=""/home/hemo/updater""

# Check if updater archive exists
if [ ! -f ""$UPDATER_PATH"" ]; then
    echo ""[Upgrade] ERROR: Updater archive not found: $UPDATER_PATH""
    exit 1
fi

# Ensure destination directory exists
mkdir -p ""$DESTINATION_DIR""

# Extract the tarball
echo ""[Upgrade] Extracting updater archive...""
tar -xvzf ""$UPDATER_PATH"" -C ""$DESTINATION_DIR""

# Check if the extraction was successful
if [ $? -eq 0 ]; then
    echo ""[Upgrade] Extraction for updater completed successfully.""
    echo ""[Upgrade] Files are located in $DESTINATION_DIR""
else
    echo ""[Upgrade] ERROR: Extraction failed.""
    exit 1
fi

# Note: The upgrade folder will be removed by the startup script after this script completes
";
        }
    }
}

