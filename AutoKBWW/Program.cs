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

        if (string.Equals(input, "W", StringComparison.OrdinalIgnoreCase) || string.Equals(input, "WATCH", StringComparison.OrdinalIgnoreCase))
        {
            await RunSalesDealsWatcherAsync(page);
            PrintCommandsHint();
            continue;
        }

        if (string.Equals(input, "T", StringComparison.OrdinalIgnoreCase) || string.Equals(input, "TEST", StringComparison.OrdinalIgnoreCase))
        {
            await RunSalesDealsTestAutomationAsync(page);
            PrintCommandsHint();
            continue;
        }

        if (!int.TryParse(input, out var displayIndex))
        {
            Console.WriteLine("Некорректный ввод. Укажите индекс, A, W, T, R, S или Q.");
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
    Console.WriteLine("Команды: индекс кнопки (0..), A - автосценарий P2P, W - мониторинг новых сделок продажи, T - тестовая авто-ветка продажи, R - перескан, S - стоп автоматики, Q - выход.");
}

async Task RunSalesDealsWatcherAsync(IPage page)
{
    stopAllRequested = false;

    var scanAccountName = ReadSalesScanAccountName();
    var notificationUsers = ReadNotificationUsers();
    Console.WriteLine($"Мониторинг продаж (аккаунт: {scanAccountName}): уведомления будут отправляться: {string.Join(", ", notificationUsers)}");
    Console.WriteLine("Запускаю мониторинг. Ищу новые сообщения вида '💡 Создана новая сделка ...'. Для остановки нажмите S.");

    var openedCrypto = await ClickChatByTitleAsync(page, "Crypto");
    if (!openedCrypto)
    {
        Console.WriteLine("Не удалось открыть чат Crypto для старта мониторинга продаж.");
        return;
    }

    await WaitWithStopAsync(page, 1000);
    if (stopAllRequested) return;

    string? lastNotifiedDealId = null;

    while (!stopAllRequested)
    {
        var latestMessage = await ExtractLatestMessageTextAsync(page);
        if (TryParseCreatedSaleDeal(latestMessage, out var saleDeal)
            && !string.Equals(lastNotifiedDealId, saleDeal.DealId, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Найдена новая сделка продажи: #{saleDeal.DealId}, {saleDeal.AmountRub} RUB, банк: {saleDeal.Bank}.");
            var vo8rOpened = await NotifyCreatedSaleDealAsync(page, notificationUsers, saleDeal, scanAccountName);
            if (stopAllRequested) return;

            lastNotifiedDealId = saleDeal.DealId;

            if (vo8rOpened)
            {
                await WaitForVo8rReactionDebugAsync(page, saleDeal);
                return;
            }

            Console.WriteLine("Чат VO8R не найден: не удалось перейти к режиму ожидания реакции.");
            return;
        }

        await WaitWithStopAsync(page, 1000);
    }

    Console.WriteLine("Мониторинг новых сделок продажи остановлен.");
}

async Task RunSalesDealsTestAutomationAsync(IPage page)
{
    stopAllRequested = false;

    var scanAccountName = ReadSalesScanAccountName();
    var actionKeywordPrefixes = ReadSalesActionKeywordPrefixes();
    var confirmPassword = ReadSalesConfirmPassword();
    var notificationUsers = ReadNotificationUsers();

    Console.WriteLine($"ТЕСТ-ПРОДАЖИ (аккаунт: {scanAccountName}). Ключевые слова кнопки: {string.Join(", ", actionKeywordPrefixes)}.");

    var openedCrypto = await ClickChatByTitleAsync(page, "Crypto");
    if (!openedCrypto)
    {
        Console.WriteLine("Не удалось открыть чат Crypto для тестовой ветки продаж.");
        return;
    }

    await WaitWithStopAsync(page, 800);
    if (stopAllRequested) return;

    string? lastProcessedDealId = null;

    while (!stopAllRequested)
    {
        var latestMessage = await ExtractLatestMessageTextAsync(page);
        if (!TryParseCreatedSaleDeal(latestMessage, out var saleDeal)
            || string.Equals(lastProcessedDealId, saleDeal.DealId, StringComparison.OrdinalIgnoreCase))
        {
            await WaitWithStopAsync(page, 1000);
            continue;
        }

        lastProcessedDealId = saleDeal.DealId;
        Console.WriteLine($"[TEST] Найдена новая сделка #{saleDeal.DealId}. Запускаю тестовую авто-цепочку...");

        await NotifyUsersWithTextAsync(page, notificationUsers,
            $"[{scanAccountName}] [TEST] Обнаружена сделка #{saleDeal.DealId} на {saleDeal.AmountRub} RUB через {saleDeal.Bank}. Запускаю тестовую цепочку кнопок.");
        if (stopAllRequested) return;

        var stepsOk = await ExecuteSalesDealTestSequenceAsync(page, actionKeywordPrefixes, confirmPassword);
        if (!stepsOk)
        {
            Console.WriteLine("[TEST] Цепочка завершилась с ошибкой или неполными шагами.");
        }
        else
        {
            Console.WriteLine("[TEST] Цепочка выполнена.");
        }

        return;
    }
}

async Task<bool> ExecuteSalesDealTestSequenceAsync(IPage page, IReadOnlyList<string> actionKeywordPrefixes, string confirmPassword)
{
    if (!await ClickVisibleButtonByTextAsync(page, "Посмотреть сделку", startsWith: true, preferExact: false))
    {
        Console.WriteLine("[TEST] Не удалось нажать 'Посмотреть сделку'.");
        return false;
    }

    await WaitWithStopAsync(page, 700);
    if (stopAllRequested) return false;

    if (!await ClickVisibleButtonByTextAsync(page, "Принять сделку", startsWith: true, preferExact: false))
    {
        Console.WriteLine("[TEST] Не удалось нажать 'Принять сделку'.");
        return false;
    }

    await WaitWithStopAsync(page, 700);
    if (stopAllRequested) return false;

    var keywordClicked = false;
    foreach (var prefix in actionKeywordPrefixes)
    {
        if (await ClickVisibleButtonByTextAsync(page, prefix, startsWith: true, preferExact: false))
        {
            Console.WriteLine($"[TEST] Нажата кнопка по ключевому слову: {prefix}");
            keywordClicked = true;
            break;
        }
    }

    if (!keywordClicked)
    {
        Console.WriteLine("[TEST] Не удалось нажать кнопку по ключевым словам.");
        return false;
    }

    await WaitWithStopAsync(page, 700);
    if (stopAllRequested) return false;

    for (var i = 1; i <= 2; i++)
    {
        if (!await ClickVisibleButtonByTextAsync(page, "Продолжить", startsWith: true, preferExact: false))
        {
            Console.WriteLine($"[TEST] Не удалось нажать 'Продолжить' (итерация {i}/2).");
            return false;
        }

        await WaitWithStopAsync(page, 700);
        if (stopAllRequested) return false;
    }

    if (!await ClickAnyDynamicActionButtonAsync(page))
    {
        Console.WriteLine("[TEST] Не удалось нажать динамическую кнопку после двух 'Продолжить'.");
        return false;
    }

    await WaitWithStopAsync(page, 700);
    if (stopAllRequested) return false;

    if (!await ClickVisibleButtonByTextAsync(page, "Продолжить", startsWith: true, preferExact: false))
    {
        Console.WriteLine("[TEST] Не удалось нажать 'Продолжить' после динамической кнопки.");
        return false;
    }

    await WaitWithStopAsync(page, 1000);
    if (stopAllRequested) return false;

    if (!await ClickVisibleButtonByTextAsync(page, "Да", startsWith: true, preferExact: false))
    {
        Console.WriteLine("[TEST] Не удалось нажать 'Да' в новом сообщении.");
        return false;
    }

    await WaitWithStopAsync(page, 700);
    if (stopAllRequested) return false;

    var sentPassword = await SendMessageToCurrentChatAsync(page, confirmPassword);
    if (!sentPassword)
    {
        Console.WriteLine("[TEST] Не удалось отправить пароль подтверждения.");
        return false;
    }

    Console.WriteLine("[TEST] Пароль подтверждения отправлен.");
    return true;
}

async Task<bool> ClickAnyDynamicActionButtonAsync(IPage page)
{
    var labels = await CollectVisibleButtonsWithTimeoutAsync(page, 1200);
    if (labels.Count == 0)
    {
        return false;
    }

    var ignored = new[]
    {
        "продолж",
        "посмотр",
        "принять",
        "да",
        "нет",
        "назад",
        "отмен",
        "ответить"
    };

    var candidate = labels
        .Select(x => (Original: x.Label, Normalized: x.Label.Trim()))
        .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Normalized)
                             && x.Normalized != "?"
                             && !ignored.Any(prefix => x.Normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    if (string.IsNullOrWhiteSpace(candidate.Normalized))
    {
        return false;
    }

    Console.WriteLine($"[TEST] Пытаюсь нажать динамическую кнопку: '{candidate.Original}'.");
    return await ClickVisibleButtonByTextAsync(page, candidate.Original, startsWith: true, preferExact: false)
           || await ClickVisibleButtonByTextAsync(page, candidate.Original, startsWith: false, containsOnly: true, preferExact: false);
}

static async Task<string> ExtractLatestMessageTextAsync(IPage page)
{
    var text = await page.EvaluateAsync<string>("""
() => {
  const blockText = (el) => (el?.innerText || el?.textContent || '').replace(/\r/g, '').trim();
  const messages = Array.from(document.querySelectorAll('.bubble, .message'));
  const last = messages.at(-1);
  if (!last) return '';

  return blockText(last.querySelector('.bubble-content-wrapper')) || blockText(last);
}
""");

    return text ?? string.Empty;
}

static bool TryParseCreatedSaleDeal(string message, out SaleDealNotification deal)
{
    deal = default!;
    if (string.IsNullOrWhiteSpace(message))
    {
        return false;
    }

    var match = Regex.Match(
        message,
        @"создана\s+новая\s+сделка\s*#(?<id>[a-z0-9]+).*?за\s*(?:🪙\s*)?(?<amount>[0-9\s.,]+)\s*rub.*?через\s*(?<bank>[^.\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    if (!match.Success)
    {
        return false;
    }

    var dealId = match.Groups["id"].Value.Trim();
    var amount = Regex.Replace(match.Groups["amount"].Value, @"\s+", " ").Trim();
    var bank = match.Groups["bank"].Value.Trim();

    if (string.IsNullOrWhiteSpace(dealId) || string.IsNullOrWhiteSpace(amount) || string.IsNullOrWhiteSpace(bank))
    {
        return false;
    }

    deal = new SaleDealNotification
    {
        DealId = dealId,
        AmountRub = amount,
        Bank = bank
    };

    return true;
}

async Task<bool> NotifyCreatedSaleDealAsync(IPage page, IReadOnlyList<string> users, SaleDealNotification deal, string scanAccountName)
{
    var message = $"[{scanAccountName}] Создана новая сделка - #{deal.DealId} на {deal.AmountRub} RUB через {deal.Bank}";

    foreach (var user in users)
    {
        await WaitWithStopAsync(page, 1000);
        if (stopAllRequested) return false;

        var openedUser = await ClickChatByTitleAsync(page, user);
        if (!openedUser)
        {
            Console.WriteLine($"Чат {user} не найден в закрепленных.");
            continue;
        }

        await WaitWithStopAsync(page, 1000);
        if (stopAllRequested) return false;

        var sent = await SendMessageToCurrentChatAsync(page, message);
        if (!sent)
        {
            Console.WriteLine($"Не удалось отправить уведомление о новой сделке в {user}.");
        }
    }

    await WaitWithStopAsync(page, 1000);
    if (stopAllRequested) return false;

    var openedVo8r = await ClickChatByTitleAsync(page, "VO8R");
    if (!openedVo8r)
    {
        Console.WriteLine("Не удалось открыть чат VO8R после отправки уведомления о новой сделке.");
        return false;
    }

    await WaitWithStopAsync(page, 1000);
    return true;
}

async Task WaitForVo8rReactionDebugAsync(IPage page, SaleDealNotification deal)
{
    Console.WriteLine($"Перешел в чат VO8R. Жду реакции на сообщение по сделке #{deal.DealId}. Для остановки нажмите S.");
    Console.WriteLine("Если реакции нет 1 минуту — запускаю звонок, затем проверяю еще 30 сек и повторяю до появления реакции.");

    var initialWaitUntil = DateTimeOffset.UtcNow.AddMinutes(1);

    while (!stopAllRequested)
    {
        var scan = await CollectVo8rReactionScanAsync(page, deal.DealId);
        var hasReaction = LogVo8rReactionScan(scan, deal.DealId);

        if (hasReaction)
        {
            await ReturnToCryptoBotAfterReactionAsync(page, deal.DealId);
            return;
        }

        if (DateTimeOffset.UtcNow < initialWaitUntil)
        {
            await WaitWithStopAsync(page, 1000);
            continue;
        }

        Console.WriteLine("[VO8R] Реакции нет 1 минуту. Нажимаю на звонок...");
        var startedCall = await ClickVo8rCallButtonAsync(page);
        if (!startedCall)
        {
            Console.WriteLine("[VO8R] Не удалось нажать кнопку звонка. Повторяю проверку через 1 сек.");
            await WaitWithStopAsync(page, 1000);
            continue;
        }

        await WaitWithStopAsync(page, 1200);
        if (stopAllRequested) break;

        var outgoingCallSeen = await DetectOutgoingCallMessageAsync(page);
        Console.WriteLine(outgoingCallSeen
            ? "[VO8R] Обнаружено системное сообщение 'Outgoing Call'."
            : "[VO8R] Сообщение 'Outgoing Call' пока не найдено, продолжаю ожидание реакции.");

        var callWaitUntil = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!stopAllRequested && DateTimeOffset.UtcNow < callWaitUntil)
        {
            var callScan = await CollectVo8rReactionScanAsync(page, deal.DealId);
            var callHasReaction = LogVo8rReactionScan(callScan, deal.DealId);
            if (callHasReaction)
            {
                await ReturnToCryptoBotAfterReactionAsync(page, deal.DealId);
                return;
            }

            await WaitWithStopAsync(page, 1000);
        }

        Console.WriteLine("[VO8R] После звонка и 30 сек ожидания реакции нет. Звоню повторно...");
    }

    Console.WriteLine("Ожидание реакции в чате VO8R остановлено.");
}

static bool LogVo8rReactionScan(SaleDealReactionScan scan, string dealId)
{
    Console.WriteLine($"[VO8R][{DateTime.Now:HH:mm:ss}] Поиск реакции: messageFound={scan.MessageFound}, outgoing={scan.IsOutgoing}, reactionNodes={scan.ReactionNodeCount}, reactionTexts={scan.ReactionTexts.Count}");

    if (!string.IsNullOrWhiteSpace(scan.MessageTextPreview))
    {
        Console.WriteLine($"[VO8R] Сообщение: {scan.MessageTextPreview}");
    }

    var hasReaction = scan.ReactionNodeCount > 0 || scan.ReactionTexts.Count > 0;
    if (hasReaction)
    {
        Console.WriteLine($"[VO8R] ✅ Обнаружена реакция на сообщение сделки #{dealId}.");
    }

    if (scan.ReactionTexts.Count > 0)
    {
        Console.WriteLine("[VO8R] Найдены тексты/эмодзи реакций:");
        foreach (var reaction in scan.ReactionTexts)
        {
            Console.WriteLine($"  - {reaction}");
        }
    }

    if (scan.DebugNodes.Count > 0)
    {
        Console.WriteLine("[VO8R] Debug-узлы вокруг реакций:");
        foreach (var node in scan.DebugNodes)
        {
            Console.WriteLine($"  - {node}");
        }
    }

    return hasReaction;
}

async Task ReturnToCryptoBotAfterReactionAsync(IPage page, string dealId)
{
    Console.WriteLine($"[VO8R] Реакция подтверждена для сделки #{dealId}. Возвращаюсь в чат Crypto Bot...");
    await WaitWithStopAsync(page, 1000);
    if (stopAllRequested) return;

    var backToBot = await ClickChatByTitleAsync(page, "Crypto");
    if (!backToBot)
    {
        Console.WriteLine("[VO8R] Не удалось вернуться в чат Crypto Bot после обнаружения реакции.");
    }
    else
    {
        Console.WriteLine("[VO8R] Успешно вернулся в чат Crypto Bot после реакции.");
    }
}

static async Task<bool> ClickVo8rCallButtonAsync(IPage page)
{
    var clicked = await page.EvaluateAsync<bool>("""
() => {
  const candidates = Array.from(document.querySelectorAll('.chat-utils .btn-icon.rp, .chat-utils .btn-icon'));
  if (candidates.length === 0) return false;

  const isVisible = (el) => {
    if (!el) return false;
    const style = window.getComputedStyle(el);
    if (!style || style.display === 'none' || style.visibility === 'hidden') return false;
    const rect = el.getBoundingClientRect();
    return rect.width > 2 && rect.height > 2;
  };

  for (const el of candidates) {
    if (!isVisible(el)) continue;
    const rect = el.getBoundingClientRect();
    const x = rect.left + rect.width / 2;
    const y = rect.top + rect.height / 2;
    el.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, cancelable: true, clientX: x, clientY: y }));
    el.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, clientX: x, clientY: y, button: 0 }));
    el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, clientX: x, clientY: y, button: 0 }));
    el.click();
    return true;
  }

  return false;
}
""");

    return clicked;
}

static async Task<bool> DetectOutgoingCallMessageAsync(IPage page)
{
    var found = await page.EvaluateAsync<bool>("""
() => {
  const messages = Array.from(document.querySelectorAll('.bubble, .message'));
  for (let i = messages.length - 1; i >= Math.max(0, messages.length - 40); i--) {
    const raw = (messages[i]?.innerText || messages[i]?.textContent || '').toLowerCase();
    if (raw.includes('outgoing call')) {
      return true;
    }
  }

  return false;
}
""");

    return found;
}

static async Task<SaleDealReactionScan> CollectVo8rReactionScanAsync(IPage page, string dealId)
{
    var scanJson = await page.EvaluateAsync<string>("""
(dealId) => {
  const normalize = (s) => (s || '').replace(/\r/g, '').replace(/\s+/g, ' ').trim();
  const byReversed = (arr) => Array.from(arr).reverse();

  const bubbles = Array.from(document.querySelectorAll('.bubble, .message'));
  let target = null;

  for (const node of byReversed(bubbles)) {
    const raw = normalize(node.innerText || node.textContent || '');
    if (!raw) continue;
    if (!raw.includes('Создана новая сделка')) continue;
    if (dealId && !raw.toLowerCase().includes(('#' + dealId).toLowerCase())) continue;
    target = node;
    break;
  }

  if (!target) {
    return JSON.stringify({
      messageFound: false,
      isOutgoing: false,
      reactionNodeCount: 0,
      reactionTexts: [],
      messageTextPreview: '',
      debugNodes: []
    });
  }

  const messageText = normalize(target.innerText || target.textContent || '');
  const className = (target.className || '').toString().toLowerCase();
  const isOutgoing = className.includes('own') || className.includes('out') || className.includes('is-out') || className.includes('message-out');

  const reactionSelectors = [
    '[class*="reaction"]',
    '[class*="reactions"]',
    '.reactions-element',
    '[data-reaction]',
    '[aria-label*="реакц"]',
    '[aria-label*="reaction"]'
  ];

  const candidates = [];
  for (const selector of reactionSelectors) {
    for (const el of target.querySelectorAll(selector)) {
      candidates.push(el);
    }
  }

  const uniq = [];
  const seen = new Set();
  for (const el of candidates) {
    if (seen.has(el)) continue;
    seen.add(el);
    uniq.push(el);
  }

  const reactionTexts = [];
  const debugNodes = [];

  for (const el of uniq.slice(0, 20)) {
    const text = normalize(el.innerText || el.textContent || '');
    const aria = normalize(el.getAttribute('aria-label') || '');
    const title = normalize(el.getAttribute('title') || '');
    const dataReaction = normalize(el.getAttribute('data-reaction') || '');
    const cls = normalize((el.className || '').toString());
    const tag = (el.tagName || '').toLowerCase();

    if (text) reactionTexts.push(text);
    if (aria) reactionTexts.push(aria);
    if (title) reactionTexts.push(title);
    if (dataReaction) reactionTexts.push(dataReaction);

    debugNodes.push(`${tag} | class='${cls}' | text='${text}' | aria='${aria}' | title='${title}' | data='${dataReaction}'`);
  }

  return JSON.stringify({
    messageFound: true,
    isOutgoing,
    reactionNodeCount: uniq.length,
    reactionTexts: Array.from(new Set(reactionTexts)).slice(0, 20),
    messageTextPreview: messageText.slice(0, 260),
    debugNodes
  });
}
""", dealId);

    var root = ParseJsonObjectOrDefault(scanJson,
        """
{"messageFound":false,"isOutgoing":false,"reactionNodeCount":0,"reactionTexts":[],"messageTextPreview":"","debugNodes":[]}
""");

    var reactionTexts = root.TryGetProperty("reactionTexts", out var reactionTextsElement) && reactionTextsElement.ValueKind == JsonValueKind.Array
        ? reactionTextsElement.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? string.Empty)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToList()
        : [];

    var debugNodes = root.TryGetProperty("debugNodes", out var debugNodesElement) && debugNodesElement.ValueKind == JsonValueKind.Array
        ? debugNodesElement.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? string.Empty)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList()
        : [];

    return new SaleDealReactionScan
    {
        MessageFound = root.TryGetProperty("messageFound", out var foundElement) && foundElement.ValueKind == JsonValueKind.True,
        IsOutgoing = root.TryGetProperty("isOutgoing", out var outgoingElement) && outgoingElement.ValueKind == JsonValueKind.True,
        ReactionNodeCount = root.TryGetProperty("reactionNodeCount", out var countElement) && countElement.ValueKind == JsonValueKind.Number ? countElement.GetInt32() : 0,
        MessageTextPreview = root.TryGetProperty("messageTextPreview", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString() ?? string.Empty
            : string.Empty,
        ReactionTexts = reactionTexts,
        DebugNodes = debugNodes
    };
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

    await NotifyUsersWithTextAsync(page, notificationUsers, "Запускаю авто-P2P поиск. Начинаю сканировать объявления.");
    if (stopAllRequested) return;

    Console.WriteLine("Запускаю автоматизацию: P2P -> Купить -> Tether (USDT) -> СБП");
    var opened = await NavigateToSbpMenuAsync(page, includeP2p: true);
    if (!opened)
    {
        Console.WriteLine("Не удалось открыть меню СБП для старта автоматики.");
        return;
    }

    async Task StopWithNotifyAsync(string reason)
    {
        Console.WriteLine(reason);
        await NotifyUsersWithTextAsync(page, notificationUsers, "Авто-P2P остановлена (команда S).");
    }

    var automationStartedAt = DateTimeOffset.UtcNow;

    while (!stopAllRequested)
    {
        var best = await FindBestOfferWithPagingAsync(page, targetPriceRub, volumeFilter, notificationUsers, automationStartedAt);
        if (best is null)
        {
            if (stopAllRequested)
            {
                await StopWithNotifyAsync("Автоматизация остановлена клавишей S.");
                return;
            }

            Console.WriteLine("Объявления по заданным параметрам не найдены.");
            return;
        }

        Console.WriteLine($"Выбираю лучшее объявление: [{best.DisplayIndex}] {best.SourceLabel}");
        Console.WriteLine("Жду 0.18 сек перед нажатием лучшего объявления...");
        await WaitWithStopAsync(page, 180);
        if (stopAllRequested)
        {
            await StopWithNotifyAsync("Автоматизация остановлена клавишей S.");
            return;
        }

        var bestClicked = await ClickVisibleButtonByIndexAsync(page, best.DisplayIndex, isAutomation: true);
        if (!bestClicked)
        {
            Console.WriteLine("Не удалось нажать лучшее объявление.");
            continue;
        }

        var actionFlowCompleted = await ExecuteDealActionFlowAsync(page, best, volumeFilter);
        if (stopAllRequested)
        {
            await StopWithNotifyAsync("Автоматизация остановлена клавишей S.");
            return;
        }

        if (!actionFlowCompleted)
        {
            Console.WriteLine("Сделка не дошла до финального шага. Перезапускаю /p2p и продолжаю поиск.");
            var restartedAfterActionFail = await RestartP2PAfterRejectedDealAsync(page);
            if (!restartedAfterActionFail)
            {
                Console.WriteLine("Не удалось восстановить поиск после сбоя шагов сделки.");
                return;
            }

            continue;
        }

        Console.WriteLine("Жду 1 сек после создания сделки, чтобы сообщение успело появиться...");
        await WaitWithStopAsync(page, 1000);
        if (stopAllRequested)
        {
            await StopWithNotifyAsync("Автоматизация остановлена клавишей S.");
            return;
        }

        var dealInfo = await ExtractDealInfoWithRescansAsync(page, maxAttempts: 20);
        if (string.IsNullOrWhiteSpace(dealInfo.MessageText))
        {
            Console.WriteLine("Сообщение сделки пока пустое. Жду 2.5 сек и делаю повторный сбор...");
            await WaitWithStopAsync(page, 2500);
            if (stopAllRequested) return;
            dealInfo = await ExtractDealInfoWithRescansAsync(page, maxAttempts: 12);
        }

        PrintDealInfo(dealInfo);

        var lower = dealInfo.MessageText?.ToLowerInvariant() ?? string.Empty;
        if (lower.Contains("продавец отказался от сделки"))
        {
            await NotifyRejectedDealToUsersAsync(page, notificationUsers, best, dealInfo);
            if (stopAllRequested) return;

            var restarted = await RestartP2PAfterRejectedDealAsync(page);
            if (!restarted)
            {
                Console.WriteLine("Не удалось перезапустить /p2p после отказа продавца.");
                return;
            }

            continue;
        }

        var sellerAccepted = lower.Contains("продавец принял сделку");

        if (!sellerAccepted)
        {
            await NotifyPendingDealToUsersAsync(page, notificationUsers, best, dealInfo);
            if (stopAllRequested) return;

            Console.WriteLine("Жду итог сделки: принятие, отказ или сообщение о проблеме цены/суммы...");
            var outcome = await WaitForDealOutcomeAsync(page, dealInfo);
            if (outcome == DealOutcome.Rejected)
            {
                var rejectedInfo = await ExtractDealInfoWithRescansAsync(page, maxAttempts: 12);
                PrintDealInfo(rejectedInfo);

                await NotifyRejectedDealToUsersAsync(page, notificationUsers, best, rejectedInfo);
                if (stopAllRequested) return;

                var restarted = await RestartP2PAfterRejectedDealAsync(page);
                if (!restarted)
                {
                    Console.WriteLine("Не удалось перезапустить /p2p после отказа продавца.");
                    return;
                }

                continue;
            }

            if (outcome == DealOutcome.Stopped)
            {
                await StopWithNotifyAsync("Ожидание итога сделки остановлено клавишей S.");
                return;
            }

            if (outcome == DealOutcome.NeedRestart)
            {
                Console.WriteLine("Сделка не создана (цена/сумма изменилась или нужен ручной ввод). Перезапускаю поиск через /p2p...");
                var restarted = await RestartP2PAfterRejectedDealAsync(page);
                if (!restarted)
                {
                    Console.WriteLine("Не удалось перезапустить /p2p после сбоя создания сделки.");
                    return;
                }

                continue;
            }

            if (outcome == DealOutcome.Accepted)
            {
                sellerAccepted = true;
            }

            dealInfo = await ExtractDealInfoWithRescansAsync(page, maxAttempts: 12);
            PrintDealInfo(dealInfo);
            sellerAccepted = sellerAccepted || (dealInfo.MessageText ?? string.Empty)
                .Contains("продавец принял сделку", StringComparison.OrdinalIgnoreCase);
        }

        if (sellerAccepted)
        {
            var acceptedDetails = await OpenAcceptedDealDetailsAfterAcceptAsync(page);
            if (acceptedDetails is null)
            {
                Console.WriteLine("Продавец принял сделку, но не удалось открыть детали через 'Посмотреть сделку'.");
                await NotifyUsersWithTextAsync(page, notificationUsers, "ПРОДАВЕЦ ПРИНЯЛ СДЕЛКУ, но не удалось открыть карточку 'Посмотреть сделку'.");
                return;
            }

            dealInfo = acceptedDetails;
            PrintDealInfo(dealInfo);
        }

        await NotifyFoundDealToUsersAsync(page, notificationUsers, best, dealInfo, sellerAccepted);
        return;
    }
}

