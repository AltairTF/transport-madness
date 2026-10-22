using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Draws the Models of <c>data/models.json</c> (M7): one MultiMesh per Model, each filled in bulk every frame like the whitebox boxes. A Model that cannot be read is
/// reported once by <see cref="Has"/> returning false and the caller draws its box instead. A Model faces -Z and stands on y = 0; <see cref="Add"/> turns it to a heading.
/// </summary>
public sealed class ModelDraw
{

    sealed class Batch
    {
        public MultiMesh Mesh = null!;
        public float[] Buffer = Array.Empty<float>();
        public int Count;
        public float Height;
    }

    static readonly int[] ScaledSlots = { 0, 1, 2, 4, 5, 6, 8, 9, 10 };

    readonly Node3D _root;
    readonly GodotObject? _library;
    readonly Dictionary<(string, int), Batch?> _batches = new();
    readonly Dictionary<string, int> _variantCounts = new();
    readonly bool _shadows;
    readonly bool _tinted;
    readonly float _rangeEnd;
    readonly int _stride; // floats per instance: the 3x4 transform, and a colour when tinted

    /// <param name="rangeEnd">Beyond this distance from the camera the Model is not drawn (0: always).</param>
    /// <param name="tinted">Each instance takes a colour that multiplies the Model's own (white leaves it as it is): for buildings that go grey, dark or take a map overlay.</param>
    public ModelDraw(Node parent, bool shadows = false, bool tinted = false, float rangeEnd = 0f)
    {
        _rangeEnd = rangeEnd;
        _root = new Node3D { Name = "Models" };
        parent.AddChild(_root);
        _shadows = shadows;
        _tinted = tinted;
        _stride = tinted ? 16 : 12;
        _library = GD.Load<GDScript>("res://game/model_library.gd").Call("load_file").AsGodotObject();
        if (_library != null)
            foreach (var line in _library.Get("errors").AsStringArray())
                GD.PushWarning("models.json: " + line);
    }

    /// <summary>True when the Model exists and is read; it is baked into a mesh the first time it is asked for.</summary>
    public bool Has(string id, int variant = 0) => Prepare(id, variant) != null;

    /// <summary>How many variants a Model has (0 for an unknown one).</summary>
    public int Variants(string id)
    {
        if (_library == null) return 0;
        if (!_variantCounts.TryGetValue(id, out int count))
            _variantCounts[id] = count = _library.Call("variant_count", id).AsInt32();
        return count;
    }

    /// <summary>Which variant a key (a building's id) chooses; the same as <c>pick</c> of the library, without the call into GDScript.</summary>
    public int Pick(string id, int key)
    {
        int count = Variants(id);
        if (count <= 1) return 0;
        long mixed = (long)key * 2654435761L >> 7;
        return (int)((mixed % count + count) % count);
    }

    /// <summary>True when the id is a composite (a few Models standing together, built node by node, not drawn by a MultiMesh).</summary>
    public bool HasComposite(string id) => _library != null && _library.Call("has", id).AsBool() && _library.Call("is_composite", id).AsBool();

    /// <summary>A fresh Node3D of a composite, or null.</summary>
    public Node3D? Compose(string id) => _library?.Call("instance", id, 0).AsGodotObject() as Node3D;

    /// <summary>How tall the Model stands, in metres (for what goes on its roof); 0 for an unknown one.</summary>
    public float HeightOf(string id, int variant = 0) => Prepare(id, variant)?.Height ?? 0f;

    /// <summary>The node that holds every MultiMesh of this set, so that a whole kind of Model can be hidden at once.</summary>
    public Node3D Root => _root;

    public void Begin()
    {
        foreach (var batch in _batches.Values)
            if (batch != null) batch.Count = 0;
    }

    /// <summary>One instance of a Model at a position, going along (<paramref name="fx"/>, <paramref name="fz"/>) and pitched by <paramref name="rise"/> (the grade, rise over run along the heading; 0 on the flat).</summary>
    public void Add(string id, float x, float y, float z, float fx, float fz, float rise, int variant = 0, Color? tint = null, float scale = 1f)
    {
        var batch = Prepare(id, variant);
        if (batch == null) return;
        float tilt = 1f / MathF.Sqrt(1f + rise * rise);
        float slope = rise * tilt;
        int o = batch.Count * _stride;
        if (o + _stride > batch.Buffer.Length)
        {
            Array.Resize(ref batch.Buffer, Math.Max(64 * _stride, batch.Buffer.Length * 2));
            batch.Mesh.InstanceCount = batch.Buffer.Length / _stride;
        }
        var b = batch.Buffer;
        // columns: X = to the right of the heading, Y = up (tilted with the grade), Z = backwards (the Model's front is -Z)
        b[o + 0] = -fz;          b[o + 1] = -fx * slope; b[o + 2] = -fx * tilt;  b[o + 3] = x;
        b[o + 4] = 0f;           b[o + 5] = tilt;        b[o + 6] = -slope;      b[o + 7] = y;
        b[o + 8] = fx;           b[o + 9] = -fz * slope; b[o + 10] = -fz * tilt; b[o + 11] = z;
        if (scale != 1f)
            foreach (int k in ScaledSlots) b[o + k] *= scale;
        if (_tinted)
        {
            var c = tint ?? Colors.White;
            b[o + 12] = c.R; b[o + 13] = c.G; b[o + 14] = c.B; b[o + 15] = 1f;
        }
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

    Batch? Prepare(string id, int variant = 0)
    {
        var key = (id, variant);
        if (_batches.TryGetValue(key, out var known)) return known;
        Batch? batch = null;
        if (_library != null && _library.Call("has", id).AsBool() && variant >= 0 && variant < _library.Call("variant_count", id).AsInt32() && !_library.Call("is_composite", id).AsBool())
        {
            var baked = _library.Call("baked", id, variant).AsGodotDictionary();
            if (baked.Count > 0 && baked["mesh"].AsGodotObject() is Mesh mesh)
            {
                if (_tinted) mesh = TintCapable(mesh);
                var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = _tinted, Mesh = mesh, InstanceCount = 0 };
                batch = new Batch { Mesh = multi, Height = baked["aabb"].AsAabb().Size.Y };
                _root.AddChild(new MultiMeshInstance3D
                {
                    Name = "Model_" + id.Replace('.', '_') + "_" + variant,
                    Multimesh = multi,
                    CastShadow = _shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
                    VisibilityRangeEnd = _rangeEnd,
                });
            }
        }
        if (batch == null) GD.PushWarning("Model " + id + " variant " + variant + " is not available; its whitebox is drawn");
        _batches[key] = batch;
        return batch;
    }

    /// <summary>The mesh with its materials copied so that the colour of an instance multiplies the texture.</summary>
    static Mesh TintCapable(Mesh mesh)
    {
        for (int i = 0; i < mesh.GetSurfaceCount(); i++)
        {
            if (mesh.SurfaceGetMaterial(i) is BaseMaterial3D material)
            {
                var copy = (BaseMaterial3D)material.Duplicate();
                copy.VertexColorUseAsAlbedo = true;
                mesh.SurfaceSetMaterial(i, copy);
            }
        }
        return mesh;
    }
}
