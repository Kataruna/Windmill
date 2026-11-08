using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Windmill;

/// <summary>
/// A user-made auto-arrange layout, kept as a tree of splits so the editor can drag dividers. A leaf is one window;
/// <see cref="Index"/> says which pick goes there (0 = first pick, the one that should get the most space).
/// </summary>
internal sealed class SplitNode
{
    public bool Vertical { get; set; } // true: A left | B right. false: A on top, B below
    public double Ratio { get; set; } = 0.5; // share of the space that A gets
    public SplitNode? A { get; set; }
    public SplitNode? B { get; set; }
    public int Index { get; set; } // leaves only

    /// <summary>Root only: what the snap menu and the editor call this layout.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }

    [JsonIgnore] public bool IsLeaf => A == null || B == null;
}

/// <summary>
/// Custom layouts, in %APPDATA%\Windmill\arrange-layouts.json. Written by the layout editor; used by both the snap
/// menu (as extra layouts) and auto-arrange (as extra arrangements for the same number of windows).
/// </summary>
internal static class ArrangeStore
{
    static string FilePath => Path.Combine(Settings.Directory, "arrange-layouts.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    static List<SplitNode>? _all;
    static Dictionary<int, List<NRect[]>> _byCount = new();

    public static List<SplitNode> All => _all ??= Load();

    static List<SplitNode> Load()
    {
        var list = new List<SplitNode>();
        try
        {
            if (File.Exists(FilePath))
                list = JsonSerializer.Deserialize<List<SplitNode>>(File.ReadAllText(FilePath), Json) ?? list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            list = new();
        }
        list = list.Where(n => n != null && Valid(n)).ToList(); // a hand-edited file can hold nulls
        Rebuild(list);
        return list;
    }

    // A hand-edited or damaged file must not crash the menu: drop layouts that don't make sense.
    static bool Valid(SplitNode n)
    {
        int count = Leaves(n).Count;
        if (count < 1 || count > Arranger.MaxWindows) return false;
        foreach (var node in Walk(n)) node.Ratio = double.IsFinite(node.Ratio) ? Math.Clamp(node.Ratio, 0.1, 0.9) : 0.5;
        var leaves = Leaves(n);
        if (!leaves.Select(l => l.leaf.Index).OrderBy(i => i).SequenceEqual(Enumerable.Range(0, count)))
            for (int i = 0; i < leaves.Count; i++) leaves[i].leaf.Index = i;
        return true;
    }

    /// <summary>Raised after the file is written, so the snap menu can pick up the new layouts.</summary>
    public static event Action? Saved;

    public static string DisplayName(SplitNode n, int i) => string.IsNullOrWhiteSpace(n.Name) ? $"Custom {i + 1}" : n.Name.Trim();

    /// <summary>Writes the file. Returns null on success, or why it failed (the layouts stay in use either way).</summary>
    public static string? Save()
    {
        var list = All;
        Rebuild(list);
        try
        {
            Directory.CreateDirectory(Settings.Directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
        finally { Saved?.Invoke(); } // in use from now on, even if the file couldn't be written
        return null;
    }

    /// <summary>Replaces every custom layout and writes the file; see <see cref="Save"/>.</summary>
    public static string? Replace(List<SplitNode> layouts)
    {
        _all = layouts;
        return Save();
    }

    public static string Serialize(List<SplitNode> layouts) => JsonSerializer.Serialize(layouts, Json);

    public static List<SplitNode> Deserialize(string json) => JsonSerializer.Deserialize<List<SplitNode>>(json, Json) ?? new();

    public static List<SplitNode> Clone(List<SplitNode> layouts) => Deserialize(Serialize(layouts));

    static void Rebuild(List<SplitNode> list) =>
        _byCount = list.GroupBy(n => Leaves(n).Count).ToDictionary(g => g.Key, g => g.Select(Slots).ToList());

    /// <summary>Custom layouts for this many windows.</summary>
    public static IReadOnlyList<NRect[]> For(int n)
    {
        _ = All;
        return _byCount.TryGetValue(n, out var l) ? l : Array.Empty<NRect[]>();
    }

    static IEnumerable<SplitNode> Walk(SplitNode n)
    {
        yield return n;
        if (n.IsLeaf) yield break;
        foreach (var c in Walk(n.A!).Concat(Walk(n.B!))) yield return c;
    }

    /// <summary>Leaves with the area each covers, top-left first.</summary>
    public static List<(SplitNode leaf, NRect rect)> Leaves(SplitNode root)
    {
        var result = new List<(SplitNode, NRect)>();
        void Go(SplitNode n, NRect r)
        {
            if (n.IsLeaf) { result.Add((n, r)); return; }
            var (a, b) = Halves(n, r);
            Go(n.A!, a);
            Go(n.B!, b);
        }
        Go(root, new NRect(0, 0, 1, 1));
        return result;
    }

    public static (NRect a, NRect b) Halves(SplitNode n, NRect r) => n.Vertical
        ? (new NRect(r.X, r.Y, r.W * n.Ratio, r.H), new NRect(r.X + r.W * n.Ratio, r.Y, r.W * (1 - n.Ratio), r.H))
        : (new NRect(r.X, r.Y, r.W, r.H * n.Ratio), new NRect(r.X, r.Y + r.H * n.Ratio, r.W, r.H * (1 - n.Ratio)));

    /// <summary>The slots in pick order.</summary>
    public static NRect[] Slots(SplitNode root) => Leaves(root).OrderBy(l => l.leaf.Index).Select(l => l.rect).ToArray();
}
