using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

using var playwright = await Playwright.CreateAsync();

double? cachedMarketPriceRub = null;
DateTimeOffset cachedMarketPriceAt = DateTimeOffset.MinValue;
bool stopAllRequested = false;

var yandexBrowserPath = ResolveYandexBrowserPath();
var userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoKBWW", "PlaywrightProfile");
Directory.CreateDirectory(userDataDir);

Console.WriteLine($"Использую Яндекс Браузер: {yandexBrowserPath}");
Console.WriteLine($"Профиль сессии: {userDataDir}");

await using var context = await playwright.Chromium.LaunchPersistentContextAsync(userDataDir, new BrowserTypeLaunchPersistentContextOptions
{
    Headless = false,
    SlowMo = 60,
    ExecutablePath = yandexBrowserPath,
    Args = ["--start-maximized"],
    ViewportSize = new ViewportSize { Width = 1440, Height = 900 }
});

var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
await page.GotoAsync("https://web.telegram.org/k/", new PageGotoOptions
{
    WaitUntil = WaitUntilState.DOMContentLoaded,
    Timeout = 0
});

Console.WriteLine("Telegram Web открыт.");
Console.WriteLine("1) Если сессия сохранилась — просто откройте CryptoBot.");
Console.WriteLine("2) Если попросило логин — войдите вручную (сессия сохранится в профиль).");
Console.WriteLine("3) Нажмите ENTER, когда меню бота открыто.");
Console.ReadLine();

await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 0 });

PrintMenuToConsole(await CollectMenuDataAsync(page));
await RunInteractiveLoopAsync(page);

Console.WriteLine();
Console.WriteLine("Нажмите ENTER для закрытия браузера...");
Console.ReadLine();

