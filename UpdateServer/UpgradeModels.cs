using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UpdateServer
{
    public class UpgradeManifest
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("appliesTo")]
        public VersionRange AppliesTo { get; set; }

        [JsonPropertyName("targetVersion")]
        public string TargetVersion { get; set; }

        [JsonPropertyName("priority")]
        public int Priority { get; set; }

        [JsonPropertyName("dependencies")]
        public List<string> Dependencies { get; set; } = new List<string>();

        [JsonPropertyName("conflicts")]
        public List<string> Conflicts { get; set; } = new List<string>();

        [JsonPropertyName("storage")]
        public UpgradeStorage Storage { get; set; }

        [JsonPropertyName("files")]
        public List<UpgradeFileParams> Files { get; set; } = new List<UpgradeFileParams>();

        [JsonPropertyName("preInstallScript")]
        public string PreInstallScript { get; set; }

        [JsonPropertyName("postInstallScript")]
        public string PostInstallScript { get; set; }

        [JsonPropertyName("rollbackScript")]
        public string RollbackScript { get; set; }

        [JsonPropertyName("checksum")]
        public UpgradeChecksum Checksum { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class VersionRange
    {
        [JsonPropertyName("minVersion")]
        public string MinVersion { get; set; }

        [JsonPropertyName("maxVersion")]
        public string MaxVersion { get; set; }

        [JsonPropertyName("excludeVersions")]
        public List<string> ExcludeVersions { get; set; }
    }

    public class UpgradeStorage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("basePath")]
        public string BasePath { get; set; }

        [JsonPropertyName("path")]
        public string Path { get; set; }
    }



    public class UpgradeFileParams
    {
        [JsonPropertyName("path")]
        public string Path { get; set; }

        [JsonPropertyName("target")]
        public string Target { get; set; }

        [JsonPropertyName("permissions")]
        public string Permissions { get; set; }

        [JsonPropertyName("required")]
        public bool IsRequired { get; set; }

        [JsonPropertyName("executable")]
        public bool IsExecutable { get; set; }

        [JsonPropertyName("explode")]
        public bool Explode { get; set; }

        [JsonPropertyName("backup")]
        public bool Backup { get; set; }

        [JsonPropertyName("runOrder")]
        public int RunOrder { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("checksum")]
        public string Checksum { get; set; }
    }

    public class UpgradeChecksum
    {
        [JsonPropertyName("algorithm")]
        public string Algorithm { get; set; }

        [JsonPropertyName("value")]
        public string Value { get; set; }
    }

    public class UpgradePackageManifest
    {
        [JsonPropertyName("fromVersion")]
        public string FromVersion { get; set; }

        [JsonPropertyName("toVersion")]
        public string ToVersion { get; set; }

        [JsonPropertyName("upgrades")]
        public List<string> Upgrades { get; set; } = new List<string>();
    }

    public class UpgradeInfoWrapper
    {
        [JsonPropertyName("currentVersion")]
        public string CurrentVersion { get; set; }

        [JsonPropertyName("targetVersion")]
        public string TargetVersion { get; set; }

        [JsonPropertyName("upgrades")]
        public List<UpgradeSummary> Upgrades { get; set; }

        [JsonPropertyName("packageSize")]
        public long PackageSize { get; set; }

        [JsonPropertyName("requiresDownload")]
        public bool RequiresDownload { get; set; }
    }

    public class UpgradeSummary
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("priority")]
        public int Priority { get; set; }
    }

    public class UpgradePackage
    {
        public string FilePath { get; set; }
        public AppVersion FromVersion { get; set; }
        public AppVersion ToVersion { get; set; }
        public List<UpgradeManifest> Upgrades { get; set; }
        public long EstimatedSize { get; set; }
    }

    public class ApplicableUpgradesResult
    {
        public string TargetVersion { get; set; }
        public List<UpgradeManifest> Upgrades { get; set; }
        public long EstimatedSize { get; set; }
    }
}
