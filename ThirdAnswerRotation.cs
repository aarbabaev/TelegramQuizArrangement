using System.Text.Json;

namespace TelegramQuizArrangement;

public sealed class ThirdAnswerRotation(string directory)
{
    private static readonly string[] Answers =
    [
        "Я Макс",
        "Не могу так далеко планировать",
        "Я в целом наквизился",
        "Мне всё с квизами понятно",
        "У меня есть более интересные дела (шпилить в комп)"
    ];
    private string StatePath => Path.Combine(directory, "third-answer.json");

    // Called serially by the same polling loop for manual and scheduled publications.
    public async Task Send(object payload, Func<object, Task> send)
    {
        Directory.CreateDirectory(directory);
        if (Directory.Exists(StatePath)) throw new IOException("Rotation state path is a directory.");
        var index = File.Exists(StatePath) ? JsonSerializer.Deserialize<int>(File.ReadAllText(StatePath)) : 0;
        if (index < 0 || index >= Answers.Length) throw new InvalidOperationException("Invalid third-answer rotation state.");
        // Keep the caller's payload reusable: failed/repeated sends must not append a fourth option.
        var poll = new Dictionary<string, object?>((Dictionary<string, object?>)payload);
        var options = JsonSerializer.SerializeToElement(poll["options"]).EnumerateArray()
            .Select(option => new { text = option.GetProperty("text").GetString()! }).ToList();
        if (options.Count != 2) throw new InvalidOperationException("Expected two base poll options.");
        options.Add(new { text = Answers[index] });
        poll["options"] = options;
        var temp = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Prepare and flush the next index before HTTP: unwritable storage must prevent publication.
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize((index + 1) % Answers.Length));
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            await send(poll);
            // Commit only after Telegram confirms publication; retain position for failed/uncertain sends.
            File.Move(temp, StatePath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static async Task SelfTest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quiz-third-" + Guid.NewGuid().ToString("N"));
        try
        {
            for (var index = 0; index < 12; index++)
            {
                var rotation = new ThirdAnswerRotation(directory);
                var payload = QuizPoll.Payload(-123, null, new DateTime(2026, 10, 11, 19, 0, 0));
                await rotation.Send(payload, poll =>
                {
                    var json = JsonSerializer.SerializeToElement(poll);
                    var options = json.GetProperty("options");
                    if (options.GetArrayLength() != 3 || options[2].GetProperty("text").GetString() != Answers[index % Answers.Length]
                        || json.GetProperty("allows_multiple_answers").GetBoolean())
                        throw new InvalidOperationException("Third answer rotation test failed.");
                    return Task.CompletedTask;
                });
            }
            var before = File.ReadAllText(Path.Combine(directory, "third-answer.json"));
            try
            {
                await new ThirdAnswerRotation(directory).Send(QuizPoll.Payload(-123, null, DateTime.Today),
                    _ => throw new HttpRequestException());
            }
            catch (HttpRequestException) { }
            if (File.ReadAllText(Path.Combine(directory, "third-answer.json")) != before)
                throw new InvalidOperationException("Failed send advanced rotation.");
            var reusable = QuizPoll.Payload(-123, null, new DateTime(2026, 10, 11, 19, 0, 0));
            var rotationForReuse = new ThirdAnswerRotation(directory);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await rotationForReuse.Send(reusable, poll =>
                {
                    if (JsonSerializer.SerializeToElement(poll).GetProperty("options").GetArrayLength() != 3)
                        throw new InvalidOperationException("Reused payload has more than three options.");
                    return Task.CompletedTask;
                });
            }
            if (JsonSerializer.SerializeToElement(reusable).GetProperty("options").GetArrayLength() != 2)
                throw new InvalidOperationException("Caller payload was modified.");
            if (Directory.EnumerateFiles(directory, "*.tmp").Any())
                throw new InvalidOperationException("Temporary rotation file was not cleaned up.");
            var success = 0;
            var statePath = Path.Combine(directory, "third-answer.json");
            File.WriteAllText(statePath, "-1");
            try { await rotationForReuse.Send(reusable, _ => { success++; return Task.CompletedTask; }); }
            catch (InvalidOperationException) { }
            if (success != 0) throw new InvalidOperationException("Corrupt rotation state allowed HTTP.");
            File.Delete(statePath);
            Directory.CreateDirectory(statePath);
            try { await rotationForReuse.Send(reusable, _ => { success++; return Task.CompletedTask; }); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            // Preflight the final destination too, not only its containing directory.
            if (success != 0) throw new InvalidOperationException("Invalid state destination allowed HTTP.");
            Console.WriteLine("All 19 third answer checks passed.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