async Task RunInteractiveLoopAsync(IPage page)
{
    PrintCommandsHint();

    while (true)
    {
        Console.Write("Ваш выбор: ");
        var input = Console.ReadLine()?.Trim();

        if (string.Equals(input, "Q", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        if (string.Equals(input, "R", StringComparison.OrdinalIgnoreCase))
        {
            PrintMenuToConsole(await CollectMenuDataAsync(page));
            PrintCommandsHint();
            continue;
        }

        if (string.Equals(input, "S", StringComparison.OrdinalIgnoreCase))
        {
            stopAllRequested = true;
            Console.WriteLine("Получен стоп-сигнал. Текущие процессы автоматики будут остановлены.");
            PrintCommandsHint();
            continue;
        }

        if (string.Equals(input, "A", StringComparison.OrdinalIgnoreCase) || string.Equals(input, "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            await RunP2PAutomationAsync(page);
            PrintCommandsHint();
            continue;
        }

        if (!int.TryParse(input, out var displayIndex))
        {
            Console.WriteLine("Некорректный ввод. Укажите индекс, A, R, S или Q.");
            PrintCommandsHint();
            continue;
        }

        var clicked = await ClickVisibleButtonByIndexAsync(page, displayIndex);
        if (!clicked)
        {
            Console.WriteLine($"Кнопка с индексом {displayIndex} не найдена в последних 30.");
            PrintCommandsHint();
            continue;
        }

        await page.WaitForTimeoutAsync(500);
        PrintMenuToConsole(await CollectMenuDataAsync(page));
        PrintCommandsHint();
    }
}

static void PrintCommandsHint()
{
    Console.WriteLine("Команды: индекс кнопки (0..), A - автосценарий P2P, R - перескан, S - стоп автоматики, Q - выход.");
}

async Task RunP2PAutomationAsync(IPage page)
{
    stopAllRequested = false;

    var targetPriceRub = await ReadTargetPriceRubAsync(
        getCached: () => (cachedMarketPriceRub, cachedMarketPriceAt),
        setCached: value => { cachedMarketPriceRub = value.Price; cachedMarketPriceAt = value.At; });
    Console.WriteLine($"Целевая цена для отбора: {targetPriceRub.ToString(CultureInfo.InvariantCulture)} RUB");

    var volumeFilter = ReadVolumeFilterRub();
    Console.WriteLine(volumeFilter.MinRub is null
        ? $"Фильтр объема: до {volumeFilter.MaxRub.ToString(CultureInfo.InvariantCulture)} RUB"
        : $"Фильтр объема: от {volumeFilter.MinRub.Value.ToString(CultureInfo.InvariantCulture)} до {volumeFilter.MaxRub.ToString(CultureInfo.InvariantCulture)} RUB");

    var notificationUsers = ReadNotificationUsers();
    Console.WriteLine($"Уведомления будут отправляться: {string.Join(", ", notificationUsers)}");

    Console.WriteLine("Запускаю автоматизацию: P2P -> Купить -> Tether (USDT) -> СБП");
    var sequence = new[] { "P2P", "Купить", "Tether (USDT)", "СБП" };

    foreach (var expected in sequence)
    {
        Console.WriteLine($"Жду 2 сек перед авто-нажатием '{expected}'...");
        await WaitWithStopAsync(page, 2000);
        if (stopAllRequested)
        {
            Console.WriteLine("Автоматизация остановлена клавишей S.");
            return;
        }

        var clicked = await ClickVisibleButtonByTextAsync(page, expected, startsWith: false);
        if (!clicked)
        {
            Console.WriteLine($"Автоматизация остановлена: кнопка '{expected}' не найдена.");
            return;
        }

        Console.WriteLine($"Авто-нажатие: '{expected}' выполнено. Жду 2 сек...");
        await WaitWithStopAsync(page, 2000);
        if (stopAllRequested)
        {
            Console.WriteLine("Автоматизация остановлена клавишей S.");
            return;
        }
        PrintMenuToConsole(await CollectMenuDataAsync(page));
    }

    var best = await FindBestOfferWithPagingAsync(page, targetPriceRub, volumeFilter, notificationUsers);
    if (best is null)
    {
        Console.WriteLine("Объявления по заданным параметрам не найдены.");
        return;
    }

    Console.WriteLine($"Выбираю лучшее объявление: [{best.DisplayIndex}] {best.SourceLabel}");
    Console.WriteLine("Жду 1 сек перед нажатием лучшего объявления...");
    await WaitWithStopAsync(page, 1000);
    if (stopAllRequested)
    {
        Console.WriteLine("Автоматизация остановлена клавишей S.");
        return;
    }

    var bestClicked = await ClickVisibleButtonByIndexAsync(page, best.DisplayIndex, isAutomation: true);
    if (!bestClicked)
    {
        Console.WriteLine("Не удалось нажать лучшее объявление.");
        return;
    }

    await ExecuteDealActionFlowAsync(page, best);
    if (stopAllRequested)
    {
        Console.WriteLine("Автоматизация остановлена клавишей S.");
        return;
    }

    Console.WriteLine("Жду 2 сек после создания сделки, чтобы сообщение успело появиться...");
    await WaitWithStopAsync(page, 2000);
    if (stopAllRequested)
    {
        Console.WriteLine("Автоматизация остановлена клавишей S.");
        return;
    }

    var dealInfo = await ExtractDealInfoWithRescansAsync(page, maxAttempts: 20);
    PrintDealInfo(dealInfo);

    if (string.IsNullOrWhiteSpace(dealInfo.MessageText))
    {
        Console.WriteLine("Сообщение сделки пока пустое. Жду 4 сек и делаю повторный сбор...");
        await WaitWithStopAsync(page, 4000);
        if (!stopAllRequested)
        {
            dealInfo = await ExtractDealInfoWithRescansAsync(page, maxAttempts: 12);
            PrintDealInfo(dealInfo);
        }
    }

    await NotifyFoundDealToUsersAsync(page, notificationUsers, best, dealInfo);
}

async Task ExecuteDealActionFlowAsync(IPage page, P2POffer best)
{
    Console.WriteLine("Готовлю действия по сделке: 'Купить' и финальная кнопка...");

    Console.WriteLine("Жду 1 сек перед нажатием 'Купить'...");
    await WaitWithStopAsync(page, 1000);
    if (stopAllRequested) return;

    var buyClicked = await ClickDealActionButtonWithRetryAsync(page, "Купить");
    if (!buyClicked)
    {
        Console.WriteLine("Кнопка 'Купить' не найдена на экране сделки.");
        return;
    }

    var isRangeOffer = best.VolumeMin is not null &&
                       best.VolumeMax is not null &&
                       Math.Abs(best.VolumeMax.Value - best.VolumeMin.Value) > 0.001;

    var finalButton = isRangeOffer ? "Макс." : "Создать сделку";

    Console.WriteLine($"Жду 1 сек перед нажатием '{finalButton}'...");
    await WaitWithStopAsync(page, 1000);
    if (stopAllRequested) return;

    var finalClicked = await ClickDealActionButtonWithRetryAsync(page, finalButton);
    if (!finalClicked)
    {
        Console.WriteLine($"Кнопка '{finalButton}' не найдена.");
        return;
    }

    if (isRangeOffer)
    {
        Console.WriteLine("Жду 1 сек перед нажатием 'Создать сделку'...");
        await WaitWithStopAsync(page, 1000);
        if (stopAllRequested) return;

        var createDealClicked = await ClickDealActionButtonWithRetryAsync(page, "Создать сделку");
        if (!createDealClicked)
        {
            Console.WriteLine("Кнопка 'Создать сделку' не найдена после нажатия 'Макс.'.");
        }
    }
}

async Task<bool> ClickDealActionButtonWithRetryAsync(IPage page, string buttonText)
{
    for (var attempt = 1; attempt <= 4; attempt++)
    {
        if (stopAllRequested)
        {
            return false;
        }

        var clicked = await ClickVisibleButtonByTextAsync(page, buttonText, startsWith: true, preferExact: true);
        if (clicked)
        {
            return true;
        }

        await WaitWithStopAsync(page, 600);
    }

    return false;
}

async Task<P2POffer?> FindBestOfferWithPagingAsync(IPage page, double targetPriceRub, VolumeFilter volumeFilter, IReadOnlyList<string> notificationUsers)
{
    var attempt = 0;
    var lastNoDealNotifyAt = DateTimeOffset.UtcNow;

    while (true)
    {
        attempt++;

        if (CheckAndMarkStopSignal())
        {
            Console.WriteLine("Поиск остановлен клавишей S.");
            return null;
        }

        var menu = await CollectMenuDataAsync(page);
        var offers = ParseOffers(menu);

        Console.WriteLine($"Скан страницы #{attempt}");
        PrintOffers(offers);

        var eligible = offers
            .Where(x => x.Price <= targetPriceRub)
            .Where(x => IsOfferVolumeSuitable(x, volumeFilter))
            .OrderBy(x => x.Price)
            .ToList();
        if (eligible.Count > 0)
        {
            var best = eligible.First();
            Console.WriteLine($"Найдено подходящее предложение: {best.RawPrice} ({best.Seller})");
            return best;
        }

        if (DateTimeOffset.UtcNow - lastNoDealNotifyAt >= TimeSpan.FromMinutes(5))
        {
            await SendNoDealsNotificationAsync(page, notificationUsers);
            if (stopAllRequested)
            {
                Console.WriteLine("Поиск остановлен клавишей S.");
                return null;
            }

            lastNoDealNotifyAt = DateTimeOffset.UtcNow;
        }

        Console.WriteLine("Подходящих объявлений нет. Жду 4 сек перед нажатием кнопки '· 1 ·'...");
        await WaitWithStopAsync(page, 4000);
        if (stopAllRequested)
        {
            Console.WriteLine("Поиск остановлен клавишей S.");
            return null;
        }

        var pagerClicked = await ClickVisibleButtonByTextAsync(page, "· 1 ·", startsWith: false, containsOnly: true);
        if (!pagerClicked)
        {
            Console.WriteLine("Кнопка '· 1 ·' не найдена. Останавливаю поиск.");
            return null;
        }

        await WaitWithStopAsync(page, 4000);
        if (stopAllRequested)
        {
            Console.WriteLine("Поиск остановлен клавишей S.");
            return null;
        }
    }
}

async Task SendNoDealsNotificationAsync(IPage page, IReadOnlyList<string> users)
{
    Console.WriteLine($"5 минут без сделок. Отправляю уведомление пользователям: {string.Join(", ", users)}...");

    foreach (var user in users)
    {
        await WaitWithStopAsync(page, 2000);
        if (stopAllRequested) return;

        var openedUser = await ClickChatByTitleAsync(page, user);
        if (!openedUser)
        {
            Console.WriteLine($"Чат {user} не найден в закрепленных.");
            continue;
        }

        await WaitWithStopAsync(page, 2000);
        if (stopAllRequested) return;

        var sent = await SendMessageToCurrentChatAsync(page, "Пока сделок нет. Продолжаю поиски..");
        if (!sent)
        {
            Console.WriteLine($"Не удалось отправить уведомление в {user}.");
        }
    }

    await WaitWithStopAsync(page, 2000);
    if (stopAllRequested) return;

    var backToBot = await ClickChatByTitleAsync(page, "Crypto");
    if (!backToBot)
    {
        Console.WriteLine("Не удалось вернуться в чат CryptoBot.");
        return;
    }

    await WaitWithStopAsync(page, 2000);
}

async Task NotifyFoundDealToUsersAsync(IPage page, IReadOnlyList<string> users, P2POffer best, DealInfo dealInfo)
{
    Console.WriteLine($"Отправляю найденное объявление пользователям: {string.Join(", ", users)}...");

    var fullDealText = TryBuildFullDealNotificationText(best, dealInfo);
    if (fullDealText is null)
    {
        Console.WriteLine("Полный текст сделки не собран. Уведомление НЕ отправлено, чтобы не слать неполные данные.");
        return;
    }

    Console.WriteLine("Полный текст сделки сформирован. Отправляю одним сообщением.");

    foreach (var user in users)
    {
        await WaitWithStopAsync(page, 2000);
        if (stopAllRequested) return;

        var openedUser = await ClickChatByTitleAsync(page, user);
        if (!openedUser)
        {
            Console.WriteLine($"Чат {user} не найден в закрепленных.");
            continue;
        }

        await WaitWithStopAsync(page, 2000);
        if (stopAllRequested) return;

        var sent = await SendMessageToCurrentChatAsync(page, fullDealText);
        if (!sent)
        {
            Console.WriteLine($"Не удалось отправить сообщение о найденной сделке в {user}.");
        }
    }

    await WaitWithStopAsync(page, 2000);
    if (stopAllRequested) return;

    var backToBot = await ClickChatByTitleAsync(page, "Crypto");
    if (!backToBot)
    {
        Console.WriteLine("Не удалось вернуться в чат CryptoBot после уведомления.");
        return;
    }

    await WaitWithStopAsync(page, 2000);
}

IReadOnlyList<string> ReadNotificationUsers()
{
    Console.Write("Введите ники получателей уведомлений через запятую (Enter = VO8R, gg): ");
    var input = Console.ReadLine();
    var users = (input ?? string.Empty)
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (users.Count == 0)
    {
        users = ["VO8R", "gg"];
    }

    return users;
}

VolumeFilter ReadVolumeFilterRub()
{
    var max = ReadPositiveDouble("Введите МАКСИМАЛЬНЫЙ объем сделки в RUB: ");

    while (true)
    {
        Console.Write("Введите МИНИМАЛЬНЫЙ объем в RUB (или Enter чтобы пропустить): ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            return new VolumeFilter { MinRub = null, MaxRub = max };
        }

        if (double.TryParse(input.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var min) && min > 0 && min <= max)
        {
            return new VolumeFilter { MinRub = min, MaxRub = max };
        }

        Console.WriteLine("Некорректный минимум. Он должен быть > 0 и <= максимума.");
    }
}

async Task<double> ReadTargetPriceRubAsync(
    Func<(double? Price, DateTimeOffset At)> getCached,
    Action<(double Price, DateTimeOffset At)> setCached)
{
    while (true)
    {
        Console.Write("Режим цены: 1 - рыночная, 2 - своя цена: ");
        var mode = Console.ReadLine()?.Trim();

        if (mode == "1")
        {
            return await ResolveMarketPriceAsync(getCached, setCached);
        }

        if (mode == "2")
        {
            return ReadPositiveDouble("Введите свою целевую цену USDT/RUB: ");
        }

        Console.WriteLine("Выберите 1 или 2.");
    }
}

double ReadPositiveDouble(string prompt)
{
    while (true)
    {
        Console.Write(prompt);
        var input = Console.ReadLine();
        if (double.TryParse((input ?? string.Empty).Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            return value;
        }

        Console.WriteLine("Некорректное число, попробуйте снова.");
    }
}

static bool IsOfferVolumeSuitable(P2POffer offer, VolumeFilter filter)
{
    if (offer.VolumeMin is null)
    {
        return true;
    }

    var min = offer.VolumeMin.Value;
    var max = offer.VolumeMax ?? offer.VolumeMin.Value;

    if (min > filter.MaxRub)
    {
        return false;
    }

    if (filter.MinRub is null)
    {
        return true;
    }

    return max >= filter.MinRub.Value;
}

bool CheckAndMarkStopSignal()
{
    if (stopAllRequested)
    {
        return true;
    }

    while (Console.KeyAvailable)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.S)
        {
            stopAllRequested = true;
            return true;
        }
    }

    return false;
}

async Task WaitWithStopAsync(IPage page, int totalMs)
{
    var remaining = totalMs;
    while (remaining > 0)
    {
        if (CheckAndMarkStopSignal())
        {
            break;
        }

        var step = Math.Min(250, remaining);
        await page.WaitForTimeoutAsync(step);
        remaining -= step;
    }
}

static async Task<double> ResolveMarketPriceAsync(
    Func<(double? Price, DateTimeOffset At)> getCached,
    Action<(double Price, DateTimeOffset At)> setCached)
{
    var cached = getCached();
    if (cached.Price is not null && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(10))
    {
        Console.WriteLine($"Использую кэш рыночной цены ({cached.At:HH:mm:ss}): {cached.Price.Value.ToString(CultureInfo.InvariantCulture)}");
        return cached.Price.Value;
    }

    var market = await TryGetMarketPriceRubAsync();
    if (market is not null)
    {
        setCached((market.Value, DateTimeOffset.UtcNow));
        Console.WriteLine($"Обновил рыночную цену из интернета: {market.Value.ToString(CultureInfo.InvariantCulture)}");
        return market.Value;
    }

    while (true)
    {
        Console.Write("Не удалось получить цену с сайтов. Введите рыночную цену USDT/RUB вручную: ");
        var input = Console.ReadLine();
        if (double.TryParse((input ?? string.Empty).Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var manual) && manual > 0)
        {
            setCached((manual, DateTimeOffset.UtcNow));
            return manual;
        }

        Console.WriteLine("Некорректная цена, попробуйте снова.");
    }
}

static async Task<double?> TryGetMarketPriceRubAsync()
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");

    // 1) CoinGecko API (основной источник)
    try
    {
        var json = await http.GetStringAsync("https://api.coingecko.com/api/v3/simple/price?ids=tether&vs_currencies=rub");
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("tether", out var tether) &&
            tether.TryGetProperty("rub", out var rub) &&
            rub.ValueKind == JsonValueKind.Number)
        {
            var value = rub.GetDouble();
            if (value is > 10 and < 200)
            {
                return value;
            }
        }
    }
    catch
    {
        // next source
    }

    // 2) CoinMarketCap page fallback
    try
    {
        var html = await http.GetStringAsync("https://coinmarketcap.com/currencies/tether/usdt/rub/");
        var patterns = new[]
        {
            @"price today is[^0-9]{0,40}([0-9]+(?:[\.,][0-9]+)?)",
            "price\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)"
        };

        foreach (var pattern in patterns)
        {
            var matches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase);
            foreach (Match m in matches)
            {
                if (m.Groups.Count < 2) continue;
                var raw = m.Groups[1].Value.Replace(',', '.');
                if (!double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)) continue;
                if (value is > 10 and < 200) return value;
            }
        }
    }
    catch
    {
        // fallback to manual
    }

    return null;
}

