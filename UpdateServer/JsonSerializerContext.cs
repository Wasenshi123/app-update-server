using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UpdateServer
{
    // Naming comes from [JsonPropertyName] on models only — avoid a second camelCase policy
    // so generated package JSON matches what the AppUpdater client expects.
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(UpgradeManifest))]
    [JsonSerializable(typeof(UpgradePackageManifest))]
    [JsonSerializable(typeof(VersionRange))]
    [JsonSerializable(typeof(UpgradeStorage))]
    [JsonSerializable(typeof(UpgradeFileParams))]
    [JsonSerializable(typeof(UpgradeChecksum))]
    [JsonSerializable(typeof(Dictionary<string, object>))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(long))]
    [JsonSerializable(typeof(bool))]
    [JsonSerializable(typeof(double))]
    public partial class UpdateServerJsonContext : JsonSerializerContext
    {
    }
}
