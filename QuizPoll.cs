using System.Globalization;
using System.Net;
using System.Text.Json;

namespace TelegramQuizArrangement;

public static class QuizPoll
{
    public const string WazeUrl = "https://waze.com/ul?ll=25.186795%2C55.26793&navigate=yes";
    public const string VenueUrl = "https://2gis.ae/dubai/firm/70000001100982879";
    private static readonly string[] Months =
        ["янв", "фев", "мар", "апр", "май", "июн", "июл", "авг", "сен", "окт", "ноя", "дек"];

    public static DateTime NextGame(DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(now, timeZone).DateTime;
        var days = (7 - (int)local.DayOfWeek) % 7;
        var game = local.Date.AddDays(days).AddHours(19);
        return game <= local ? game.AddDays(7) : game;
    }

    public static string Question(DateTime game) =>
        $"Играем в это воскресенье в 19:00?\n{game.Day.ToString("00", CultureInfo.InvariantCulture)}-{Months[game.Month - 1]}";

    public static bool IsStart(string? text, string username)
    {
        var command = text?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return command == "/start" ||
            string.Equals(command, $"/start@{username}", StringComparison.OrdinalIgnoreCase);
    }

    public static Dictionary<string, object?> Payload(long chatId, int? threadId, DateTime game) => new()
    {
        ["chat_id"] = chatId,
        ["message_thread_id"] = threadId,
        ["question"] = Question(game),
        ["description"] = $"Harat’s Republic, Radisson Blu Hotel, Dubai\n<a href=\"{WebUtility.HtmlEncode(WazeUrl)}\">Как проехать — Waze</a>",
        ["description_parse_mode"] = "HTML",
        ["options"] = new[] { new { text = "ДА" }, new { text = "НЕТ" } },
        ["type"] = "regular",
        ["is_anonymous"] = false,
        ["allows_multiple_answers"] = false,
        ["allow_adding_options"] = true,
        ["reply_markup"] = new
        {
            inline_keyboard = new[] { new[] { new { text = "Как проехать — Waze", url = WazeUrl } } }
        }
    };

    public static void SelfTest()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException($"Test failed: {name}");
        }
        DateTime Game(string value) => NextGame(DateTimeOffset.Parse(value, CultureInfo.InvariantCulture), zone);
        Check(Question(Game("2026-10-09T12:00:00+04:00")) == "Играем в это воскресенье в 19:00?\n11-окт", "Friday");
        Check(Game("2026-10-11T18:59:00+04:00").Day == 11, "Sunday before game");
        Check(Game("2026-10-11T19:00:00+04:00").Day == 18, "Sunday at game time");
        Check(Game("2026-10-10T22:00:00Z").Day == 11, "Dubai timezone");
        Check(Question(Game("2026-12-31T12:00:00+04:00")).EndsWith("03-янв"), "Year boundary");
        Check(IsStart("/start@QuizBot", "QuizBot") && IsStart("/start hello", "QuizBot"), "Commands");
        Check(!IsStart("/start@OtherBot", "QuizBot") && !IsStart("/starting", "QuizBot"), "Other commands");
        var json = JsonSerializer.SerializeToElement(Payload(-100123, 42, Game("2026-10-09T12:00:00+04:00")));
        Check(!json.GetProperty("is_anonymous").GetBoolean() && !json.GetProperty("allows_multiple_answers").GetBoolean()
            && json.GetProperty("allow_adding_options").GetBoolean(), "Poll flags");
        Check(json.GetProperty("options")[0].GetProperty("text").GetString() == "ДА"
            && json.GetProperty("options")[1].GetProperty("text").GetString() == "НЕТ", "Options");
        Check(json.GetProperty("message_thread_id").GetInt32() == 42, "Forum topic");
        Console.WriteLine("All 10 checks passed.");
    }
}
