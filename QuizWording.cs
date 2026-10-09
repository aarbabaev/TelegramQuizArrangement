using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TelegramQuizArrangement;

public sealed record PollWording(string Question, string Yes, string No);

public sealed record OpenAiSettings(string ApiKey = "", string Model = "gpt-6-luna")
{
    public static OpenAiSettings Read(JsonElement root)
    {
        if (!root.TryGetProperty("OpenAI", out var section) || section.ValueKind != JsonValueKind.Object)
            return new();
        string Value(string name, string fallback) => section.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim() : fallback;
        return new(Value("ApiKey", ""), Value("Model", "gpt-6-luna"));
    }
}

public sealed class QuizWording(HttpClient client, OpenAiSettings settings, Action<int, int>? usage = null, Action<string>? diagnostic = null)
{
    public const int MaxOutputTokens = 128;
    private static readonly string Prompt = ReadPrompt();
    private static readonly Regex Rejected = new(
        @"\b(бля\w*|хуй\w*|хуе\w*|хуё\w*|пизд\w*|еб\w*|ёб\w*|сук\w*|дерьм\w*|идиот\w*|дебил\w*|мудак\w*|шлюх\w*|пидор\w*|ниггер\w*|наци\w*|секс\w*|порн\w*|сдох\w*|убь\w*|уби\w*|пас|скип\w*)\b|\bне\s+(ид\w*|пойд\w*|пойдем|пойдём|игра\w*|хоч\w*)\b|\b(понедельник\w*|вторник\w*|сред\w*|четверг\w*|пятниц\w*|суббот\w*|воскресень\w*|завтра|сегодня|вчера|утром|вечером|ночью|час\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private string? lastQuestion;

    private static string ReadPrompt()
    {
        using var stream = typeof(QuizWording).Assembly.GetManifestResourceStream("TelegramQuizArrangement.QuizWording.prompt.txt")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    public async Task<PollWording?> Generate(CancellationToken cancellation)
    {
        PollWording? Fallback(string reason) { diagnostic?.Invoke(reason); return null; }
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) return Fallback("disabled_empty_key");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            request.Content = JsonContent.Create(new
            {
                model = settings.Model, store = false, instructions = Prompt,
                input = lastQuestion is null ? "Новый вопрос." : $"Предыдущий вопрос: {lastQuestion}",
                max_output_tokens = MaxOutputTokens,
                reasoning = new { effort = "none" },
                text = new
                {
                    verbosity = "low",
                    format = new
                    {
                        type = "json_schema", name = "quiz_wording", strict = true,
                        schema = new
                        {
                            type = "object", additionalProperties = false,
                            properties = new
                            {
                                q = new { type = "string", maxLength = 70 },
                                y = new { type = "string", @enum = PollAnswers.Yes },
                                n = new { type = "string", @enum = PollAnswers.No }
                            },
                            required = new[] { "q", "y", "n" }
                        }
                    }
                }
            });
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return Fallback($"http_{(int)response.StatusCode}");
            await response.Content.LoadIntoBufferAsync(32768, timeout.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var root = document.RootElement;
            if (root.TryGetProperty("usage", out var counters) && counters.ValueKind == JsonValueKind.Object
                && counters.TryGetProperty("input_tokens", out var input) && input.TryGetInt32(out var inCount)
                && counters.TryGetProperty("output_tokens", out var output) && output.TryGetInt32(out var outCount)
                && inCount >= 0 && outCount >= 0) usage?.Invoke(inCount, outCount);
            var wording = Parse(root);
            if (wording is null) return Fallback("invalid_response_or_wording");
            if (string.Equals(wording.Question, lastQuestion, StringComparison.OrdinalIgnoreCase)) return Fallback("repeated_question");
            lastQuestion = wording.Question;
            diagnostic?.Invoke("accepted");
            return wording;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return Fallback("timeout"); }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        { return Fallback(error is HttpRequestException ? "network_or_response_size" : "invalid_response_or_wording"); }
    }

    internal static PollWording? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "completed"
            || !root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return null;
        string? json = null;
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "message") continue;
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
            foreach (var part in content.EnumerateArray())
            {
                if (part.GetProperty("type").GetString() != "output_text" || json is not null) return null;
                json = part.GetProperty("text").GetString();
            }
        }
        // Allow valid Unicode-escaped JSON; enforce limits on decoded strings below.
        if (json is null || json.Length > 1024) return null;
        using var wording = JsonDocument.Parse(json);
        var value = wording.RootElement;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 3
            || !value.TryGetProperty("q", out var q) || !value.TryGetProperty("y", out var y)
            || !value.TryGetProperty("n", out var n) || q.ValueKind != JsonValueKind.String
            || y.ValueKind != JsonValueKind.String || n.ValueKind != JsonValueKind.String) return null;
        var question = q.GetString()!;
        var yes = y.GetString()!;
        var no = n.GetString()!;
        // A narrow conservative filter, not a general semantic moderation guarantee.
        if (question.Length is < 5 or > 70 || !Regex.IsMatch(question, @"^[А-Яа-яЁё ,—!?-]+\?$" )
            || Rejected.IsMatch(question)
            || !PollAnswers.Yes.Contains(yes) || !PollAnswers.No.Contains(no)) return null;
        return new(question, yes, no);
    }
}