static List<P2POffer> ParseOffers(JsonElement menu)
{
    var result = new List<P2POffer>();
    if (!menu.TryGetProperty("visibleButtons", out var buttons) || buttons.ValueKind != JsonValueKind.Array)
    {
        return result;
    }

    var list = buttons.EnumerateArray().ToList();
    var filtersIndex = list.FindIndex(x => GetString(x, "label").Contains("Фильтры и сортировка", StringComparison.OrdinalIgnoreCase));
    var candidates = filtersIndex >= 0 ? list.Skip(filtersIndex + 1) : list;

    foreach (var button in candidates)
    {
        var label = GetString(button, "label");
        var parts = label.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            continue;
        }

        string seller;
        string rawPrice;
        string rawVolume;

        if (parts.Length == 2)
        {
            seller = "(не указан)";
            rawPrice = parts[0];
            rawVolume = parts[1];
        }
        else
        {
            seller = parts[0];
            rawPrice = parts[1];
            rawVolume = string.Join(" · ", parts.Skip(2));
        }

        var priceValue = TryParseFlexibleNumber(rawPrice);
        if (priceValue is null)
        {
            continue;
        }

        var (volumeMin, volumeMax) = TryParseVolumeRange(rawVolume);

        result.Add(new P2POffer
        {
            DisplayIndex = GetInt(button, "index"),
            Seller = seller,
            Price = priceValue.Value,
            RawPrice = rawPrice,
            Volume = rawVolume,
            VolumeMin = volumeMin,
            VolumeMax = volumeMax,
            SourceLabel = label
        });
    }

    return result;
}

