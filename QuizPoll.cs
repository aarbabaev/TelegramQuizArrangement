using System.Globalization;
using System.Net;
using System.Text.Json;

namespace TelegramQuizArrangement;

public static class QuizPoll
{
    public const string WazeUrl = "https://waze.com/ul?ll=25.186795%2C55.26793&navigate=yes";
    public const string VenueUrl = "https://2gis.ae/dubai/firm/70000001100982879";
    private static readonly string[] Months =
        ["января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];

    public static DateTime NextGame(DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(now, timeZone).DateTime;
        var days = (7 - (int)local.DayOfWeek) % 7;
        var game = local.Date.AddDays(days).AddHours(19);
        return game <= local ? game.AddDays(7) : game;
    }

    public static string Question(DateTime game) =>
        $"Играем в это воскресенье?\n{GameDate(game)}";

    public static string GameDate(DateTime game) => $"{game.Day.ToString("00", CultureInfo.InvariantCulture)}-{Months[game.Month - 1]} {game.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    public static bool IsStart(string? text, string username) => IsCommand(text, username, "/start");

    public static bool IsCommand(string? text, string username, string expected)
    {
        var command = text?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return command == expected ||
            string.Equals(command, $"{expected}@{username}", StringComparison.OrdinalIgnoreCase);
    }

    public static Dictionary<string, object?> Payload(long chatId, int? threadId, DateTime game, PollWording? wording = null)
    {
        var answers = PollAnswers.Choose(wording?.Yes, wording?.No);
        return new()
    {
        ["chat_id"] = chatId,
        ["message_thread_id"] = threadId,
        ["question"] = wording is null ? Question(game) : $"{wording.Question}\n{GameDate(game)}",
        ["description"] = $"Harat’s Republic, Radisson Blu Hotel, Dubai\n<a href=\"{WebUtility.HtmlEncode(WazeUrl)}\">Как проехать — Waze</a>",
        ["description_parse_mode"] = "HTML",
        ["options"] = new[] { new { text = $"🟩 {answers.Yes}" }, new { text = $"🟥 {answers.No}" } },
        ["type"] = "regular",
        ["is_anonymous"] = false,
        ["allows_multiple_answers"] = false,
        ["allow_adding_options"] = true,
        ["reply_markup"] = new
        {
            inline_keyboard = new[] { new[] { new { text = "Как проехать — Waze", url = WazeUrl } } }
        }
    };
    }

    public static void SelfTest()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException($"Test failed: {name}");
        }
        DateTime Game(string value) => NextGame(DateTimeOffset.Parse(value, CultureInfo.InvariantCulture), zone);
        Check(Question(Game("2026-10-09T12:00:00+04:00")) == "Играем в это воскресенье?\n11-октября 19:00", "Friday");
        Check(Game("2026-10-11T18:59:00+04:00").Day == 11, "Sunday before game");
        Check(Game("2026-10-11T19:00:00+04:00").Day == 18, "Sunday at game time");
        Check(Game("2026-10-10T22:00:00Z").Day == 11, "Dubai timezone");
        Check(Question(Game("2026-12-31T12:00:00+04:00")).EndsWith("03-января 19:00"), "Year boundary");
        Check(IsStart("/start@QuizBot", "QuizBot") && IsStart("/start hello", "QuizBot"), "Commands");
        Check(!IsStart("/start@OtherBot", "QuizBot") && !IsStart("/starting", "QuizBot"), "Other commands");
        var json = JsonSerializer.SerializeToElement(Payload(-100123, 42, Game("2026-10-09T12:00:00+04:00")));
        Check(!json.GetProperty("is_anonymous").GetBoolean() && !json.GetProperty("allows_multiple_answers").GetBoolean()
            && json.GetProperty("allow_adding_options").GetBoolean(), "Poll flags");
        Check(PollAnswers.Yes.Contains(json.GetProperty("options")[0].GetProperty("text").GetString()![3..])
            && PollAnswers.No.Contains(json.GetProperty("options")[1].GetProperty("text").GetString()![3..]), "Options");
        Check(json.GetProperty("message_thread_id").GetInt32() == 42, "Forum topic");
        Console.WriteLine("All 10 checks passed.");
    }
}
