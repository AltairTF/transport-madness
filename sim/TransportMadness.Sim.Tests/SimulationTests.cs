using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class SimulationTests
{
    static (RoadNetwork Net, LaneId Lane) StraightRoad(float length, float limit = 14f)
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0);
        int b = net.AddNode(length, 0);
        var road = net.AddRoad(a, b, RoadType.TwoLane, limit);
        return (net, net.GetRoad(road).ForwardLanes[0]);
    }

    static void Run(Simulation sim, float seconds)
    {
        int steps = (int)MathF.Round(seconds / Simulation.StepSeconds);
        for (int i = 0; i < steps; i++) sim.Step();
    }

    [Fact]
    public void LoneVehicle_AcceleratesTowardSpeedLimitWithoutExceedingIt()
    {
        var (net, lane) = StraightRoad(5000f, limit: 14f);
        var sim = new Simulation(net, seed: 1);
        int car = sim.SpawnVehicle(lane, position: 10f, speed: 0f);

        Run(sim, 5f);
        float early = sim.GetVehicle(car).Speed;
        Run(sim, 40f);
        float late = sim.GetVehicle(car).Speed;

        Assert.InRange(early, 3f, 14f);
        Assert.InRange(late, 13f, 14f);
    }

    [Fact]
    public void Follower_StopsBehindStoppedVehicleWithoutOverlap()
    {
        var (net, lane) = StraightRoad(2000f, limit: 14f);
        var sim = new Simulation(net, seed: 1);
        int blocker = sim.SpawnVehicle(lane, position: 500f, speed: 0f);
        sim.SetHalted(blocker, true);
        int follower = sim.SpawnVehicle(lane, position: 20f, speed: 14f);

        Run(sim, 120f);

        var b = sim.GetVehicle(blocker);
        var f = sim.GetVehicle(follower);
        float gap = b.Position - Simulation.VehicleLength - f.Position;
        Assert.True(gap > 0.5f, $"gap {gap}");
        Assert.True(gap < 6f, $"gap {gap}");
        Assert.True(f.Speed < 0.1f);
    }

    [Fact]
    public void Follower_MatchesSpeedOfSlowerLeader()
    {
        var (net, lane) = StraightRoad(10000f, limit: 14f);
        var sim = new Simulation(net, seed: 1);
        int leader = sim.SpawnVehicle(lane, position: 100f, speed: 5f);
        sim.SetDesiredSpeed(leader, 5f);
        int follower = sim.SpawnVehicle(lane, position: 10f, speed: 14f);

        Run(sim, 60f);

        Assert.InRange(sim.GetVehicle(follower).Speed, 4.5f, 5.5f);
        float gap = sim.GetVehicle(leader).Position - Simulation.VehicleLength - sim.GetVehicle(follower).Position;
        Assert.True(gap > 1f);
    }

    [Fact]
    public void Vehicle_ContinuesOntoNextRoadThroughJunction()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0);
        int b = net.AddNode(100, 0);
        int c = net.AddNode(100, 100);
        var ab = net.AddRoad(a, b, RoadType.TwoLane, 14f);
        var bc = net.AddRoad(b, c, RoadType.TwoLane, 14f);
        var sim = new Simulation(net, seed: 1);
        int car = sim.SpawnVehicle(net.GetRoad(ab).ForwardLanes[0], position: 50f, speed: 10f);

        Run(sim, 10f);

        var state = sim.GetVehicle(car);
        Assert.Equal(net.GetRoad(bc).ForwardLanes[0], state.Lane);
        Assert.InRange(state.Position, 0f, 100f);
    }

    [Fact]
    public void Vehicle_TurnsAroundAtDeadEnd()
    {
        var (net, lane) = StraightRoad(100f);
        var sim = new Simulation(net, seed: 1);
        int car = sim.SpawnVehicle(lane, position: 50f, speed: 10f);

        Run(sim, 8f);

        Assert.Equal(net.GetLane(lane).Oncoming, sim.GetVehicle(car).Lane);
    }

    [Fact]
    public void Vehicle_StopsForObstacleOnTheNextLane()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0);
        int b = net.AddNode(200, 0);
        int c = net.AddNode(400, 0);
        var ab = net.AddRoad(a, b, RoadType.TwoLane, 14f);
        var bc = net.AddRoad(b, c, RoadType.TwoLane, 14f);
        var sim = new Simulation(net, seed: 1);
        int wreck = sim.SpawnVehicle(net.GetRoad(bc).ForwardLanes[0], position: 3f, speed: 0f);
        sim.SetHalted(wreck, true);
        int car = sim.SpawnVehicle(net.GetRoad(ab).ForwardLanes[0], position: 20f, speed: 14f);

        Run(sim, 60f);

        var w = sim.GetVehicle(wreck);
        var s = sim.GetVehicle(car);
        Assert.Equal(net.GetRoad(ab).ForwardLanes[0], s.Lane); // still queued before the junction
        float gap = 200f - s.Position + w.Position - Simulation.VehicleLength;
        Assert.InRange(gap, 0.5f, 6f);
    }

    [Fact]
    public void VehicleFarAboveTheSpeedLimit_SlowsDownWithinPhysicalBrakingLimit()
    {
        var (net, lane) = StraightRoad(5000f, limit: 14f);
        var sim = new Simulation(net, seed: 1);
        int car = sim.SpawnVehicle(lane, position: 10f, speed: 30f);

        sim.Step();

        float speed = sim.GetVehicle(car).Speed;
        Assert.InRange(speed, 30f - 9f * Simulation.StepSeconds - 0.001f, 30f);
    }

    [Fact]
    public void DesiredSpeed_MustBePositive()
    {
        var (net, lane) = StraightRoad(500f);
        var sim = new Simulation(net, seed: 1);
        int car = sim.SpawnVehicle(lane, position: 10f, speed: 0f);

        Assert.Throws<ArgumentOutOfRangeException>(() => sim.SetDesiredSpeed(car, 0f));
    }

    [Fact]
    public void StateHash_ReflectsHaltedVehicles()
    {
        var (net, lane) = StraightRoad(500f);
        var plain = new Simulation(net, seed: 1);
        var halted = new Simulation(net, seed: 1);
        plain.SpawnVehicle(lane, 100f, 0f);
        halted.SetHalted(halted.SpawnVehicle(lane, 100f, 0f), true);

        Assert.NotEqual(plain.StateHash(), halted.StateHash());
    }
}