static void PrintOffers(List<P2POffer> offers)
{
    if (offers.Count == 0)
    {
        Console.WriteLine("Подходящие P2P офферы не распознаны на текущем экране.");
        return;
    }

    Console.WriteLine("=== ОБЪЯВЛЕНИЯ P2P (структурировано) ===");
    foreach (var offer in offers.OrderBy(x => x.Price))
    {
        Console.WriteLine($"[{offer.DisplayIndex}] Продавец: {offer.Seller}");
        Console.WriteLine($"    Цена: {offer.RawPrice} (число: {offer.Price.ToString(CultureInfo.InvariantCulture)})");
        Console.WriteLine($"    Объем: {offer.Volume}");
    }

    Console.WriteLine("=== КОНЕЦ СПИСКА ===");
}

static void PrintDealInfo(DealInfo info)
{
    Console.WriteLine();
    Console.WriteLine("=== ИНФО ПО СДЕЛКЕ ===");
    Console.WriteLine(string.IsNullOrWhiteSpace(info.MessageText) ? "Сообщение сделки не найдено." : FormatDealMessage(info.MessageText));
    Console.WriteLine($"Кнопка действия: {info.ActionButtonLabel}");
    Console.WriteLine("=== КОНЕЦ ИНФО ===");
}

static async Task<DealInfo> ExtractDealInfoWithRescansAsync(IPage page, int maxAttempts)
{
    DealInfo? best = null;

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        var current = await ExtractDealInfoAsync(page);

        var currentScore = ScoreDealMessage(current.MessageText);
        var bestScore = ScoreDealMessage(best?.MessageText ?? string.Empty);
        if (best is null || currentScore > bestScore)
        {
            best = current;
        }

        if (currentScore >= 4)
        {
            return current;
        }

        await page.WaitForTimeoutAsync(1200);
    }

    return best ?? new DealInfo { MessageText = string.Empty, ActionButtonLabel = "(не удалось извлечь)" };
}

