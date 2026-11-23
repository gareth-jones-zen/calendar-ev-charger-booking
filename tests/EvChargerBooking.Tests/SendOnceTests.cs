using System.Net;
using System.Text.Json.Nodes;
using EvChargerBooking.Parity;
using M365Graph;

namespace EvChargerBooking.Tests;

/// <summary>A write that may have reached Microsoft is never resent (background-tools-lib 62f653f): the rename is
/// sent once, and a 5xx or a timeout after sending is a question saying it may have worked, never a retry. Reads are
/// still retried. Through the real adapter (M365GraphClient) over a fake handler.</summary>
public sealed class SendOnceTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 12);   // a Thursday
    private readonly Folder folder = new();
    private Settings Config => folder.Settings;

    public void Dispose() => folder.Dispose();

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Requests.Add($"{request.Method} {request.RequestUri}");
            return Task.FromResult(answer(request, Requests.Count));
        }
    }

    private static (M365GraphClient Graph, Handler Handler, List<TimeSpan> Waits) Make(Func<HttpRequestMessage, int, HttpResponseMessage> answer)
    {
        var handler = new Handler(answer);
        var waits = new List<TimeSpan>();
        var client = new GraphClient(new GraphClientOptions { Scopes = M365GraphClient.Scopes, Log = _ => { } }, handler,
            token: (_, _) => Task.FromResult("token"),
            delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        return (new M365GraphClient(client), handler, waits);
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body = "{}") =>
        new(status) { Content = new StringContent(body) };

    private static readonly Dictionary<string, string> Headers = EvChargerBooking.Outlook.HeadersFor("GMT Standard Time");

    /// <summary>The fake's reads, and the real Outlook's rename through a live door onto the adapter.</summary>
    private sealed class RenameThrough(FakeOutlook reads, EvChargerBooking.Outlook writes) : IOutlook
    {
        public List<JsonObject> WorkPlan(DateOnly start, DateOnly end) => reads.WorkPlan(start, end);
        public List<JsonObject> Events(DateOnly start, DateOnly end) => reads.Events(start, end);
        public List<JsonObject> MessagesFrom(string sender, DateOnly since) => reads.MessagesFrom(sender, since);
        public void Rename(string eventId, string subject, string? location) => writes.Rename(eventId, subject, location);
    }

    private IOutlook Through(M365GraphClient graph) =>
        new RenameThrough(
            new FakeOutlook(events: [PythonTests.Event("2026-11-24", "Travel; EV", 7, "tue"), PythonTests.Event("2026-11-23", "Travel; EV", 7, "mon")],
                messages: [PythonTests.Reply("2026-11-24"), PythonTests.Reply("2026-11-23", point: "Charger 2 (Front)")]),
            new EvChargerBooking.Outlook(Config, graph, WriteDoor.Live(graph)));

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void A_rename_answered_with_a_5xx_is_sent_once_and_unknown(HttpStatusCode status)
    {
        var (graph, handler, waits) = Make((_, _) => Answer(status, "{\"error\":{\"code\":\"busy\"}}"));
        var error = Assert.Throws<GraphDeliveryUnknownException>(() =>
            graph.Send("PATCH", EvChargerBooking.Outlook.EventUrl("tue"), new JsonObject { ["subject"] = "x" }, Headers));
        Assert.Equal(["PATCH https://graph.microsoft.com/v1.0/me/events/tue"], handler.Requests);
        Assert.Empty(waits);
        Assert.Equal(status, error.Status);
    }

    [Fact]
    public void A_rename_timing_out_after_sending_is_sent_once_and_unknown()
    {
        var (graph, handler, waits) = Make((_, _) => throw new TaskCanceledException("timed out", new TimeoutException()));
        var error = Assert.Throws<GraphDeliveryUnknownException>(() =>
            graph.Send("PATCH", EvChargerBooking.Outlook.EventUrl("tue"), new JsonObject { ["subject"] = "x" }, Headers));
        Assert.Single(handler.Requests);
        Assert.Empty(waits);
        Assert.Null(error.Status);
    }

    [Fact]
    public void A_throttled_rename_is_still_retried()
    {
        var (graph, handler, _) = Make((_, n) => n == 1 ? Answer((HttpStatusCode)429) : Answer(HttpStatusCode.OK));
        graph.Send("PATCH", EvChargerBooking.Outlook.EventUrl("tue"), new JsonObject { ["subject"] = "x" }, Headers);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void A_read_still_retries_a_5xx()
    {
        var (graph, handler, waits) = Make((_, n) => n == 1 ? Answer(HttpStatusCode.ServiceUnavailable) : Answer(HttpStatusCode.OK, "{\"value\":[]}"));
        var page = graph.Get(EvChargerBooking.Outlook.EventsUrl(Today, Today.AddDays(1)), Headers);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(5)], waits);
        Assert.IsType<JsonArray>(page["value"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_rename_that_may_have_gone_is_a_question_not_a_failure_and_isnt_resent(bool timesOut)
    {
        var (graph, handler, _) = Make((request, _) =>
            !request.RequestUri!.AbsolutePath.EndsWith("/mon", StringComparison.Ordinal) ? Answer(HttpStatusCode.OK)
            : timesOut ? throw new TaskCanceledException("timed out", new TimeoutException())
            : Answer(HttpStatusCode.BadGateway));
        var questions = Runner.ApplyApprovals(Config, Through(graph), Today, false, folder.Log);
        // Sent once each: Monday's may have gone, and Tuesday's (after it) is still renamed.
        Assert.Equal(["PATCH https://graph.microsoft.com/v1.0/me/events/mon", "PATCH https://graph.microsoft.com/v1.0/me/events/tue"],
            handler.Requests);
        Assert.Equal(["Mon 23 Nov: renaming the Travel block to \"Travel; EV – Charger 2 (Front), 08:00–12:00\" may or may not have worked "
                      + "(Microsoft didn't confirm). Check the entry; the next run renames it only if it still needs it."], questions);
        var saved = State.Load(Config.StateFile);
        Assert.Equal("Charger 2 (Front)", (string)saved["2026-11-23"]!["approved"]!);
        Assert.Equal("Charger 1 (Rear)", (string)saved["2026-11-24"]!["approved"]!);
        Assert.Contains(folder.Lines, line => line.Contains("Mon 23 Nov: renaming the Travel block may not have worked (not sent again)", StringComparison.Ordinal));
        Assert.Contains("updated Tue 24 Nov Travel block -> Travel; EV – Charger 1 (Rear), 08:00–12:00", folder.Lines);
    }

    [Fact]
    public void The_next_run_leaves_an_entry_the_unknown_rename_did_reach()
    {
        var (graph, handler, _) = Make((_, _) => Answer(HttpStatusCode.OK));
        var outlook = new RenameThrough(
            new FakeOutlook(events: [PythonTests.Event("2026-11-24", "Travel; EV – Charger 1 (Rear), 08:00–12:00", 7, "tue")],
                messages: [PythonTests.Reply("2026-11-24")]),
            new EvChargerBooking.Outlook(Config, graph, WriteDoor.Live(graph)));
        Assert.Empty(Runner.ApplyApprovals(Config, outlook, Today, false, folder.Log));
        Assert.Empty(handler.Requests);
    }
}
