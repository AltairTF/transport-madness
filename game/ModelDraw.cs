using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Draws the Models of <c>data/models.json</c> (M7): one MultiMesh per Model, each filled in bulk every frame like the whitebox boxes. A Model that cannot be read is
/// reported once by <see cref="Has"/> returning false and the caller draws its box instead. A Model faces -Z and stands on y = 0; <see cref="Add"/> turns it to a heading.
/// </summary>
public sealed class ModelDraw
{
    const int FloatsPerInstance = 12; // 3x4 transform

    sealed class Batch
    {
        public MultiMesh Mesh = null!;
        public float[] Buffer = Array.Empty<float>();
        public int Count;
        public float Height;
    }

    readonly Node _parent;
    readonly GodotObject? _library;
    readonly Dictionary<string, Batch?> _batches = new();
    readonly bool _shadows;

    public ModelDraw(Node parent, bool shadows = false)
    {
        _parent = parent;
        _shadows = shadows;
        _library = GD.Load<GDScript>("res://game/model_library.gd").Call("load_file").AsGodotObject();
        if (_library != null)
            foreach (var line in _library.Get("errors").AsStringArray())
                GD.PushWarning("models.json: " + line);
    }

    /// <summary>True when the Model exists and is read; it is baked into a mesh the first time it is asked for.</summary>
    public bool Has(string id) => Prepare(id) != null;

    /// <summary>How tall the Model stands, in metres (for what goes on its roof); 0 for an unknown one.</summary>
    public float HeightOf(string id) => Prepare(id)?.Height ?? 0f;

    public void Begin()
    {
        foreach (var batch in _batches.Values)
            if (batch != null) batch.Count = 0;
    }

    /// <summary>One instance of a Model at a position, going along (<paramref name="fx"/>, <paramref name="fz"/>) and pitched by <paramref name="rise"/> (the grade, rise over run along the heading; 0 on the flat).</summary>
    public void Add(string id, float x, float y, float z, float fx, float fz, float rise)
    {
        var batch = Prepare(id);
        if (batch == null) return;
        float tilt = 1f / MathF.Sqrt(1f + rise * rise);
        float slope = rise * tilt;
        int o = batch.Count * FloatsPerInstance;
        if (o + FloatsPerInstance > batch.Buffer.Length)
        {
            Array.Resize(ref batch.Buffer, Math.Max(64 * FloatsPerInstance, batch.Buffer.Length * 2));
            batch.Mesh.InstanceCount = batch.Buffer.Length / FloatsPerInstance;
        }
        var b = batch.Buffer;
        // columns: X = to the right of the heading, Y = up (tilted with the grade), Z = backwards (the Model's front is -Z)
        b[o + 0] = -fz;          b[o + 1] = -fx * slope; b[o + 2] = -fx * tilt;  b[o + 3] = x;
        b[o + 4] = 0f;           b[o + 5] = tilt;        b[o + 6] = -slope;      b[o + 7] = y;
        b[o + 8] = fx;           b[o + 9] = -fz * slope; b[o + 10] = -fz * tilt; b[o + 11] = z;
        batch.Count++;
    }

    public void End()
    {
        foreach (var batch in _batches.Values)
        {
            if (batch == null) continue;
            batch.Mesh.Buffer = batch.Buffer;
            batch.Mesh.VisibleInstanceCount = batch.Count;
        }
    }

    Batch? Prepare(string id)
    {
        if (_batches.TryGetValue(id, out var known)) return known;
        Batch? batch = null;
        if (_library != null && _library.Call("has", id).AsBool())
        {
            var baked = _library.Call("baked", id, 0).AsGodotDictionary();
            if (baked.Count > 0 && baked["mesh"].AsGodotObject() is Mesh mesh)
            {
                var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = 0 };
                batch = new Batch { Mesh = multi, Height = baked["aabb"].AsAabb().Size.Y };
                _parent.AddChild(new MultiMeshInstance3D
                {
                    Name = "Model_" + id.Replace('.', '_'),
                    Multimesh = multi,
                    CastShadow = _shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
                });
            }
        }
        if (batch == null) GD.PushWarning("Model " + id + " is not available; its whitebox is drawn");
        _batches[id] = batch;
        return batch;
    }
}
