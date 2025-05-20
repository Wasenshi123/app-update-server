using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace UpdateServer
{
    public class AppVersion : IComparable<AppVersion>
    {
        public Version Version { get; }
        public string PreRelease { get; }
        public string CommitSha { get; }
        public bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

        public AppVersion(Version version, string preRelease = null, string commitSha = null)
        {
            Version = version;
            PreRelease = preRelease;
            CommitSha = commitSha;
        }

        public static AppVersion Parse(string versionString)
        {
            if (string.IsNullOrEmpty(versionString))
                return null;

            // Remove v prefix if present
            if (versionString.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                versionString = versionString.Substring(1);

            // Check for pre-release pattern: x.x.x-alpha.commitsha or x.x.x-beta.commitsha
            var match = Regex.Match(versionString, @"^(\d+\.\d+\.\d+(?:\.\d+)?)-([a-zA-Z]+)\.([a-z0-9]+)$");
            if (match.Success)
            {
                var version = Version.Parse(match.Groups[1].Value);
                var preRelease = match.Groups[2].Value.ToLowerInvariant();
                var commitSha = match.Groups[3].Value;
                return new AppVersion(version, preRelease, commitSha);
            }

            // Standard version
            try
            {
                return new AppVersion(Version.Parse(versionString));
            }
            catch
            {
                return null;
            }
        }

        public int CompareTo(AppVersion other)
        {
            if (other == null) return 1;
            
            // Compare standard version part first
            int versionComparison = Version.CompareTo(other.Version);
            if (versionComparison != 0)
                return versionComparison;

            // Same version, now handle pre-release logic
            // No pre-release is higher than any pre-release
            if (!IsPreRelease && other.IsPreRelease) return 1;
            if (IsPreRelease && !other.IsPreRelease) return -1;
            if (!IsPreRelease && !other.IsPreRelease) return 0;

            // Both are pre-releases, compare by type (release > rc > beta > alpha)
            int preReleaseComparison = ComparePreReleaseType(PreRelease, other.PreRelease);
            if (preReleaseComparison != 0)
                return preReleaseComparison;

            // Same pre-release type, use commit sha or timestamp as tiebreaker
            // For simplicity we'll rely on timestamp comparison elsewhere
            return 0;
        }

        private int ComparePreReleaseType(string a, string b)
        {
            // Define order of pre-release types
            var order = new Dictionary<string, int>
            {
                { "alpha", 0 },
                { "beta", 1 },
                { "rc", 2 },
                { "preview", 1 }  // Same as beta
            };

            int aValue = order.ContainsKey(a) ? order[a] : -1;
            int bValue = order.ContainsKey(b) ? order[b] : -1;
            
            return aValue.CompareTo(bValue);
        }

        public override string ToString()
        {
            if (IsPreRelease)
                return $"{Version}-{PreRelease}.{CommitSha}";
            return Version.ToString();
        }
    }

    public class AppUpdateFileInfo
    {
        public string FilePath { get; set; }
        public AppVersion Version { get; set; }
        public DateTime LastModified { get; set; }
        public bool IsPreRelease => Version?.IsPreRelease ?? false;
    }

    public class AppUpdateInfo
    {
        public AppUpdateFileInfo LatestStable { get; set; }
        public AppUpdateFileInfo LatestPreRelease { get; set; }
    }

    public class UpdateManager
    {
        private readonly IOptionsMonitor<Dictionary<string, string>> dicts;
        private readonly ILogger<UpdateManager> logger;
        public const string PREFIX_FOLDER = "apps";

        public UpdateManager(IOptionsMonitor<Dictionary<string, string>> dicts, ILogger<UpdateManager> logger)
        {
            this.dicts = dicts;
            this.logger = logger;
        }

        /// <summary>
        /// Get the physical folder of the app's update file
        /// </summary>
        /// <param name="appName"></param>
        /// <returns></returns>
        public string GetFolder(string appName)
        {
            string appFolder = null;
            var appNameDict = dicts.Get("AppNames");
            if (appNameDict.ContainsKey(appName))
            {
                appFolder = Path.Combine(PREFIX_FOLDER, appNameDict[appName]);
            }
            else if (Directory.Exists(Path.Combine(PREFIX_FOLDER, appName)))
            {
                appFolder = Path.Combine(PREFIX_FOLDER, appName);
            }

            if (appFolder != null)
            {
                appFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, appFolder);
            }

            return appFolder;
        }

        public string GetUpdateFileForApp(string appName, bool includePrerelease = true)
        {
            var baseAppPath = GetFolder(appName);
            if (string.IsNullOrWhiteSpace(baseAppPath))
            {
                return null;
            }
            var filePath = GetLatestAppFile(baseAppPath, includePrerelease);
            return filePath;
        }

        /// <summary>
        /// Check the latest version of the update file against the requested value.
        /// Return true if up-to-date, otherwise, return false.
        /// </summary>
        /// <param name="appFolder"></param>
        /// <param name="check"></param>
        /// <returns>true if up-to-date, otherwise, false</returns>
        public bool CheckVersion(string appFolder, CheckRequest check, bool includePrerelease = true)
        {
            var filePath = GetLatestAppFile(appFolder, includePrerelease);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return true;
            }
            var latest = new FileInfo(filePath);

            if (check.Version == null && check.Modified == null)
            {
                return false;
            }

            var latestAppVersion = GetFileVersionFromName(latest);
            var clientAppVersion = AppVersion.Parse(check.Version);

            if (latestAppVersion != null && clientAppVersion != null && latestAppVersion.CompareTo(clientAppVersion) > 0)
            {
                logger.LogInformation("latest version: {version}", latestAppVersion);
                return false;
            }

            var latestExeVersion = GetFileVersion(filePath);
            if (latestExeVersion != null && clientAppVersion != null && 
                new AppVersion(latestExeVersion).CompareTo(clientAppVersion) > 0)
            {
                logger.LogInformation("latest version: {version}", latestExeVersion);
                return false;
            }

            logger.LogInformation("Checking modified & checksum..");

            if (latest.LastWriteTimeUtc.TrimMilliseconds() > check.Modified.Value.UtcDateTime)
            {
                logger.LogInformation("File on server is newer.");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(check.Checksum))
            {
                var checksum = GetMD5HashFromFile(latest.FullName);
                return checksum.Equals(check.Checksum, StringComparison.InvariantCultureIgnoreCase);
            }

            return true;
        }

        // Overload for backward compatibility
        public bool CheckVersion(string appFolder, CheckRequest check)
        {
            return CheckVersion(appFolder, check, true);
        }

        private static string GetLatestAppFile(string appFolder, bool includePrerelease = true)
        {
            var files = Directory.EnumerateFiles(appFolder).ToList();
            if (files.Count == 0) return null;

            var fileInfos = files
                .Select(file => new
                {
                    FilePath = file,
                    Version = GetFileVersionFromName(new FileInfo(file)),
                    LastModified = new FileInfo(file).LastWriteTimeUtc
                })
                .Where(f => f.Version != null)
                .ToList();

            if (!includePrerelease)
            {
                fileInfos = fileInfos.Where(f => !f.Version.IsPreRelease).ToList();
            }

            var latest = fileInfos
                .OrderByDescending(f => f.Version)
                .ThenByDescending(f => f.LastModified)
                .FirstOrDefault();

            return latest?.FilePath;
        }

        private static AppVersion GetFileVersionFromName(string filePath)
        {
            return GetFileVersionFromName(new FileInfo(filePath));
        }

        private static AppVersion GetFileVersionFromName(FileInfo fileInfo)
        {
            var fileName = Path.GetFileNameWithoutExtension(fileInfo.Name)
                .Replace(".tar", "")
                .Replace(".gz", "")
                .Replace(".exe", "");

            // First try to match pre-release pattern like app-1.2.3-beta.02ba741
            var match = Regex.Match(fileName, @".*-(\d+\.\d+\.\d+(?:\.\d+)?-[a-zA-Z]+\.[a-z0-9]+)$");
            if (match.Success)
            {
                return AppVersion.Parse(match.Groups[1].Value);
            }
            
            // Then try standard version pattern like app-1.2.3
            match = Regex.Match(fileName, @".*-(\d+\.\d+\.\d+(?:\.\d+)?)$");
            if (match.Success)
            {
                return AppVersion.Parse(match.Groups[1].Value);
            }

            // Try looking for v prefix version like app-v1.2.3
            match = Regex.Match(fileName, @".*-(v\d+\.\d+\.\d+(?:\.\d+)?)$");
            if (match.Success)
            {
                return AppVersion.Parse(match.Groups[1].Value);
            }

            return null;
        }

        private static Version GetFileVersion(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || new FileInfo(filePath).Extension != ".exe")
            {
                return null;
            }
            var fileInfo = FileVersionInfo.GetVersionInfo(filePath);
            string versionStr = fileInfo.ProductVersion ?? fileInfo.FileVersion;
            if (versionStr == null)
            {
                return null;
            }
            return Version.Parse(versionStr);
        }

        private static string GetMD5HashFromFile(string fileName)
        {
            using (var md5 = MD5.Create())
            {
                using (var stream = File.OpenRead(fileName))
                {
                    return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", string.Empty);
                }
            }
        }

        // Returns both the latest stable and pre-release update files for an app
        public AppUpdateInfo GetLatestUpdateInfo(string appFolder)
        {
            var files = Directory.EnumerateFiles(appFolder).ToList();
            if (files.Count == 0) return null;

            var fileInfos = files
                .Select(file => new
                {
                    FilePath = file,
                    Version = GetFileVersionFromName(new FileInfo(file)),
                    LastModified = new FileInfo(file).LastWriteTimeUtc
                })
                .Where(f => f.Version != null)
                .ToList();

            var latestStable = fileInfos
                .Where(f => !f.Version.IsPreRelease)
                .OrderByDescending(f => f.Version)
                .ThenByDescending(f => f.LastModified)
                .FirstOrDefault();

            var latestPre = fileInfos
                .Where(f => f.Version.IsPreRelease)
                .OrderByDescending(f => f.Version)
                .ThenByDescending(f => f.LastModified)
                .FirstOrDefault();

            return new AppUpdateInfo
            {
                LatestStable = latestStable == null ? null : new AppUpdateFileInfo
                {
                    FilePath = latestStable.FilePath,
                    Version = latestStable.Version,
                    LastModified = latestStable.LastModified
                },
                LatestPreRelease = latestPre == null ? null : new AppUpdateFileInfo
                {
                    FilePath = latestPre.FilePath,
                    Version = latestPre.Version,
                    LastModified = latestPre.LastModified
                }
            };
        }
    }
}
