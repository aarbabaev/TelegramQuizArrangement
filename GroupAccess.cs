using System.Text.Json;

namespace TelegramQuizArrangement;

public static class GroupAccess
{
    public static bool MustLeave(JsonElement update, long ownerUserId) =>
        update.GetProperty("chat").GetProperty("type").GetString() is "group" or "supergroup"
        && !IsMember(update.GetProperty("old_chat_member"))
        && IsMember(update.GetProperty("new_chat_member"))
        && update.GetProperty("from").GetProperty("id").GetInt64() != ownerUserId;

    private static bool IsMember(JsonElement member) => member.GetProperty("status").GetString() switch
    {
        "member" or "administrator" or "creator" => true,
        "restricted" => member.TryGetProperty("is_member", out var value) && value.GetBoolean(),
        _ => false
    };

    public static void SelfTest()
    {
        var checks = 0;
        void Check(long owner, long actor, string type, string oldStatus, string newStatus,
            bool expected, bool? oldIsMember = null, bool? newIsMember = null)
        {
            Dictionary<string, object> Member(string status, bool? isMember)
            {
                var member = new Dictionary<string, object> { ["status"] = status };
                if (isMember.HasValue) member["is_member"] = isMember.Value;
                return member;
            }
            var update = JsonSerializer.SerializeToElement(new
            {
                chat = new { type }, from = new { id = actor },
                old_chat_member = Member(oldStatus, oldIsMember),
                new_chat_member = Member(newStatus, newIsMember)
            });
            if (MustLeave(update, owner) != expected)
                throw new InvalidOperationException($"Group access test failed: {type}, {oldStatus} -> {newStatus}.");
            checks++;
        }

        foreach (var owner in new long[] { 1001, 9000000001 })
        {
            var other = owner + 1;
            foreach (var type in new[] { "group", "supergroup" })
            {
                foreach (var absent in new[] { "left", "kicked", "restricted" })
                {
                    foreach (var joined in new[] { "member", "administrator", "creator", "restricted" })
                    {
                        Check(owner, owner, type, absent, joined, false, false, true);
                        Check(owner, other, type, absent, joined, true, false, true);
                    }
                }
                foreach (var present in new[] { "member", "administrator", "creator", "restricted" })
                {
                    foreach (var next in new[] { "member", "administrator", "restricted", "left", "kicked" })
                        Check(owner, other, type, present, next, false, true, true);
                }
                Check(owner, other, type, "left", "restricted", false, newIsMember: false);
                Check(owner, other, type, "left", "restricted", false);
                Check(owner, other, type, "restricted", "member", true);
                Check(owner, other, type, "restricted", "restricted", false, false, false);
            }
            foreach (var type in new[] { "private", "channel" })
                Check(owner, other, type, "left", "member", false);
        }
        Console.WriteLine($"All {checks} group access checks passed.");
    }
}