static int ScoreDealMessage(string message)
{
    if (string.IsNullOrWhiteSpace(message))
    {
        return 0;
    }

    var score = 0;
    if (message.Contains("Объявление", StringComparison.OrdinalIgnoreCase) || message.Contains("Сделка #", StringComparison.OrdinalIgnoreCase)) score++;
    if (message.Contains("Цена за 1 USDT", StringComparison.OrdinalIgnoreCase) || message.Contains("Покупаете", StringComparison.OrdinalIgnoreCase)) score++;
    if (message.Contains("Доступный объём", StringComparison.OrdinalIgnoreCase)) score++;
    if (message.Contains("Способ оплаты", StringComparison.OrdinalIgnoreCase)) score++;
    if (message.Contains("Условия сделки", StringComparison.OrdinalIgnoreCase)) score++;

    return score;
}

static async Task<DealInfo> ExtractDealInfoAsync(IPage page)
{
    var data = await page.EvaluateAsync<DealInfo?>("""
() => {
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();
  const blockText = (el) => (el?.innerText || el?.textContent || '').replace(/\r/g, '').trim();
  const messages = Array.from(document.querySelectorAll('.bubble, .message'));

  const candidates = [];
  for (let i = messages.length - 1; i >= 0; i--) {
    const t = blockText(messages[i].querySelector('.bubble-content-wrapper')) || blockText(messages[i]);
    if (t.toLowerCase().includes('объявление') || t.toLowerCase().includes('сделка #')) {
      candidates.push(t);
    }
  }

  const score = (t) => {
    let s = t.length;
    if (t.toLowerCase().includes('цена за 1 usdt') || t.toLowerCase().includes('покупаете')) s += 1000;
    if (t.toLowerCase().includes('доступный объём')) s += 1000;
    if (t.toLowerCase().includes('способ оплаты')) s += 1000;
    if (t.toLowerCase().includes('условия сделки')) s += 1000;
    return s;
  };

  const fallback = blockText(messages[messages.length - 1]?.querySelector('.bubble-content-wrapper')) || blockText(messages[messages.length - 1]);
  const dealMessage = candidates.sort((a, b) => score(b) - score(a))[0] || fallback || '';

  const buyButton = Array.from(document.querySelectorAll('button, [role="button"], .reply-markup-button, .Button'))
    .find((btn) => text(btn).toLowerCase().startsWith('купить'));

  return {
    MessageText: dealMessage,
    ActionButtonLabel: buyButton ? text(buyButton) : '(кнопка Купить* не найдена)'
  };
}
""");

    return data ?? new DealInfo { MessageText = string.Empty, ActionButtonLabel = "(не удалось извлечь)" };
}

