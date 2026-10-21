using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace TransportMadness.Sim;

/// <summary>
/// A field the snapshot of a save leaves out. On load it gets a new empty object of its type (a list, a dictionary)
/// or its default value. Only for state that must start again with a loaded game, such as the command log.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class NotSavedAttribute : Attribute;

/// <summary>
/// Writes and reads a whole object graph by its fields (M6a). Every instance field of every object reachable from the root is
/// written, private and readonly ones included, so a field added to the simulation later is saved without anyone having to
/// remember it. Shared references stay shared and cycles are fine. Arrays of plain values are copied as bytes.
///
/// The output is a table of the types used, each with its fields (names and types), then the objects. Reading checks the
/// table against the types of the running build first: a save whose types have changed shape is refused, never misread.
///
/// Dictionaries and hash sets whose keys are not plain numbers (tuples, strings) are written as their entries and filled
/// again on load, because the hash codes of those keys can differ from one process to the next. Everything else, the
/// collections of the base library included, is written by its fields, so a loaded object is the same down to the order
/// of its free slots.
/// </summary>
static class StateGraph
{
    const int Version = 1;
    const BindingFlags DeclaredFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    enum Shape : byte { Plain, Enum, Struct, Nullable, Class, Array, Rebuilt, Boxed }
    enum Token : byte { Null, Back, String, New }

    /// <summary>What the graph needs to know of a type; built once per type and kept.</summary>
    sealed class Layout
    {
        public required Type Type;
        public required Shape Shape;
        public FieldInfo[] Fields = Array.Empty<FieldInfo>();
        public bool[] Skip = Array.Empty<bool>();
        public Type? Element;          // arrays: the element type; nullable: the inner type
        public bool Bulk;              // arrays: the elements are plain values copied as bytes
        public int ElementSize;
        public Type? KeyType, ValueType; // rebuilt collections (ValueType null for a hash set)
        public string Signature = "";
    }

    static readonly Dictionary<Type, Layout> Layouts = new();
    static readonly object LayoutLock = new();

    static readonly Assembly SimAssembly = typeof(Simulation).Assembly;
    static readonly Assembly CoreAssembly = typeof(object).Assembly;

    static Layout LayoutOf(Type t)
    {
        lock (LayoutLock)
        {
            if (Layouts.TryGetValue(t, out var known)) return known;
            var layout = Build(t);
            Layouts[t] = layout;
            return layout;
        }
    }

    static Layout Build(Type t)
    {
        if (t.IsPointer || t.IsByRef || t == typeof(IntPtr) || t == typeof(UIntPtr) || typeof(Delegate).IsAssignableFrom(t)
            || typeof(MemberInfo).IsAssignableFrom(t) || typeof(Stream).IsAssignableFrom(t))
            throw new NotSupportedException($"A save cannot hold a {TypeName(t)}.");
        if (t.IsEnum)
            return new Layout
            {
                Type = t, Shape = Shape.Enum,
                Signature = TypeName(Enum.GetUnderlyingType(t)) + ":" + string.Join(",", Enum.GetNames(t).Zip(Enum.GetValuesAsUnderlyingType(t).Cast<object>(), (n, v) => $"{n}={v}")),
            };
        if (IsPlain(t)) return new Layout { Type = t, Shape = Shape.Plain, Signature = TypeName(t) };
        if (t.IsArray)
        {
            if (t.GetArrayRank() != 1 || !t.IsSZArray) throw new NotSupportedException($"A save cannot hold the array {TypeName(t)}.");
            var e = t.GetElementType()!;
            bool bulk = e.IsValueType && !ContainsReferences(e);
            return new Layout { Type = t, Shape = Shape.Array, Element = e, Bulk = bulk, ElementSize = bulk ? SizeOf(e) : 0 };
        }
        if (Nullable.GetUnderlyingType(t) is { } inner) return new Layout { Type = t, Shape = Shape.Nullable, Element = inner };
        if (t.IsGenericType && !t.IsValueType)
        {
            var def = t.GetGenericTypeDefinition();
            var args = t.GetGenericArguments();
            if ((def == typeof(Dictionary<,>) || def == typeof(HashSet<>)) && !IsPlain(args[0]) && !args[0].IsEnum)
                return new Layout { Type = t, Shape = Shape.Rebuilt, KeyType = args[0], ValueType = args.Length > 1 ? args[1] : null };
        }
        var fields = new List<FieldInfo>();
        var levels = new List<Type>();
        for (var level = t; level != null && level != typeof(object) && level != typeof(ValueType); level = level.BaseType) levels.Add(level);
        levels.Reverse();
        foreach (var level in levels)
            fields.AddRange(level.GetFields(DeclaredFields).OrderBy(f => f.MetadataToken));
        var skip = fields.Select(f => f.IsDefined(typeof(NotSavedAttribute), false)).ToArray();
        var signature = string.Join(";", fields.Select((f, i) => $"{f.Name}:{TypeName(f.FieldType)}{(skip[i] ? "!" : "")}"));
        return new Layout { Type = t, Shape = t.IsValueType ? Shape.Struct : Shape.Class, Fields = fields.ToArray(), Skip = skip, Signature = signature };
    }

