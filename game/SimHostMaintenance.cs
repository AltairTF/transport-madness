using System;
using System.Linq;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>
/// The maintenance side of the host: Condition, the policy at its three levels, repairs, depot upgrades, road resurfacing, the option for
/// random events, and the event feed. Vehicles and trains share ids with the Vehicles window (trains are negative: -1 - train).
/// </summary>
public partial class SimHost
{
    static readonly string[] RepairNames = { "Patch", "Service", "Overhaul" };
    static readonly string[] LevelNames = { "Simple", "Workshop", "Complex" };

    int _eventsShown;

    /// <summary>A policy as {mode (0 inherit, 1 off, 2 on), days, below, kind (0 patch, 1 service, 2 overhaul)}.</summary>
    static Godot.Collections.Dictionary PolicyDictionary(MaintenancePolicy p) =>
        new() { ["mode"] = (int)p.Mode, ["days"] = p.EveryDays, ["below"] = p.BelowCondition, ["kind"] = (int)p.Kind };

    /// <summary>"Service every 60 days or when Condition is below 40", "no maintenance", "same as the level above".</summary>
    public static string PolicyText(MaintenancePolicy p)
    {
        if (p.Mode == PolicyMode.Inherit) return "same as the level above";
        if (p.Mode == PolicyMode.Off) return "no maintenance";
        string when = (p.EveryDays > 0, p.BelowCondition > 0) switch
        {
            (true, true) => $"every {p.EveryDays} days or when Condition is below {p.BelowCondition}",
            (true, false) => $"every {p.EveryDays} days",
            (false, true) => $"when Condition is below {p.BelowCondition}",
            _ => "never (no trigger set)",
        };
        return $"{RepairNames[(int)p.Kind]} {when}";
    }

    static string PhaseText(RepairPhase phase, RepairKind? kind) => phase switch
    {
        RepairPhase.Requested => $"{RepairNames[(int)(kind ?? RepairKind.Patch)]} due at the next stop",
        RepairPhase.ToDepot => $"Going to a depot for a {RepairNames[(int)(kind ?? RepairKind.Patch)].ToLowerInvariant()}",
        RepairPhase.Waiting => $"In the depot, waiting for a bay ({RepairNames[(int)(kind ?? RepairKind.Patch)].ToLowerInvariant()})",
        RepairPhase.Working => $"In a bay: {RepairNames[(int)(kind ?? RepairKind.Patch)].ToLowerInvariant()} under way",
        _ => "",
    };

    /// <summary>Adds Condition and the policy to a vehicle's dictionary: condition, patched, days_since, repair (text), policy, policy_text, policy_source.</summary>
    void AddMaintenance(Godot.Collections.Dictionary d, int vehicle)
    {
        var (effective, source) = Sim.EffectivePolicyOf(vehicle);
        d["condition"] = Sim.ConditionOf(vehicle);
        d["patched"] = Sim.IsPatched(vehicle);
        d["days_since"] = Sim.DaysSinceRepair(vehicle);
        d["repair"] = PhaseText(Sim.RepairPhaseOf(vehicle), Sim.RepairOf(vehicle));
        d["repair_phase"] = (int)Sim.RepairPhaseOf(vehicle);
        d["policy"] = PolicyDictionary(Sim.PolicyOf(vehicle));
        d["policy_text"] = PolicyText(effective);
        d["policy_source"] = source.ToString().ToLowerInvariant();
    }

    void AddMaintenance(Godot.Collections.Dictionary d, Train train)
    {
        var (effective, source) = Sim.EffectivePolicyOf(train);
        d["condition"] = train.Condition;
        d["patched"] = train.Patched;
        d["days_since"] = Sim.DaysSinceRepair(train);
        d["repair"] = PhaseText(Sim.RepairPhaseOf(train), Sim.RepairOf(train));
        d["repair_phase"] = (int)Sim.RepairPhaseOf(train);
        d["policy"] = PolicyDictionary(train.Policy);
        d["policy_text"] = PolicyText(effective);
        d["policy_source"] = source.ToString().ToLowerInvariant();
    }

