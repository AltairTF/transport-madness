using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class IncidentTests
{
    const float RoadLength = 3000f;

    sealed class World
    {
        public readonly RoadNetwork Net = new();
        public readonly LaneId Forward;
        public readonly LaneId Backward;
        public readonly int NearNode;
        public readonly int FarNode;

        public World()
        {
            int a = Net.AddNode(0, 0);
            int b = Net.AddNode(RoadLength, 0);
            NearNode = Net.AddNode(1000, 60);   // 60 m from the crash site used below
            FarNode = Net.AddNode(4000, 0);      // 3 km from it
            var road = Net.GetRoad(Net.AddRoad(a, b, RoadType.TwoLane, 14f));
            Forward = road.ForwardLanes[0];
            Backward = road.BackwardLanes[0];
        }
    }

    static void Run(Simulation sim, float seconds)
    {
        for (int i = 0, n = (int)(seconds / Simulation.StepSeconds); i < n; i++) sim.Step();
    }

    [Fact]
    public void CrashedVehicle_BecomesAWreckThatBlocksTheLane_AndFollowersQueueBehindIt()
    {
        var w = new World();
        var sim = new Simulation(w.Net, seed: 1);
        int victim = sim.SpawnVehicle(w.Forward, 1000f, 10f);
        int follower = sim.SpawnVehicle(w.Forward, 900f, 14f);
        sim.SetKeepLane(follower, true);

        sim.Crash(victim);
        Run(sim, 40f);

        Assert.Equal(VehicleKind.Wreck, sim.GetVehicle(victim).Kind);
        Assert.Equal(0f, sim.GetVehicle(victim).Speed);
        var f = sim.GetVehicle(follower);
        Assert.Equal(VehicleKind.Car, f.Kind);
        Assert.InRange(f.Position, 980f, 1000f - Simulation.VehicleLength);
        Assert.Single(sim.Incidents);
        Assert.Equal(IncidentKind.Breakdown, sim.Incidents[0].Kind);
    }

    [Fact]
    public void FastVehicleThatCannotStopInTime_RearEndsTheWreck_AndJoinsTheIncident()
    {
        var w = new World();
        var sim = new Simulation(w.Net, seed: 1);
        int victim = sim.SpawnVehicle(w.Forward, 1000f, 0f);
        int driver = sim.SpawnVehicle(w.Forward, 985f, 14f);
        sim.SetKeepLane(driver, true);

        sim.Crash(victim);
        Run(sim, 5f);

        Assert.Equal(VehicleKind.Wreck, sim.GetVehicle(driver).Kind);
        Assert.Single(sim.Incidents);
        Assert.Contains(driver, sim.Incidents[0].VehicleIds);
        Assert.Contains(victim, sim.Incidents[0].VehicleIds);
    }

    [Fact]
    public void RescueCrew_ClearsTheWreckAndTrafficFlowsAgain()
    {
        var w = new World();
        w.Net.GetNode(w.NearNode);
        var sim = new Simulation(w.Net, seed: 1);
        sim.AddDepot(w.NearNode, DepotLevel.Simple);
        int victim = sim.SpawnVehicle(w.Forward, 1000f, 0f);
        int follower = sim.SpawnVehicle(w.Forward, 900f, 14f);
        sim.SetKeepLane(follower, true);
        sim.Crash(victim);

        Run(sim, 30f);
        Assert.True(sim.GetVehicle(follower).Position < 1000f, "follower should still be held up");
        Assert.NotEqual(IncidentState.Cleared, sim.Incidents[0].State);

        Run(sim, 120f);

        Assert.Equal(IncidentState.Cleared, sim.Incidents[0].State);
        Assert.Equal(VehicleKind.Removed, sim.GetVehicle(victim).Kind);
        var f = sim.GetVehicle(follower);
        Assert.True(f.Position > 1500f, $"follower at {f.Position}");
        Assert.InRange(f.Speed, 12f, 14.1f);
    }

    [Fact]
    public void ResponseTime_GrowsWithDistanceToTheNearestDepot_AndIsSlowestWithNone()
    {
        long ClearedAt(Func<World, Simulation, int?> setup)
        {
            var w = new World();
            var sim = new Simulation(w.Net, seed: 1);
            setup(w, sim);
            int victim = sim.SpawnVehicle(w.Forward, 1000f, 0f);
            sim.Crash(victim);
            Run(sim, 700f);
            Assert.Equal(IncidentState.Cleared, sim.Incidents[0].State);
            return sim.Incidents[0].ClearedTick;
        }

        long near = ClearedAt((w, s) => s.AddDepot(w.NearNode, DepotLevel.Simple));
        long far = ClearedAt((w, s) => s.AddDepot(w.FarNode, DepotLevel.Simple));
        long none = ClearedAt((w, s) => null);

        Assert.True(near < far, $"near {near} far {far}");
        Assert.True(far < none, $"far {far} none {none}");
    }

    [Fact]
    public void OneCrewServesOneIncidentAtATime()
    {
        var w = new World();
        var sim = new Simulation(w.Net, seed: 1);
        sim.AddDepot(w.NearNode, DepotLevel.Simple);
        int first = sim.SpawnVehicle(w.Forward, 1000f, 0f);
        int second = sim.SpawnVehicle(w.Backward, 1800f, 0f);
        sim.Crash(first);
        sim.Crash(second);

        Run(sim, 600f);

        Assert.Equal(2, sim.Incidents.Count);
        Assert.All(sim.Incidents, i => Assert.Equal(IncidentState.Cleared, i.State));
        Assert.True(sim.Incidents[1].ClearedTick > sim.Incidents[0].ClearedTick + 40 * 20, "second incident was cleared too early");
    }

    [Fact]
    public void OvertakerMeetingOncomingCarTooLateToEscape_CausesAHeadOnIncident()
    {
        var w = new World();
        var sim = new Simulation(w.Net, seed: 1);
        int truck = sim.SpawnVehicle(w.Forward, 1200f, 5f);
        sim.SetDesiredSpeed(truck, 5f);
        int car = sim.SpawnVehicle(w.Forward, 1180f, 5f);
        sim.SetDriver(car, 0f, 0.5f, 1f);
        for (int i = 0; i < 40 * 20 && sim.GetVehicle(car).Overtake != OvertakePhase.Passing; i++) sim.Step();
        Assert.Equal(OvertakePhase.Passing, sim.GetVehicle(car).Overtake);

        // An oncoming car comes out of nowhere right in front of the overtaker.
        int oncoming = sim.SpawnVehicle(w.Backward, RoadLength - (sim.GetVehicle(car).Position + 6f), 14f);
        Run(sim, 5f);

        Assert.NotEmpty(sim.Incidents);
        Assert.Equal(IncidentKind.HeadOn, sim.Incidents[0].Kind);
        Assert.Equal(VehicleKind.Wreck, sim.GetVehicle(car).Kind);
        Assert.Equal(VehicleKind.Wreck, sim.GetVehicle(oncoming).Kind);
    }

    [Fact]
    public void HeadOnWreck_BlocksBothDirections()
    {
        var w = new World();
        var sim = new Simulation(w.Net, seed: 1);
        int truck = sim.SpawnVehicle(w.Forward, 1200f, 5f);
        sim.SetDesiredSpeed(truck, 5f);
        int car = sim.SpawnVehicle(w.Forward, 1180f, 5f);
        sim.SetDriver(car, 0f, 0.5f, 1f);
        for (int i = 0; i < 40 * 20 && sim.GetVehicle(car).Overtake != OvertakePhase.Passing; i++) sim.Step();
        int oncoming = sim.SpawnVehicle(w.Backward, RoadLength - (sim.GetVehicle(car).Position + 6f), 14f);
        Run(sim, 4f);
        float crashAt = sim.GetVehicle(car).Position;

        // Fresh traffic arrives from both ends after the crash.
        int eastbound = sim.SpawnVehicle(w.Forward, crashAt - 300f, 14f);
        int westbound = sim.SpawnVehicle(w.Backward, RoadLength - (crashAt + 300f), 14f);
        sim.SetKeepLane(eastbound, true);
        sim.SetKeepLane(westbound, true);
        Run(sim, 60f);

        Assert.True(sim.GetVehicle(eastbound).Position < crashAt, "eastbound drove through the crash");
        Assert.True(RoadLength - sim.GetVehicle(westbound).Position > crashAt, "westbound drove through the crash");
        Assert.True(sim.GetVehicle(eastbound).Speed < 0.5f);
        Assert.True(sim.GetVehicle(westbound).Speed < 0.5f);
    }
}
