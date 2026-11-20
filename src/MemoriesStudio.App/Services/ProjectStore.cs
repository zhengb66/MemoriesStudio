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
    {
        // 属性面板选中图片片段时会预置一条空字幕（Text 为空，方便直接打字使用）。
        // 用户没输入文字就保存时，这条空字幕不应写进工程：预览与导出本就按 IsValid 过滤，
        // 留着只会让工程文件出现"没加字幕却有字幕记录"的噪音。
        // 序列化前临时摘除、序列化后原样恢复，不影响运行中的 UI 绑定与撤销快照。
        var removed = new List<(List<Caption> List, int Index, Caption Cap)>();
        foreach (var c in project.Clips)
        {
            for (var i = c.Captions.Count - 1; i >= 0; i--)
            {
                if (c.Captions[i].IsValid) continue;
                removed.Add((c.Captions, i, c.Captions[i]));
                c.Captions.RemoveAt(i);
            }
        }
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(project, Options));
        }
        finally
        {
            // removed 按每个列表的 index 降序收集，倒序插回才能还原原位。
            for (var k = removed.Count - 1; k >= 0; k--)
            {
                var (list, index, cap) = removed[k];
                list.Insert(index, cap);
            }
        }
    }

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
