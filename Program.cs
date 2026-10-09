using TelegramQuizArrangement;
using System.Text.Json;
using System.Text;
using System.Runtime.InteropServices;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

if (args.Contains("--self-test"))
{
    QuizPoll.SelfTest();
    GroupAccess.SelfTest();
    await QuizWordingTests.Run();
    await WeeklyScheduleTests.Run();
    await ThirdAnswerRotation.SelfTest();
    await PollPublisherTests.Run();
    return;
}

string? token;
long ownerUserId;
OpenAiSettings openAiSettings;
ScheduleSettings scheduleSettings;
try
{
    var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
    token = settings.RootElement.GetProperty("Telegram").GetProperty("BotToken").GetString();
    ownerUserId = settings.RootElement.GetProperty("Telegram").GetProperty("OwnerUserId").GetInt64();
    if (ownerUserId <= 0) throw new InvalidOperationException();
    openAiSettings = OpenAiSettings.Read(settings.RootElement);
    scheduleSettings = ScheduleSettings.Read(settings.RootElement);
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
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
using var terminate = OperatingSystem.IsLinux() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM,
    context => { context.Cancel = true; cancellation.Cancel(); }) : null;
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
var api = new TelegramApi(http, token);
using var openAiHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
var wordingGenerator = new QuizWording(openAiHttp, openAiSettings,
    (input, output) => Console.WriteLine($"OpenAI usage: input_tokens={input}, output_tokens={output}"),
    status => Console.WriteLine($"OpenAI status: {status}"));
Console.WriteLine(string.IsNullOrWhiteSpace(openAiSettings.ApiKey)
    ? "OpenAI отключён: используется исходный опрос." : "OpenAI включён; при ошибке используется исходный опрос.");
long offset = 0;

try
{
    var publisher = new PollPublisher(Path.Combine(AppContext.BaseDirectory, "data"), api.Call);
    Task SendPoll(object payload, CancellationToken ct) => publisher.Send(payload, ct);
    using var schedule = new WeeklySchedule(scheduleSettings,
        Path.Combine(AppContext.BaseDirectory, "data"), DateTimeOffset.UtcNow);
    Console.WriteLine(schedule.NextRun(DateTimeOffset.UtcNow) is DateTime next
        ? $"Schedule: Thursday 20:00 Asia/Dubai; next {next:yyyy-MM-dd HH:mm}."
        : "Schedule disabled.");
    Task ScheduleTick() => schedule.Tick(DateTimeOffset.UtcNow,
        (_, ct) => wordingGenerator.Generate(ct), SendPoll, cancellation.Token,
        () => DateTimeOffset.UtcNow);
    var me = await api.Call("getMe", new { }, cancellation.Token);
    var username = me.GetProperty("username").GetString()!;
    Console.WriteLine($"@{username} запущен. Команда в группе: /start. Ctrl+C — остановить.");
    while (!cancellation.IsCancellationRequested)
    {
        await ScheduleTick();
        using var pollingDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var pollingWait = schedule.PollWaitSeconds(DateTimeOffset.UtcNow);
        pollingDeadline.CancelAfter(TimeSpan.FromSeconds(pollingWait));
        try
        {
            var updates = await api.Call("getUpdates", new { offset, timeout = pollingWait - 1, allowed_updates = new[] { "message", "my_chat_member" } }, pollingDeadline.Token);
            foreach (var update in updates.EnumerateArray())
            {
                await ScheduleTick();
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
                        else if (GroupAccess.ShouldBind(membership, ownerUserId))
                        {
                            schedule.SetDestination(membership.GetProperty("chat").GetProperty("id").GetInt64(), null);
                            Console.WriteLine("Schedule automatically bound to group where owner added bot.");
                        }
                        continue;
                    }
                    if (!update.TryGetProperty("message", out var message)) continue;
                    var incomingChat = message.GetProperty("chat");
                    if (incomingChat.GetProperty("type").GetString() is "group" or "supergroup"
                        && !message.TryGetProperty("sender_chat", out _)
                        && message.TryGetProperty("from", out var ownerSender)
                        && ownerSender.GetProperty("id").GetInt64() == ownerUserId)
                    {
                        int? incomingThread = message.TryGetProperty("message_thread_id", out var incomingTopic)
                            ? incomingTopic.GetInt32() : null;
                        schedule.SetDestination(incomingChat.GetProperty("id").GetInt64(), incomingThread);
                    }
                    if (!message.TryGetProperty("text", out var text)) continue;
                    var setGroup = QuizPoll.IsCommand(text.GetString(), username, "/setgroup");
                    if (!setGroup && !QuizPoll.IsStart(text.GetString(), username)) continue;
                    var chat = message.GetProperty("chat");
                    var chatId = chat.GetProperty("id").GetInt64();
                    int? thread = message.TryGetProperty("message_thread_id", out var threadId) ? threadId.GetInt32() : null;
                    if (setGroup)
                    {
                        if (chat.GetProperty("type").GetString() is not ("group" or "supergroup")
                            || message.TryGetProperty("sender_chat", out _)
                            || !message.TryGetProperty("from", out var sender)
                            || sender.GetProperty("id").GetInt64() != ownerUserId) continue;
                        schedule.SetDestination(chatId, thread);
                        Console.WriteLine("Schedule destination updated by owner; saved in persistent volume.");
                        continue;
                    }
                    if (chat.GetProperty("type").GetString() is not ("group" or "supergroup"))
                    {
                        await api.Call("sendMessage", new { chat_id = chatId, text = "Добавьте меня в группу и отправьте /start — я создам опрос о квизе." }, cancellation.Token);
                        continue;
                    }
                    var wording = await wordingGenerator.Generate(cancellation.Token);
                    await SendPoll(QuizPoll.Payload(chatId, thread, QuizPoll.NextGame(DateTimeOffset.UtcNow, zone), wording), cancellation.Token);
                    Console.WriteLine("Ручной опрос отправлен.");
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
        catch (OperationCanceledException) when (pollingDeadline.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            await ScheduleTick();
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
            // Keep weekly scheduling responsive during Telegram polling backoff, without parallel AI access.
            while (delay > 0)
            {
                var chunk = Math.Min(delay, 30);
                await Task.Delay(TimeSpan.FromSeconds(chunk), cancellation.Token);
                delay -= chunk;
                await ScheduleTick();
            }
        }
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
catch (Exception error)
{
    Console.Error.WriteLine($"Не удалось запустить бота ({error.GetType().Name}). Проверьте токен и соединение.");
    Environment.ExitCode = 1;
}
