using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Windmill;

/// <summary>Rectangle in 0..1 coordinates relative to a monitor work area.</summary>
internal readonly record struct NRect(double X, double Y, double W, double H)
{
    public double Area => W * H;
    public bool Contains(double x, double y) => x >= X && x < X + W && y >= Y && y < Y + H;
}

/// <param name="Angle">Math angle in degrees (0 = east, 90 = north) for radial slots; null for plain zones.</param>
/// <param name="Inner">Radial only: drawn as a separate arc on the inner ring instead of the main ring.</param>
internal sealed record Zone(NRect Rect, double? Angle = null, bool Maximize = false, bool Inner = false);

/// <summary>Radial menu radii in pixels at 96 dpi. The centre (below Inner) selects nothing; the inner arc is a separate sub-menu.</summary>
internal static class RadialMetrics
{
    /// <summary>User-chosen size multiplier (Settings.RingSizePercent / 100); set on settings load.</summary>
    public static double Scale = 1;

    public static double Inner => 40 * Scale;      // empty centre
    public static double Split => 80 * Scale;      // outer edge of the inner "full screen" arc
    public static double RingStart => 94 * Scale;  // main ring, separated from the inner arc by a gap
    public static double Outer => 158 * Scale;
}

/// <summary>Miniature zone map shown under the cursor for non-radial layouts, in pixels at 96 dpi.</summary>
internal static class MiniMetrics
{
    public const double Size = 280;     // longer side of the map; the shorter follows the work area's aspect ratio
    public const double Margin = 16;    // keep this far from the monitor edges
    public const double Below = 96;     // room under the map for the layout name and hints
}

internal sealed class Layout
{
    public string Name { get; init; } = "";
    public bool Radial { get; init; }
    public List<Zone> Zones { get; init; } = new();
}

internal static class Layouts
{
    public static List<Layout> LoadAll(Settings settings)
    {
        var list = new List<Layout> { Radial() };
        list.AddRange(BuiltIn());
        if (settings.IncludeFancyZones)
            list.AddRange(FancyZones.Load());
        return list;
    }

    static Layout Radial() => new()
    {
        Name = "Radial",
        Radial = true,
        Zones =
        {
            new(new(0.5, 0, 0.5, 1), 0),
            new(new(0.5, 0, 0.5, 0.5), 45),
            new(new(0, 0, 1, 0.5), 90),
            new(new(0, 0, 0.5, 0.5), 135),
            new(new(0, 0, 0.5, 1), 180),
            new(new(0, 0.5, 0.5, 0.5), 225),
            new(new(0, 0.5, 1, 0.5), 270),
            new(new(0.5, 0.5, 0.5, 0.5), 315),
            new(new(0, 0, 1, 1), 90, Maximize: true, Inner: true), // short push up = full screen
        },
    };

    // Presets mirror the Windows 11 snap layouts.
    static IEnumerable<Layout> BuiltIn()
    {
        yield return Grid("Halves", new[] { 1.0, 1 }, new[] { 1.0 });
        yield return Grid("Thirds", new[] { 1.0, 1, 1 }, new[] { 1.0 });
        yield return Grid("Priority Columns", new[] { 1.0, 2, 1 }, new[] { 1.0 });
        yield return Grid("Left 2/3 + Right 1/3", new[] { 2.0, 1 }, new[] { 1.0 });
        yield return Grid("Left 1/3 + Right 2/3", new[] { 1.0, 2 }, new[] { 1.0 });
        yield return Grid("Rows", new[] { 1.0 }, new[] { 1.0, 1 });
        yield return Grid("Quarters", new[] { 1.0, 1 }, new[] { 1.0, 1 });
        yield return Grid("Grid 3x2", new[] { 1.0, 1, 1 }, new[] { 1.0, 1 });
        yield return new Layout
        {
            Name = "Main + Stack",
            Zones =
            {
                new(new(0, 0, 0.5, 1)),
                new(new(0.5, 0, 0.5, 0.5)),
                new(new(0.5, 0.5, 0.5, 0.5)),
            },
        };
    }

    static Layout Grid(string name, double[] cols, double[] rows)
    {
        var xs = Edges(cols);
        var ys = Edges(rows);
        var layout = new Layout { Name = name };
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < cols.Length; c++)
                layout.Zones.Add(new(new(xs[c], ys[r], xs[c + 1] - xs[c], ys[r + 1] - ys[r])));
        return layout;
    }

    public static double[] Edges(IReadOnlyList<double> weights)
    {
        double total = weights.Sum();
        var edges = new double[weights.Count + 1];
        double acc = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            acc += weights[i] / total;
            edges[i + 1] = acc;
        }
        edges[^1] = 1;
        return edges;
    }
}

/// <summary>Reads custom layouts that PowerToys FancyZones has saved for this user.</summary>
internal static class FancyZones
{
    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "PowerToys", "FancyZones", "custom-layouts.json");

    public static List<Layout> Load()
    {
        var result = new List<Layout>();
        try
        {
            if (!File.Exists(FilePath)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (!doc.RootElement.TryGetProperty("custom-layouts", out var items)) return result;

            foreach (var item in items.EnumerateArray())
            {
                var name = "FZ: " + (item.TryGetProperty("name", out var n) ? n.GetString() : "Custom");
                var type = item.GetProperty("type").GetString();
                var info = item.GetProperty("info");
                var layout = type == "grid" ? FromGrid(name, info) : FromCanvas(name, info);
                if (layout.Zones.Count > 0) result.Add(layout);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // A malformed or locked FancyZones file shouldn't stop Windmill; built-in layouts still work.
        }
        return result;
    }

    static Layout FromGrid(string name, JsonElement info)
    {
        var colWeights = info.GetProperty("columns-percentage").EnumerateArray().Select(e => e.GetDouble()).ToList();
        var rowWeights = info.GetProperty("rows-percentage").EnumerateArray().Select(e => e.GetDouble()).ToList();
        var xs = Layouts.Edges(colWeights);
        var ys = Layouts.Edges(rowWeights);

        // cell-child-map[row][col] holds the zone index occupying that cell.
        var bounds = new SortedDictionary<int, (int r0, int r1, int c0, int c1)>();
        int row = 0;
        foreach (var rowEl in info.GetProperty("cell-child-map").EnumerateArray())
        {
            int col = 0;
            foreach (var cell in rowEl.EnumerateArray())
            {
                int id = cell.GetInt32();
                bounds[id] = bounds.TryGetValue(id, out var b)
                    ? (Math.Min(b.r0, row), Math.Max(b.r1, row), Math.Min(b.c0, col), Math.Max(b.c1, col))
                    : (row, row, col, col);
                col++;
            }
            row++;
        }

        var layout = new Layout { Name = name };
        foreach (var b in bounds.Values)
            layout.Zones.Add(new(new(xs[b.c0], ys[b.r0], xs[b.c1 + 1] - xs[b.c0], ys[b.r1 + 1] - ys[b.r0])));
        return layout;
    }

    static Layout FromCanvas(string name, JsonElement info)
    {
        double refW = info.GetProperty("ref-width").GetDouble();
        double refH = info.GetProperty("ref-height").GetDouble();
        var layout = new Layout { Name = name };
        foreach (var z in info.GetProperty("zones").EnumerateArray())
        {
            layout.Zones.Add(new(new(
                z.GetProperty("X").GetDouble() / refW,
                z.GetProperty("Y").GetDouble() / refH,
                z.GetProperty("width").GetDouble() / refW,
                z.GetProperty("height").GetDouble() / refH)));
        }
        return layout;
    }
}