async Task<DealInfo?> OpenAcceptedDealDetailsAfterAcceptAsync(IPage page)
{
    Console.WriteLine("Сделка принята. Нажимаю 'Посмотреть сделку', затем делаю новый рескан сообщения...");

    await WaitWithStopAsync(page, 500);
    if (stopAllRequested) return null;

    var opened = await ClickAnyDealActionButtonWithRetryAsync(page, "Посмотреть сделку", "Посмотреть");
    if (!opened)
    {
        return null;
    }

    await WaitWithStopAsync(page, 500);
    if (stopAllRequested) return null;

    return await ExtractDealInfoWithRescansAsync(page, maxAttempts: 30);
}

async Task<bool> NavigateToSbpMenuAsync(IPage page, bool includeP2p)
{
    var sequence = includeP2p
        ? new[] { "P2P", "Купить", "Tether (USDT)", "СБП" }
        : new[] { "Купить", "Tether (USDT)", "СБП" };

    foreach (var expected in sequence)
    {
        var variants = expected switch
        {
            "P2P" => new[] { "P2P" },
            "Купить" => new[] { "Купить", "Купить USDT" },
            "Tether (USDT)" => new[] { "Tether (USDT)", "USDT", "Tether" },
            "СБП" => new[] { "СБП" },
            _ => new[] { expected }
        };

        var clicked = false;
        for (var attempt = 1; attempt <= 4 && !clicked; attempt++)
        {
            Console.WriteLine($"Жду 2 сек перед авто-нажатием '{expected}' (попытка {attempt}/4)...");
            await WaitWithStopAsync(page, 2000);
            if (stopAllRequested) return false;

            foreach (var variant in variants)
            {
                if (await ClickVisibleButtonByTextAsync(page, variant, startsWith: true, preferExact: false))
                {
                    clicked = true;
                    break;
                }
            }

            if (!clicked)
            {
                await WaitWithStopAsync(page, 250);
                if (stopAllRequested) return false;
            }
        }

        if (!clicked)
        {
            Console.WriteLine($"Кнопка '{expected}' не найдена после нескольких попыток.");
            return false;
        }

        Console.WriteLine($"Авто-нажатие: '{expected}' выполнено. Жду 2 сек...");
        await WaitWithStopAsync(page, 2000);
        if (stopAllRequested) return false;
    }

    return true;
}

