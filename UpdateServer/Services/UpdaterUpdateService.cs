using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace UpdateServer.Services
{
    public class UpdaterUpdateService
    {
        private readonly ILogger<UpdaterUpdateService> logger;
        private readonly UpdateManager updateManager;
        private readonly CompressionService compressionService;
        private readonly IOptionsMonitor<Dictionary<string, string>> appFolderMapping;
        private const string MIN_UPDATER_VERSION = "2.0.0";

        public UpdaterUpdateService(
            ILogger<UpdaterUpdateService> logger, 
            UpdateManager updateManager,
            CompressionService compressionService,
            IOptionsMonitor<Dictionary<string, string>> appFolderMapping)
        {
            this.logger = logger;
            this.updateManager = updateManager;
            this.compressionService = compressionService;
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
        /// Check if a specific client needs an updater update
        /// </summary>
        public bool IsUpdaterUpdateNeeded(string currentVersionStr)
        {
            if (string.IsNullOrEmpty(currentVersionStr)) return false;

            var updaterFolder = updateManager.GetFolder("Updater");
            if (updaterFolder == null) return false;

            var latestUpdater = updateManager.GetLatestUpdateInfo(updaterFolder);
            var latestVersion = latestUpdater?.LatestStable?.Version;

            if (latestVersion != null)
            {
                var currentVersion = AppVersion.Parse(currentVersionStr);
                if (currentVersion == null) return false;

                return latestVersion.CompareTo(currentVersion) > 0;
            }

            return false;
        }
        
        public SelfUpdateCheckInfo? GetSelfUpdateCheckInfo(string? currentVersionStr, bool includePrerelease = false)
        {
            if (string.IsNullOrWhiteSpace(currentVersionStr) || !IsUpdaterUpdateNeeded(currentVersionStr))
            {
                return null;
            }

            var updaterFolder = updateManager.GetFolder("Updater");
            if (updaterFolder == null)
            {
                return null;
            }

            var latestUpdater = updateManager.GetLatestUpdateInfo(updaterFolder);
            var latestFile = includePrerelease && latestUpdater?.LatestPreRelease != null
                ? latestUpdater.LatestPreRelease
                : latestUpdater?.LatestStable;

            if (latestFile?.Version == null)
            {
                return null;
            }

            return new SelfUpdateCheckInfo
            {
                Available = true,
                CurrentVersion = currentVersionStr.Trim(),
                TargetVersion = latestFile.Version.ToString(),
                PackageFile = string.IsNullOrEmpty(latestFile.FilePath)
                    ? null
                    : Path.GetFileName(latestFile.FilePath)
            };
        }

        public UpgradeManifest? GenerateSelfUpdateManifest(string currentVersionStr)
        {
            if (!IsUpdaterUpdateNeeded(currentVersionStr)) return null;

            var updaterFolder = updateManager.GetFolder("Updater");
            var latestUpdater = updateManager.GetLatestUpdateInfo(updaterFolder);
            var latestVersion = latestUpdater?.LatestStable?.Version;

            if (latestVersion == null) return null;

            return new UpgradeManifest
            {
                Id = $"updater-self-update-{latestVersion}",
                Name = $"Updater Self-Update {latestVersion}",
                Description = "Self-update for the Updater application",
                Version = latestVersion.ToString(),
                Priority = 1000, // Highest priority
                Metadata = new Dictionary<string, object> { { "Type", "UpdaterSelfUpdate" } },
                Files = new List<UpgradeFileParams>(), // Will be populated by PackageUpgrades
                PostInstallScript = null, // No post-install script needed - bootstrap script checks for pending-update folder
                AppliesTo = new VersionRange
                {
                    // Apply to all versions (no min/max restrictions for self-update)
                    // The IsUpdaterUpdateNeeded check already validates the version
                }
            };
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
                await compressionService.ExtractTarGz(appUpdatePath, appExtractDir);

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

                // Check for bootstrap-updater.sh in upgrade/shared/bootstrap
                var bootstrapDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "upgrade", "shared", "bootstrap");
                var bootstrapPath = Path.Combine(bootstrapDir, "bootstrap-updater.sh");
                var setupPath = Path.Combine(bootstrapDir, "setup-bootstrap.sh");
                
                bool hasBootstrap = File.Exists(bootstrapPath);
                bool hasSetup = File.Exists(setupPath);
                
                if (hasBootstrap)
                {
                    var bootstrapDest = Path.Combine(upgradeDir, "bootstrap-updater.sh");
                    File.Copy(bootstrapPath, bootstrapDest, true);
                    logger.LogInformation("Bundled bootstrap-updater.sh from {path}", bootstrapPath);
                }
                else
                {
                    logger.LogWarning("bootstrap-updater.sh not found at {path}", bootstrapPath);
                }

                if (hasSetup)
                {
                    var setupDest = Path.Combine(upgradeDir, "setup-bootstrap.sh");
                    File.Copy(setupPath, setupDest, true);
                    logger.LogInformation("Bundled setup-bootstrap.sh from {path}", setupPath);
                }
                else
                {
                    logger.LogWarning("setup-bootstrap.sh not found at {path}", setupPath);
                }

                // Create run.sh script to extract updater to correct place
                var runScriptPath = Path.Combine(upgradeDir, "run.sh");
                var runScriptContent = GenerateRunScript("updater-new.tar.gz", appFolderName, appName, hasBootstrap, hasSetup);
                // Ensure Linux line endings (LF only, not CRLF) for shell script
                var normalizedContent = runScriptContent.Replace("\r\n", "\n").Replace("\r", "\n");
                File.WriteAllText(runScriptPath, normalizedContent, new System.Text.UTF8Encoding(false));
                
                // Make run.sh executable
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
                {
                    var chmodProcess = System.Diagnostics.Process.Start("chmod", $"+x {runScriptPath}");
                    chmodProcess?.WaitForExit();
                }
                
                logger.LogInformation("Created run.sh script in upgrade folder");

                // Create new package
                var packagePath = Path.Combine(Path.GetTempPath(), $"app-with-updater-{Guid.NewGuid()}.tar.gz");
                await compressionService.CreateTarGz(appExtractDir, packagePath);

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
        /// Linux directory for Updater legacy migration + <c>settings.json</c> in the generated <c>run.sh</c>.
        /// HemoCheckIn runs as <c>hemo</c> (same as <c>LocalApplicationData</c>/Updater on that user).
        /// HemoBox IoT typically runs the updater chain as root.
        /// </summary>
        private static string GetUpdaterSettingsDirectoryForPackagedScript(string? appName)
        {
            if (IsHemoCheckInApp(appName))
            {
                return "/home/hemo/.local/share/Updater";
            }

            return "/root/.local/share/Updater";
        }

        private static bool IsHemoCheckInApp(string? appName)
        {
            if (string.IsNullOrWhiteSpace(appName))
            {
                return false;
            }

            var key = appName.Trim();
            return key.Equals("HemoCheckIn", StringComparison.OrdinalIgnoreCase)
                || key.Equals("CheckIn", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Generate run.sh script to extract updater to Updater folder
        /// Matches the existing upgrade pattern used on IoT devices.
        ///
        /// Also migrates legacy version-scoped Updater settings
        /// (&lt;SETTINGS_DIR&gt;/&lt;Updater_Url_xxx&gt;/1.0.0.0/user.config)
        /// to the new non-version-scoped JSON file (settings.json next to legacy trees).
        /// The SETTINGS_DIR value is chosen from the app being updated (HemoCheckIn vs HemoBox).
        /// For HemoCheckIn, after migration the script <c>chown -R hemo:hemo</c> on <c>SETTINGS_DIR</c>
        /// (so config is writable if tar fails). After a successful extract and optional bootstrap copy,
        /// it chowns <c>/home/hemo/updater</c>, the app root <c>/home/hemo/$APP_FOLDER</c> (e.g. checkinapp),
        /// and <c>/home/hemo/bootstrap-updater.sh</c> when present.
        /// The migration is idempotent: it skips if the new settings.json already exists or if no legacy file is found.
        /// </summary>
        private string GenerateRunScript(string updaterFileName, string appFolderName, string appName, bool hasBootstrap, bool hasSetup)
        {
            var settingsDir = GetUpdaterSettingsDirectoryForPackagedScript(appName);
            logger.LogInformation(
                "Generating embedded run.sh: SETTINGS_DIR={settingsDir} for app {appName}",
                settingsDir,
                appName);

            var script = @"#!/bin/bash

# Upgrade script to update appUpdater
# This script is automatically generated by UpdateServer
# Matches existing upgrade pattern: extracts updater from upgrade/updater-new.tar.gz

APP_FOLDER=""" + appFolderName + @"""
UPDATER_PATH=""/home/hemo/$APP_FOLDER/upgrade/" + updaterFileName + @"""
DESTINATION_DIR=""/home/hemo/updater""
# Updater settings live under XDG data dir. Old versions used a version-scoped
# subfolder (Updater_Url_<hash>/1.0.0.0/user.config); v2+ uses settings.json
# directly under this directory.
SETTINGS_DIR=""" + settingsDir + @"""
NEW_SETTINGS_FILE=""$SETTINGS_DIR/settings.json""

if [ ! -f ""$UPDATER_PATH"" ]; then
    echo ""[Upgrade] ERROR: Updater archive not found: $UPDATER_PATH""
    exit 1
fi

mkdir -p ""$DESTINATION_DIR""

# ---------------------------------------------------------------------------
# One-time migration: legacy version-scoped user.config -> settings.json
# ---------------------------------------------------------------------------
migrate_legacy_settings() {
    if [ -f ""$NEW_SETTINGS_FILE"" ]; then
        echo ""[Upgrade] Updater settings already migrated ($NEW_SETTINGS_FILE), skipping""
        return 0
    fi

    local legacy
    legacy=$(find ""$SETTINGS_DIR"" -path '*/1.0.0.0/user.config' -type f 2>/dev/null | head -n 1)
    if [ -z ""$legacy"" ]; then
        echo ""[Upgrade] No legacy updater settings found under $SETTINGS_DIR, nothing to migrate""
        return 0
    fi

    echo ""[Upgrade] Migrating legacy updater settings from $legacy""

    extract_setting() {
        sed -n ""/<setting name=\""$1\""/,/<\/setting>/p"" ""$legacy"" \
            | sed -n 's|.*<value>\(.*\)</value>.*|\1|p' \
            | head -n 1
    }
    json_escape() {
        printf '%s' ""$1"" | sed -e 's|\\|\\\\|g' -e 's|""|\\""|g'
    }
    to_json_bool() {
        case ""$(printf '%s' ""$1"" | tr '[:upper:]' '[:lower:]')"" in
            true) echo ""true"" ;;
            false) echo ""false"" ;;
            *) echo ""$2"" ;;
        esac
    }

    local client_app_path update_server app_name
    local auto_reboot progress_fullscreen enable_prerelease

    client_app_path=$(extract_setting ClientAppPath)
    update_server=$(extract_setting UpdateServer)
    app_name=$(extract_setting AppName)
    auto_reboot=$(extract_setting AutoReboot)
    progress_fullscreen=$(extract_setting ProgressFullscreen)
    enable_prerelease=$(extract_setting EnablePreReleaseVersions)

    mkdir -p ""$SETTINGS_DIR""
    cat > ""$NEW_SETTINGS_FILE"" <<EOF
{
  ""ClientAppPath"": ""$(json_escape ""$client_app_path"")"",
  ""UpdateServer"": ""$(json_escape ""$update_server"")"",
  ""AppName"": ""$(json_escape ""$app_name"")"",
  ""LastVersion"": null,
  ""AutoReboot"": $(to_json_bool ""$auto_reboot"" ""false""),
  ""ProgressFullscreen"": $(to_json_bool ""$progress_fullscreen"" ""true""),
  ""EnablePreReleaseVersions"": $(to_json_bool ""$enable_prerelease"" ""false"")
}
EOF
    echo ""[Upgrade] Wrote migrated updater settings to $NEW_SETTINGS_FILE""
}

if ! migrate_legacy_settings; then
    echo ""[Upgrade] WARNING: legacy settings migration encountered an error, continuing anyway""
fi
";

            if (IsHemoCheckInApp(appName))
            {
                script += @"

# ---------------------------------------------------------------------------
# HemoCheckIn: service user hemo must own updater config under SETTINGS_DIR.
# Run once after migration (covers root-owned settings even if tar fails later).
# ---------------------------------------------------------------------------
if id hemo >/dev/null 2>&1; then
    mkdir -p ""$SETTINGS_DIR"" 2>/dev/null || true
    if chown -R hemo:hemo ""$SETTINGS_DIR"" 2>/dev/null; then
        echo ""[Upgrade] Ownership of $SETTINGS_DIR set to hemo:hemo""
    else
        echo ""[Upgrade] WARNING: chown hemo:hemo on $SETTINGS_DIR failed (insufficient privileges?)""
    fi
else
    echo ""[Upgrade] WARNING: user hemo not found; skipped chown on updater config dir""
fi
";
            }

            script += @"
echo ""[Upgrade] Extracting updater archive...""
tar -xzf ""$UPDATER_PATH"" -C ""$DESTINATION_DIR""
rc=$?

if [ $rc -eq 0 ]; then
    echo ""[Upgrade] Extraction for updater completed successfully.""
    echo ""[Upgrade] Files are located in $DESTINATION_DIR""
else
    echo ""[Upgrade] ERROR: Extraction failed.""
    exit 1
fi
";

            if (hasBootstrap)
            {
                script += @"
BOOTSTRAP_PATH=""/home/hemo/$APP_FOLDER/upgrade/bootstrap-updater.sh""
BOOTSTRAP_DEST=""/home/hemo/bootstrap-updater.sh""

if [ -f ""$BOOTSTRAP_PATH"" ]; then
    echo ""[Upgrade] Installing bootstrap-updater.sh...""
    cp ""$BOOTSTRAP_PATH"" ""$BOOTSTRAP_DEST""
    chmod +x ""$BOOTSTRAP_DEST""
    echo ""[Upgrade] bootstrap-updater.sh installed.""
fi
";
            }

            if (hasSetup)
            {
                script += @"
SETUP_PATH=""/home/hemo/$APP_FOLDER/upgrade/setup-bootstrap.sh""
SETUP_DEST=""/home/hemo/setup-bootstrap.sh""

if [ -f ""$SETUP_PATH"" ]; then
    echo ""[Upgrade] Running setup-bootstrap.sh...""
    cp ""$SETUP_PATH"" ""$SETUP_DEST""
    chmod +x ""$SETUP_DEST""
    /bin/bash ""$SETUP_DEST""
    
    # Check result?
    if [ $? -eq 0 ]; then
         echo ""[Upgrade] setup-bootstrap.sh completed.""
         rm -f ""$SETUP_DEST"" 
    else
         echo ""[Upgrade] WARNING: setup-bootstrap.sh failed.""
    fi
fi
";
            }

            if (IsHemoCheckInApp(appName))
            {
                script += @"

# ---------------------------------------------------------------------------
# HemoCheckIn: upgrade often runs as root; tar/bootstrap leave root-owned files
# under the CheckIn app tree and updater install — chown so hemo can run and update.
# (Updater JSON/XDG config is handled after migration, above.)
# ---------------------------------------------------------------------------
if id hemo >/dev/null 2>&1; then
    APP_ROOT=""/home/hemo/$APP_FOLDER""
    for target in ""$DESTINATION_DIR"" ""$APP_ROOT"" ""/home/hemo/bootstrap-updater.sh""; do
        if [ -e ""$target"" ]; then
            if chown -R hemo:hemo ""$target"" 2>/dev/null; then
                echo ""[Upgrade] Ownership of $target set to hemo:hemo""
            else
                echo ""[Upgrade] WARNING: chown hemo:hemo on $target failed (insufficient privileges?)""
            fi
        fi
    done
else
    echo ""[Upgrade] WARNING: user hemo not found; skipped chown on app/updater/bootstrap paths""
fi
";
            }

            script += @"
# Note: The upgrade folder will be removed by the startup script after this script completes
";
            return script;
        }
    }
}