    /// <summary>The draw colour of a company vehicle: darker under Condition 40, reddish under 20.</summary>
    static Color ConditionTint(Color color, float condition)
    {
        if (condition >= 40f) return color;
        if (condition >= 20f) return color.Darkened(0.35f * (40f - condition) / 20f);
        return color.Darkened(0.35f).Lerp(new Color(0.55f, 0.12f, 0.08f), 0.6f * (20f - condition) / 20f + 0.2f);
    }

    // ---- Reading ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The maintenance rules and totals: {company (policy), frequency (0 off .. 3 high), repairs (for each {kind, name, gain, ceiling,
    /// seconds, share, level, level_name}), maintenance, compensation, fines (spent so far, as positive numbers), injured}.
    /// </summary>
    public Godot.Collections.Dictionary GetMaintenance()
    {
        var rules = Sim.MaintenanceRules;
        var repairs = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (RepairKind kind in Enum.GetValues<RepairKind>())
        {
            var r = rules.Of(kind);
            repairs.Add(new Godot.Collections.Dictionary
            {
                ["kind"] = (int)kind, ["name"] = RepairNames[(int)kind], ["gain"] = r.Gain, ["ceiling"] = r.Ceiling, ["seconds"] = r.Seconds, ["share"] = r.PriceShare,
                ["level"] = (int)kind, ["level_name"] = LevelNames[(int)kind],
            });
        }
        var c = Sim.Company;
        return new Godot.Collections.Dictionary
        {
            ["company"] = PolicyDictionary(Sim.CompanyPolicy),
            ["company_text"] = PolicyText(Sim.CompanyPolicy),
            ["frequency"] = (int)Sim.Frequency,
            ["repairs"] = repairs,
            ["maintenance"] = -c.Total(LedgerEntry.Maintenance),
            ["compensation"] = -c.Total(LedgerEntry.Compensation),
            ["fines"] = -c.Total(LedgerEntry.Fines),
            ["injured"] = c.Injured,
            ["critical"] = rules.CriticalCondition,
        };
    }

    /// <summary>What the feed has gained since the last call: for each {id, kind, severity (0 info, 1 warning, 2 serious), x, z, text}.</summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> TakeEvents()
    {
        var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        int first = Math.Max(_eventsShown, Sim.EventsDropped);
        for (int id = first; id < Sim.EventCount; id++)
        {
            var e = Sim.Events[id - Sim.EventsDropped];
            list.Add(new Godot.Collections.Dictionary { ["id"] = e.Id, ["kind"] = (int)e.Kind, ["severity"] = (int)e.Severity, ["x"] = e.X, ["z"] = e.Z, ["text"] = e.Text });
        }
        _eventsShown = Sim.EventCount;
        return list;
    }

    /// <summary>The repairs the garage entry can have, for its row: {can (bool per kind), price (per kind), repair (text, "" when none)}.</summary>
    void AddParkedMaintenance(Godot.Collections.Dictionary d, ParkedVehicle entry)
    {
        var level = entry.Rail ? Sim.RailDepots[entry.Depot].Level : Sim.Depots[entry.Depot].Level;
        var can = new Godot.Collections.Array<bool>();
        var price = new Godot.Collections.Array<long>();
        foreach (RepairKind kind in Enum.GetValues<RepairKind>())
        {
            can.Add(Simulation.CanRepair(level, kind));
            price.Add(Sim.RepairPrice(kind, Sim.PriceOf(entry), Sim.FromYearOf(entry)));
        }
        var job = Sim.JobFor(RepairSubject.Parked, entry.Id);
        d["condition"] = entry.Condition;
        d["can_repair"] = can;
        d["repair_price"] = price;
        d["repair"] = job == null ? "" : job.Working ? $"{RepairNames[(int)job.Kind]} under way" : $"{RepairNames[(int)job.Kind]}: waiting for a bay";
    }

    // ---- Commands ---------------------------------------------------------------------------------------------------

