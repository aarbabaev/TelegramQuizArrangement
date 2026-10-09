using TelegramQuizArrangement;
using System.Text.Json;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

if (args.Contains("--self-test"))
{
    QuizPoll.SelfTest();
    GroupAccess.SelfTest();
    return;
}

string? token;
long ownerUserId;
try
{
    var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
    token = settings.RootElement.GetProperty("Telegram").GetProperty("BotToken").GetString();
    ownerUserId = settings.RootElement.GetProperty("Telegram").GetProperty("OwnerUserId").GetInt64();
    if (ownerUserId <= 0) throw new InvalidOperationException();
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
{
    Console.Error.WriteLine("Не удалось прочитать appsettings.json. Укажите Telegram:BotToken и положительный Telegram:OwnerUserId.");
    Environment.ExitCode = 1;
    return;
}
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Укажите токен BotFather в appsettings.json, поле Telegram:BotToken.");
    Environment.ExitCode = 1;
    return;
}

var zone = TimeZoneInfo.FindSystemTimeZoneById(
    Environment.GetEnvironmentVariable("QUIZ_TIME_ZONE") ?? "Asia/Dubai");
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
var api = new TelegramApi(http, token);
long offset = 0;

try
{
    var me = await api.Call("getMe", new { }, cancellation.Token);
    var username = me.GetProperty("username").GetString()!;
    Console.WriteLine($"@{username} запущен. Команда в группе: /start. Ctrl+C — остановить.");
    while (!cancellation.IsCancellationRequested)
    {
        try
        {
            var updates = await api.Call("getUpdates", new { offset, timeout = 30, allowed_updates = new[] { "message", "my_chat_member" } }, cancellation.Token);
            foreach (var update in updates.EnumerateArray())
            {
                var updateId = update.GetProperty("update_id").GetInt64();
                try
                {
                    if (update.TryGetProperty("my_chat_member", out var membership))
                    {
                        if (GroupAccess.MustLeave(membership, ownerUserId))
                        {
                            await api.Call("leaveChat", new { chat_id = membership.GetProperty("chat").GetProperty("id").GetInt64() }, cancellation.Token);
                            Console.WriteLine("Бот покинул группу: добавление разрешено только владельцу.");
                        }
                        continue;
                    }
                    if (!update.TryGetProperty("message", out var message)
                        || !message.TryGetProperty("text", out var text)) continue;
                    if (!QuizPoll.IsStart(text.GetString(), username)) continue;
                    var chat = message.GetProperty("chat");
                    var chatId = chat.GetProperty("id").GetInt64();
                    int? thread = message.TryGetProperty("message_thread_id", out var threadId) ? threadId.GetInt32() : null;
                    if (chat.GetProperty("type").GetString() is not ("group" or "supergroup"))
                    {
                        await api.Call("sendMessage", new { chat_id = chatId, text = "Добавьте меня в группу и отправьте /start — я создам опрос о квизе." }, cancellation.Token);
                        continue;
                    }
                    await api.Call("sendPoll", QuizPoll.Payload(chatId, thread, QuizPoll.NextGame(DateTimeOffset.UtcNow, zone)), cancellation.Token);
                    Console.WriteLine($"Опрос отправлен в чат {chatId}.");
                }
                catch (TelegramApiException error) when (error.Code != 429)
                {
                    Console.Error.WriteLine($"Не удалось обработать команду: Telegram API {error.Code}. Проверьте права бота отправлять опросы.");
                }
                finally
                {
                    // A failed/uncertain send is not retried automatically, to avoid duplicate polls.
                    offset = updateId + 1;
                }
            }
        }
        catch (TelegramApiException error) when (error.Code is 401 or 409)
        {
            Console.Error.WriteLine($"Telegram API {error.Code}: проверьте токен, отключите webhook и другие экземпляры бота.");
            Environment.ExitCode = 1;
            break;
        }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or TelegramApiException
            || error is TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            var delay = error is TelegramApiException telegram ? Math.Clamp(telegram.RetryAfter, 1, 3600) : 5;
            Console.Error.WriteLine($"Ошибка соединения/API ({error.GetType().Name}). Повтор через {delay} сек.");
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellation.Token);
        }
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
catch (Exception error)
{
    Console.Error.WriteLine($"Не удалось запустить бота ({error.GetType().Name}). Проверьте токен и соединение.");
    Environment.ExitCode = 1;
}
