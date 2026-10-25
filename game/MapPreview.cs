using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>
/// The picture of the land on the new-game screen (R2): the same seed and settings as the game, made on a coarse grid
/// (<see cref="WorldBuilder.Preview"/>) and coloured by biome with a little relief shading. GDScript makes one with
/// <c>load("res://game/MapPreview.cs").new()</c>.
/// </summary>
public partial class MapPreview : RefCounted
{
    const int Cells = 128;

    /// <summary>The land of a map as an image of 128 x 128 pixels. <paramref name="size"/> is 0 small, 1 normal, 2 large.</summary>
    public Image Render(long seed, int size, double mountains, double water, double forests)
    {
        var settings = new TerrainSettings((float)mountains, (float)water, (float)forests);
        var terrain = WorldBuilder.Preview((ulong)seed, (MapSize)System.Math.Clamp(size, 0, 2), settings, Cells);
        var pixels = new byte[Cells * Cells * 3];
        for (int j = 0; j < Cells; j++)
            for (int i = 0; i < Cells; i++)
            {
                // Light from the north-west: a slope that rises away from it faces it and is bright, one that falls away is dark.
                float slope = terrain.GetSample(i + 1, j + 1) - terrain.GetSample(i - 1, j - 1);
                float light = Mathf.Clamp(1f + slope * 0.012f * (Cells / 128f), 0.6f, 1.35f);
                var colour = BiomeColour(terrain.BiomeAt(i, j)) * light;
                int k = (j * Cells + i) * 3;
                pixels[k] = (byte)Mathf.Clamp(colour.R * 255f, 0f, 255f);
                pixels[k + 1] = (byte)Mathf.Clamp(colour.G * 255f, 0f, 255f);
                pixels[k + 2] = (byte)Mathf.Clamp(colour.B * 255f, 0f, 255f);
            }
        return Image.CreateFromData(Cells, Cells, false, Image.Format.Rgb8, pixels);
    }

    /// <summary>The side of the map in metres, for the scale label.</summary>
    public int SideMetres(int size) => WorldBuilder.CellsOf((MapSize)System.Math.Clamp(size, 0, 2)) * 8;

    /// <summary>The most towns that fit a map of this size (the slider's top).</summary>
    public int MostTowns(int size) => WorldBuilder.MostTowns((MapSize)System.Math.Clamp(size, 0, 2));

    static Color BiomeColour(Biome biome) => biome switch
    {
        Biome.Water => new Color(0.18f, 0.36f, 0.56f),
        Biome.Forest => new Color(0.20f, 0.42f, 0.22f),
        Biome.Rock => new Color(0.46f, 0.44f, 0.42f),
        Biome.Sand => new Color(0.78f, 0.70f, 0.50f),
        Biome.Swamp => new Color(0.30f, 0.42f, 0.34f),
        Biome.Snow => new Color(0.92f, 0.94f, 0.96f),
        _ => new Color(0.52f, 0.62f, 0.32f),
    };
}
