using System.Net;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

string token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
               ?? "8674827165:AAF9llVMbBQJrkce4_nDFq0FejrIfuqpqyQ";
var botClient = new TelegramBotClient(token);

// Базовый список задач
List<string> defaultTasks = new() {
    "Подмести пол",
    "Помыть посуду",
    "Заправить кровать",
    "Помыть стекла",
    "Протереть пыль",
    "Постирать вещи",
    "Выкинуть мусор"
};

// Хранилища данных
var userTasks = new Dictionary<long, List<string>>();
var userProgress = new Dictionary<long, int>();
var userPhotos = new Dictionary<long, List<string>>();
var userWantedPartner = new Dictionary<long, long>();
var usernameToId = new Dictionary<string, long>();
var userState = new Dictionary<long, string>();
var mediaGroupBuffer = new Dictionary<long, List<string>>();
var mediaGroupWaitTask = new Dictionary<long, Task>();

var mainKeyboard = new ReplyKeyboardMarkup(new[]
{
    new KeyboardButton[] { new("Не моя обязанность") },
    new KeyboardButton[] { new("Добавить обязанность"), new("Удалить обязанность") },
    new KeyboardButton[] { new("Начать отчет заново") },
    new KeyboardButton[] { new("Сменить партнера") }
})
{ ResizeKeyboard = true };

Console.WriteLine("Бот запущен...");

using var cts = new CancellationTokenSource();
botClient.StartReceiving(HandleUpdateAsync, HandlePollingErrorAsync, new ReceiverOptions { AllowedUpdates = [] }, cts.Token);

// Получить реального партнера (если есть взаимность)
long? GetActualPartner(long chatId)
{
    if (userWantedPartner.TryGetValue(chatId, out long wanted))
    {
        if (userWantedPartner.TryGetValue(wanted, out long wantsMe) && wantsMe == chatId)
        {
            return wanted;
        }
    }
    return null;
}

// Получить список задач (общий для пары или свой)
List<string> GetTasksForUser(long chatId)
{
    long? partner = GetActualPartner(chatId);
    if (partner.HasValue)
    {
        long pairKey = Math.Min(chatId, partner.Value) * 1000000 + Math.Max(chatId, partner.Value);
        if (!userTasks.ContainsKey(pairKey))
        {
            userTasks[pairKey] = new List<string>(defaultTasks);
        }
        return userTasks[pairKey];
    }
    else
    {
        if (!userTasks.ContainsKey(chatId))
        {
            userTasks[chatId] = new List<string>(defaultTasks);
        }
        return userTasks[chatId];
    }
}

// Сохранить задачи для пользователя/пары
void SetTasksForUser(long chatId, List<string> tasks)
{
    long? partner = GetActualPartner(chatId);
    if (partner.HasValue)
    {
        long pairKey = Math.Min(chatId, partner.Value) * 1000000 + Math.Max(chatId, partner.Value);
        userTasks[pairKey] = tasks;
    }
    else
    {
        userTasks[chatId] = tasks;
    }
}

