using System.Net;
using System.Text.Json;

namespace TelegramQuizArrangement;

internal static class QuizWordingTests
{
    private sealed class MockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Calls++;
            return respond(request, cancellation);
        }
    }

    private static string Envelope(string text, string status = "completed") => JsonSerializer.Serialize(new
    {
        status,
        output = new object[] { new { type = "reasoning" }, new { type = "message", content = new[] { new { type = "output_text", text } } } },
        usage = new { input_tokens = 150, output_tokens = 30 }
    });
    private static HttpResponseMessage Reply(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body) };

    public static async Task Run()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new ApplicationException($"OpenAI mock check failed: {name}");
            checks++;
        }
        using var empty = JsonDocument.Parse("{}");
        Check(OpenAiSettings.Read(empty.RootElement) == new OpenAiSettings(), "missing settings defaults");
        using var configured = JsonDocument.Parse("""{"OpenAI":{"ApiKey":"","Model":"custom-model"}}""");
        Check(OpenAiSettings.Read(configured.RootElement).Model == "custom-model", "configurable model");
        const string valid = """{"q":"Квизим как обычно?","y":"В деле","n":"Я пас"}""";
        var used = false;
        var handlerCalls = 0;
        using var handler = new MockHandler(async (request, cancellation) =>
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://api.openai.com/v1/responses", "endpoint");
            Check(request.Headers.Authorization?.Scheme == "Bearer", "authentication header");
            var body = await request.Content!.ReadAsStringAsync(cancellation);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Check(root.GetProperty("model").GetString() == "gpt-6-luna", "default model");
            Check(root.GetProperty("max_output_tokens").GetInt32() == 128, "output cap");
            Check(!root.GetProperty("store").GetBoolean() && !root.TryGetProperty("previous_response_id", out _)
                && !root.TryGetProperty("tools", out _), "stateless without tools");
            Check(root.GetProperty("reasoning").GetProperty("effort").GetString() == "none"
                && root.GetProperty("text").GetProperty("verbosity").GetString() == "low", "cost settings");
            var format = root.GetProperty("text").GetProperty("format");
            Check(format.GetProperty("strict").GetBoolean()
                && !format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean(), "strict schema");
            Check(root.GetProperty("instructions").GetString()!.Length < 700 && body.Length < 11000
                && !body.Contains("https:") && !body.Contains("chat_id") && !body.Contains("19:00"), "bounded private input");
            Check(root.GetProperty("input").GetString() == (handlerCalls++ == 0 ? "Новый вопрос." : "Предыдущий вопрос: Квизим как обычно?"), "last question only");
            return Reply(Envelope(valid));
        });
        using var client = new HttpClient(handler);
        var generator = new QuizWording(client, new("synthetic-key"), (input, output) => used = input == 150 && output == 30);
        var wording = await generator.Generate(CancellationToken.None);
        Check(wording == new PollWording("Квизим как обычно?", "В деле", "Я пас") && used, "output parsing and usage");
        Check(await generator.Generate(CancellationToken.None) is null && handler.Calls == 2, "repeat rejected without retry");
        var game = QuizPoll.NextGame(DateTimeOffset.Parse("2026-10-09T12:00:00+04:00"), TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai"));
        var poll = JsonSerializer.SerializeToElement(QuizPoll.Payload(-100123, 42, game, wording));
        Check(poll.GetProperty("question").GetString() == "Квизим как обычно?\n11-октября 19:00", "fixed date and time");
        Check(poll.GetProperty("options")[0].GetProperty("text").GetString() == "🟩 В деле"
            && poll.GetProperty("options")[1].GetProperty("text").GetString() == "🟥 Я пас", "yes/no order");
        var fallback = JsonSerializer.SerializeToElement(QuizPoll.Payload(-100123, 42, game));
        Check(poll.GetProperty("description").GetString() == fallback.GetProperty("description").GetString()
            && poll.GetProperty("reply_markup").GetRawText() == fallback.GetProperty("reply_markup").GetRawText()
            && poll.GetProperty("message_thread_id").GetInt32() == 42, "unchanged place link and topic");

        foreach (var question in new[] { "Квизим как обычно?", "Суетим в конце недели?", "По старинке разминаем мозги?" })
        {
            using var exampleHandler = new MockHandler((_, _) => Task.FromResult(Reply(Envelope(
                JsonSerializer.Serialize(new { q = question, y = "Иду", n = "Не иду" })))));
            using var exampleClient = new HttpClient(exampleHandler);
            Check((await new QuizWording(exampleClient, new("synthetic-key")).Generate(CancellationToken.None))?.Question == question,
                "accepted user example");
        }

        foreach (var bad in new[]
        {
            "not json", "{}", "[]", """{"q":"Квизим?","y":"Не иду","n":"Иду"}""",
            """{"q":"Квизим в 20:00?","y":"Иду","n":"Не иду"}""",
            """{"q":"Квизим, идиоты?","y":"Иду","n":"Я пас"}""",
            """{"q":"Квизим не идём?","y":"Иду","n":"Я пас"}""",
            """{"q":"Квизим в субботу?","y":"Иду","n":"Я пас"}""",
            """{"q":"Квизим https://example.org?","y":"Иду","n":"Я пас"}""",
            """{"q":"<b>Квизим?</b>","y":"Иду","n":"Я пас"}""",
            """{"q":"Квизим?\nИдём?","y":"Иду","n":"Я пас"}""",
            """{"q":"Квизим?","y":"Иду","n":"Я пас","extra":1}""",
            """{"q":"Квизим?","y":1,"n":"Я пас"}""",
            JsonSerializer.Serialize(new { q = new string('а', 71) + " квиз?", y = "Иду", n = "Я пас" })
        })
        {
            using var invalidHandler = new MockHandler((_, _) => Task.FromResult(Reply(Envelope(bad))));
            using var invalidClient = new HttpClient(invalidHandler);
            Check(await new QuizWording(invalidClient, new("synthetic-key")).Generate(CancellationToken.None) is null
                && invalidHandler.Calls == 1, "invalid wording fallback once");
        }
        foreach (var body in new[] { "{}", "[]", "not json", Envelope(valid, "incomplete"),
            """{"status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"no"}]}]}""",
            """{"status":"completed","output":[{"type":"message","content":[{}]}]}""", new string('x', 40000) })
        {
            using var invalidHandler = new MockHandler((_, _) => Task.FromResult(Reply(body)));
            using var invalidClient = new HttpClient(invalidHandler);
            Check(await new QuizWording(invalidClient, new("synthetic-key")).Generate(CancellationToken.None) is null
                && invalidHandler.Calls == 1, "malformed response fallback once");
        }
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError })
        {
            using var errorHandler = new MockHandler((_, _) => Task.FromResult(Reply("", status)));
            using var errorClient = new HttpClient(errorHandler);
            string? reason = null;
            Check(await new QuizWording(errorClient, new("synthetic-key"), diagnostic: value => reason = value).Generate(CancellationToken.None) is null
                && errorHandler.Calls == 1 && reason == $"http_{(int)status}", "HTTP error no retries with safe reason");
        }
        foreach (var error in new Exception[] { new HttpRequestException(), new TaskCanceledException() })
        {
            using var errorHandler = new MockHandler((_, _) => Task.FromException<HttpResponseMessage>(error));
            using var errorClient = new HttpClient(errorHandler);
            Check(await new QuizWording(errorClient, new("synthetic-key")).Generate(CancellationToken.None) is null
                && errorHandler.Calls == 1, "network timeout fallback");
        }
        using var disabledHandler = new MockHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var disabledClient = new HttpClient(disabledHandler);
        string? disabledReason = null;
        Check(await new QuizWording(disabledClient, new(), diagnostic: value => disabledReason = value).Generate(CancellationToken.None) is null
            && disabledHandler.Calls == 0 && disabledReason == "disabled_empty_key", "empty key zero requests and reason");
        for (var index = 0; index < PollAnswers.Yes.Length; index++)
        {
            var text = JsonSerializer.Serialize(new { q = "По старинке разминаем мозги?", y = PollAnswers.Yes[index], n = PollAnswers.No[index] });
            using var variantsHandler = new MockHandler((_, _) => Task.FromResult(Reply(Envelope(text))));
            using var variantsClient = new HttpClient(variantsHandler);
            Check(await new QuizWording(variantsClient, new("synthetic-key")).Generate(CancellationToken.None) is not null,
                "expanded explicit yes/no phrases accepted");
        }
        var pairs = new HashSet<string>();
        string? last = null;
        for (var index = 0; index < 32; index++)
        {
            var rotating = JsonSerializer.SerializeToElement(QuizPoll.Payload(-100123, null, game));
            var yes = rotating.GetProperty("options")[0].GetProperty("text").GetString()!;
            var no = rotating.GetProperty("options")[1].GetProperty("text").GetString()!;
            var pair = yes + "/" + no;
            Check(pair != last && yes.StartsWith("🟩 ") && no.StartsWith("🟥 ")
                && PollAnswers.Yes.Contains(yes[3..]) && PollAnswers.No.Contains(no[3..]), "fallback rotates with fixed meaning");
            last = pair;
            pairs.Add(pair);
        }
        Check(pairs.Count == 16, "sixteen fallback pairs");
        var pairFirst = PollAnswers.Choose("Иду", "Я пас");
        var pairNext = PollAnswers.Choose("Иду", "Я пас");
        Check(pairFirst != pairNext, "AI repeated pair rotated locally");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var cancellationHandler = new MockHandler((_, token) => Task.FromCanceled<HttpResponseMessage>(token));
        using var cancellationClient = new HttpClient(cancellationHandler);
        try
        {
            await new QuizWording(cancellationClient, new("synthetic-key")).Generate(cancelled.Token);
            Check(false, "shutdown cancellation");
        }
        catch (OperationCanceledException) { Check(true, "shutdown cancellation"); }
        Console.WriteLine($"All {checks} OpenAI mock checks passed (no network).");
    }
}
