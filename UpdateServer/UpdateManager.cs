using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace UpdateServer
{
    public class UpdateManager
    {
        private readonly IOptionsMonitor<Dictionary<string, string>> dicts;

        public const string PREFIX_FOLDER = "apps";

        public UpdateManager(IOptionsMonitor<Dictionary<string, string>> dicts)
        {
            this.dicts = dicts;
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

        public string GetUpdateFileForApp(string appName)
        {
            var baseAppPath = GetFolder(appName);
            if (string.IsNullOrWhiteSpace(baseAppPath))
            {
                return null;
            }
            var filePath = GetLatestAppFile(baseAppPath);
            return filePath;
        }

        /// <summary>
        /// Check the latest version of the update file against the requested value.
        /// Return true if up-to-date, otherwise, return false.
        /// </summary>
        /// <param name="appFolder"></param>
        /// <param name="check"></param>
        /// <returns>true if up-to-date, otherwise, false</returns>
        public bool CheckVersion(string appFolder, CheckRequest check)
        {
            var filePath = GetLatestAppFile(appFolder);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return true;
            }
            var latest = new FileInfo(filePath);

            if (check.Version == null && check.Modified == null)
            {
                return false;
            }

            var splits = Path.GetFileNameWithoutExtension(latest.Name).Split('-');
            var latestVersion = splits.Length > 1 ? splits.Last().Replace(".tar", "") : null;
            if (latestVersion != null && !string.IsNullOrWhiteSpace(check.Version) 
                && Version.Parse(check.Version) < Version.Parse(latestVersion))
            {
                return false;
            }

            if (latest.LastWriteTimeUtc.TrimMilliseconds() > check.Modified.Value.UtcDateTime)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(check.Checksum))
            {
                var checksum = GetMD5HashFromFile(latest.FullName);
                return checksum.Equals(check.Checksum, StringComparison.InvariantCultureIgnoreCase);
            }

            return true;
        }

        private string GetLatestAppFile(string appFolder)
        {
            var fileList = Directory.EnumerateFiles(appFolder)
                .OrderByDescending(x => x)
                .ThenByDescending(x => new FileInfo(Path.Combine(appFolder, x)).LastWriteTimeUtc)
                .ToList();

            if (fileList.Count == 0)
            {
                return null;
            }

            string latest = fileList.First();
            return latest;
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

    }
}