async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
{
    if (update.Message is not { } message) return;
    long chatId = message.Chat.Id;
    string? text = message.Text;
    string? username = message.From?.Username?.ToLower();
    string senderName = message.From?.FirstName ?? "Партнер";

    if (!string.IsNullOrEmpty(username)) usernameToId[username] = chatId;

    // Инициализация
    if (!userProgress.ContainsKey(chatId)) userProgress[chatId] = 0;
    if (!userPhotos.ContainsKey(chatId)) userPhotos[chatId] = new List<string>();
    if (!userWantedPartner.ContainsKey(chatId)) userWantedPartner[chatId] = 0;
    if (!mediaGroupBuffer.ContainsKey(chatId)) mediaGroupBuffer[chatId] = new List<string>();

    // /start или кнопка смены партнера
    if (text == "/start" || text == "Сменить партнера")
    {
        await StartPartnerSetup(bot, chatId, ct);
        return;
    }

    // Логика состояний
    if (userState.TryGetValue(chatId, out var state))
    {
        if (state == "awaiting_partner" && text != null)
        {
            string targetUser = text.Replace("@", "").Trim().ToLower();

            if (!usernameToId.TryGetValue(targetUser, out long targetId))
            {
                await bot.SendMessage(chatId, "❌ Я не вижу этого пользователя. Пусть он напишет мне /start, а потом повтори ввод.");
                return;
            }

            if (targetId == chatId)
            {
                await bot.SendMessage(chatId, "❌ Нельзя выбрать самого себя");
                return;
            }

            userWantedPartner[chatId] = targetId;
            userState.Remove(chatId);

            long? actualPartner = GetActualPartner(chatId);

            if (actualPartner.HasValue)
            {
                await bot.SendMessage(chatId, $"🎉 <b>Пара создана</b>\n\nВаш партнер: @{targetUser}\nТеперь у вас общий список обязанностей 🏠",
                    ParseMode.Html, replyMarkup: mainKeyboard);

                await bot.SendMessage(actualPartner.Value,
                    $"🎉 <b>Пара создана</b>\n\nВаш партнер: @{username}\nТеперь у вас общий список обязанностей! 🏠",
                    ParseMode.Html, replyMarkup: mainKeyboard);

                userProgress[chatId] = 0;
                userPhotos[chatId].Clear();
                await StartTaskFlow(bot, chatId, ct);
            }
            else
            {
                await bot.SendMessage(chatId,
                    $"⏳ <b>Ожидание подтверждения</b>\n\nВы указали @{targetUser} как партнера.\nКогда он тоже укажет вас - создастся общая пара и общий список обязанностей!\n\nПока что вы работаете со своим списком задач.",
                    ParseMode.Html, replyMarkup: mainKeyboard);
                await StartTaskFlow(bot, chatId, ct);
            }
            return;
        }

        if (state == "adding_task" && text != null)
        {
            var tasks = GetTasksForUser(chatId);
            tasks.Add(text);
            SetTasksForUser(chatId, tasks);
            userState.Remove(chatId);

            long? partner = GetActualPartner(chatId);
            if (partner.HasValue)
            {
                await bot.SendMessage(chatId, $"➕ Задача {text} добавлена в общий список", replyMarkup: mainKeyboard);
                await bot.SendMessage(partner.Value, $"📝 Партнер добавил новую задачу в общий список: <b>{text}</b>", ParseMode.Html);
            }
            else
            {
                await bot.SendMessage(chatId, $"➕ Задача {text} добавлена в ваш список!", replyMarkup: mainKeyboard);
            }
            return;
        }

        if (state == "removing_task" && text != null)
        {
            var tasks = GetTasksForUser(chatId);
            bool removed = tasks.Remove(text);
            SetTasksForUser(chatId, tasks);
            userState.Remove(chatId);

            if (removed)
            {
                long? partner = GetActualPartner(chatId);
                if (partner.HasValue)
                {
                    await bot.SendMessage(chatId, "➖ Задача удалена из общего списка", replyMarkup: mainKeyboard);
                    await bot.SendMessage(partner.Value, $"🗑 Партнер удалил задачу из общего списка: <b>{text}</b>", ParseMode.Html);
                }
                else
                {
                    await bot.SendMessage(chatId, "➖ Задача удалена из вашего списка", replyMarkup: mainKeyboard);
                }
            }
            else
            {
                await bot.SendMessage(chatId, "⚠️ Задача не найдена", replyMarkup: mainKeyboard);
            }
            return;
        }
    }

    // Обработка фото
    if (message.Photo is not null)
    {
        string photoId = message.Photo.Last().FileId;

        // Если это одиночное фото
        if (string.IsNullOrEmpty(message.MediaGroupId))
        {
            userPhotos[chatId].Add(photoId);
            userProgress[chatId]++;
            await SendNextStep(bot, chatId, senderName, ct);
        }
        else
        {
            // Это медиа-группа
            mediaGroupBuffer[chatId].Add(photoId);

            if (!mediaGroupWaitTask.ContainsKey(chatId) || mediaGroupWaitTask[chatId]?.IsCompleted != false)
            {
                mediaGroupWaitTask[chatId] = Task.Delay(1000, ct).ContinueWith(async _ =>
                {
                    foreach (var pId in mediaGroupBuffer[chatId])
                    {
                        userPhotos[chatId].Add(pId);
                    }

                    mediaGroupBuffer[chatId].Clear();

                    userProgress[chatId]++;

                    await SendNextStep(bot, chatId, senderName, ct);

                    mediaGroupWaitTask[chatId] = null;
                }, ct);
            }
        }
        return;
    }

    // Кнопки меню
    switch (text)
    {
        case "Начать отчет заново":
            userProgress[chatId] = 0;
            userPhotos[chatId].Clear();
            await StartTaskFlow(bot, chatId, ct);
            return;

        case "Добавить обязанность":
            userState[chatId] = "adding_task";
            await bot.SendMessage(chatId, "Напиши новую задачу:", replyMarkup: new ReplyKeyboardRemove());
            return;

        case "Удалить обязанность":
            userState[chatId] = "removing_task";
            await bot.SendMessage(chatId, "Напиши название задачи для удаления:", replyMarkup: new ReplyKeyboardRemove());
            return;

        case "Не моя обязанность":
            userProgress[chatId]++;
            await SendNextStep(bot, chatId, senderName, ct);
            return;
    }
}

