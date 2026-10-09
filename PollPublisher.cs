using System.Text.Json;

namespace TelegramQuizArrangement;

public sealed class PollPublisher(string directory,
    Func<string, object, CancellationToken, Task<JsonElement>> call)
{
    private readonly ThirdAnswerRotation rotation = new(directory);
    private string StatePath => Path.Combine(directory, "pinned-polls.json");
    private sealed record PinState(int MessageId, List<int> Pending);

    public async Task Send(object payload, CancellationToken cancellation)
    {
        JsonElement published = default;
        await rotation.Send(payload, async poll => published = await call("sendPoll", poll, cancellation));
        // Publication and third-answer advancement are complete. Pinning must never repeat sendPoll.
        try
        {
            var poll = JsonSerializer.SerializeToElement(payload);
            var chat = poll.GetProperty("chat_id").GetInt64();
            int? thread = poll.TryGetProperty("message_thread_id", out var topic) && topic.ValueKind == JsonValueKind.Number
                ? topic.GetInt32() : null;
            var message = published.GetProperty("message_id").GetInt32();
            if (message <= 0) throw new InvalidOperationException("Invalid published message ID.");
            var key = $"{chat}:{thread ?? 0}";
            var states = File.Exists(StatePath)
                ? JsonSerializer.Deserialize<Dictionary<string, PinState>>(File.ReadAllText(StatePath))
                    ?? throw new InvalidOperationException("Invalid pin state.")
                : new Dictionary<string, PinState>();
            if (states.Values.Any(state => state.MessageId <= 0 || state.Pending is null || state.Pending.Any(id => id <= 0)))
                throw new InvalidOperationException("Invalid pin state.");
            states.TryGetValue(key, out var previous);
            var pinned = await call("pinChatMessage", new { chat_id = chat, message_id = message, disable_notification = true }, cancellation);
            if (pinned.ValueKind != JsonValueKind.True) throw new InvalidOperationException("Pin not confirmed.");
            var pending = previous?.Pending.ToList() ?? [];
            if (previous is not null && previous.MessageId != message) pending.Add(previous.MessageId);
            pending = pending.Distinct().Where(id => id != message).ToList();
            states[key] = new(message, pending);
            // Persist the new successful pin before removing any previous known bot pin.
            Save(states);
            foreach (var old in pending.ToArray())
            {
                try
                {
                    var unpinned = await call("unpinChatMessage", new { chat_id = chat, message_id = old }, cancellation);
                    if (unpinned.ValueKind != JsonValueKind.True) throw new InvalidOperationException("Unpin not confirmed.");
                    pending.Remove(old);
                    Save(states);
                }
                catch (TelegramApiException error) { Console.Error.WriteLine($"Poll pin status: unpin_http_{error.Code}; publication retained."); }
                catch (HttpRequestException) { Console.Error.WriteLine("Poll pin status: unpin_network; publication retained."); }
            }
            Console.WriteLine("Poll pin status: pinned.");
        }
        catch (TelegramApiException error) { Console.Error.WriteLine($"Poll pin status: pin_http_{error.Code}; publication retained."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException or HttpRequestException or OperationCanceledException)
        { Console.Error.WriteLine("Poll pin status: unavailable_or_uncertain; publication retained."); }
    }

    private void Save(Dictionary<string, PinState> states)
    {
        var temp = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(states);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, StatePath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
