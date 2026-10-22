using System;
using Godot;

/// <summary>
/// The whitebox sizes and colours of what the game draws as plain boxes (M6e): depots, stations, buildings and the marks of an access point. They are read from
/// <c>data/whitebox.json</c> so that art can replace the boxes later and keep the footprints; a missing file or key leaves the value below.
/// </summary>
public sealed class Whitebox
{
    /// <summary>A box on a node: its size, where its middle stands from the node on the ground plane, and its colour.</summary>
    public sealed record Block(Vector3 Size, Vector2 Offset, Color Color);

    /// <summary>A depot by level (Simple, Workshop, Complex): the same footprint, a taller and darker box for each step up (the upgrade grows it). Offset: where a depot on a node stands from the node.</summary>
    public Block[] Depots =
    {
        new(new Vector3(22f, 4f, 22f), new Vector2(26f, 26f), new Color(0.92f, 0.45f, 0.38f)),
        new(new Vector3(22f, 6f, 22f), new Vector2(26f, 26f), new Color(0.85f, 0.25f, 0.2f)),
        new(new Vector3(22f, 9f, 22f), new Vector2(26f, 26f), new Color(0.65f, 0.15f, 0.15f)),
    };
    public Block Depot => Depots[1];
    /// <summary>The colour of a depot no road reaches.</summary>
    public Color DepotIdle = new(0.5f, 0.5f, 0.52f);
    /// <summary>A stop on the road: Size is the slab on the lane (length along it, thickness, width), Offset is unused.</summary>
    public Block Station = new(new Vector3(14f, 0.3f, 3.6f), Vector2.Zero, new Color(0.2f, 0.45f, 0.85f));
    public float StationPostHeight = 5f;
    public Block RailDepot = new(new Vector3(16f, 5f, 12f), new Vector2(16f, 16f), new Color(0.72f, 0.28f, 0.22f));
    public Color GatewayRoad = new(0.30f, 0.55f, 0.85f), GatewayRail = new(0.80f, 0.55f, 0.25f);
    /// <summary>Home, commerce, industry.</summary>
    public Color[] Buildings = { new(0.80f, 0.64f, 0.46f), new(0.38f, 0.58f, 0.86f), new(0.62f, 0.62f, 0.66f) };
    public float PadSize = 7f, DoorWidth = 9f, DoorHeight = 4f, MarkerHeight = 5f;
    public Color PadColor = new(0.5f, 0.44f, 0.34f), DoorColor = new(0.1f, 0.1f, 0.12f), MarkerColor = new(0.95f, 0.78f, 0.45f);

    public static Whitebox Load(string path = "res://data/whitebox.json")
    {
        var box = new Whitebox();
        if (!FileAccess.FileExists(path)) return box;
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        var parsed = Json.ParseString(file.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary) return box;
        var root = parsed.AsGodotDictionary();
        if (Dict(root, "depot") is { } depot)
        {
            var offset = Numbers(depot, "offset", 2);
            if (depot.TryGetValue("levels", out var levels) && levels.VariantType == Variant.Type.Array)
            {
                var array = levels.AsGodotArray();
                for (int i = 0; i < Math.Min(3, array.Count); i++)
                {
                    if (array[i].VariantType != Variant.Type.Dictionary) continue;
                    var d = array[i].AsGodotDictionary();
                    var block = box.Depots[i];
                    var size = Numbers(d, "size", 3);
                    box.Depots[i] = new Block(size is null ? block.Size : new Vector3(size[0], size[1], size[2]), offset is null ? block.Offset : new Vector2(offset[0], offset[1]), Colour(d, "color", block.Color));
                }
            }
            box.DepotIdle = Colour(depot, "idle", box.DepotIdle);
        }
        box.Station = Read(root, "station", box.Station);
        if (Dict(root, "station") is { } stop) box.StationPostHeight = Number(stop, "postHeight", box.StationPostHeight);
        box.RailDepot = Read(root, "railDepot", box.RailDepot);
        if (Dict(root, "gateway") is { } gateway)
        {
            box.GatewayRoad = Colour(gateway, "road", box.GatewayRoad);
            box.GatewayRail = Colour(gateway, "rail", box.GatewayRail);
        }
        if (Dict(root, "buildings") is { } buildings)
        {
            box.Buildings = new[] { Colour(buildings, "home", box.Buildings[0]), Colour(buildings, "commerce", box.Buildings[1]), Colour(buildings, "industry", box.Buildings[2]) };
        }
        if (Dict(root, "access") is { } access)
        {
            box.PadSize = Number(access, "padSize", box.PadSize);
            box.DoorWidth = Number(access, "doorWidth", box.DoorWidth);
            box.DoorHeight = Number(access, "doorHeight", box.DoorHeight);
            box.MarkerHeight = Number(access, "markerHeight", box.MarkerHeight);
            box.PadColor = Colour(access, "padColor", box.PadColor);
            box.DoorColor = Colour(access, "doorColor", box.DoorColor);
            box.MarkerColor = Colour(access, "markerColor", box.MarkerColor);
        }
        return box;
    }

    static Godot.Collections.Dictionary? Dict(Godot.Collections.Dictionary from, string key) =>
        from.TryGetValue(key, out var v) && v.VariantType == Variant.Type.Dictionary ? v.AsGodotDictionary() : null;

    static float Number(Godot.Collections.Dictionary from, string key, float fallback) =>
        from.TryGetValue(key, out var v) && v.VariantType is Variant.Type.Float or Variant.Type.Int ? (float)v.AsDouble() : fallback;

    static float[]? Numbers(Godot.Collections.Dictionary from, string key, int count)
    {
        if (!from.TryGetValue(key, out var v) || v.VariantType != Variant.Type.Array) return null;
        var array = v.AsGodotArray();
        if (array.Count != count) return null;
        var result = new float[count];
        for (int i = 0; i < count; i++) result[i] = (float)array[i].AsDouble();
        return result;
    }

    static Color Colour(Godot.Collections.Dictionary from, string key, Color fallback) =>
        Numbers(from, key, 3) is { } c ? new Color(c[0], c[1], c[2]) : fallback;

    static Block Read(Godot.Collections.Dictionary root, string key, Block fallback)
    {
        if (Dict(root, key) is not { } d) return fallback;
        var size = Numbers(d, "size", 3);
        var offset = Numbers(d, "offset", 2);
        return new Block(size is null ? fallback.Size : new Vector3(size[0], size[1], size[2]), offset is null ? fallback.Offset : new Vector2(offset[0], offset[1]), Colour(d, "color", fallback.Color));
    }
}
