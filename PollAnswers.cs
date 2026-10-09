namespace TelegramQuizArrangement;

public static class PollAnswers
{
    public static readonly string[] Yes = ["Иду", "В деле", "Вписываюсь", "Буду", "Я с вами", "Участвую", "Место мне", "Готов играть", "Приду", "Погнали", "Меня записывайте", "Я в команде", "Буду играть", "Иду на квиз", "С вами в деле", "Присоединяюсь"];
    public static readonly string[] No = ["Я пас", "Не иду", "Пропускаю", "Без меня", "В этот раз мимо", "Скипаю", "Не смогу", "Останусь дома", "Не приду", "На этот квиз пас", "Пропущу квиз", "В этот раз пас", "Не участвую", "Откажусь", "Соррян, скипаю", "Пока без меня"];
    private static int position;
    private static (string Yes, string No)? previous;
    private static readonly object Gate = new();

    public static (string Yes, string No) Choose(string? yes = null, string? no = null)
    {
        lock (Gate)
        {
            (string Yes, string No) pair = yes is not null && no is not null ? (yes, no)
                : (Yes[position % Yes.Length], No[position % No.Length]);
            while (previous == pair)
            {
                position = (position + 1) % Yes.Length;
                pair = (Yes[position], No[position]);
            }
            previous = pair;
            position = (position + 1) % Yes.Length;
            return pair;
        }
    }
}