static string? TryBuildFullDealNotificationText(P2POffer best, DealInfo dealInfo)
{
    var raw = dealInfo.MessageText?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(raw))
    {
        return null;
    }

    var hasStart = raw.Contains("Объявление", StringComparison.OrdinalIgnoreCase) || raw.Contains("Сделка #", StringComparison.OrdinalIgnoreCase);
    var hasCoreFields = raw.Contains("Цена за 1 USDT", StringComparison.OrdinalIgnoreCase)
                        || raw.Contains("Доступный объём", StringComparison.OrdinalIgnoreCase)
                        || raw.Contains("Способ оплаты", StringComparison.OrdinalIgnoreCase)
                        || raw.Contains("Покупаете", StringComparison.OrdinalIgnoreCase)
                        || raw.Contains("Платите", StringComparison.OrdinalIgnoreCase);

    if (!hasStart || !hasCoreFields)
    {
        return null;
    }

    var formatted = FormatDealMessage(raw);
    if (string.IsNullOrWhiteSpace(formatted))
    {
        return null;
    }

    return $"Найдена сделка:\n[{best.DisplayIndex}] {best.SourceLabel}\n\n{formatted}";
}

static string FormatDealMessage(string message)
{
    if (string.IsNullOrWhiteSpace(message))
    {
        return string.Empty;
    }

    var formatted = message.Replace("\r", string.Empty).Trim();
    formatted = formatted.Replace("Цена за 1 USDT", "\nЦена за 1 USDT");
    formatted = formatted.Replace("Доступный объём", "\nДоступный объём");
    formatted = formatted.Replace("Способ оплаты", "\nСпособ оплаты");
    formatted = formatted.Replace("Условия сделки", "\n\nУсловия сделки");

    while (formatted.Contains("\n\n\n", StringComparison.Ordinal))
    {
        formatted = formatted.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
    }

    return formatted;
}

static double? TryParseFlexibleNumber(string source)
{
    if (string.IsNullOrWhiteSpace(source)) return null;

    var hasK = source.Contains('K', StringComparison.OrdinalIgnoreCase);
    var filtered = new string(source.Where(ch => char.IsDigit(ch) || ch == '.' || ch == ',').ToArray());
    if (string.IsNullOrWhiteSpace(filtered)) return null;

    filtered = filtered.Replace(',', '.');
    if (!double.TryParse(filtered, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)) return null;

    return hasK ? value * 1000d : value;
}

