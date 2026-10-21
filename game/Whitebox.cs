using Godot;

/// <summary>
/// The whitebox sizes and colours of what the game draws as plain boxes (M6e): depots, stations, buildings and the marks of an access point. They are read from
/// <c>data/whitebox.json</c> so that art can replace the boxes later and keep the footprints; a missing file or key leaves the value below.
/// </summary>
public sealed class Whitebox
{
    /// <summary>A box on a node: its size, where its middle stands from the node on the ground plane, and its colour.</summary>
    public sealed record Block(Vector3 Size, Vector2 Offset, Color Color);

    public Block Depot = new(new Vector3(22f, 6f, 22f), new Vector2(26f, 26f), new Color(0.85f, 0.25f, 0.2f));
    public Block Station = new(new Vector3(14f, 4f, 10f), new Vector2(-24f, -24f), new Color(0.2f, 0.45f, 0.85f));
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
        box.Depot = Read(root, "depot", box.Depot);
        box.Station = Read(root, "station", box.Station);
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