async Task<bool> RestartP2PAfterRejectedDealAsync(IPage page)
{
    Console.WriteLine("Перезапускаю поиск: возвращаюсь в CryptoBot и отправляю /p2p...");

    await WaitWithStopAsync(page, 2000);
    if (stopAllRequested) return false;

    var backToBot = await ClickChatByTitleAsync(page, "Crypto");
    if (!backToBot) return false;

    await WaitWithStopAsync(page, 2000);
    if (stopAllRequested) return false;

    var sent = await SendMessageToCurrentChatAsync(page, "/p2p");
    if (!sent) return false;

    await WaitWithStopAsync(page, 2000);
    if (stopAllRequested) return false;

    return await NavigateToSbpMenuAsync(page, includeP2p: false);
}

async Task NotifyUsersWithTextAsync(IPage page, IReadOnlyList<string> users, string text)
{
    foreach (var user in users)
    {
        await WaitWithStopAsync(page, 2000);
        if (stopAllRequested) return;

        var opened = await ClickChatByTitleAsync(page, user);
        if (!opened) continue;

        await WaitWithStopAsync(page, 2000);
        if (stopAllRequested) return;

        await SendMessageToCurrentChatAsync(page, text);
    }

    await WaitWithStopAsync(page, 2000);
    if (stopAllRequested) return;

    await ClickChatByTitleAsync(page, "Crypto");
    await WaitWithStopAsync(page, 2000);
}

