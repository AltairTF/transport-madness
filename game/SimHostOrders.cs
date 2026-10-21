using System;
using System.Linq;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>The orders of the government (C5) for the Orders window.</summary>
public partial class SimHost
{
    const int ClosedOrdersShown = 5;

    /// <summary>
    /// The orders on offer and under way, then the last few that were closed, as {id, kind ("Quota", "Civil" or "StateWork"), title, state
    /// ("Offered", "Active", "Met", "Late", "Failed", "Expired" or "Declined"), units, progress, days_left (to the deadline of an active order,
    /// to the lapse of an offer), days (allowed), reward, standing, paid, can (it can be accepted now), why (what is missing, empty when it can
    /// be), x, z (where the camera goes)}.
    /// </summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetOrders()
    {
        var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var o in Sim.Orders.Where(o => o.IsOpen).OrderBy(o => o.State).ThenBy(o => o.Id))
            list.Add(OrderDictionary(o));
        foreach (var o in Sim.Orders.Where(o => !o.IsOpen && o.State != OrderState.Declined).OrderByDescending(o => o.ClosedAt).Take(ClosedOrdersShown))
            list.Add(OrderDictionary(o));
        return list;
    }

    Godot.Collections.Dictionary OrderDictionary(Order o)
    {
        float day = Sim.DayLengthSeconds / Simulation.StepSeconds;
        long until = o.State == OrderState.Active ? o.Deadline : o.State == OrderState.Offered ? o.ExpiresAt : o.ClosedAt;
        float daysLeft = o.IsOpen ? Math.Max(0f, (until - Sim.Tick) / day) : 0f;
        string? why = o.State == OrderState.Offered ? Sim.WhyNotAccept(o.Id) : null;
        var (x, z) = o.Kind switch
        {
            OrderKind.Quota => (Sim.Gateways[o.Gateway].X, Sim.Gateways[o.Gateway].Z),
            OrderKind.Civil => (Sim.Towns[o.Town].X, Sim.Towns[o.Town].Z),
            _ => o.Works.Count > 0 ? (o.Works[0].X, o.Works[0].Z) : (0f, 0f),
        };
        return new Godot.Collections.Dictionary
        {
            ["id"] = o.Id, ["kind"] = o.Kind.ToString(), ["title"] = char.ToUpperInvariant(o.Title[0]) + o.Title[1..], ["state"] = o.State.ToString(),
            ["units"] = o.Units, ["progress"] = o.Progress, ["days_left"] = daysLeft, ["days"] = o.Days, ["reward"] = o.Reward, ["standing"] = o.Standing,
            ["paid"] = o.Paid, ["can"] = o.State == OrderState.Offered && why == null, ["why"] = why ?? "", ["x"] = x, ["z"] = z,
        };
    }

    /// <summary>Takes up an order that was offered; false when refused (<see cref="WhyNotAccept"/> says why).</summary>
    public bool AcceptOrder(int order) => Sim.Execute(new AcceptOrderCommand(order)) == 1;

    public bool DeclineOrder(int order) => Sim.Execute(new DeclineOrderCommand(order)) == 1;

    public string WhyNotAccept(int order) => Sim.WhyNotAccept(order) ?? "";
}
