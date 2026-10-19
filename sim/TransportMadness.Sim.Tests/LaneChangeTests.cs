using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class LaneChangeTests
{
    static (RoadNetwork Net, LaneId Right, LaneId Left) Avenue(float length)
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0);
        int b = net.AddNode(length, 0);
        var road = net.AddRoad(a, b, RoadType.Avenue2x2, 14f);
        var lanes = net.GetRoad(road).ForwardLanes;
        return (net, lanes[0], lanes[1]);
    }

    static void Run(Simulation sim, float seconds)
    {
        for (int i = 0, n = (int)(seconds / Simulation.StepSeconds); i < n; i++) sim.Step();
    }

    [Fact]
    public void FastVehicleStuckBehindSlowOne_PassesOnTheLeftAndKeepsItsSpeed()
    {
        var (net, right, left) = Avenue(5000f);
        var sim = new Simulation(net, seed: 1);
        int slow = sim.SpawnVehicle(right, position: 80f, speed: 4f);
        sim.SetDesiredSpeed(slow, 4f);
        int fast = sim.SpawnVehicle(right, position: 40f, speed: 12f);

        bool usedLeft = false;
        for (int i = 0; i < 40 * 20; i++)
        {
            sim.Step();
            usedLeft |= sim.GetVehicle(fast).Lane == left;
        }

        Assert.True(usedLeft, "fast vehicle never moved to the left lane");
        Assert.True(sim.GetVehicle(fast).Position > sim.GetVehicle(slow).Position + 20f);
        Assert.InRange(sim.GetVehicle(fast).Speed, 12f, 14.5f);
    }

    [Fact]
    public void AfterPassing_VehicleReturnsToTheRightLane()
    {
        var (net, right, left) = Avenue(5000f);
        var sim = new Simulation(net, seed: 1);
        int slow = sim.SpawnVehicle(right, position: 80f, speed: 4f);
        sim.SetDesiredSpeed(slow, 4f);
        int fast = sim.SpawnVehicle(right, position: 40f, speed: 12f);

        Run(sim, 90f);

        Assert.Equal(right, sim.GetVehicle(fast).Lane);
    }

    [Fact]
    public void VehicleDoesNotChangeLaneIntoAnOccupiedGap()
    {
        var (net, right, left) = Avenue(5000f);
        var sim = new Simulation(net, seed: 1);
        int slow = sim.SpawnVehicle(right, position: 80f, speed: 4f);
        sim.SetDesiredSpeed(slow, 4f);
        int fast = sim.SpawnVehicle(right, position: 40f, speed: 12f);
        int blocker = sim.SpawnVehicle(left, position: 60f, speed: 4f);
        sim.SetDesiredSpeed(blocker, 4f);

        for (int i = 0; i < 20 * 20; i++)
        {
            sim.Step();
            if (sim.GetVehicle(fast).Lane == left)
            {
                var f = sim.GetVehicle(fast);
                var b = sim.GetVehicle(blocker);
                bool overlap = f.Position > b.Position - Simulation.VehicleLength && b.Position > f.Position - Simulation.VehicleLength;
                Assert.False(overlap, "vehicles overlap in the left lane");
            }
        }
    }

    [Fact]
    public void LaneChange_ShowsAsASidewaysShiftThatFadesOut()
    {
        var (net, right, left) = Avenue(5000f);
        var sim = new Simulation(net, seed: 1);
        int slow = sim.SpawnVehicle(right, position: 80f, speed: 4f);
        sim.SetDesiredSpeed(slow, 4f);
        int fast = sim.SpawnVehicle(right, position: 40f, speed: 12f);

        while (sim.GetVehicle(fast).Lane != left) sim.Step();
        // Moving to the left lane, the car starts out 3.5 m to the right of where its new lane is.
        Assert.InRange(sim.GetVehicle(fast).Shift, Simulation.LaneWidth - 0.2f, Simulation.LaneWidth);

        Run(sim, 0.6f);
        Assert.InRange(sim.GetVehicle(fast).Shift, 0.5f, Simulation.LaneWidth - 0.5f);
        Run(sim, 1.5f);
        Assert.Equal(0f, sim.GetVehicle(fast).Shift);
    }
}