async Task<DealOutcome> WaitForDealOutcomeAsync(IPage page, DealInfo initialDealInfo)
{
    var checkCounter = 0;
    var startedAt = DateTimeOffset.UtcNow;
    var dealId = ExtractDealId(initialDealInfo.MessageText);
    if (!string.IsNullOrWhiteSpace(dealId))
    {
        Console.WriteLine($"Ожидаю исход по сделке #{dealId}.");
    }

    while (true)
    {
        if (stopAllRequested)
        {
            return DealOutcome.Stopped;
        }

        var fastSignal = await DetectDealOutcomeSignalAsync(page, dealId);
        if (fastSignal != DealOutcome.Unknown)
        {
            return fastSignal;
        }

        var dealInfo = await ExtractDealInfoAsync(page);
        if (string.IsNullOrWhiteSpace(dealId))
        {
            dealId = ExtractDealId(dealInfo.MessageText);
        }

        var lower = dealInfo.MessageText?.ToLowerInvariant() ?? string.Empty;
        if (lower.Contains("продавец принял сделку")) return DealOutcome.Accepted;
        if (lower.Contains("продавец отказался от сделки") && !string.IsNullOrWhiteSpace(dealId)) return DealOutcome.Rejected;
        if (HasDealCreationProblem(lower)) return DealOutcome.NeedRestart;

        checkCounter++;
        if (checkCounter % 3 == 0)
        {
            Console.WriteLine("Итог сделки ещё не пришёл. Продолжаю ждать подтверждение/отказ...");
        }

        if (DateTimeOffset.UtcNow - startedAt > TimeSpan.FromMinutes(11))
        {
            Console.WriteLine("Истекло время ожидания итога сделки (11 минут). Перезапускаю поиск.");
            return DealOutcome.NeedRestart;
        }

        await WaitWithStopAsync(page, 250);
    }
}