    /// <summary>Sets a policy: scope 0 company, 1 line, 2 vehicle (a train when the id is negative); mode 0 inherit, 1 off, 2 on; kind 0 patch, 1 service, 2 overhaul. True when done.</summary>
    public bool SetPolicy(int scope, int target, int mode, int days, int below, int kind)
    {
        var policy = new MaintenancePolicy((PolicyMode)mode, days, below, (RepairKind)kind);
        if (scope == 2 && target < 0) return Sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Train, -1 - target, policy)) == 1;
        return Sim.Execute(new SetMaintenancePolicyCommand((PolicyScope)Math.Clamp(scope, 0, 2), target, policy)) == 1;
    }

    /// <summary>Asks for a repair of a vehicle (a train when the id is negative) at its next stop. Returns "" when asked, else why not.</summary>
    public string RequestRepair(int vehicle, int kind)
    {
        var subject = vehicle < 0 ? RepairSubject.Train : RepairSubject.Vehicle;
        int target = vehicle < 0 ? -1 - vehicle : vehicle;
        if (Sim.WhyNotRepair(subject, target, (RepairKind)kind) is { } why) return why;
        return Sim.Execute(new RequestRepairCommand(subject, target, (RepairKind)kind)) == 1 ? "" : "it cannot be repaired now";
    }

    /// <summary>Takes a vehicle waiting in a depot into a bay for a repair. Returns "" when done, else why not.</summary>
    public string RepairParked(int parked, int kind)
    {
        if (Sim.WhyNotRepair(RepairSubject.Parked, parked, (RepairKind)kind) is { } why) return why;
        return Sim.Execute(new RequestRepairCommand(RepairSubject.Parked, parked, (RepairKind)kind)) == 1 ? "" : "it cannot be repaired now";
    }

    /// <summary>Raises a depot to a level in place. Returns "" when done, else why not.</summary>
    public string UpgradeDepot(bool rail, int depot, int level)
    {
        if (Sim.WhyNotUpgrade(rail, depot, (DepotLevel)level) is { } why) return why;
        return Sim.Execute(new UpgradeDepotCommand(rail, depot, (DepotLevel)level)) == 1 ? "" : "it cannot be upgraded now";
    }

    /// <summary>Sets how often random events happen: 0 off, 1 low, 2 normal, 3 high.</summary>
    public bool SetFrequency(int frequency) => Sim.Execute(new SetFrequencyCommand((IncidentFrequency)frequency)) == 1;

    /// <summary>The Condition of a road and what resurfacing it would take: {condition, price, seconds, why ("" when it can be done)}.</summary>
    public Godot.Collections.Dictionary ResurfaceInfo(int road) => new()
    {
        ["condition"] = Sim.RoadConditionOf(road),
        ["price"] = Sim.ResurfacePrice(road),
        ["seconds"] = Sim.ResurfaceSeconds(road),
        ["why"] = Sim.WhyNotResurface(road) ?? "",
    };

    /// <summary>The roads in the worst shape, worst first: for each {road, condition, length, price, why ("" when it can be resurfaced now), x, z}.</summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetWornRoads(int limit)
    {
        var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var (road, condition) in Sim.WornRoads(limit))
        {
            var points = RoadPoints(road);
            var middle = points.Length > 0 ? points[points.Length / 2] : Vector3.Zero;
            list.Add(new Godot.Collections.Dictionary
            {
                ["road"] = road, ["condition"] = condition, ["length"] = Sim.Network.GetRoad(new RoadId(road)).Length, ["price"] = Sim.ResurfacePrice(road),
                ["why"] = Sim.WhyNotResurface(road) ?? "", ["x"] = middle.X, ["z"] = middle.Z,
            });
        }
        return list;
    }

    /// <summary>Has a crew resurface a road. Returns "" when started, else why not.</summary>
    public string Resurface(int road)
    {
        if (Sim.WhyNotResurface(road) is { } why) return why;
        return Sim.Execute(new ResurfaceRoadCommand(road)) == 1 ? "" : "it cannot be resurfaced now";
    }
}
