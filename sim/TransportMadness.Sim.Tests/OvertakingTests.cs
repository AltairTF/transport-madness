using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class OvertakingTests
{
    // 3 km straight 2-lane road. Forward lane runs west-to-east; the oncoming lane carries traffic back.
    sealed class Road
    {
        public readonly RoadNetwork Net = new();
        public readonly LaneId Forward;
        public readonly LaneId Backward;

        public Road(float length = 3000f)
        {
            int a = Net.AddNode(0, 0);
            int b = Net.AddNode(length, 0);
            var road = Net.GetRoad(Net.AddRoad(a, b, RoadType.TwoLane, 14f));
            Forward = road.ForwardLanes[0];
            Backward = road.BackwardLanes[0];
        }
    }

    // A slow truck ahead of a fast car, both in the forward lane.
    static (Simulation Sim, int Truck, int Car) FollowingScenario(Road road, float carPatience = 0f, float carAttention = 1f)
    {
        var sim = new Simulation(road.Net, seed: 1);
        int truck = sim.SpawnVehicle(road.Forward, position: 200f, speed: 5f);
        sim.SetDesiredSpeed(truck, 5f);
        int car = sim.SpawnVehicle(road.Forward, position: 180f, speed: 5f);
        sim.SetDriver(car, patience: carPatience, aggression: 0.5f, attention: carAttention);
        return (sim, truck, car);
    }

    static int StepsUntil(Simulation sim, Func<bool> condition, float maxSeconds)
    {
        int max = (int)(maxSeconds / Simulation.StepSeconds);
        for (int i = 0; i < max; i++)
        {
            if (condition()) return i;
            sim.Step();
        }
        return condition() ? max : -1;
    }

    [Fact]
    public void FastCarBehindSlowTruck_OvertakesOnEmptyRoadAndReturnsToItsLane()
    {
        var road = new Road();
        var (sim, truck, car) = FollowingScenario(road);

        bool wentOut = false;
        for (int i = 0; i < 60 * 20; i++)
        {
            sim.Step();
            wentOut |= sim.GetVehicle(car).Lateral > 0.9f;
        }

        Assert.True(wentOut, "car never used the oncoming lane");
        var c = sim.GetVehicle(car);
        Assert.Equal(OvertakePhase.None, c.Overtake);
        Assert.Equal(0f, c.Lateral);
        Assert.True(c.Position > sim.GetVehicle(truck).Position + 15f, "car did not get ahead of the truck");
        Assert.Equal(road.Forward, c.Lane);
    }

    [Fact]
    public void NearbyOncomingCar_PreventsTheOvertake_UntilItHasPassed()
    {
        var road = new Road();
        var (sim, truck, car) = FollowingScenario(road);
        // Oncoming car 150 m ahead of the truck, cruising toward us.
        int oncoming = sim.SpawnVehicle(road.Backward, position: 3000f - 380f, speed: 14f);

        bool outWhileOncomingAhead = false;
        for (int i = 0; i < 12 * 20; i++)
        {
            sim.Step();
            var o = sim.GetVehicle(oncoming);
            bool oncomingStillAhead = 3000f - o.Position > sim.GetVehicle(car).Position;
            if (oncomingStillAhead && sim.GetVehicle(car).Lateral > 0f) outWhileOncomingAhead = true;
        }

        Assert.False(outWhileOncomingAhead, "car pulled out in front of oncoming traffic");
        // Once the oncoming car is gone, it does overtake.
        bool wentOut = StepsUntil(sim, () => sim.GetVehicle(car).Lateral > 0.9f, 60f) >= 0;
        Assert.True(wentOut);
    }

    [Fact]
    public void InattentiveDriver_MisjudgesDistanceAndPullsOutWhereAnAttentiveOneWouldNot()
    {
        bool PullsOut(float attention)
        {
            var road = new Road();
            var (sim, _, car) = FollowingScenario(road, carAttention: attention);
            sim.SpawnVehicle(road.Backward, position: 3000f - 330f, speed: 14f);
            for (int i = 0; i < 6 * 20; i++)
            {
                sim.Step();
                if (sim.GetVehicle(car).Overtake != OvertakePhase.None) return true;
            }
            return false;
        }

        Assert.False(PullsOut(attention: 1f));
        Assert.True(PullsOut(attention: 0f));
    }

    [Fact]
    public void ImpatientDriver_PullsOutSoonerThanPatientOne()
    {
        int StepsToPullOut(float patience)
        {
            var road = new Road();
            var (sim, _, car) = FollowingScenario(road, carPatience: patience);
            return StepsUntil(sim, () => sim.GetVehicle(car).Overtake != OvertakePhase.None, 60f);
        }

        int impatient = StepsToPullOut(0f);
        int patient = StepsToPullOut(1f);
        Assert.True(impatient >= 0 && patient >= 0);
        Assert.True(impatient < patient, $"{impatient} vs {patient}");
    }

    [Fact]
    public void OncomingCarAppearingMidOvertake_MakesTheOvertakerFallBackBehindTheTruck()
    {
        var road = new Road();
        var (sim, truck, car) = FollowingScenario(road);
        StepsUntil(sim, () => sim.GetVehicle(car).Overtake == OvertakePhase.MovingOut, 30f);
        // A car comes round the bend: close enough to be a real threat.
        sim.SpawnVehicle(road.Backward, position: 3000f - (sim.GetVehicle(car).Position + 90f), speed: 14f);

        bool aborted = false;
        for (int i = 0; i < 20 * 20 && !aborted; i++)
        {
            sim.Step();
            var c = sim.GetVehicle(car);
            aborted = c.Overtake == OvertakePhase.Returning && c.Position < sim.GetVehicle(truck).Position - Simulation.VehicleLength;
        }
        Assert.True(aborted, "overtaker never aborted");

        StepsUntil(sim, () => sim.GetVehicle(car).Overtake == OvertakePhase.None, 5f);
        var back = sim.GetVehicle(car);
        var t = sim.GetVehicle(truck);
        Assert.Equal(OvertakePhase.None, back.Overtake);
        Assert.True(back.Position < t.Position - Simulation.VehicleLength, "overtaker is not behind the truck");
    }

    [Fact]
    public void OncomingTraffic_BrakesForAnOvertakerInItsLane()
    {
        var road = new Road();
        var (sim, truck, car) = FollowingScenario(road);
        StepsUntil(sim, () => sim.GetVehicle(car).Lateral > 0.9f, 30f);
        int oncoming = sim.SpawnVehicle(road.Backward, position: 3000f - (sim.GetVehicle(car).Position + 100f), speed: 14f);

        sim.Step();
        float before = sim.GetVehicle(oncoming).Speed;
        for (int i = 0; i < 2 * 20; i++) sim.Step();

        Assert.True(sim.GetVehicle(oncoming).Speed < before - 0.5f, "oncoming car did not slow down");
    }
}
