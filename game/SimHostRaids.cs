using System;
using System.Collections.Generic;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>
/// Raids (C9d) in 3D: when the feed gets a raid event, a few bombs fall on the place and a flash and a dark crater are left. The damage itself
/// (a dark industry, a shut gateway, destroyed buildings, craters in the road) is drawn from the simulation's state; this is only the picture of
/// the bombs, with no people in it.
/// </summary>
public partial class SimHost
{
    const float BombStartHeight = 160f, BombFallSeconds = 1.6f, FlashSeconds = 1.2f, ScarSeconds = 90f;
    const int BombsPerRaid = 5;

    sealed class RaidEffect
    {
        public Node3D Bomb = null!;
        public MeshInstance3D? Flash;
        public Vector3 Target;
        public double Age, Delay;
    }

    readonly List<RaidEffect> _raidEffects = new();
    readonly List<(MeshInstance3D Scar, double Age)> _scars = new();
    Node3D? _raidRoot;
    int _raidEventsSeen;
    SphereMesh? _bombMesh, _flashMesh;
    CylinderMesh? _scarMesh;
    StandardMaterial3D? _bombMaterial, _flashMaterial, _scarMaterial;
    readonly RandomNumberGenerator _raidLook = new() { Seed = 1938 };

    void UpdateRaidEffects(double delta)
    {
        _raidRoot ??= new Node3D { Name = "Raids" };
        if (_raidRoot.GetParent() == null) AddChild(_raidRoot);
        for (int id = Math.Max(_raidEventsSeen, Sim.EventsDropped); id < Sim.EventCount; id++)
        {
            var e = Sim.Events[id - Sim.EventsDropped];
            if (e.Kind == SimEventKind.Raid && e.Severity == EventSeverity.Serious) StartRaidEffect(e.X, e.Z);
        }
        _raidEventsSeen = Sim.EventCount;

        for (int k = _raidEffects.Count - 1; k >= 0; k--)
        {
            var fx = _raidEffects[k];
            fx.Age += delta;
            double t = fx.Age - fx.Delay;
            if (t < 0) { fx.Bomb.Visible = false; continue; }
            if (t < BombFallSeconds)
            {
                fx.Bomb.Visible = true;
                float f = (float)(t / BombFallSeconds);
                fx.Bomb.Position = fx.Target + new Vector3(0, BombStartHeight * (1f - f * f), 0);
                continue;
            }
            fx.Bomb.Visible = false;
            double flash = t - BombFallSeconds;
            if (fx.Flash == null)
            {
                fx.Flash = new MeshInstance3D { Mesh = _flashMesh, MaterialOverride = _flashMaterial, Position = fx.Target + new Vector3(0, 2f, 0) };
                _raidRoot.AddChild(fx.Flash);
                var scar = new MeshInstance3D { Mesh = _scarMesh, MaterialOverride = _scarMaterial, Position = fx.Target + new Vector3(0, 0.15f, 0) };
                _raidRoot.AddChild(scar);
                _scars.Add((scar, 0));
            }
            if (flash >= FlashSeconds)
            {
                fx.Flash.QueueFree();
                fx.Bomb.QueueFree();
                _raidEffects.RemoveAt(k);
                continue;
            }
            float g = (float)(flash / FlashSeconds);
            fx.Flash.Scale = Vector3.One * (1f + 11f * g);
        }
        for (int k = _scars.Count - 1; k >= 0; k--)
        {
            var (scar, age) = _scars[k];
            age += delta;
            if (age >= ScarSeconds) { scar.QueueFree(); _scars.RemoveAt(k); continue; }
            _scars[k] = (scar, age);
        }
    }

    void StartRaidEffect(float x, float z)
    {
        _bombMesh ??= new SphereMesh { Radius = 1.1f, Height = 2.6f };
        _flashMesh ??= new SphereMesh { Radius = 2.2f, Height = 4.4f };
        _scarMesh ??= new CylinderMesh { TopRadius = 7f, BottomRadius = 7f, Height = 0.2f };
        _bombMaterial ??= new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.1f, 0.12f) };
        _flashMaterial ??= new StandardMaterial3D { AlbedoColor = new Color(1f, 0.65f, 0.2f), EmissionEnabled = true, Emission = new Color(1f, 0.55f, 0.15f), EmissionEnergyMultiplier = 3f };
        _scarMaterial ??= new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.07f, 0.06f) };
        for (int b = 0; b < BombsPerRaid; b++)
        {
            float bx = x + _raidLook.RandfRange(-45f, 45f), bz = z + _raidLook.RandfRange(-45f, 45f);
            var target = new Vector3(bx, HeightAt(bx, bz), bz);
            var bomb = new MeshInstance3D { Mesh = _bombMesh, MaterialOverride = _bombMaterial, Visible = false };
            _raidRoot!.AddChild(bomb);
            _raidEffects.Add(new RaidEffect { Bomb = bomb, Target = target, Delay = b * 0.35 + _raidLook.RandfRange(0f, 0.3f) });
        }
    }
}
