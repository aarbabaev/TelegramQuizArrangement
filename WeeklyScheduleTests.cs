using System.Net;
using System.Text.Json;

namespace TelegramQuizArrangement;

internal static class WeeklyScheduleTests
{
    private sealed class PollHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Payload { get; private set; }
        public HttpStatusCode Code { get; set; } = HttpStatusCode.OK;
        public bool Uncertain { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Calls++;
            Payload = await request.Content!.ReadAsStringAsync(cancellation);
            if (Uncertain) throw new HttpRequestException();
            return new(Code) { Content = new StringContent(Code == HttpStatusCode.OK
                ? "{\"ok\":true,\"result\":{\"message_id\":1}}" : "{\"ok\":false,\"error_code\":429}") };
        }
    }

    public static async Task Run()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new ApplicationException($"Schedule test failed: {name}");
            checks++;
        }
        DateTimeOffset Now(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        var first = WeeklySchedule.ParseFirst("2026-10-15T20:00:00");
        Check(first.DayOfWeek == DayOfWeek.Thursday && WeeklySchedule.GameFor(first) == new DateTime(2026, 10, 18, 19, 0, 0), "Thursday to Sunday");
        Check(WeeklySchedule.LatestThursday(new DateTime(2026, 10, 15, 19, 59, 59)) == first.AddDays(-7), "before Thursday cutoff");
        Check(WeeklySchedule.LatestThursday(first) == first, "at Thursday cutoff");
        Check(WeeklySchedule.NextThursday(new DateTime(2026, 12, 31, 20, 0, 0)) == new DateTime(2027, 1, 7, 20, 0, 0), "year rollover");
        using var defaults = JsonDocument.Parse("{}");
        Check(!ScheduleSettings.Read(defaults.RootElement).Enabled, "default disabled");
        try { WeeklySchedule.ParseFirst("2026-10-09T20:00:00"); Check(false, "invalid first date"); }
        catch (InvalidOperationException) { Check(true, "invalid first date"); }
        using var invalid = JsonDocument.Parse("""{"Schedule":{"Enabled":true,"ChatId":0}}""");
        try { ScheduleSettings.Read(invalid.RootElement); Check(false, "invalid destination"); }
        catch (InvalidOperationException) { Check(true, "invalid destination"); }

        var root = Path.Combine(Path.GetTempPath(), "quiz-schedule-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new ScheduleSettings(true, -100123, 42, "2026-10-15T20:00:00");
            var state = Path.Combine(root, "main");
            using var handler = new PollHandler();
            using var http = new HttpClient(handler);
            var api = new TelegramApi(http, "synthetic-token");
            var aiCalls = 0;
            Task<PollWording?> Wording(DateTime _, CancellationToken ct)
            {
                Check(File.Exists(Path.Combine(state, "week-2026-10-15.json")), "reservation before AI");
                aiCalls++;
                return Task.FromResult<PollWording?>(new("Суетим в конце недели?", "Иду", "Я пас"));
            }
            async Task Send(object payload, CancellationToken ct) { await api.Call("sendPoll", payload, ct); }
            using (var schedule = new WeeklySchedule(settings, state, Now("2026-10-09T12:00:00+04:00")))
            {
                Check(schedule.Due(Now("2026-10-09T12:00:00+04:00")) is null && schedule.NextRun(Now("2026-10-09T12:00:00+04:00")) == first, "first enable no retrospective post");
                await schedule.Tick(Now("2026-10-09T12:00:00+04:00"), Wording, Send, CancellationToken.None);
                Check(handler.Calls == 0 && aiCalls == 0, "no requests before activation");
                Check(schedule.Due(Now("2026-10-15T15:59:59Z")) is null && schedule.Due(Now("2026-10-15T16:00:00Z")) == first, "Dubai UTC boundary");
                Check(schedule.PollWaitSeconds(Now("2026-10-15T19:59:55+04:00")) == 5
                    && schedule.PollWaitSeconds(Now("2026-10-09T12:00:00+04:00")) == 30, "poll deadline capped at schedule");
                Check(schedule.Due(Now("2026-10-18T18:59:59+04:00")) == first
                    && schedule.Due(Now("2026-10-18T19:00:00+04:00")) is null, "catchup Sunday cutoff");
                await schedule.Tick(Now("2026-10-16T12:00:00+04:00"), Wording, Send, CancellationToken.None);
                Check(handler.Calls == 1 && aiCalls == 1, "one catchup send");
                using var payload = JsonDocument.Parse(handler.Payload!);
                var poll = payload.RootElement;
                Check(poll.GetProperty("chat_id").GetInt64() == -100123 && poll.GetProperty("message_thread_id").GetInt32() == 42, "destination topic");
                Check(poll.GetProperty("question").GetString() == "Суетим в конце недели?\n18-октября 19:00"
                    && poll.GetProperty("options")[0].GetProperty("text").GetString() == "🟩 Иду"
                    && poll.GetProperty("options")[1].GetProperty("text").GetString() == "🟥 Я пас"
                    && poll.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0].GetProperty("url").GetString() == QuizPoll.WazeUrl, "shared AI poll format");
                await schedule.Tick(Now("2026-10-16T12:01:00+04:00"), Wording, Send, CancellationToken.None);
                Check(handler.Calls == 1, "same process no duplicate");
                try { using var second = new WeeklySchedule(settings, state, Now("2026-10-16T12:00:00+04:00")); Check(false, "exclusive state owner"); }
                catch (IOException) { Check(true, "exclusive state owner"); }
            }
            using (var restarted = new WeeklySchedule(settings, state, Now("2026-10-16T13:00:00+04:00")))
            {
                await restarted.Tick(Now("2026-10-16T13:00:00+04:00"), Wording, Send, CancellationToken.None);
                Check(handler.Calls == 1 && restarted.NextRun(Now("2026-10-16T13:00:00+04:00")) == first.AddDays(7), "restart persists sent claim");
                Check(restarted.Due(Now("2026-11-06T12:00:00+04:00")) == new DateTime(2026, 11, 5, 20, 0, 0), "long outage only latest week");
            }
            var markPath = Path.Combine(state, "week-2026-10-15.json");
            var sent = JsonSerializer.Deserialize<ScheduleMark>(File.ReadAllText(markPath))!;
            Check(sent.Status == "sent" && sent.GameLocal == WeeklySchedule.GameFor(first), "state serialization");
            File.WriteAllText(markPath, JsonSerializer.Serialize(sent with { Status = "reserved" }));
            using (var crashed = new WeeklySchedule(settings, state, Now("2026-10-16T14:00:00+04:00")))
            {
                await crashed.Tick(Now("2026-10-16T14:00:00+04:00"), Wording, Send, CancellationToken.None);
                Check(handler.Calls == 1 && JsonSerializer.Deserialize<ScheduleMark>(File.ReadAllText(markPath))!.Status == "uncertain", "crash reserved becomes uncertain without retry");
            }
            foreach (var mode in new[] { "failed", "uncertain" })
            {
                var failureState = Path.Combine(root, mode);
                handler.Code = mode == "failed" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK;
                handler.Uncertain = mode == "uncertain";
                var before = handler.Calls;
                using (var failing = new WeeklySchedule(settings, failureState, Now("2026-10-15T20:00:00+04:00")))
                    await failing.Tick(Now("2026-10-15T20:00:00+04:00"), (_, _) => Task.FromResult<PollWording?>(null), Send, CancellationToken.None);
                using (var failing = new WeeklySchedule(settings, failureState, Now("2026-10-16T12:00:00+04:00")))
                    await failing.Tick(Now("2026-10-16T12:00:00+04:00"), (_, _) => Task.FromResult<PollWording?>(null), Send, CancellationToken.None);
                Check(handler.Calls == before + 1 && JsonSerializer.Deserialize<ScheduleMark>(File.ReadAllText(Path.Combine(failureState, "week-2026-10-15.json")))!.Status == mode, "failed/uncertain restart no retry");
                using var fallback = JsonDocument.Parse(handler.Payload!);
                Check(fallback.RootElement.GetProperty("question").GetString() == "Играем в это воскресенье?\n18-октября 19:00", "scheduled safe fallback");
            }
            var activationState = Path.Combine(root, "activation");
            using (var initial = new WeeklySchedule(settings with { FirstRunLocal = null }, activationState, Now("2026-10-09T12:00:00+04:00")))
                Check(initial.NextRun(Now("2026-10-09T12:00:00+04:00")) == first, "implicit first enable next Thursday");
            using (var restart = new WeeklySchedule(settings with { FirstRunLocal = null }, activationState, Now("2026-10-16T12:00:00+04:00")))
                Check(restart.Due(Now("2026-10-16T12:00:00+04:00")) == first, "activation survives restart enables catchup");
            Check(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "atomic writes no pending temps");
            var cutoffState = Path.Combine(root, "cutoff");
            using (var cutoff = new WeeklySchedule(settings, cutoffState, Now("2026-10-18T18:59:59+04:00")))
            {
                var before = handler.Calls;
                await cutoff.Tick(Now("2026-10-18T18:59:59+04:00"), (_, _) => Task.FromResult<PollWording?>(null), Send,
                    CancellationToken.None, () => Now("2026-10-18T19:00:00+04:00"));
                Check(handler.Calls == before && JsonSerializer.Deserialize<ScheduleMark>(File.ReadAllText(
                    Path.Combine(cutoffState, "week-2026-10-15.json")))!.Status == "failed", "AI crosses game cutoff no send");
            }
            var brokenState = Path.Combine(root, "broken");
            using (var broken = new WeeklySchedule(settings, brokenState, Now("2026-10-15T20:00:00+04:00")))
            {
                Directory.CreateDirectory(Path.Combine(brokenState, "week-2026-10-15.json"));
                var before = handler.Calls;
                await broken.Tick(Now("2026-10-15T20:00:00+04:00"), (_, _) => throw new ApplicationException("AI must not run"),
                    Send, CancellationToken.None);
                Check(handler.Calls == before && broken.NextRun(Now("2026-10-15T20:00:00+04:00")) is null, "storage failure fails closed");
            }
            using var disabled = new WeeklySchedule(new(), Path.Combine(root, "disabled"), Now("2026-10-16T12:00:00+04:00"));
            Check(disabled.Due(Now("2026-10-16T12:00:00+04:00")) is null && !Directory.Exists(Path.Combine(root, "disabled")), "disabled no storage or send");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"All {checks} schedule checks passed (synthetic state and mock HTTP).");
    }
}
