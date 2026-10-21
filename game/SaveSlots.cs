using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>
/// The save files in <c>user://saves/</c> for the menus (M6a): the list with the info of each, the most recent one, deleting, and the
/// names of the files of each kind. A save is <c>&lt;file&gt;.tmsave</c> with its screenshot beside it as <c>&lt;file&gt;.png</c>.
/// Kinds: a named save of the player (<c>slot-…</c>), the three autosaves that rotate (<c>auto-1</c> to <c>auto-3</c>) and the quick save.
/// GDScript makes one with <c>load("res://game/SaveSlots.cs").new()</c>.
/// </summary>
public partial class SaveSlots : RefCounted
{
    public const string Folder = "user://saves";
    const string Extension = ".tmsave";
    const int Autosaves = 3;

    static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames;

    static string Dir => ProjectSettings.GlobalizePath(Folder);

    /// <summary>
    /// Every save, newest first: {path, shot (the screenshot's user:// path, "" when there is none), kind ("slot", "auto", "quick"), name, date
    /// ("March 1940"), money, standing, saved (local time as text), unix (seconds), error ("" or why it cannot be loaded)}.
    /// </summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> List()
    {
        var rows = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        if (!Directory.Exists(Dir)) return rows;
        var entries = Directory.GetFiles(Dir, "*" + Extension).Select(file =>
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            string path = $"{Folder}/{stem}{Extension}";
            string shot = File.Exists(Path.Combine(Dir, stem + ".png")) ? $"{Folder}/{stem}.png" : "";
            string kind = stem.StartsWith("auto-") ? "auto" : stem == "quick" ? "quick" : "slot";
            var row = new Godot.Collections.Dictionary { ["path"] = path, ["shot"] = shot, ["kind"] = kind, ["error"] = "" };
            DateTime saved = File.GetLastWriteTimeUtc(file);
            try
            {
                var (info, _) = SaveGame.ReadHeader(file);
                saved = info.SavedAt;
                row["name"] = info.Name;
                row["date"] = $"{MonthNames[Math.Clamp(info.Month - 1, 0, 11)]} {info.Year}";
                row["money"] = info.Money;
                row["standing"] = info.Standing;
            }
            catch (Exception e) when (e is SaveFormatException or IOException or UnauthorizedAccessException)
            {
                row["name"] = stem;
                row["date"] = "";
                row["money"] = 0L;
                row["standing"] = 0f;
                row["error"] = e.Message;
            }
            row["saved"] = saved.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            row["unix"] = new DateTimeOffset(saved).ToUnixTimeSeconds();
            return (Saved: saved, Row: row);
        }).OrderByDescending(e => e.Saved);
        foreach (var (_, row) in entries) rows.Add(row);
        return rows;
    }

    /// <summary>The path of the newest save that can be loaded, "" when there is none (Continue).</summary>
    public string Latest()
    {
        foreach (var row in List())
            if ((string)row["error"] == "") return (string)row["path"];
        return "";
    }

    /// <summary>Deletes a save and its screenshot.</summary>
    public bool Delete(string path)
    {
        string file = ProjectSettings.GlobalizePath(path);
        if (!File.Exists(file) || !file.EndsWith(Extension)) return false;
        File.Delete(file);
        string shot = Path.ChangeExtension(file, ".png");
        if (File.Exists(shot)) File.Delete(shot);
        return true;
    }

    /// <summary>The path of a named save: the name made safe for a file name; the same name gives the same file, so saving again overwrites it.</summary>
    public string SlotPath(string name)
    {
        var safe = new string(name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (safe == "") safe = "save";
        if (safe.Length > 40) safe = safe[..40];
        return $"{Folder}/slot-{safe}{Extension}";
    }

    public string QuickPath() => $"{Folder}/quick{Extension}";

    /// <summary>The autosave to write next: a missing one, else the oldest of the three.</summary>
    public string NextAutosavePath()
    {
        string best = "";
        DateTime oldest = DateTime.MaxValue;
        for (int k = 1; k <= Autosaves; k++)
        {
            string file = Path.Combine(Dir, $"auto-{k}{Extension}");
            if (!File.Exists(file)) return $"{Folder}/auto-{k}{Extension}";
            var at = File.GetLastWriteTimeUtc(file);
            if (at < oldest) { oldest = at; best = $"{Folder}/auto-{k}{Extension}"; }
        }
        return best;
    }

    /// <summary>The user:// path of the screenshot that goes with a save.</summary>
    public string ShotPath(string path) => path.EndsWith(Extension) ? path[..^Extension.Length] + ".png" : path + ".png";

    public bool Exists(string path) => File.Exists(ProjectSettings.GlobalizePath(path));
}