async Task<DealOutcome> DetectDealOutcomeSignalAsync(IPage page, string? dealId)
{
    var signal = await page.EvaluateAsync<string>("""
(args) => {
  const text = (el) => (el?.innerText || el?.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const wantedDealId = (args?.dealId || '').toString().trim().toLowerCase();

  const messages = Array.from(document.querySelectorAll('.bubble, .message')).slice(-60);
  for (let i = messages.length - 1; i >= 0; i--) {
    const t = text(messages[i]);
    if (!t) continue;

    const fitsDeal = wantedDealId.length === 0 ? true : t.includes(`#${wantedDealId}`) || t.includes(`сделк` ) && t.includes(wantedDealId);

    if (t.includes('продавец принял сделку') && fitsDeal) return 'accepted';

    if (wantedDealId.length > 0 && t.includes('продавец отказался от сделки') && fitsDeal) return 'rejected';

    if (t.includes('цена объявления изменилась') || t.includes('пришлите сумму сделки') || t.includes('попробуйте повторить попытку быстрее') || t.includes('в пределах от')) {
      return 'needrestart';
    }
  }

  return 'unknown';
}
""", new { dealId = dealId ?? string.Empty });

    return signal switch
    {
        "accepted" => DealOutcome.Accepted,
        "rejected" => DealOutcome.Rejected,
        "needrestart" => DealOutcome.NeedRestart,
        _ => DealOutcome.Unknown
    };
}

static string? ExtractDealId(string? message)
{
    if (string.IsNullOrWhiteSpace(message))
    {
        return null;
    }

    var match = Regex.Match(message, @"сделка\s*#\s*([a-z0-9]+)", RegexOptions.IgnoreCase);
    if (!match.Success || match.Groups.Count < 2)
    {
        return null;
    }

    return match.Groups[1].Value.Trim();
}

bool HasDealCreationProblem(string lowerMessage)
{
    if (string.IsNullOrWhiteSpace(lowerMessage)) return false;

    return lowerMessage.Contains("цена объявления изменилась")
           || lowerMessage.Contains("пришлите сумму сделки")
           || lowerMessage.Contains("попробуйте повторить попытку быстрее")
           || lowerMessage.Contains("в пределах от");
}

async Task<bool> ExecuteDealActionFlowAsync(IPage page, P2POffer best, VolumeFilter volumeFilter)
{
    if (best is null)
    {
        Console.WriteLine("Ошибка: выбранное объявление отсутствует (null). Отменяю шаги сделки.");
        return false;
    }

    Console.WriteLine($"Готовлю действия по сделке для объявления: [{best.DisplayIndex}] {best.SourceLabel}");
    await LogVisibleButtonsAsync(page, "Кнопки после открытия объявления");

    Console.WriteLine("Жду 0.25 сек перед нажатием 'Купить'...");
    await WaitWithStopAsync(page, 250);
    if (stopAllRequested) return false;

    var buyClicked = await ClickBuyButtonWithRetryAsync(page);
    if (!buyClicked)
    {
        Console.WriteLine("Кнопка 'Купить' не найдена на экране сделки.");
        return false;
    }

    var enteredDealActions = await EnsureDealActionsOpenedAsync(page);
    if (!enteredDealActions)
    {
        Console.WriteLine("Не удалось перейти к шагу выбора суммы/создания сделки после нажатия 'Купить'.");
        await LogVisibleButtonsAsync(page, "Кнопки после попытки перехода к шагу сделки");
        return false;
    }

    var actionButtons = await DetectDealActionButtonsStateAsync(page);
    Console.WriteLine($"Проверка кнопок сделки: Купить={actionButtons.HasBuy}, Макс={actionButtons.HasMax}, Создать={actionButtons.HasCreate}, УказатьRUB={actionButtons.HasSpecifyRub}.");

    if (ShouldUseSpecifyRubFlow(best, volumeFilter, actionButtons))
    {
        Console.WriteLine($"Подходящий объём ограничен максимумом {volumeFilter.MaxRub.ToString(CultureInfo.InvariantCulture)} RUB. Использую ветку 'Указать в RUB'.");
        return await ExecuteSpecifyRubAmountFlowAsync(page, volumeFilter.MaxRub);
    }

    if (actionButtons.HasMax)
    {
        Console.WriteLine("Найдена кнопка 'Макс'. Нажимаю её перед созданием сделки...");
        var maxClicked = await ClickAnyDealActionButtonWithRetryAsync(page, "Макс");
        if (!maxClicked)
        {
            Console.WriteLine("Кнопка 'Макс' была обнаружена, но нажать её не удалось.");
            return false;
        }

        Console.WriteLine("Нажата 'Макс'. Делаю повторный рескан кнопок после обновления суммы...");
        await WaitWithStopAsync(page, 250);
        if (stopAllRequested) return false;

        var afterMaxState = await DetectDealActionButtonsStateAsync(page);
        Console.WriteLine($"После 'Макс': Купить={afterMaxState.HasBuy}, Макс={afterMaxState.HasMax}, Создать={afterMaxState.HasCreate}, УказатьRUB={afterMaxState.HasSpecifyRub}.");

        if (!afterMaxState.HasCreate && !afterMaxState.HasSpecifyRub)
        {
            await WaitWithStopAsync(page, 250);
            if (stopAllRequested) return false;

            afterMaxState = await DetectDealActionButtonsStateAsync(page);
            Console.WriteLine($"Повторный рескан после 'Макс': Купить={afterMaxState.HasBuy}, Макс={afterMaxState.HasMax}, Создать={afterMaxState.HasCreate}, УказатьRUB={afterMaxState.HasSpecifyRub}.");
        }

        if (afterMaxState.HasCreate)
        {
            var createAfterMaxClicked = await ClickAnyDealActionButtonWithRetryAsync(page, "Созд");
            if (!createAfterMaxClicked)
            {
                Console.WriteLine("Кнопка 'Создать сделку' не найдена после нажатия 'Макс'.");
                return false;
            }

            return true;
        }

        if (afterMaxState.HasSpecifyRub)
        {
            Console.WriteLine("После 'Макс' доступна ветка 'Указать в RUB'. Перехожу к ней.");
            return await ExecuteSpecifyRubAmountFlowAsync(page, volumeFilter.MaxRub);
        }

        Console.WriteLine("После 'Макс' не нашел ни 'Создать', ни 'Указать в RUB'.");
        await LogVisibleButtonsAsync(page, "Кнопки после нажатия 'Макс'");
        return false;
    }

    if (actionButtons.HasCreate)
    {
        Console.WriteLine("Кнопки 'Макс' нет, но есть 'Создать сделку' (единый объем). Нажимаю сразу 'Создать сделку'...");
        await WaitWithStopAsync(page, 250);
        if (stopAllRequested) return false;

        var createDealClicked = await ClickAnyDealActionButtonWithRetryAsync(page, "Созд");
        if (!createDealClicked)
        {
            Console.WriteLine("Кнопка 'Создать сделку' была на экране, но нажать её не удалось.");
            return false;
        }

        return true;
    }

    Console.WriteLine("Не найдены ни 'Макс', ни 'Создать сделку'. Снимаю debug-список кнопок и прерываю шаг сделки.");
    await LogVisibleButtonsAsync(page, "Кнопки в карточке сделки (ожидались 'Макс' или 'Создать сделку')");
    return false;
}

bool ShouldUseSpecifyRubFlow(P2POffer offer, VolumeFilter volumeFilter, DealActionButtonsState state)
{
    if (volumeFilter.MinRub is not null)
    {
        return false;
    }

    if (!state.HasSpecifyRub)
    {
        return false;
    }

    if (offer.VolumeMax is null)
    {
        return false;
    }

    return offer.VolumeMax.Value > volumeFilter.MaxRub + 0.01;
}

