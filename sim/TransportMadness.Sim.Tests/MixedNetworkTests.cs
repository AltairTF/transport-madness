using TransportMadness.Sim;
using static TransportMadness.Sim.Tests.Worlds;

namespace TransportMadness.Sim.Tests;

/// <summary>M4's exit: a network of roads and railway runs for days with buses, trains and the traffic of the towns, without deadlock.</summary>
public class MixedNetworkTests
{
    static (Simulation Sim, int Line, int[] Trains, int BusLine) Mixed(ulong seed)
    {
        var sc = RailTownWorlds.Find(seed);
        var sim = sc.Sim;
        var trains = new List<int>();
        for (int k = 0; k < 2; k++)
        {
            int depot = k == 0 ? sc.DepotA : sc.DepotB;
            int id = -1;
            for (int tries = 0; tries < 60 && id < 0; tries++)
            {
                id = sim.Execute(new BuyTrainCommand(depot, "diesel", "coach", 4, sc.Line));
                if (id < 0) Run(sim, 0.01);
            }
            Assert.True(id >= 0, "train bought");
            trains.Add(id);
        }
        // Buses on the road between the same towns.
        var (i, j) = ClosestPair(sim);
        var sa = StationsNear(sim, sim.Towns[i].X, sim.Towns[i].Z, 2);
        var sb = StationsNear(sim, sim.Towns[j].X, sim.Towns[j].Z, 2);
        int busDepot = Depot(sim, sim.Towns[i]);
        int busLine = sim.Execute(new CreateLineCommand(sa.Concat(Enumerable.Reverse(sb)).ToArray()));
        Assert.True(busLine >= 0 && busDepot >= 0);
        for (int k = 0; k < 3; k++) Buy(sim, busDepot, "bus", busLine);
        return (sim, sc.Line, trains.ToArray(), busLine);
    }

    [Fact]
    public void TownsWithRoadsBusesAndTrains_RunForDaysWithoutDeadlock_AndEveryoneKeepsEarning()
    {
        var (sim, line, trains, busLine) = Mixed(6);
        int stops = 0;
        var seen = new TrainStatus[2];

        for (int day = 0; day < 8; day++)
        {
            for (int t = 0; t < 12000; t++)
            {
                sim.Step();
                if (t % 10 != 0) continue;
                for (int k = 0; k < 2; k++)
                {
                    var s = sim.Trains[trains[k]].Status;
                    if (s == TrainStatus.AtStation && seen[k] != TrainStatus.AtStation) stops++;
                    seen[k] = s;
                }
            }
            Assert.Empty(sim.RailWarnings);
        }

        Assert.True(stops >= 16, $"the trains made {stops} stops in 8 days");
        Assert.All(trains, t => Assert.True(sim.Trains[t].Income > 0, $"train {t} was paid"));
        Assert.True(sim.Lines[busLine].Income > 0, "the buses were paid too");
        Assert.True(sim.Crossings.Count >= 2); // the towns are laid out street by street: the track meets fewer streets than it did on a grid
        Assert.Equal(0, sim.Crossings.Sum(c => c.Collisions));
        Assert.Empty(sim.DeadlockedTrains);
    }

    [Fact]
    public void AMixedRun_IsTheSameEveryTime()
    {
        var a = Mixed(6).Sim;
        var b = Mixed(6).Sim;

        RailWorlds.Run(a, 1200);
        RailWorlds.Run(b, 1200);

        Assert.Equal(a.StateHash(), b.StateHash());
    }
}
