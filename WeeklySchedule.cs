using System.Globalization;
using System.Text.Json;

namespace TelegramQuizArrangement;

public sealed record ScheduleSettings(bool Enabled = false, long ChatId = 0, int? ThreadId = null,
    string? FirstRunLocal = null)
{
    public static ScheduleSettings Read(JsonElement root)
    {
        var settings = root.TryGetProperty("Schedule", out var section)
            ? section.Deserialize<ScheduleSettings>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
            : new ScheduleSettings();
        if (settings.Enabled && (settings.ChatId >= 0 || settings.ThreadId is <= 0))
            throw new InvalidOperationException("Invalid schedule destination.");
        if (settings.FirstRunLocal is not null) WeeklySchedule.ParseFirst(settings.FirstRunLocal);
        return settings;
    }
}

public sealed record ScheduleMark(DateTime DueLocal, DateTime GameLocal, string Status);

// One synchronous owner in the polling loop; never concurrently touches QuizWording.
public sealed class WeeklySchedule : IDisposable
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");
    private readonly ScheduleSettings settings;
    private readonly string directory;
    private readonly DateTime firstRun;
    private readonly FileStream? processLock;
    private bool storageFailed;

    public WeeklySchedule(ScheduleSettings settings, string directory, DateTimeOffset now)
    {
        this.settings = settings;
        this.directory = directory;
        if (!settings.Enabled) return;
        Directory.CreateDirectory(directory);
        processLock = new FileStream(Path.Combine(directory, "schedule.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var activationPath = Path.Combine(directory, "activation.json");
            if (!File.Exists(activationPath))
            {
                var initial = settings.FirstRunLocal is null ? NextThursday(Local(now)) : ParseFirst(settings.FirstRunLocal);
                AtomicWrite(activationPath, JsonSerializer.Serialize(initial), overwrite: false);
            }
            var activated = JsonSerializer.Deserialize<DateTime>(File.ReadAllText(activationPath));
            ValidateThursday(activated);
            // Never move the persisted activation backwards when settings change.
            firstRun = settings.FirstRunLocal is null ? activated : Max(activated, ParseFirst(settings.FirstRunLocal));
            foreach (var path in Directory.EnumerateFiles(directory, "week-*.json"))
            {
                var mark = ReadMark(path);
                if (mark.Status == "reserved")
                    AtomicWrite(path, JsonSerializer.Serialize(mark with { Status = "uncertain" }));
            }
        }
        catch { processLock.Dispose(); throw; }
    }

    public static DateTime ParseFirst(string value)
    {
        var parsed = DateTime.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None);
        ValidateThursday(parsed);
        return parsed;
    }
    private static void ValidateThursday(DateTime value)
    {
        if (value.DayOfWeek != DayOfWeek.Thursday || value.TimeOfDay != TimeSpan.FromHours(20))
            throw new InvalidOperationException("Schedule starts on Thursday at 20:00 Dubai.");
    }
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Local(DateTimeOffset now) => TimeZoneInfo.ConvertTime(now, Zone).DateTime;
    public static DateTime LatestThursday(DateTime local) =>
        local.Date.AddDays(-((7 + (int)local.DayOfWeek - (int)DayOfWeek.Thursday) % 7)).AddHours(20)
            is var due && due > local ? due.AddDays(-7) : due;
    public static DateTime NextThursday(DateTime local) => LatestThursday(local).AddDays(7);
    public static DateTime GameFor(DateTime due) => due.Date.AddDays(3).AddHours(19);
    private string MarkPath(DateTime due) => Path.Combine(directory, $"week-{due:yyyy-MM-dd}.json");
    private static ScheduleMark ReadMark(string path)
    {
        var mark = JsonSerializer.Deserialize<ScheduleMark>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Invalid schedule state.");
        ValidateThursday(mark.DueLocal);
        if (mark.GameLocal != GameFor(mark.DueLocal) || mark.Status is not ("reserved" or "sent" or "failed" or "uncertain"))
            throw new InvalidOperationException("Invalid schedule state.");
        return mark;
    }
    public DateTime? Due(DateTimeOffset now)
    {
        if (!settings.Enabled || storageFailed) return null;
        var local = Local(now);
        var due = LatestThursday(local);
        return due >= firstRun && local < GameFor(due) && !File.Exists(MarkPath(due)) ? due : null;
    }
    public DateTime? NextRun(DateTimeOffset now) => !settings.Enabled || storageFailed ? null
        : Due(now) ?? Max(NextThursday(Local(now)), firstRun);
    public int PollWaitSeconds(DateTimeOffset now) => NextRun(now) is DateTime next
        ? Math.Clamp((int)Math.Ceiling(Math.Min(30, (next - Local(now)).TotalSeconds)), 1, 30) : 30;

    public async Task Tick(DateTimeOffset now, Func<DateTime, CancellationToken, Task<PollWording?>> wording,
        Func<object, CancellationToken, Task> send, CancellationToken cancellation, Func<DateTimeOffset>? clock = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var due = Due(now);
        if (due is null) return;
        var mark = new ScheduleMark(due.Value, GameFor(due.Value), "reserved");
        var path = MarkPath(due.Value);
        try
        {
            // Persist reservation before AI and Telegram. Any crash sacrifices this slot rather than retries it.
            AtomicWrite(path, JsonSerializer.Serialize(mark), overwrite: false);
        }
        catch (IOException) when (File.Exists(path)) { return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            storageFailed = true;
            Console.Error.WriteLine("Schedule storage error: automatic polls disabled; no send attempted.");
            return;
        }
        var finalStatus = "uncertain";
        try
        {
            var generated = await wording(mark.GameLocal, cancellation);
            // AI latency may cross the Sunday cutoff: do not start a late Telegram request.
            if (Local(clock?.Invoke() ?? now) >= mark.GameLocal) finalStatus = "failed";
            else
            {
                await send(QuizPoll.Payload(settings.ChatId, settings.ThreadId, mark.GameLocal, generated), cancellation);
                finalStatus = "sent";
            }
        }
        catch (TelegramApiException) { finalStatus = "failed"; }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        { finalStatus = "uncertain"; }
        finally
        {
            try { AtomicWrite(path, JsonSerializer.Serialize(mark with { Status = finalStatus })); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                storageFailed = true;
                Console.Error.WriteLine("Schedule result save failed; reserved slot will not be retried.");
            }
            Console.WriteLine($"Schedule {mark.DueLocal:yyyy-MM-dd HH:mm} Dubai: {finalStatus}; no automatic retry.");
        }
        cancellation.ThrowIfCancellationRequested();
    }

    private static void AtomicWrite(string path, string value, bool overwrite = true)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(value);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Dispose() => processLock?.Dispose();
}
