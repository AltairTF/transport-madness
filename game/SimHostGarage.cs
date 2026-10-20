using System;
using System.Linq;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>
/// The garage side of the host: the depots, buying a vehicle for no line in particular, putting parked vehicles on a line and sending
/// vehicles back to a depot. Vehicles and trains share ids with the Vehicles window (trains are negative: -1 - train).
/// </summary>
public partial class SimHost
{
    /// <summary>The open depots, road and rail: for each {id, rail, name, x, z}.</summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetDepots()
    {
        var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var depot in Sim.Depots)
        {
            if (!depot.Active) continue;
            var (x, z) = Sim.Network.GetNode(depot.Node);
            list.Add(new Godot.Collections.Dictionary { ["id"] = depot.Id, ["rail"] = false, ["name"] = $"Road depot {depot.Id + 1}", ["x"] = x, ["z"] = z });
        }
        foreach (var depot in Sim.RailDepots)
        {
            if (!depot.Active) continue;
            var (x, z) = Sim.Rail.GetNode(depot.Node);
            list.Add(new Godot.Collections.Dictionary { ["id"] = depot.Id, ["rail"] = true, ["name"] = $"Rail depot {depot.Id + 1}", ["x"] = x, ["z"] = z });
        }
        return list;
    }

    /// <summary>
    /// Buys a vehicle into a depot's garage, on no line. <paramref name="option"/> is a model id for a road depot, "locomotive:wagon:count"
    /// for a rail depot. Returns the garage id, -1 when refused.
    /// </summary>
    public int BuyParked(bool rail, int depot, string option)
    {
        if (!rail) return Sim.Execute(new BuyParkedVehicleCommand(depot, option));
        var parts = option.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[2], out int count)) return -1;
        return Sim.Execute(new BuyParkedTrainCommand(depot, parts[0], parts[1], count));
    }

    /// <summary>The vehicles waiting in depots: for each {id, rail, model, depot, price, x, z}.</summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetParked()
    {
        var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var entry in Sim.Garage)
        {
            if (!entry.Active) continue;
            string model, depotName;
            float x, z;
            if (entry.Rail)
            {
                var loco = Sim.Locomotives[entry.Locomotive];
                var wagon = Sim.Wagons[entry.Wagon];
                model = $"{loco.Name} + {entry.Wagons} x {wagon.Name.ToLowerInvariant()}";
                depotName = $"Rail depot {entry.Depot + 1}";
                (x, z) = Sim.Rail.GetNode(Sim.RailDepots[entry.Depot].Node);
            }
            else
            {
                model = Sim.VehicleModels[entry.Model].Name;
                depotName = $"Road depot {entry.Depot + 1}";
                (x, z) = Sim.Network.GetNode(Sim.Depots[entry.Depot].Node);
            }
            list.Add(new Godot.Collections.Dictionary
            {
                ["id"] = entry.Id, ["rail"] = entry.Rail, ["model"] = model, ["depot"] = depotName,
                ["price"] = Sim.PriceOf(entry), ["x"] = x, ["z"] = z,
            });
        }
        return list;
    }

    /// <summary>Puts a parked vehicle on a line. Returns "" when done, else why it cannot be.</summary>
    public string AssignParked(int parked, int line)
    {
        if (Sim.WhyNotAssign(parked, line) is { } why) return why;
        return Sim.Execute(new AssignParkedCommand(parked, line)) >= 0 ? "" : "it cannot be put on that line now";
    }

    /// <summary>Sells a parked vehicle. True when sold.</summary>
    public bool SellParked(int parked) => Sim.Execute(new SellParkedCommand(parked)) >= 0;

    /// <summary>Sends a vehicle (a train when the id is negative) to the nearest depot to wait there. Returns "" when it is on its way, else why not.</summary>
    public string SendToDepot(int vehicle)
    {
        if (vehicle < 0)
        {
            int train = -1 - vehicle;
            if (Sim.WhyNotParkTrain(train) is { } trainWhy) return trainWhy;
            return Sim.Execute(new ParkTrainCommand(train)) > 0 ? "" : "no track leads from where it is to a rail depot";
        }
        if (Sim.WhyNotPark(vehicle) is { } why) return why;
        return Sim.Execute(new ParkVehicleCommand(vehicle)) > 0 ? "" : "it cannot go to a depot now";
    }

    /// <summary>
    /// Buys a vehicle (or train) for a line in one go: at the depot with the quickest way to the line's first station, coming out of it at
    /// once. <paramref name="option"/> is as for <see cref="BuyParked"/>. Returns "" when bought, else why not.
    /// </summary>
    public string BuyForLine(bool rail, string option, int line)
    {
        long price;
        int result;
        if (rail)
        {
            var parts = option.Split(':');
            if (parts.Length != 3 || !int.TryParse(parts[2], out int count)) return "no such train";
            price = Sim.TrainPrice(parts[0], parts[1], count);
            result = Sim.Execute(new BuyTrainCommand(BuyTrainCommand.NearestDepot, parts[0], parts[1], count, line));
        }
        else
        {
            int model = Sim.ModelIndex(option);
            if (model < 0) return "no such vehicle";
            price = Sim.VehicleModels[model].Price;
            result = Sim.Execute(new BuyVehicleCommand(BuyVehicleCommand.NearestDepot, option, line));
        }
        if (result >= 0) return "";
        if (line < 0 || line >= Sim.Lines.Count || !Sim.Lines[line].Active) return "that line does not exist";
        if (Sim.Company.Money < price) return $"not enough money, it costs {price:N0}";
        if (rail) return Sim.RailDepots.Any(d => d.Active)
            ? "no rail depot has a track to the line's first station (build track from a depot to it); you can still buy it for a depot in the Vehicles window and put it on the line later"
            : "build a rail depot first (Rail > Rail depot)";
        return Sim.Depots.Any(d => d.Active)
            ? "no depot has a road to the line's first station; you can still buy it for a depot in the Vehicles window and put it on the line later"
            : "build a depot first (Transport > Depot)";
    }
}
