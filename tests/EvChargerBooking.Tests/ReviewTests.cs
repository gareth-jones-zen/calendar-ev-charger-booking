using System.Text.Json.Nodes;
using PyCompat;
using EvChargerBooking.Parity;
using ReviewQueue;

namespace EvChargerBooking.Tests;

/// <summary>[review]: the questions queued exactly as the Python after_run hook queued them for the same report
/// (Golden/review.json: the hook run on each report against a seeded queue, the items it left, added times dropped),
/// and only when the hook would have run.</summary>
public sealed class ReviewTests : IDisposable
{
    private readonly Folder folder = new();

    public void Dispose() => folder.Dispose();

    private string QueuePath => Path.Combine(folder.Path, "review", "pending.json");

    private ReviewSettings Review(string source = "ev") => new(QueuePath, source, "ev-review-test-" + Guid.NewGuid().ToString("N"));

    public static TheoryData<string> Cases() => [.. Golden().Select(c => (string)c!["name"]!)];

    private static JsonArray Golden() => PyJson.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "review.json")))!.AsArray();

    private static List<string> Strings(JsonNode? node) => node!.AsArray().Select(n => (string)n!).ToList();

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_items_are_the_python_hooks(string name)
    {
        var golden = Golden().Single(c => (string)c!["name"]! == name)!;
        Directory.CreateDirectory(Path.GetDirectoryName(QueuePath)!);
        PyStr.WriteText(QueuePath, PyJson.Dump(golden["seed"], indent: 2, ensureAscii: false));
        folder.Settings.Review = Review();
        var report = new Report();
        report.Questions.AddRange(Strings(golden["report"]!["questions"]));
        report.Booked.AddRange(Strings(golden["report"]!["booked"]));
        report.Failed.AddRange(Strings(golden["report"]!["failed"]));

        Runner.Review(folder.Settings, report, folder.Log);

        var items = folder.Settings.Review.Open().Items();
        var withoutAdded = new JsonArray(items.Select(i =>
        {
            var copy = i.DeepClone().AsObject();
            copy.Remove("added");
            return (JsonNode)copy;
        }).ToArray());
        Assert.Equal(PyJson.Dump(golden["items"]), PyJson.Dump(withoutAdded));
        Assert.Contains($"review: {report.Questions.Count} question(s) queued under ev in {QueuePath}", folder.Lines);
    }

    private static FakeOutlook Outlook() => new(
        [PythonTests.Segment("2026-11-16", "office"), PythonTests.Segment("2026-11-17", "remote")],
        [PythonTests.Event("2026-11-16", "Travel; EV"), PythonTests.Event("2026-11-17", "Travel; EV")], []);

    private Report LockedRun(bool hook, bool dryRun = false, bool approvalsOnly = false) =>
        Runner.LockedRun(folder.Settings, Outlook(), (_, _) => "Submitted", folder.Log, dryRun, hook, approvalsOnly,
            new DateOnly(2026, 11, 12), () => new DateTime(2026, 11, 12, 8, 0, 0));

    [Fact]
    public void A_hooked_run_queues_its_questions()
    {
        folder.Settings.Review = Review();
        var report = LockedRun(hook: true);
        Assert.NotEmpty(report.Questions);
        Assert.Equal(report.Questions, folder.Settings.Review.Open().Items("ev").Select(i => (string)i["title"]!));
        Assert.All(folder.Settings.Review.Open().Items("ev"), i => Assert.Equal("question", (string)i["type"]!));
    }

    [Theory]
    [InlineData(false, false, false)]   // no --hook
    [InlineData(true, true, false)]     // a dry run
    [InlineData(true, false, true)]     // approvals only
    public void Nothing_is_queued_when_the_hook_wouldnt_run(bool hook, bool dryRun, bool approvalsOnly)
    {
        folder.Settings.Review = Review();
        LockedRun(hook, dryRun, approvalsOnly);
        Assert.False(File.Exists(QueuePath));
    }

    [Fact]
    public void Without_review_nothing_is_queued()
    {
        Assert.Null(folder.Settings.Review);
        LockedRun(hook: true);
        Assert.False(File.Exists(QueuePath));
    }

    [Fact]
    public void A_queue_that_cant_be_written_is_logged_and_the_run_goes_on()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(QueuePath)!);
        File.WriteAllText(QueuePath, "[]");   // JSON, but not a queue: refused, never overwritten
        folder.Settings.Review = Review();
        var report = LockedRun(hook: true);
        Assert.NotEmpty(report.Questions);
        Assert.Contains(folder.Lines, line => line.StartsWith("review queue failed: ", StringComparison.Ordinal));
        Assert.Equal("[]", File.ReadAllText(QueuePath));
    }

    [Fact]
    public void Review_settings_are_read_with_paths_like_the_others()
    {
        var settings = new Folder("""

            [review]
            file = "queue/pending.json"
            source = "ev"
            """).Settings;
        Assert.Equal(PyPath.Resolve(Path.Combine(Path.GetDirectoryName(settings.Path)!, "queue", "pending.json")), settings.Review!.File);
        Assert.Equal("ev", settings.Review.Source);
        Assert.Null(settings.Review.Lock);
        Assert.Equal("queue-pending", settings.Review.Open().LockName);
        Directory.Delete(Path.GetDirectoryName(settings.Path)!, true);
    }

    [Theory]
    [InlineData("[review]\nfile = \"q.json\"", "needs source")]
    [InlineData("[review]\nsource = \"ev\"", "needs file")]
    [InlineData("[review]\nfile = \"q.json\"\nsource = \"ev\"\nfiel = 1", "fiel under [review] in ")]
    [InlineData("[review]\nfile = \"q.json\"\nsource = true", "source under [review] in ")]
    [InlineData("review = \"q.json\"", "review must be a [review] table.")]
    public void Bad_review_settings_are_refused(string toml, string message)
    {
        var path = Path.Combine(folder.Path, "bad.toml");
        var error = Assert.Throws<PyValueError>(() => Settings.Parse(toml, path));
        Assert.Contains(message, error.Message);
    }
}