async Task StartPartnerSetup(ITelegramBotClient bot, long chatId, CancellationToken ct)
{
    long? existingPartner = GetActualPartner(chatId);

    if (existingPartner.HasValue)
    {
        await bot.SendMessage(chatId,
            "⚠️ <b>Внимание!</b>\n\n" +
            $"Вы уже состоите в паре с @{usernameToId.FirstOrDefault(x => x.Value == existingPartner.Value).Key}\n\n" +
            "Если вы смените партнера, старая связь разорвется и общий список задач пропадет у обоих.\n\n" +
            "Продолжить? Используйте /start еще раз для подтверждения.",
            ParseMode.Html);

        userWantedPartner.Remove(chatId);
        if (userWantedPartner.TryGetValue(existingPartner.Value, out long oldPartnerWants) && oldPartnerWants == chatId)
        {
            userWantedPartner.Remove(existingPartner.Value);
            await bot.SendMessage(existingPartner.Value,
                "🔌 Ваш партнер разорвал связь. Теперь вы работаете со своим списком задач.",
                replyMarkup: mainKeyboard);
        }
        return;
    }

    userState[chatId] = "awaiting_partner";
    await bot.SendMessage(chatId,
        "🔗 <b>Настройка пары</b>\n\n" +
        "Введите @username человека, с которым хотите создать пару.\n\n" +
        "❗️ <b>Как это работает:</b>\n" +
        "1) Вы указываете username партнера\n" +
        "2) Партнер указывает ваш username\n" +
        "3) Только после взаимного выбора создается общий список обязанностей\n\n" +
        "Если взаимности нет - каждый работает со своим списком.\n\n" +
        "Пример: @username",
        ParseMode.Html,
        replyMarkup: new ReplyKeyboardRemove());
}

async Task StartTaskFlow(ITelegramBotClient bot, long chatId, CancellationToken ct)
{
    var tasks = GetTasksForUser(chatId);
    long? partner = GetActualPartner(chatId);

    string mode = partner.HasValue ? "👥 Режим пары" : "👤 Личный режим";

    if (tasks.Count == 0)
    {
        await bot.SendMessage(chatId, $"{mode}\n\nСписок задач пуст. Добавь что-нибудь через меню!");
        return;
    }

    userProgress[chatId] = 0;
    userPhotos[chatId].Clear();

    string partnerText = partner.HasValue ? $"\n👫 Ваш партнер: @{usernameToId.FirstOrDefault(x => x.Value == partner.Value).Key}" : "";

    await bot.SendMessage(chatId,
        $"🎯 <b>Начало проверки</b>\n{mode}{partnerText}\n\n" +
        $"📋 Задача №1: <b>{tasks[0]}</b>\n\n" +
        $"📸 Отправь фото/фотки как доказательство выполнения",
        ParseMode.Html,
        replyMarkup: mainKeyboard);
}

async Task SendNextStep(ITelegramBotClient bot, long chatId, string senderName, CancellationToken ct)
{
    int step = userProgress[chatId];
    var tasks = GetTasksForUser(chatId);
    long? partner = GetActualPartner(chatId);

    if (step < tasks.Count)
    {
        await bot.SendMessage(chatId,
            $"✅ Принято! Следующая задача: <b>{tasks[step]}</b>\n\n📸 Отправь фото как доказательство",
            ParseMode.Html);
    }
    else
    {
        await bot.SendMessage(chatId, "🎉 <b>Отлично! Все задачи выполнены.</b> Отправляю отчет...", ParseMode.Html);

        if (partner.HasValue)
        {
            var photos = userPhotos[chatId];
            if (photos.Count > 0)
            {
                await bot.SendMessage(partner.Value,
                    $"📢 <b>Отчет об уборке</b>\n\n" +
                    $"👤 От: {senderName}\n" +
                    $"✅ Выполнено задач: {tasks.Count}\n" +
                    $"📸 Всего доказательств: {photos.Count}");

                var chunks = photos.Chunk(10);
                foreach (var chunk in chunks)
                {
                    var mediaGroup = chunk.Select(id => new InputMediaPhoto(InputFile.FromFileId(id))).ToList();
                    await bot.SendMediaGroup(partner.Value, mediaGroup, cancellationToken: ct);
                    await Task.Delay(500, ct);
                }
            }
            else
            {
                await bot.SendMessage(partner.Value, $"📢 {senderName} закончил(а) уборку, но не приложил(а) фото.");
            }

            await bot.SendMessage(chatId, "📨 Отчет успешно отправлен партнеру");
        }
        else
        {
            await bot.SendMessage(chatId, "📝 Отчет сохранен (у вас нет партнера для отправки).");
        }

        userProgress[chatId] = 0;
        userPhotos[chatId].Clear();
    }
}

Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
{
    Console.WriteLine($"Ошибка: {ex.Message}");
    return Task.CompletedTask;
}

// Минимальный HTTP-сервер для Render Health Check

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
var httpUrl = $"http://0.0.0.0:{port}/";

var httpListener = new HttpListener();
httpListener.Prefixes.Add(httpUrl);
httpListener.Start();

Console.WriteLine($"Health check server running on {httpUrl}");

_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            var context = await httpListener.GetContextAsync();
            var response = context.Response;
            string responseString = "OK";
            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Health check error: {ex.Message}");
        }
    }
});

await Task.Delay(-1);