async Task<bool> ExecuteSpecifyRubAmountFlowAsync(IPage page, double targetRub)
{
    var specifyClicked = await ClickAnyDealActionButtonWithRetryAsync(page, "Указ");
    if (!specifyClicked)
    {
        Console.WriteLine("Кнопка 'Указать в RUB' не найдена.");
        return false;
    }

    await WaitWithStopAsync(page, 250);
    if (stopAllRequested) return false;

    var amountText = ((int)Math.Round(targetRub, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
    Console.WriteLine($"Отправляю сумму сделки в RUB: {amountText}");

    var amountSent = await SendMessageToCurrentChatAsync(page, amountText);
    if (!amountSent)
    {
        Console.WriteLine("Не удалось отправить сумму в RUB.");
        return false;
    }

    await WaitWithStopAsync(page, 250);
    if (stopAllRequested) return false;

    var createClicked = await ClickAnyDealActionButtonWithRetryAsync(page, "Созд");
    if (!createClicked)
    {
        Console.WriteLine("Кнопка 'Создать сделку' не найдена после указания RUB.");
        return false;
    }

    return true;
}

async Task<bool> ClickBuyButtonWithRetryAsync(IPage page)
{
    var byCommonText = await ClickAnyDealActionButtonWithRetryAsync(page, "Купить");
    if (byCommonText)
    {
        return true;
    }

    Console.WriteLine("Стандартный поиск кнопки 'Купить' не сработал. Пробую прямой клик по видимой кнопке с текстом 'Купить...'.");
    var directClicked = await ClickVisibleButtonByTextAsync(page, "Купить", startsWith: true);
    if (directClicked)
    {
        Console.WriteLine("Успешно нажал 'Купить' через прямой поиск кнопки.");
        return true;
    }

    return false;
}

async Task<bool> EnsureDealActionsOpenedAsync(IPage page)
{
    for (var attempt = 1; attempt <= 2; attempt++)
    {
        if (stopAllRequested) return false;

        Console.WriteLine($"Жду 0.25 сек перед проверкой перехода к шагу сделки (попытка {attempt}/2)...");
        await WaitWithStopAsync(page, 250);
        if (stopAllRequested) return false;

        var state = await DetectDealActionButtonsStateAsync(page);
        Console.WriteLine($"Состояние после 'Купить': Купить={state.HasBuy}, Макс={state.HasMax}, Создать={state.HasCreate}.");

        if (state.HasMax || state.HasCreate)
        {
            return true;
        }

        if (!state.HasBuy)
        {
            continue;
        }

        Console.WriteLine("Похоже, карточка сделки не открылась (кнопка 'Купить' всё ещё на месте). Пробую нажать 'Купить' повторно...");
        var buyClicked = await ClickBuyButtonWithRetryAsync(page);
        if (!buyClicked)
        {
            Console.WriteLine("Повторно нажать 'Купить' не удалось.");
        }
    }

    return false;
}

async Task<DealActionButtonsState> DetectDealActionButtonsStateAsync(IPage page)
{
    var buttons = await CollectVisibleButtonsWithTimeoutAsync(page, timeoutMs: 1200);
    var labels = buttons
        .Select(x => x.Label)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => x.Trim())
        .ToList();

    var hasBuy = labels.Any(label => label.Contains("куп", StringComparison.OrdinalIgnoreCase));
    var hasMax = labels.Any(label => label.Contains("макс", StringComparison.OrdinalIgnoreCase));
    var hasCreate = labels.Any(label => label.Contains("созд", StringComparison.OrdinalIgnoreCase));
    var hasSpecifyRub = labels.Any(label => label.Contains("rub", StringComparison.OrdinalIgnoreCase)
                                            && (label.Contains("указ", StringComparison.OrdinalIgnoreCase)
                                                || label.Contains("ввест", StringComparison.OrdinalIgnoreCase)));

    return new DealActionButtonsState { HasBuy = hasBuy, HasMax = hasMax, HasCreate = hasCreate, HasSpecifyRub = hasSpecifyRub };
}

async Task<bool> ClickDealActionButtonWithRetryAsync(IPage page, string buttonText)
{
    for (var attempt = 1; attempt <= 2; attempt++)
    {
        if (stopAllRequested)
        {
            return false;
        }

        Console.WriteLine($"Пытаюсь нажать кнопку '{buttonText}' (попытка {attempt}/2)...");
        var clicked = await ClickVisibleButtonByTextAsync(page, buttonText, startsWith: true, preferExact: false);
        if (!clicked)
        {
            clicked = await ClickVisibleButtonByTextAsync(page, buttonText, startsWith: false, containsOnly: true);
        }
        if (clicked)
        {
            Console.WriteLine($"Успешно нажал '{buttonText}' на попытке {attempt}.");
            return true;
        }

        Console.WriteLine($"Кнопка '{buttonText}' не найдена на попытке {attempt}. Снимаю список видимых кнопок...");
        await LogVisibleButtonsAsync(page, $"Видимые кнопки (поиск '{buttonText}', попытка {attempt})");
        await WaitWithStopAsync(page, 150);
    }

    Console.WriteLine($"Не удалось нажать '{buttonText}' после 2 попыток.");
    return false;
}

async Task LogVisibleButtonsAsync(IPage page, string title)
{
    try
    {
        var buttons = await CollectVisibleButtonsWithTimeoutAsync(page, timeoutMs: 1200);
        Console.WriteLine($"=== {title} ===");
        if (buttons is null || buttons.Count == 0)
        {
            Console.WriteLine("Видимых кнопок не найдено.");
        }
        else
        {
            for (var i = 0; i < buttons.Count; i++)
            {
                var label = buttons[i]?.Label;
                Console.WriteLine($"  ({i}) '{(string.IsNullOrWhiteSpace(label) ? "(пусто)" : label)}'");
            }
        }

        Console.WriteLine("=== КОНЕЦ СПИСКА КНОПОК ===");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Не удалось собрать список видимых кнопок: {ex.GetType().Name}: {ex.Message}");
    }
}

async Task<List<ButtonDebugInfo>> CollectVisibleButtonsWithTimeoutAsync(IPage page, int timeoutMs)
{
    var collectTask = CollectVisibleButtonsAsync(page);
    var completed = await Task.WhenAny(collectTask, Task.Delay(timeoutMs));
    if (completed != collectTask)
    {
        Console.WriteLine($"Сбор видимых кнопок занял слишком долго ({timeoutMs}ms). Пропускаю debug-список.");
        return new List<ButtonDebugInfo>();
    }

    return await collectTask;
}

static async Task<List<ButtonDebugInfo>> CollectVisibleButtonsAsync(IPage page)
{
    var buttonsJson = await page.EvaluateAsync<string>("""
() => {
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();
  const items = Array.from(document.querySelectorAll('button, [role="button"], .reply-markup-button, .Button'))
    .filter((btn) => {
      const rect = btn.getBoundingClientRect();
      const style = getComputedStyle(btn);
      return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
    })
    .map((btn) => text(btn))
    .filter((t) => typeof t === 'string' && t.length > 0)
    .slice(-40);

  return JSON.stringify(items);
}
""");

    return ParseStringArrayJson(buttonsJson)
        .Where(label => !string.IsNullOrWhiteSpace(label))
        .Select(label => new ButtonDebugInfo { Label = label })
        .ToList();
}

async Task<bool> ClickAnyDealActionButtonWithRetryAsync(IPage page, params string[] buttonTexts)
{
    foreach (var text in buttonTexts)
    {
        var clicked = await ClickDealActionButtonWithRetryAsync(page, text);
        if (clicked) return true;
    }

    return false;
}

async Task<P2POffer?> FindBestOfferWithPagingAsync(IPage page, double targetPriceRub, VolumeFilter volumeFilter, IReadOnlyList<string> notificationUsers, DateTimeOffset startedAt)
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
            .Where(x => x.Price <= targetPriceRub + 0.0001)
            .Where(x => IsOfferVolumeSuitable(x, volumeFilter))
            .OrderBy(x => x.Price)
            .ToList();
        if (eligible.Count > 0)
        {
            var best = eligible.First();
            Console.WriteLine($"Найдено подходящее предложение: {best.RawPrice} ({best.Seller})");
            return best;
        }

        PrintOfferFilterDebug(offers, targetPriceRub, volumeFilter);

        if (DateTimeOffset.UtcNow - lastNoDealNotifyAt >= TimeSpan.FromMinutes(5))
        {
            await SendNoDealsNotificationAsync(page, notificationUsers, startedAt);
            if (stopAllRequested)
            {
                Console.WriteLine("Поиск остановлен клавишей S.");
                return null;
            }

            lastNoDealNotifyAt = DateTimeOffset.UtcNow;
        }

        Console.WriteLine("Подходящих объявлений нет. Жду 4.5 сек перед нажатием кнопки '· 1 ·'...");
        await WaitWithStopAsync(page, 4500);
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

        await WaitWithStopAsync(page, 4500);
        if (stopAllRequested)
        {
            Console.WriteLine("Поиск остановлен клавишей S.");
            return null;
        }
    }
}

async Task SendNoDealsNotificationAsync(IPage page, IReadOnlyList<string> users, DateTimeOffset startedAt)
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

        var sent = await SendMessageToCurrentChatAsync(page, $"Пока сделок нет. Продолжаю поиски. Ищу уже {FormatElapsedSince(startedAt)}.");
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

static string FormatElapsedSince(DateTimeOffset startedAt)
{
    var elapsed = DateTimeOffset.UtcNow - startedAt;
    if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

    if (elapsed.TotalHours >= 1)
    {
        return $"{(int)elapsed.TotalHours}ч {elapsed.Minutes}м";
    }

    return $"{elapsed.Minutes}м {elapsed.Seconds}с";
}