    static bool IsPlain(Type t) => Type.GetTypeCode(t) switch
    {
        TypeCode.Boolean or TypeCode.Char or TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32
            or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal => !t.IsEnum,
        _ => false,
    };

    static readonly MethodInfo ContainsReferencesMethod = typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!;
    static readonly MethodInfo SizeOfMethod = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!;
    static bool ContainsReferences(Type t) => (bool)ContainsReferencesMethod.MakeGenericMethod(t).Invoke(null, null)!;
    static int SizeOf(Type t) => (int)SizeOfMethod.MakeGenericMethod(t).Invoke(null, null)!;

    /// <summary>A name with no assembly versions in it, so the signature is the same on every runtime of the same build.</summary>
    static string TypeName(Type t)
    {
        if (t.IsArray) return TypeName(t.GetElementType()!) + "[]";
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
            return t.GetGenericTypeDefinition().FullName + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName)) + ">";
        return t.FullName ?? t.Name;
    }

    static Span<byte> BytesOf(Array array, int elementSize) =>
        MemoryMarshal.CreateSpan(ref MemoryMarshal.GetArrayDataReference(array), array.Length * elementSize);

    // ---------------------------------------------------------------- writing

    public static byte[] Write(object root)
    {
        var writer = new GraphWriter();
        return writer.Run(root);
    }

    sealed class GraphWriter
    {
        readonly MemoryStream _body = new();
        readonly BinaryWriter _w;
        readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
        readonly Queue<object> _pending = new();
        readonly Dictionary<Type, int> _typeIds = new();
        readonly List<Type> _types = new();
        string _where = "the simulation";

        public GraphWriter() => _w = new BinaryWriter(_body, Encoding.UTF8, leaveOpen: true);

        public byte[] Run(object root)
        {
            WriteRef(root);
            while (_pending.Count > 0) WriteBody(_pending.Dequeue());
            _w.Flush();

            var output = new MemoryStream((int)_body.Length + 64 * _types.Count);
            using (var head = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
            {
                head.Write(Version);
                head.Write7BitEncodedInt(_types.Count);
                foreach (var t in _types) WriteTypeEntry(head, t);
                head.Write7BitEncodedInt(_ids.Count);
            }
            _body.Position = 0;
            _body.CopyTo(output);
            return output.ToArray();
        }

        void WriteTypeEntry(BinaryWriter head, Type t)
        {
            if (t.IsArray)
            {
                head.Write((byte)2);
                head.Write7BitEncodedInt(_typeIds[t.GetElementType()!]);
            }
            else if (t.IsGenericType && !t.IsGenericTypeDefinition)
            {
                head.Write((byte)1);
                head.Write7BitEncodedInt(_typeIds[t.GetGenericTypeDefinition()]);
                var args = t.GetGenericArguments();
                head.Write7BitEncodedInt(args.Length);
                foreach (var a in args) head.Write7BitEncodedInt(_typeIds[a]);
            }
            else
            {
                head.Write((byte)0);
                head.Write(t.Assembly == SimAssembly ? "" : t.Assembly == CoreAssembly ? "*" : t.Assembly.GetName().Name!);
                head.Write(t.FullName!);
            }
            head.Write(t.IsGenericTypeDefinition ? "" : LayoutOf(t).Signature);
        }

        /// <summary>The type's number in the table, adding it (and the types it is made of) on first use.</summary>
        int TypeId(Type t)
        {
            if (_typeIds.TryGetValue(t, out int id)) return id;
            if (t.IsArray) TypeId(t.GetElementType()!);
            else if (t.IsGenericType && !t.IsGenericTypeDefinition)
            {
                TypeId(t.GetGenericTypeDefinition());
                foreach (var a in t.GetGenericArguments()) TypeId(a);
            }
            if (!t.IsGenericTypeDefinition)
            {
                // The shapes of the fields' types are checked on load too, values and structs written inline included.
                var layout = LayoutOf(t);
                _typeIds[t] = id = _types.Count;
                _types.Add(t);
                foreach (var f in layout.Fields) if (f.FieldType.IsValueType) TypeId(f.FieldType);
                if (layout.Element != null) TypeId(layout.Element);
                return id;
            }
            _typeIds[t] = id = _types.Count;
            _types.Add(t);
            return id;
        }

        void WriteRef(object? value)
        {
            if (value is null) { _w.Write((byte)Token.Null); return; }
            if (value is string s) { _w.Write((byte)Token.String); _w.Write(s); return; }
            if (_ids.TryGetValue(value, out int id)) { _w.Write((byte)Token.Back); _w.Write7BitEncodedInt(id); return; }
            var t = value.GetType();
            Layout layout;
            try { layout = LayoutOf(t); }
            catch (NotSupportedException e) { throw new NotSupportedException($"{e.Message} (in {_where})"); }
            _w.Write((byte)Token.New);
            _w.Write7BitEncodedInt(TypeId(t));
            _ids[value] = _ids.Count;
            switch (layout.Shape)
            {
                case Shape.Plain:
                case Shape.Enum:
                    WriteValue(t, value); // a boxed number: written at once
                    break;
                case Shape.Array:
                    _w.Write7BitEncodedInt(((Array)value).Length);
                    _pending.Enqueue(value);
                    break;
                default:
                    _pending.Enqueue(value);
                    break;
            }
        }

        void WriteBody(object obj)
        {
            var layout = LayoutOf(obj.GetType());
            switch (layout.Shape)
            {
                case Shape.Array:
                {
                    var array = (Array)obj;
                    _where = TypeName(layout.Type);
                    if (layout.Bulk) _w.Write(BytesOf(array, layout.ElementSize));
                    else for (int i = 0; i < array.Length; i++) WriteValue(layout.Element!, array.GetValue(i));
                    break;
                }
                case Shape.Rebuilt:
                    WriteRebuilt(layout, obj);
                    break;
                default:
                    WriteFields(layout, obj);
                    break;
            }
        }

        void WriteFields(Layout layout, object obj)
        {
            for (int i = 0; i < layout.Fields.Length; i++)
            {
                if (layout.Skip[i]) continue;
                var f = layout.Fields[i];
                _where = $"{layout.Type.Name}.{f.Name}";
                WriteValue(f.FieldType, f.GetValue(obj));
            }
        }

        void WriteRebuilt(Layout layout, object obj)
        {
            var comparer = layout.Type.GetProperty("Comparer")!.GetValue(obj);
            var standard = typeof(EqualityComparer<>).MakeGenericType(layout.KeyType!).GetProperty("Default")!.GetValue(null);
            if (!Equals(comparer, standard))
                throw new NotSupportedException($"A save cannot hold a {TypeName(layout.Type)} with its own comparer (in {_where}).");
            if (layout.ValueType != null)
            {
                var dict = (System.Collections.IDictionary)obj;
                _w.Write7BitEncodedInt(dict.Count);
                foreach (System.Collections.DictionaryEntry e in dict)
                {
                    WriteValue(layout.KeyType!, e.Key);
                    WriteValue(layout.ValueType, e.Value);
                }
            }
            else
            {
                var items = ((System.Collections.IEnumerable)obj).Cast<object?>().ToList();
                _w.Write7BitEncodedInt(items.Count);
                foreach (var item in items) WriteValue(layout.KeyType!, item);
            }
        }

        void WriteValue(Type t, object? value)
        {
            if (!t.IsValueType) { WriteRef(value); return; }
            var layout = LayoutOf(t);
            switch (layout.Shape)
            {
                case Shape.Plain:
                    WritePlain(value!);
                    break;
                case Shape.Enum:
                    WritePlain(Convert.ChangeType(value!, Enum.GetUnderlyingType(t)));
                    break;
                case Shape.Nullable:
                    _w.Write(value != null);
                    if (value != null) WriteValue(layout.Element!, value);
                    break;
                default:
                    TypeId(t);
                    for (int i = 0; i < layout.Fields.Length; i++)
                        if (!layout.Skip[i]) WriteValue(layout.Fields[i].FieldType, layout.Fields[i].GetValue(value));
                    break;
            }
        }

        void WritePlain(object v)
        {
            switch (v)
            {
                case bool b: _w.Write(b); break;
                case char c: _w.Write((ushort)c); break;
                case sbyte sb: _w.Write(sb); break;
                case byte by: _w.Write(by); break;
                case short sh: _w.Write(sh); break;
                case ushort us: _w.Write(us); break;
                case int i: _w.Write(i); break;
                case uint ui: _w.Write(ui); break;
                case long l: _w.Write(l); break;
                case ulong ul: _w.Write(ul); break;
                case float f: _w.Write(f); break;
                case double d: _w.Write(d); break;
                case decimal m: _w.Write(m); break;
                default: throw new NotSupportedException($"Not a plain value: {v.GetType()}");
            }
        }
    }

    // ---------------------------------------------------------------- reading

    public static object Read(byte[] data)
    {
        try { return new GraphReader(data).Run(); }
        catch (EndOfStreamException) { throw new SaveFormatException("The save is cut short."); }
        catch (Exception e) when (e is InvalidCastException or ArgumentException or IndexOutOfRangeException or FormatException)
        {
            throw new SaveFormatException("The save is damaged (" + e.Message + ").");
        }
    }

    sealed class GraphReader
    {
        readonly BinaryReader _r;
        readonly byte[] _data;
        Type[] _types = Array.Empty<Type>();
        object?[] _objects = Array.Empty<object?>();
        int _nextId;
        readonly Queue<object> _pending = new();
        readonly List<(object Target, Layout Layout, List<object?> Keys, List<object?> Values)> _refills = new();

        public GraphReader(byte[] data)
        {
            _data = data;
            _r = new BinaryReader(new MemoryStream(data, writable: false), Encoding.UTF8);
        }

        public object Run()
        {
            int version = _r.ReadInt32();
            if (version != Version) throw new SaveFormatException($"This save was made by another version of the game (snapshot {version}, this game reads {Version}).");
            int typeCount = _r.Read7BitEncodedInt();
            _types = new Type[typeCount];
            for (int i = 0; i < typeCount; i++) _types[i] = ReadTypeEntry();
            _objects = new object?[_r.Read7BitEncodedInt()];

            var root = ReadRef() ?? throw new SaveFormatException("The save is empty.");
            while (_pending.Count > 0) ReadBody(_pending.Dequeue());
            // Dictionaries and sets keyed by tuples or strings are filled last, when every key is whole.
            foreach (var (target, layout, keys, values) in _refills)
            {
                if (layout.ValueType != null)
                {
                    var dict = (System.Collections.IDictionary)target;
                    for (int i = 0; i < keys.Count; i++) dict.Add(keys[i]!, values[i]);
                }
                else
                {
                    var add = layout.Type.GetMethod("Add")!;
                    foreach (var k in keys) add.Invoke(target, new[] { k });
                }
            }
            if (_r.BaseStream.Position != _data.Length) throw new SaveFormatException("The save is damaged (data after the end).");
            return root;
        }

        Type ReadTypeEntry()
        {
            byte kind = _r.ReadByte();
            Type t;
            switch (kind)
            {
                case 2:
                    t = _types[_r.Read7BitEncodedInt()].MakeArrayType();
                    break;
                case 1:
                {
                    var def = _types[_r.Read7BitEncodedInt()];
                    var args = new Type[_r.Read7BitEncodedInt()];
                    for (int i = 0; i < args.Length; i++) args[i] = _types[_r.Read7BitEncodedInt()];
                    t = def.MakeGenericType(args);
                    break;
                }
                case 0:
                {
                    string assembly = _r.ReadString();
                    string name = _r.ReadString();
                    var asm = assembly == "" ? SimAssembly : assembly == "*" ? CoreAssembly
                        : AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assembly) ?? Assembly.Load(assembly);
                    t = asm.GetType(name) ?? throw new SaveFormatException($"This save was made by another version of the game ({name} no longer exists).");
                    break;
                }
                default:
                    throw new SaveFormatException("The save is damaged (type table).");
            }
            string signature = _r.ReadString();
            if (!t.IsGenericTypeDefinition && LayoutOf(t).Signature != signature)
                throw new SaveFormatException($"This save was made by another version of the game ({TypeName(t)} has changed).");
            return t;
        }

        object? ReadRef()
        {
            var token = (Token)_r.ReadByte();
            switch (token)
            {
                case Token.Null: return null;
                case Token.String: return _r.ReadString();
                case Token.Back: return _objects[_r.Read7BitEncodedInt()];
                case Token.New: break;
                default: throw new SaveFormatException("The save is damaged (reference).");
            }
            var t = _types[_r.Read7BitEncodedInt()];
            var layout = LayoutOf(t);
            int id = _nextId++;
            object obj;
            switch (layout.Shape)
            {
                case Shape.Plain:
                case Shape.Enum:
                    obj = ReadValue(t)!;
                    _objects[id] = obj;
                    return obj;
                case Shape.Array:
                    obj = Array.CreateInstance(layout.Element!, _r.Read7BitEncodedInt());
                    break;
                case Shape.Rebuilt:
                    obj = Activator.CreateInstance(t)!;
                    break;
                default:
                    obj = RuntimeHelpers.GetUninitializedObject(t);
                    break;
            }
            _objects[id] = obj;
            _pending.Enqueue(obj);
            return obj;
        }

        void ReadBody(object obj)
        {
            var layout = LayoutOf(obj.GetType());
            switch (layout.Shape)
            {
                case Shape.Array:
                {
                    var array = (Array)obj;
                    if (layout.Bulk) _r.BaseStream.ReadExactly(BytesOf(array, layout.ElementSize));
                    else for (int i = 0; i < array.Length; i++) array.SetValue(ReadValue(layout.Element!), i);
                    break;
                }
                case Shape.Rebuilt:
                {
                    int count = _r.Read7BitEncodedInt();
                    var keys = new List<object?>(count);
                    var values = new List<object?>(layout.ValueType != null ? count : 0);
                    for (int i = 0; i < count; i++)
                    {
                        keys.Add(ReadValue(layout.KeyType!));
                        if (layout.ValueType != null) values.Add(ReadValue(layout.ValueType));
                    }
                    _refills.Add((obj, layout, keys, values));
                    break;
                }
                default:
                    ReadFields(layout, obj);
                    break;
            }
        }

        void ReadFields(Layout layout, object target)
        {
            for (int i = 0; i < layout.Fields.Length; i++)
            {
                var f = layout.Fields[i];
                f.SetValue(target, layout.Skip[i] ? Fresh(f.FieldType) : ReadValue(f.FieldType));
            }
        }

        static object? Fresh(Type t) =>
            t.IsValueType ? Activator.CreateInstance(t) : t.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(t) : null;

        object? ReadValue(Type t)
        {
            if (!t.IsValueType) return ReadRef();
            var layout = LayoutOf(t);
            switch (layout.Shape)
            {
                case Shape.Plain: return ReadPlain(t);
                case Shape.Enum: return Enum.ToObject(t, ReadPlain(Enum.GetUnderlyingType(t)));
                case Shape.Nullable: return _r.ReadBoolean() ? ReadValue(layout.Element!) : null;
                default:
                {
                    var box = RuntimeHelpers.GetUninitializedObject(t);
                    ReadFields(layout, box);
                    return box;
                }
            }
        }

        object ReadPlain(Type t) => Type.GetTypeCode(t) switch
        {
            TypeCode.Boolean => _r.ReadBoolean(),
            TypeCode.Char => (char)_r.ReadUInt16(),
            TypeCode.SByte => _r.ReadSByte(),
            TypeCode.Byte => _r.ReadByte(),
            TypeCode.Int16 => _r.ReadInt16(),
            TypeCode.UInt16 => _r.ReadUInt16(),
            TypeCode.Int32 => _r.ReadInt32(),
            TypeCode.UInt32 => _r.ReadUInt32(),
            TypeCode.Int64 => _r.ReadInt64(),
            TypeCode.UInt64 => _r.ReadUInt64(),
            TypeCode.Single => _r.ReadSingle(),
            TypeCode.Double => _r.ReadDouble(),
            TypeCode.Decimal => _r.ReadDecimal(),
            _ => throw new SaveFormatException("The save is damaged (value)."),
        };
    }
}
