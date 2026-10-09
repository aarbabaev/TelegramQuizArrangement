using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TelegramQuizArrangement;

public sealed class TelegramApi(HttpClient client, string token)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<JsonElement> Call(string method, object payload, CancellationToken cancellation)
    {
        using var response = await client.PostAsJsonAsync(
            $"https://api.telegram.org/bot{token}/{method}", payload, JsonOptions, cancellation);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var root = document.RootElement;
        if (!root.GetProperty("ok").GetBoolean())
        {
            var code = root.TryGetProperty("error_code", out var errorCode) ? errorCode.GetInt32() : (int)response.StatusCode;
            var retry = root.TryGetProperty("parameters", out var parameters)
                && parameters.TryGetProperty("retry_after", out var seconds) ? seconds.GetInt32() : 5;
            // Do not log request URLs or exception messages: the URL contains the bot token.
            throw new TelegramApiException(code, retry);
        }
        return root.GetProperty("result").Clone();
    }
}

public sealed class TelegramApiException(int code, int retryAfter) : Exception
{
    public int Code { get; } = code;
    public int RetryAfter { get; } = retryAfter;
}
