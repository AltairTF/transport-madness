using System;
using System.Threading.Tasks;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>
/// Save and load (M6a): the snapshot is taken here between two steps, on the main thread, and compressed and written on
/// another thread, so the game stops only for the snapshot. A loaded game replaces the world as a new one does.
/// </summary>
public partial class SimHost
{
    Task? _saving;
    string _saveError = "";
    readonly object _saveLock = new();
    double _lastCaptureMs;

    /// <summary>The block of the game layer's own state (camera, feed) that came with the last loaded save; empty for a new game.</summary>
    public byte[] LoadedHostBlob { get; private set; } = Array.Empty<byte>();

    public long CurrentTick => Sim.Tick;
    /// <summary>Commands executed since the game was started or loaded: with the tick, what tells the game layer there is progress to save.</summary>
    public int CommandCount => Sim.CommandLog.Count;
    public long MonthIndex => Sim.MonthIndex;
    /// <summary>How long the last snapshot held the main thread, in milliseconds.</summary>
    public double LastCaptureMs => _lastCaptureMs;
    /// <summary>A save is still being written.</summary>
    public bool IsSaving => _saving is { IsCompleted: false };

    /// <summary>
    /// Saves the game under <paramref name="path"/> (a <c>user://</c> path) with <paramref name="name"/> for the list and the game layer's
    /// <paramref name="hostBlob"/>. Returns "" when the snapshot was taken (the file is written in the background; see <see cref="TakeSaveError"/>),
    /// otherwise why not.
    /// </summary>
    public string SaveGameTo(string path, string name, byte[] hostBlob)
    {
        if (IsSaving) _saving!.Wait();
        byte[] snapshot;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { snapshot = SaveGame.Capture(Sim); }
        catch (Exception e) { return "The game could not be saved: " + e.Message; }
        _lastCaptureMs = clock.Elapsed.TotalMilliseconds;
        var info = SaveGame.InfoOf(Sim, name, DateTime.UtcNow);
        string file = ProjectSettings.GlobalizePath(path);
        _saving = Task.Run(() =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                SaveGame.Write(file, info, hostBlob, snapshot);
            }
            catch (Exception e)
            {
                lock (_saveLock) _saveError = "The save could not be written: " + e.Message;
            }
        });
        return "";
    }

    /// <summary>Why the last save in the background failed, once; "" when nothing went wrong.</summary>
    public string TakeSaveError()
    {
        lock (_saveLock)
        {
            string error = _saveError;
            _saveError = "";
            return error;
        }
    }

    /// <summary>Waits for a save being written (before leaving the game).</summary>
    public void FinishSaving() => _saving?.Wait();

    /// <summary>Loads the save at <paramref name="path"/> in place of the world. Returns "" when it was loaded, otherwise why not (the world is left as it was).</summary>
    public string LoadGameFrom(string path)
    {
        FinishSaving();
        try
        {
            var (sim, _, host) = SaveGame.Load(ProjectSettings.GlobalizePath(path));
            Sim = sim;
            LoadedHostBlob = host;
        }
        catch (SaveFormatException e) { return e.Message; }
        catch (System.IO.IOException e) { return "The save could not be read: " + e.Message; }
        catch (UnauthorizedAccessException e) { return "The save could not be read: " + e.Message; }
        // The news and warnings from before the save were shown then (the feed comes back with the game layer's block), so they are not told again.
        _eventsShown = Sim.EventCount;
        _warningsShown = Sim.RailWarnings.Count;
        RebuildRoads();
        _accumulator = 0;
        return "";
    }

    public override void _ExitTree() => FinishSaving();
}
