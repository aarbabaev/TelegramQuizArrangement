using System.Net;
using System.Text.Json;

namespace TelegramQuizArrangement;

internal static class PollPublisherTests
{
    private sealed class MockHandler : HttpMessageHandler
    {
        public readonly List<(string Method, JsonElement Payload)> Calls = [];
        public string? FailMethod;
        public int ErrorCode = 403;
        public int NextMessage = 101;
        public Exception? PinException;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            var method = request.RequestUri!.Segments[^1];
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            Calls.Add((method, payload.RootElement.Clone()));
            if (method == "pinChatMessage" && PinException is not null) throw PinException;
            var body = method == FailMethod ? JsonSerializer.Serialize(new { ok = false, error_code = ErrorCode })
                : method == "sendPoll" ? JsonSerializer.Serialize(new { ok = true, result = new { message_id = NextMessage++ } })
                : "{\"ok\":true,\"result\":true}";
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    public static async Task Run()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new ApplicationException($"Publisher check failed: {name}");
            checks++;
        }
        var directory = Path.Combine(Path.GetTempPath(), "quiz-pin-test-" + Guid.NewGuid().ToString("N"));
        using var handler = new MockHandler();
        using var http = new HttpClient(handler);
        var api = new TelegramApi(http, "synthetic-token");
        var publisher = new PollPublisher(directory, api.Call);
        object Payload(long chat = -1001, int? topic = null) => QuizPoll.Payload(chat, topic, new DateTime(2026, 10, 18, 19, 0, 0));
        string Index() => File.ReadAllText(Path.Combine(directory, "third-answer.json"));
        try
        {
            await publisher.Send(Payload(), CancellationToken.None);
            Check(handler.Calls.Select(call => call.Method).SequenceEqual(["sendPoll", "pinChatMessage"]), "first publication only pin");
            Check(handler.Calls[1].Payload.GetProperty("message_id").GetInt32() == 101
                && handler.Calls[1].Payload.GetProperty("disable_notification").GetBoolean(), "returned poll ID and silent pin");
            Check(handler.Calls[0].Payload.GetProperty("options").GetArrayLength() == 3 && Index() == "1", "third option advances before pin");
            handler.Calls.Clear();
            await publisher.Send(Payload(), CancellationToken.None);
            Check(handler.Calls.Select(call => call.Method).SequenceEqual(["sendPoll", "pinChatMessage", "unpinChatMessage"])
                && handler.Calls[1].Payload.GetProperty("message_id").GetInt32() == 102
                && handler.Calls[2].Payload.GetProperty("message_id").GetInt32() == 101, "new pin before old unpin");
            handler.Calls.Clear();
            await publisher.Send(Payload(-1002), CancellationToken.None);
            Check(handler.Calls.Count == 2 && handler.Calls.All(call => call.Payload.GetProperty("chat_id").GetInt64() == -1002), "separate chats");
            handler.Calls.Clear();
            await publisher.Send(Payload(-1001, 42), CancellationToken.None);
            Check(handler.Calls.Count == 2, "separate topics");
            handler.Calls.Clear();
            publisher = new PollPublisher(directory, api.Call);
            await publisher.Send(Payload(), CancellationToken.None);
            Check(handler.Calls[2].Payload.GetProperty("message_id").GetInt32() == 102 && Index() == "0", "restart persists previous pin and global third cycle");
            handler.Calls.Clear();
            handler.FailMethod = "pinChatMessage";
            await publisher.Send(Payload(), CancellationToken.None);
            Check(handler.Calls.Count == 2 && Index() == "1", "pin permission failure retains publication and advancement");
            handler.FailMethod = null;
            handler.Calls.Clear();
            await publisher.Send(Payload(), CancellationToken.None);
            Check(handler.Calls[2].Payload.GetProperty("message_id").GetInt32() == 105, "pin failure preserves earlier pin");
            handler.FailMethod = "unpinChatMessage";
            handler.ErrorCode = 400;
            handler.Calls.Clear();
            await publisher.Send(Payload(), CancellationToken.None);
            Check(handler.Calls.Count == 3 && Index() == "3", "deleted or already unpinned old message error is nonfatal");
            handler.FailMethod = null;
            handler.Calls.Clear();
            await new PollPublisher(directory, api.Call).Send(Payload(), CancellationToken.None);
            Check(handler.Calls.Count == 4 && handler.Calls[2].Payload.GetProperty("message_id").GetInt32() == 107
                && handler.Calls[3].Payload.GetProperty("message_id").GetInt32() == 108, "pending specific cleanup survives restart");
            Check(handler.Calls.All(call => call.Method != "unpinAllChatMessages"
                && (call.Method != "unpinChatMessage" || call.Payload.TryGetProperty("message_id", out _))), "never clears other pins");
            handler.Calls.Clear();
            handler.FailMethod = "sendPoll";
            var before = Index();
            var failed = false;
            try { await publisher.Send(Payload(), CancellationToken.None); }
            catch (TelegramApiException) { failed = true; }
            Check(failed && handler.Calls.Count == 1 && Index() == before, "sendPoll failure no pin or index advance");
            handler.FailMethod = null;
            foreach (var error in new Exception[] { new HttpRequestException(), new TaskCanceledException() })
            {
                handler.Calls.Clear();
                handler.PinException = error;
                var previousIndex = Index();
                await publisher.Send(Payload(), CancellationToken.None);
                Check(handler.Calls.Count == 2 && Index() != previousIndex, "pin network/cancellation preserves publication");
            }
            handler.PinException = null;
            handler.Calls.Clear();
            before = Index();
            File.WriteAllText(Path.Combine(directory, "pinned-polls.json"), "broken");
            await publisher.Send(Payload(), CancellationToken.None);
            Check(handler.Calls.Count == 1 && Index() != before, "corrupt pin state never undoes successful publication");
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "persistent atomic writes cleanup");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        Console.WriteLine($"All {checks} publisher mock HTTP checks passed (no network).");
    }
}