static (double? Min, double? Max) TryParseVolumeRange(string rawVolume)
{
    if (string.IsNullOrWhiteSpace(rawVolume)) return (null, null);

    var parts = rawVolume.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 1)
    {
        var single = TryParseFlexibleNumber(parts[0]);
        return (single, single);
    }

    return (TryParseFlexibleNumber(parts[0]), TryParseFlexibleNumber(parts[1]));
}

static async Task<bool> ClickChatByTitleAsync(IPage page, string titlePart)
{
    var target = await page.EvaluateAsync<ClickTarget?>("""
(titlePart) => {
  const normalize = (s) => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const wanted = normalize(titlePart);
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();

  const items = Array.from(document.querySelectorAll('.chatlist-chat, .chat-item, [data-peer-id], .ListItem'));
  const hit = items.find((item) => normalize(text(item)).includes(wanted));
  if (!hit) return null;

  const rect = hit.getBoundingClientRect();
  return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2, label: text(hit) };
}
""", titlePart);

    if (target is null) return false;

    await page.Mouse.MoveAsync((float)target.X, (float)target.Y);
    await page.Mouse.DownAsync();
    await page.Mouse.UpAsync();
    return true;
}

static async Task<bool> SendMessageToCurrentChatAsync(IPage page, string message)
{
    var focused = await page.EvaluateAsync<bool>("""
() => {
  const input = document.querySelector('div[contenteditable="true"], .input-message-input, .composer_rich_textarea');
  if (!input) return false;
  input.focus();
  return true;
}
""");

    if (!focused) return false;

    // Важно: InsertText вставляет весь текст сразу (включая переносы строк)
    // без по-символьных Enter, чтобы не отправлять сообщения частями.
    await page.Keyboard.InsertTextAsync(message);
    await page.WaitForTimeoutAsync(200);
    await page.Keyboard.PressAsync("Enter");
    return true;
}

static async Task<bool> ClickVisibleButtonByTextAsync(IPage page, string expectedText, bool startsWith, bool containsOnly = false, bool preferExact = false)
{
    var target = await page.EvaluateAsync<ClickTarget?>("""
(args) => {
  const normalize = (s) => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();

  const allVisibleButtons = Array.from(document.querySelectorAll('button, [role="button"], .reply-markup-button, .Button'))
    .filter((btn) => {
      const rect = btn.getBoundingClientRect();
      const style = getComputedStyle(btn);
      return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
    });

  const expected = normalize(args.expectedText);
  const matches = allVisibleButtons.filter((btn) => {
    const t = normalize(text(btn));
    if (args.preferExact && t === expected) return true;
    if (args.containsOnly) return t.includes(expected);
    if (args.startsWith) return t.startsWith(expected);
    return t.includes(expected);
  });

  if (!matches.length) return null;

  const scored = matches
    .map((btn) => {
      const t = normalize(text(btn));
      const rect = btn.getBoundingClientRect();
      let score = 0;
      if (t === expected) score += 1000;
      if (t.startsWith(expected)) score += 500;
      if (rect.top > window.innerHeight * 0.45) score += 200;
      if (rect.width > 60 && rect.height > 20) score += 50;
      return { btn, score, rect };
    })
    .sort((a, b) => b.score - a.score);

  const hit = scored[0].btn;
  const rect = hit.getBoundingClientRect();
  return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2, label: text(hit) };
}
""", new { expectedText, startsWith, containsOnly, preferExact });

    if (target is null) return false;

    await page.Mouse.MoveAsync((float)target.X, (float)target.Y);
    await page.Mouse.DownAsync();
    await page.Mouse.UpAsync();
    return true;
}

static async Task<bool> ClickVisibleButtonByIndexAsync(IPage page, int displayIndex, bool isAutomation = false)
{
    var target = await page.EvaluateAsync<ClickTarget?>("""
(displayIndex) => {
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();
  const allVisibleButtons = Array.from(document.querySelectorAll('button, [role="button"], .reply-markup-button, .Button'))
    .map((btn) => ({ btn }))
    .filter(({ btn }) => {
      const rect = btn.getBoundingClientRect();
      const style = getComputedStyle(btn);
      const visible = rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
      if (!visible) return false;
      const t = text(btn);
      return t.length > 0 || (btn.getAttribute('aria-label') || '').trim().length > 0;
    })
    .sort((a, b) => {
      const ar = a.btn.getBoundingClientRect();
      const br = b.btn.getBoundingClientRect();
      if (Math.abs(ar.top - br.top) > 6) return ar.top - br.top;
      return ar.left - br.left;
    });

  const last30 = allVisibleButtons.slice(-30);
  const chosen = last30[displayIndex];
  if (!chosen) return null;

  const rect = chosen.btn.getBoundingClientRect();
  return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2, label: text(chosen.btn) };
}
""", displayIndex);

    if (target is null) return false;

    await page.Mouse.MoveAsync((float)target.X, (float)target.Y);
    await page.Mouse.DownAsync();
    await page.Mouse.UpAsync();
    Console.WriteLine($"Нажата кнопка [{displayIndex}] '{target.Label}'.");

    if (isAutomation)
    {
        await page.WaitForTimeoutAsync(2000);
    }

    return true;
}

