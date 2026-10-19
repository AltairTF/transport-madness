using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>A flat test map: a square grid of roads, with an avenue on every third row.</summary>
public static class DemoWorld
{
    public static void Build(RoadNetwork net, int size = 10, float spacing = 250f)
    {
        float origin = -0.5f * (size - 1) * spacing;
        var nodes = new int[size, size];
        for (int x = 0; x < size; x++)
            for (int z = 0; z < size; z++)
                nodes[x, z] = net.AddNode(origin + x * spacing, origin + z * spacing);

        for (int x = 0; x < size; x++)
            for (int z = 0; z < size; z++)
            {
                if (x + 1 < size)
                    net.AddRoad(nodes[x, z], nodes[x + 1, z], z % 3 == 0 ? RoadType.Avenue2x2 : RoadType.TwoLane, z % 3 == 0 ? 19f : 14f);
                if (z + 1 < size)
                    net.AddRoad(nodes[x, z], nodes[x, z + 1], RoadType.TwoLane, 14f);
            }
    }
}
