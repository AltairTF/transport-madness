using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace TransportMadness.Sim;

/// <summary>What the list of saves shows without loading the game.</summary>
/// <param name="Name">The slot's name, as the player typed it or as the game chose it ("Autosave 2").</param>
/// <param name="MonthIndex">Months since the start of the campaign (January 1938 is 0).</param>
/// <param name="Year">The date of the game, for the list (<see cref="Month"/> is 1 to 12).</param>
/// <param name="SavedAt">Real time of the save, UTC.</param>
public sealed record SaveInfo(string Name, long Tick, long MonthIndex, int Year, int Month, long Money, float Standing, DateTime SavedAt);

/// <summary>A save that cannot be read: another version of the game, or a damaged file.</summary>
public sealed class SaveFormatException(string message) : Exception(message);

/// <summary>
/// Save and load (M6a, <c>docs/m6-plan.md</c>): a save is a full snapshot of the <see cref="Simulation"/>, the tuning it was
/// started with included, written by <see cref="StateGraph"/>. The file is a short header (a magic word, the format version,
/// the <see cref="SaveInfo"/> as JSON and an opaque block of the game layer's own state), then the compressed snapshot.
/// Saving is split so that the game can take the snapshot between two steps (<see cref="Capture"/>, on the main thread)
/// and compress and write it on another thread (<see cref="Write(Stream, SaveInfo, byte[], byte[])"/>).
/// </summary>
public static class SaveGame
{
    static readonly byte[] Magic = "TMSAVE01"u8.ToArray();
    /// <summary>Changes when the file layout or the way <see cref="StateGraph"/> writes changes, or when the generated land changes for a seed (2: the new terrain generator; 4: the town upgrade, whose lots carry the biggest size a building may grow to; 5: the village redo, no lots and streets that grow; 6: road types, dirt road and lanes each way); a save of another version is refused.</summary>
    public const int FormatVersion = 6;

    /// <summary>The snapshot of the whole simulation, uncompressed. Call between steps.</summary>
    public static byte[] Capture(Simulation sim) => StateGraph.Write(sim);

    /// <summary>The info of the list for <paramref name="sim"/> as it is now.</summary>
    public static SaveInfo InfoOf(Simulation sim, string name, DateTime savedAt) =>
        new(name, sim.Tick, sim.MonthIndex, sim.Today.Year, sim.Today.Month, sim.Company.Money, sim.Company.Standing, savedAt);

    /// <summary>Writes a save: the header, then <paramref name="snapshot"/> (from <see cref="Capture"/>) compressed. Safe on any thread.</summary>
    public static void Write(Stream stream, SaveInfo info, byte[] host, byte[] snapshot)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(FormatVersion);
        var json = JsonSerializer.SerializeToUtf8Bytes(info);
        w.Write(json.Length);
        w.Write(json);
        w.Write(host.Length);
        w.Write(host);
        w.Write(snapshot.Length);
        w.Flush();
        using (var gz = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(snapshot);
        stream.Flush();
    }

    /// <summary>Writes a save to a file, through a temporary file so that a crash midway never leaves half a save under the name.</summary>
    public static void Write(string path, SaveInfo info, byte[] host, byte[] snapshot)
    {
        string temp = path + ".tmp";
        using (var file = File.Create(temp)) Write(file, info, host, snapshot);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Saves <paramref name="sim"/> in one go (tests, tools).</summary>
    public static void Save(Simulation sim, Stream stream, SaveInfo info, byte[]? host = null) =>
        Write(stream, info, host ?? Array.Empty<byte>(), Capture(sim));

    /// <summary>The header of a save: its info and the game layer's block. Throws <see cref="SaveFormatException"/>.</summary>
    public static (SaveInfo Info, byte[] Host) ReadHeader(Stream stream)
    {
        var (info, host, _) = Header(stream);
        return (info, host);
    }

    public static (SaveInfo Info, byte[] Host) ReadHeader(string path)
    {
        using var file = File.OpenRead(path);
        return ReadHeader(file);
    }

    /// <summary>Loads a save. The command log of the loaded game starts empty. Throws <see cref="SaveFormatException"/>.</summary>
    public static (Simulation Sim, SaveInfo Info, byte[] Host) Load(Stream stream)
    {
        var (info, host, length) = Header(stream);
        var snapshot = new byte[length];
        using (var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true))
        {
            int read = 0;
            while (read < length)
            {
                int n = gz.Read(snapshot, read, length - read);
                if (n == 0) throw new SaveFormatException("The save is cut short.");
                read += n;
            }
        }
        return (Restore(snapshot), info, host);
    }

    public static (Simulation Sim, SaveInfo Info, byte[] Host) Load(string path)
    {
        using var file = File.OpenRead(path);
        return Load(file);
    }

    /// <summary>Makes a simulation again from a snapshot taken by <see cref="Capture"/>.</summary>
    public static Simulation Restore(byte[] snapshot) => (Simulation)StateGraph.Read(snapshot);

    static (SaveInfo Info, byte[] Host, int Length) Header(Stream stream)
    {
        try
        {
            using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var magic = r.ReadBytes(Magic.Length);
            if (!magic.AsSpan().SequenceEqual(Magic)) throw new SaveFormatException("This is not a save of this game.");
            int version = r.ReadInt32();
            if (version != FormatVersion)
                throw new SaveFormatException($"This save was made by another version of the game (save format {version}, this game reads {FormatVersion}).");
            var info = JsonSerializer.Deserialize<SaveInfo>(r.ReadBytes(r.ReadInt32())) ?? throw new SaveFormatException("The save has no info.");
            var host = r.ReadBytes(r.ReadInt32());
            int length = r.ReadInt32();
            if (length < 0) throw new SaveFormatException("The save is damaged.");
            return (info, host, length);
        }
        catch (EndOfStreamException) { throw new SaveFormatException("The save is cut short."); }
        catch (JsonException) { throw new SaveFormatException("The save is damaged."); }
    }
}