static async Task<JsonElement> CollectMenuDataAsync(IPage page)
{
    return await page.EvaluateAsync<JsonElement>("""
() => {
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();
  const blockText = (el) => (el?.innerText || el?.textContent || '').replace(/\r/g, '').trim();

  const bubbles = Array.from(document.querySelectorAll('.bubble, .message'));
  const lastBubble = bubbles.at(-1) || null;

  const allVisibleButtons = Array.from(document.querySelectorAll('button, [role="button"], .reply-markup-button, .Button'))
    .map((btn, domIndex) => ({ btn, domIndex }))
    .filter(({ btn }) => {
      const rect = btn.getBoundingClientRect();
      const style = getComputedStyle(btn);
      const visible = rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
      if (!visible) return false;
      const t = text(btn);
      return t.length > 0 || (btn.getAttribute('aria-label') || '').trim().length > 0;
    })
    .sort((a, b) => {
      const ar = a.btn.getBoundingClientRect();
      const br = b.btn.getBoundingClientRect();
      if (Math.abs(ar.top - br.top) > 6) return ar.top - br.top;
      return ar.left - br.left;
    });

  const last30 = allVisibleButtons.slice(-30);
  const visibleButtons = last30.map(({ btn, domIndex }, displayIndex) => ({
    index: displayIndex,
    domIndex,
    label: text(btn),
    ariaLabel: btn.getAttribute('aria-label') || ''
  }));

  const messageAboveButtons = blockText(lastBubble?.querySelector('.bubble-content-wrapper')) || blockText(lastBubble);
  const messageTime = text(lastBubble?.querySelector('time, .time, .message-time'));
  const activeChatTitle = text(document.querySelector('.chat-info .title, .chat-info-wrapper .title, .topbar .title, header .title'));

  return {
    extractedAt: new Date().toISOString(),
    activeChatTitle,
    messageAboveButtons,
    messageTime,
    visibleButtonCount: visibleButtons.length,
    visibleButtons
  };
}
""");
}

static void PrintMenuToConsole(JsonElement result)
{
    Console.WriteLine();
    Console.WriteLine("=== ТЕКУЩЕЕ МЕНЮ CRYPTOBOT ===");
    Console.WriteLine($"Чат: {GetString(result, "activeChatTitle")}");
    Console.WriteLine($"Время скана: {GetString(result, "extractedAt")}");
    Console.WriteLine($"Текст над кнопками: {GetString(result, "messageAboveButtons")}");
    Console.WriteLine($"Время сообщения: {GetString(result, "messageTime")}");
    Console.WriteLine($"Показано кнопок (последние): {GetInt(result, "visibleButtonCount")}");

    if (result.TryGetProperty("visibleButtons", out var buttons) && buttons.ValueKind == JsonValueKind.Array)
    {
        foreach (var button in buttons.EnumerateArray())
        {
            Console.WriteLine($"  [{GetInt(button, "index")}] label='{GetString(button, "label")}', aria='{GetString(button, "ariaLabel")}'");
        }
    }

    Console.WriteLine("=== КОНЕЦ МЕНЮ ===");
}

static int GetInt(JsonElement source, string propertyName)
{
    return source.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
        ? value.GetInt32()
        : -1;
}

static string GetString(JsonElement source, string propertyName)
{
    return source.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? string.Empty
        : string.Empty;
}

static string ResolveYandexBrowserPath()
{
    var fromEnv = Environment.GetEnvironmentVariable("YANDEX_BROWSER_PATH");
    if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
    {
        return fromEnv;
    }

    if (OperatingSystem.IsWindows())
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yandex", "YandexBrowser", "Application", "browser.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Yandex", "YandexBrowser", "Application", "browser.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Yandex", "YandexBrowser", "Application", "browser.exe")
        };

        var found = candidates.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(found))
        {
            return found;
        }
    }

    throw new FileNotFoundException("Не найден executable Яндекс Браузера. Укажите YANDEX_BROWSER_PATH.");
}

file sealed class ClickTarget
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required string Label { get; init; }
}

file sealed class P2POffer
{
    public required int DisplayIndex { get; init; }
    public required string Seller { get; init; }
    public required double Price { get; init; }
    public required string RawPrice { get; init; }
    public required string Volume { get; init; }
    public double? VolumeMin { get; init; }
    public double? VolumeMax { get; init; }
    public required string SourceLabel { get; init; }
}

file sealed class VolumeFilter
{
    public double? MinRub { get; init; }
    public required double MaxRub { get; init; }
}

file sealed class DealInfo
{
    public required string MessageText { get; init; }
    public required string ActionButtonLabel { get; init; }
}
