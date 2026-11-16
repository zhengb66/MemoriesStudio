using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MemoriesStudio.Models;

namespace MemoriesStudio.Services;

public static class ProjectStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(ProjectFile project, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(project, Options));

    public static ProjectFile? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path, Encoding.UTF8);
        var p = JsonSerializer.Deserialize<ProjectFile>(json, Options);
        if (p is null) return null;
        p.Clips ??= new();
        return p;
    }
}
