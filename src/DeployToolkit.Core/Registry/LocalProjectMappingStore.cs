using System.Text.Json;

namespace DeployToolkit.Core.Registry;

/// <summary>
/// Remembers which local git folder maps to which registry ComponentId, on
/// THIS machine only. Per the plan: "the Packager keeps a small local
/// mapping (folder path -> ComponentId), since paths are machine-specific
/// and shouldn't live in the shared registry." First time a folder is
/// selected there's no entry yet, so the caller (PackageBuilder) prompts to
/// create/select a component and this store persists the choice for next
/// time.
/// </summary>
public interface ILocalProjectMappingStore
{
    Task<string?> FindComponentIdAsync(string localFolderPath);
    Task RememberAsync(string localFolderPath, string componentId);
}

public sealed class JsonFileProjectMappingStore : ILocalProjectMappingStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileProjectMappingStore(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    private static string Normalize(string folderPath) =>
        Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public async Task<string?> FindComponentIdAsync(string localFolderPath)
    {
        var map = await LoadAsync();
        return map.TryGetValue(Normalize(localFolderPath), out var componentId) ? componentId : null;
    }

    public async Task RememberAsync(string localFolderPath, string componentId)
    {
        await _lock.WaitAsync();
        try
        {
            var map = await LoadAsync();
            map[Normalize(localFolderPath)] = componentId;
            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, string>> LoadAsync()
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, string>();

        var json = await File.ReadAllTextAsync(_filePath);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }
}