async Task NotifyPendingDealToUsersAsync(IPage page, IReadOnlyList<string> users, P2POffer best, DealInfo dealInfo)
{
    var announcement = TryBuildFullDealNotificationText(best, dealInfo)
        ?? $"Найдено подходящее объявление: [{best.DisplayIndex}] {best.SourceLabel}";

    await NotifyUsersWithTextAsync(page, users, announcement);
    if (stopAllRequested) return;

    await NotifyUsersWithTextAsync(page, users, "Жду решения продавца: подтверждение или отказ.");
}

async Task NotifyRejectedDealToUsersAsync(IPage page, IReadOnlyList<string> users, P2POffer best, DealInfo dealInfo)
{
    Console.WriteLine($"Отправляю уведомление об отмененной сделке пользователям: {string.Join(", ", users)}...");

    var message = "Продавец отказался от сделки.";

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

        Console.WriteLine($"Отправляю уведомление об отмене сделки в {user}...");
        var sent = await SendMessageToCurrentChatAsync(page, message);
        if (!sent)
        {
            Console.WriteLine($"Не удалось отправить сообщение об отмене сделки в {user}.");
        }
    }

    await WaitWithStopAsync(page, 2000);
    if (stopAllRequested) return;

    var backToBot = await ClickChatByTitleAsync(page, "Crypto");
    if (!backToBot)
    {
        Console.WriteLine("Не удалось вернуться в чат CryptoBot после уведомления об отмене сделки.");
        return;
    }

    await WaitWithStopAsync(page, 2000);
}

