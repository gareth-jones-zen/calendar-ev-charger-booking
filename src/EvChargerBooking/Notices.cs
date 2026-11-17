using System.Text;
using System.Text.Json.Nodes;
using PyCompat;

namespace EvChargerBooking;

/// <summary>What a scheduled run has already told you (2.1.0), so a run every 15 minutes doesn't repeat itself: each
/// question is notified once (and again only if it goes away and comes back), and a failure only on the third run in a
/// row that fails (a sign-in that's needed, on the first). Kept beside the state file, as
/// <c>{state}.notified.json</c>: <c>{"questions": {"text": "first notified"}, "failures": 0}</c>.</summary>
public static class Notices
{
    public const int FailuresBeforeNotice = 3;

    public static string PathFor(Settings settings) =>
        Path.Combine(Path.GetDirectoryName(settings.StateFile) ?? ".",
            Path.GetFileNameWithoutExtension(settings.StateFile) + ".notified.json");

    /// <summary>The questions not yet notified; records them, and forgets ones no longer asked. A run that worked
    /// clears the failure count.</summary>
    public static List<string> NewQuestions(Settings settings, IReadOnlyList<string> questions, DateTime now)
    {
        var state = Load(settings);
        var known = state["questions"] as JsonObject ?? [];
        var fresh = questions.Where(q => !known.ContainsKey(q)).Distinct().ToList();
        var kept = new JsonObject();
        foreach (var q in questions.Distinct())
            kept[q] = known.ContainsKey(q) ? known[q]!.DeepClone() : PyTime.Iso(now, "seconds");
        state["questions"] = kept;
        state["failures"] = 0;
        Save(settings, state);
        return fresh;
    }

    /// <summary>Counts a failed run; true when it should be notified: the first run needing a sign-in, otherwise the
    /// third failure in a row (and not again until a run works).</summary>
    public static bool Failed(Settings settings, bool signIn)
    {
        var state = Load(settings);
        var count = (state["failures"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0) + 1;
        state["failures"] = count;
        Save(settings, state);
        return signIn ? count == 1 : count == FailuresBeforeNotice;
    }

    private static JsonObject Load(Settings settings)
    {
        try
        {
            return File.Exists(PathFor(settings)) && JsonNode.Parse(File.ReadAllText(PathFor(settings))) is JsonObject o ? o : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];   // unreadable: start again, at worst notifying a question twice
        }
    }

    private static void Save(Settings settings, JsonObject state)
    {
        var path = PathFor(settings);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, PyJson.Dump(state, indent: 2, ensureAscii: false), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Not saved: the next run may notify again, which is better than not running.
        }
    }
}
