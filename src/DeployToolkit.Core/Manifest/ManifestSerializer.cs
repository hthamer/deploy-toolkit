using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeployToolkit.Core.Manifest;

/// <summary>
/// A plain, JSON-friendly mirror of <see cref="ComponentManifest"/>. We
/// serialize through this DTO rather than the domain type directly so the
/// on-disk manifest.json shape stays stable even as the domain model grows
/// (e.g. Status is deliberately NOT written to disk — it's registry-owned).
/// </summary>
internal sealed class ManifestDto
{
    /// <summary>Null for legacy manifests — omitted on disk when null
    /// (JsonIgnoreCondition.WhenWritingNull), absent in old zips.</summary>
    public string? PackageId { get; set; }
    public string ComponentId { get; set; } = "";
    public string Client { get; set; } = "";
    public string Component { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public string? GitCommitSha { get; set; }
    public string TargetFramework { get; set; } = "";
    public bool IsSelfContained { get; set; }
    public string? BaselineManifest { get; set; }
    public List<FileDto> Files { get; set; } = new();
    public List<string> DeletedFiles { get; set; } = new();
    public Dictionary<string, object?> AppSettingsDelta { get; set; } = new();
    public List<DbScriptDto> DbScripts { get; set; } = new();
    public string? HealthCheckUrl { get; set; }
    public List<string> AppliedMigrations { get; set; } = new();

    public sealed class FileDto
    {
        public string Path { get; set; } = "";
        public string Hash { get; set; } = "";
        public long SizeBytes { get; set; }
    }

    public sealed class DbScriptDto
    {
        public string File { get; set; } = "";

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DbScriptKind Kind { get; set; }
    }
}

public static class ManifestSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(ComponentManifest manifest)
    {
        var dto = new ManifestDto
        {
            PackageId = manifest.PackageId,
            ComponentId = manifest.ComponentId,
            Client = manifest.Client,
            Component = manifest.Component,
            Version = manifest.Version,
            CreatedUtc = manifest.CreatedUtc,
            GitCommitSha = manifest.GitCommitSha,
            TargetFramework = manifest.TargetFramework,
            IsSelfContained = manifest.IsSelfContained,
            BaselineManifest = manifest.BaselineManifest,
            Files = manifest.Files
                .Select(f => new ManifestDto.FileDto { Path = f.Path, Hash = f.Hash, SizeBytes = f.SizeBytes })
                .ToList(),
            DeletedFiles = manifest.DeletedFiles.ToList(),
            AppSettingsDelta = manifest.AppSettingsDelta.ToDictionary(kv => kv.Key, kv => kv.Value),
            DbScripts = manifest.DbScripts
                .Select(s => new ManifestDto.DbScriptDto { File = s.File, Kind = s.Kind })
                .ToList(),
            HealthCheckUrl = manifest.HealthCheckUrl,
            AppliedMigrations = manifest.AppliedMigrations.ToList(),
        };

        return JsonSerializer.Serialize(dto, Options);
    }

    public static ComponentManifest Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize<ManifestDto>(json, Options)
            ?? throw new InvalidDataException("manifest.json was empty or invalid.");

        return new ComponentManifest
        {
            PackageId = string.IsNullOrWhiteSpace(dto.PackageId) ? null : dto.PackageId,
            ComponentId = dto.ComponentId,
            Client = dto.Client,
            Component = dto.Component,
            Version = dto.Version,
            CreatedUtc = dto.CreatedUtc,
            GitCommitSha = dto.GitCommitSha,
            TargetFramework = dto.TargetFramework,
            IsSelfContained = dto.IsSelfContained,
            BaselineManifest = dto.BaselineManifest,
            Files = dto.Files.Select(f => new ManifestFile(f.Path, f.Hash, f.SizeBytes)).ToList(),
            DeletedFiles = dto.DeletedFiles,
            AppSettingsDelta = dto.AppSettingsDelta,
            DbScripts = dto.DbScripts.Select(s => new DbScriptRef(s.File, s.Kind)).ToList(),
            HealthCheckUrl = dto.HealthCheckUrl,
            AppliedMigrations = dto.AppliedMigrations,
        };
    }
}