async Task NotifyFoundDealToUsersAsync(IPage page, IReadOnlyList<string> users, P2POffer best, DealInfo dealInfo, bool sellerAccepted)
{
    Console.WriteLine($"Отправляю найденное объявление пользователям: {string.Join(", ", users)}...");

    var fullDealText = TryBuildFullDealNotificationText(best, dealInfo);
    if (sellerAccepted && !string.IsNullOrWhiteSpace(fullDealText))
    {
        fullDealText = "ПРОДАВЕЦ ПРИНЯЛ СДЕЛКУ.\n\n" + fullDealText;
    }
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

static string ReadSalesScanAccountName()
{
    Console.Write("Введите обозначение аккаунта для логов/уведомлений (Enter = Account1): ");
    var value = (Console.ReadLine() ?? string.Empty).Trim();
    return string.IsNullOrWhiteSpace(value) ? "Account1" : value;
}

static IReadOnlyList<string> ReadSalesActionKeywordPrefixes()
{
    Console.Write("Введите ключевые слова (префиксы) для кнопки шага после 'Принять сделку' через запятую: ");
    var raw = Console.ReadLine() ?? string.Empty;

    var values = raw
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (values.Count == 0)
    {
        values.Add("СБП");
    }

    return values;
}

static string ReadSalesConfirmPassword()
{
    Console.Write("Введите пароль подтверждения для тестовой ветки продаж: ");
    var value = (Console.ReadLine() ?? string.Empty).Trim();
    return value;
}

IReadOnlyList<string> ReadNotificationUsers()
{
    Console.Write("Введите ники получателей уведомлений через запятую (Enter = VO8R): ");
    var input = Console.ReadLine();
    var users = (input ?? string.Empty)
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (users.Count == 0)
    {
        users = ["VO8R"];
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
    var marketPreview = await TryGetMarketPricePreviewAsync(getCached, setCached);
    if (marketPreview is null)
    {
        Console.WriteLine("Текущую рыночную цену USDT/RUB определить не удалось.");
    }
    else
    {
        Console.WriteLine($"Текущая рыночная цена USDT/RUB: {marketPreview.Value.ToString(CultureInfo.InvariantCulture)}");
    }

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

static void PrintOfferFilterDebug(List<P2POffer> offers, double targetPriceRub, VolumeFilter filter)
{
    if (offers.Count == 0)
    {
        Console.WriteLine("Диагностика фильтра: офферов нет.");
        return;
    }

    var pricePass = offers.Count(x => x.Price <= targetPriceRub + 0.0001);
    var volumePass = offers.Count(x => IsOfferVolumeSuitable(x, filter));
    var bothPass = offers.Count(x => x.Price <= targetPriceRub + 0.0001 && IsOfferVolumeSuitable(x, filter));

    Console.WriteLine($"Диагностика фильтра: всего={offers.Count}, по цене={pricePass}, по объему={volumePass}, по обоим={bothPass}.");

    foreach (var sample in offers.OrderBy(x => x.Price).Take(5))
    {
        var reason = GetOfferRejectReason(sample, targetPriceRub, filter);
        Console.WriteLine($"  -> [{sample.DisplayIndex}] {sample.SourceLabel} | {reason}");
    }
}

static string GetOfferRejectReason(P2POffer offer, double targetPriceRub, VolumeFilter filter)
{
    var priceOk = offer.Price <= targetPriceRub + 0.0001;
    var volumeOk = IsOfferVolumeSuitable(offer, filter);

    if (priceOk && volumeOk)
    {
        return "ПОДХОДИТ";
    }

    if (!priceOk && !volumeOk)
    {
        return $"цена {offer.Price.ToString(CultureInfo.InvariantCulture)} > лимита {targetPriceRub.ToString(CultureInfo.InvariantCulture)} и не проходит объем";
    }

    if (!priceOk)
    {
        return $"цена {offer.Price.ToString(CultureInfo.InvariantCulture)} > лимита {targetPriceRub.ToString(CultureInfo.InvariantCulture)}";
    }

    var min = offer.VolumeMin?.ToString(CultureInfo.InvariantCulture) ?? "?";
    var max = (offer.VolumeMax ?? offer.VolumeMin)?.ToString(CultureInfo.InvariantCulture) ?? "?";
    var filterMin = filter.MinRub?.ToString(CultureInfo.InvariantCulture) ?? "(без минимума)";
    return $"не проходит объем: оффер [{min}..{max}], фильтр [{filterMin}..{filter.MaxRub.ToString(CultureInfo.InvariantCulture)}]";
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

static async Task<double?> TryGetMarketPricePreviewAsync(
    Func<(double? Price, DateTimeOffset At)> getCached,
    Action<(double Price, DateTimeOffset At)> setCached)
{
    var cached = getCached();
    if (cached.Price is not null && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(10))
    {
        return cached.Price.Value;
    }

    var market = await TryGetMarketPriceRubAsync();
    if (market is null)
    {
        return null;
    }

    setCached((market.Value, DateTimeOffset.UtcNow));
    return market.Value;
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

        await page.WaitForTimeoutAsync(500);
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
    var data = await page.EvaluateAsync<string>("""
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

  return JSON.stringify({
    MessageText: dealMessage,
    ActionButtonLabel: buyButton ? text(buyButton) : '(кнопка Купить* не найдена)'
  });
}
""");

    if (TryReadDealInfo(data, out var info))
    {
        return info;
    }

    return new DealInfo { MessageText = string.Empty, ActionButtonLabel = "(не удалось извлечь)" };
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
    var targetData = await page.EvaluateAsync<string>("""
(titlePart) => {
  const normalize = (s) => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const wanted = normalize(titlePart);
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();

  const items = Array.from(document.querySelectorAll('.chatlist-chat, .chat-item, [data-peer-id], .ListItem'));
  const hit = items.find((item) => normalize(text(item)).includes(wanted));
  if (!hit) return null;

  const rect = hit.getBoundingClientRect();
  return JSON.stringify({ x: rect.left + rect.width / 2, y: rect.top + rect.height / 2, label: text(hit) });
}
""", titlePart);

    if (!TryReadClickTarget(targetData, out var target)) return false;

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
    var targetData = await page.EvaluateAsync<string>("""
(args) => {
  const normalize = (s) => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const loose = (s) => normalize(s).replace(/[^\p{L}\p{N}]+/gu, '');
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();

  const allVisibleButtons = Array.from(document.querySelectorAll('button, [role="button"], .reply-markup-button, .Button'))
    .map((btn) => ({ btn, rect: btn.getBoundingClientRect() }))
    .filter(({ btn, rect }) => {
      const style = getComputedStyle(btn);
      return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
    });

  const bubbles = Array.from(document.querySelectorAll('.bubble, .message'));
  const lastBubble = bubbles.at(-1) || null;
  const anchorTop = lastBubble ? lastBubble.getBoundingClientRect().top : -100000;

  const aroundLatestMessage = allVisibleButtons.filter((x) => x.rect.top >= anchorTop - 40);
  const scopedButtons = (aroundLatestMessage.length > 0 ? aroundLatestMessage : allVisibleButtons).map((x) => x.btn);

  const expected = normalize(args.expectedText);
  const expectedLoose = loose(args.expectedText);
  const expectedTokens = expected.split(/\s+/).filter(Boolean);
  const tokenMatch = (t) => expectedTokens.length > 0 && expectedTokens.every((token) => t.includes(token));

  const matches = scopedButtons.filter((btn) => {
    const t = normalize(text(btn));
    const tLoose = loose(text(btn));
    if (args.preferExact && t === expected) return true;
    if (args.containsOnly) return t.includes(expected) || tokenMatch(t);
    if (args.startsWith)
      return t.startsWith(expected)
        || (expectedLoose.length > 0 && tLoose.startsWith(expectedLoose))
        || tokenMatch(t);

    return t.includes(expected)
      || (expectedLoose.length > 0 && tLoose.includes(expectedLoose))
      || tokenMatch(t);
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
  return JSON.stringify({ x: rect.left + rect.width / 2, y: rect.top + rect.height / 2, label: text(hit) });
}
""", new { expectedText, startsWith, containsOnly, preferExact });

    if (!TryReadClickTarget(targetData, out var target)) return false;

    await page.Mouse.MoveAsync((float)target.X, (float)target.Y);
    await page.Mouse.DownAsync();
    await page.Mouse.UpAsync();
    return true;
}

static async Task<bool> ClickVisibleButtonByIndexAsync(IPage page, int displayIndex, bool isAutomation = false)
{
    var targetData = await page.EvaluateAsync<string>("""
(displayIndex) => {
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();
  const allVisibleButtons = Array.from(document.querySelectorAll('button, [role="button"], .reply-markup-button, .Button'))
    .map((btn) => ({ btn, rect: btn.getBoundingClientRect() }))
    .filter(({ btn, rect }) => {
      const style = getComputedStyle(btn);
      const visible = rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
      if (!visible) return false;
      const t = text(btn);
      return t.length > 0 || (btn.getAttribute('aria-label') || '').trim().length > 0;
    });

  const bubbles = Array.from(document.querySelectorAll('.bubble, .message'));
  const lastBubble = bubbles.at(-1) || null;
  const anchorTop = lastBubble ? lastBubble.getBoundingClientRect().top : -100000;

  const aroundLatestMessage = allVisibleButtons.filter((x) => x.rect.top >= anchorTop - 40);

  const targetList = (aroundLatestMessage.length > 0 ? aroundLatestMessage : allVisibleButtons)
    .sort((a, b) => {
      if (Math.abs(a.rect.top - b.rect.top) > 6) return a.rect.top - b.rect.top;
      return a.rect.left - b.rect.left;
    });

  const chosen = targetList[displayIndex];
  if (!chosen) return null;

  const rect = chosen.btn.getBoundingClientRect();
  return JSON.stringify({ x: rect.left + rect.width / 2, y: rect.top + rect.height / 2, label: text(chosen.btn) });
}
""", displayIndex);

    if (!TryReadClickTarget(targetData, out var target)) return false;

    await page.Mouse.MoveAsync((float)target.X, (float)target.Y);
    await page.Mouse.DownAsync();
    await page.Mouse.UpAsync();
    Console.WriteLine($"Нажата кнопка [{displayIndex}] '{target.Label}'.");

    if (isAutomation)
    {
        await page.WaitForTimeoutAsync(650);
    }

    return true;
}

static async Task<JsonElement> CollectMenuDataAsync(IPage page)
{
    var menuJson = await page.EvaluateAsync<string>("""
() => {
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();
  const blockText = (el) => (el?.innerText || el?.textContent || '').replace(/\r/g, '').trim();

  const bubbles = Array.from(document.querySelectorAll('.bubble, .message'));
  const lastBubble = bubbles.at(-1) || null;

  const allVisibleButtons = Array.from(document.querySelectorAll('button, [role="button"], .reply-markup-button, .Button'))
    .map((btn, domIndex) => ({ btn, domIndex, rect: btn.getBoundingClientRect() }))
    .filter(({ btn, rect }) => {
      const style = getComputedStyle(btn);
      const visible = rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
      if (!visible) return false;
      const t = text(btn);
      return t.length > 0 || (btn.getAttribute('aria-label') || '').trim().length > 0;
    });

  const anchorTop = lastBubble ? lastBubble.getBoundingClientRect().top : -100000;
  const aroundLatestMessage = allVisibleButtons.filter((x) => x.rect.top >= anchorTop - 40);

  const targetButtons = (aroundLatestMessage.length > 0 ? aroundLatestMessage : allVisibleButtons)
    .sort((a, b) => {
      if (Math.abs(a.rect.top - b.rect.top) > 6) return a.rect.top - b.rect.top;
      return a.rect.left - b.rect.left;
    })
    .slice(-30);

  const visibleButtons = targetButtons.map(({ btn, domIndex }, displayIndex) => ({
    index: displayIndex,
    domIndex,
    label: text(btn),
    ariaLabel: btn.getAttribute('aria-label') || ''
  }));

  const messageAboveButtons = blockText(lastBubble?.querySelector('.bubble-content-wrapper')) || blockText(lastBubble);
  const messageTime = text(lastBubble?.querySelector('time, .time, .message-time'));
  const activeChatTitle = text(document.querySelector('.chat-info .title, .chat-info-wrapper .title, .topbar .title, header .title'));

  return JSON.stringify({
    extractedAt: new Date().toISOString(),
    activeChatTitle,
    messageAboveButtons,
    messageTime,
    visibleButtonCount: visibleButtons.length,
    visibleButtons
  });
}
""");

    return ParseJsonObjectOrDefault(menuJson,
        """{"extractedAt":"","activeChatTitle":"","messageAboveButtons":"","messageTime":"","visibleButtonCount":0,"visibleButtons":[]}""");
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

static JsonElement ParseJsonObjectOrDefault(string? data, string fallbackJson)
{
    try
    {
        using var fallbackDoc = JsonDocument.Parse(fallbackJson);
        if (string.IsNullOrWhiteSpace(data))
        {
            return fallbackDoc.RootElement.Clone();
        }

        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return fallbackDoc.RootElement.Clone();
        }

        return doc.RootElement.Clone();
    }
    catch (JsonException)
    {
        using var fallbackDoc = JsonDocument.Parse(fallbackJson);
        return fallbackDoc.RootElement.Clone();
    }
}

static List<string> ParseStringArrayJson(string? data)
{
    if (string.IsNullOrWhiteSpace(data))
    {
        return new List<string>();
    }

    try
    {
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        var list = new List<string>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                list.Add(value);
            }
        }

        return list;
    }
    catch (JsonException)
    {
        return new List<string>();
    }
}

static bool TryReadDealInfo(string? data, out DealInfo info)
{
    info = default!;
    if (string.IsNullOrWhiteSpace(data))
    {
        return false;
    }

    try
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var messageText = root.TryGetProperty("MessageText", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString() ?? string.Empty
            : string.Empty;

        var actionButtonLabel = root.TryGetProperty("ActionButtonLabel", out var actionElement) && actionElement.ValueKind == JsonValueKind.String
            ? actionElement.GetString() ?? string.Empty
            : string.Empty;

        info = new DealInfo
        {
            MessageText = messageText,
            ActionButtonLabel = string.IsNullOrWhiteSpace(actionButtonLabel) ? "(кнопка Купить* не найдена)" : actionButtonLabel
        };

        return true;
    }
    catch (JsonException)
    {
        return false;
    }
}

static bool TryReadClickTarget(string? data, out ClickTarget target)
{
    target = default;
    if (string.IsNullOrWhiteSpace(data))
    {
        return false;
    }

    try
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!root.TryGetProperty("x", out var xElement) || xElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (!root.TryGetProperty("y", out var yElement) || yElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var label = root.TryGetProperty("label", out var labelElement) && labelElement.ValueKind == JsonValueKind.String
            ? labelElement.GetString() ?? string.Empty
            : string.Empty;

        target = new ClickTarget(xElement.GetDouble(), yElement.GetDouble(), label);
        return true;
    }
    catch (JsonException)
    {
        return false;
    }
}

enum DealOutcome
{
    Unknown,
    Accepted,
    Rejected,
    NeedRestart,
    Stopped
}

file readonly record struct ClickTarget(double X, double Y, string Label);

file sealed class DealActionButtonsState
{
    public required bool HasBuy { get; init; }
    public required bool HasMax { get; init; }
    public required bool HasCreate { get; init; }
    public required bool HasSpecifyRub { get; init; }
}

file sealed class SaleDealNotification
{
    public required string DealId { get; init; }
    public required string AmountRub { get; init; }
    public required string Bank { get; init; }
}

file sealed class SaleDealReactionScan
{
    public required bool MessageFound { get; init; }
    public required bool IsOutgoing { get; init; }
    public required int ReactionNodeCount { get; init; }
    public required string MessageTextPreview { get; init; }
    public required List<string> ReactionTexts { get; init; }
    public required List<string> DebugNodes { get; init; }
}

file sealed class ButtonDebugInfo
{
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
