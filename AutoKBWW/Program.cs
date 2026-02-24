using System.Text.Json;
using Microsoft.Playwright;

var outputDirectory = Path.Combine(AppContext.BaseDirectory, "output");
Directory.CreateDirectory(outputDirectory);

using var playwright = await Playwright.CreateAsync();

var cdpUrl = Environment.GetEnvironmentVariable("TELEGRAM_CDP_URL");

await using var browser = await OpenBrowserAsync(playwright, cdpUrl);
var context = await GetWorkingContextAsync(browser, cdpUrl);
var page = await context.NewPageAsync();
await page.GotoAsync("https://web.telegram.org/k/", new PageGotoOptions
{
    WaitUntil = WaitUntilState.DOMContentLoaded,
    Timeout = 0
});

Console.WriteLine("Telegram Web открыт в Яндекс Браузере.");
Console.WriteLine("1) Войдите в аккаунт вручную.");
Console.WriteLine("2) Откройте CryptoBot.");
Console.WriteLine("3) Нажмите ENTER здесь, когда будете готовы собрать структурированные данные...");
Console.ReadLine();

await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 0 });

var extractionResult = await CollectStructuredDataAsync(page);
PrintExtractionToConsole(extractionResult);

var options = new JsonSerializerOptions { WriteIndented = true };
var json = JsonSerializer.Serialize(extractionResult, options);

var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
var jsonPath = Path.Combine(outputDirectory, $"cryptobot_structured_{timestamp}.json");
var htmlPath = Path.Combine(outputDirectory, $"telegram_snapshot_{timestamp}.html");

await File.WriteAllTextAsync(jsonPath, json);
await File.WriteAllTextAsync(htmlPath, await page.ContentAsync());

Console.WriteLine();
Console.WriteLine("Сбор и структурирование данных завершены.");
Console.WriteLine($"JSON: {jsonPath}");
Console.WriteLine($"HTML snapshot: {htmlPath}");

await RunInteractiveButtonClickLoopAsync(page);

Console.WriteLine();
Console.WriteLine("Нажмите ENTER для закрытия браузера...");
Console.ReadLine();


static async Task<IBrowser> OpenBrowserAsync(IPlaywright playwright, string? cdpUrl)
{
    if (!string.IsNullOrWhiteSpace(cdpUrl))
    {
        Console.WriteLine($"Подключаюсь к уже открытому браузеру по CDP: {cdpUrl}");
        return await playwright.Chromium.ConnectOverCDPAsync(cdpUrl);
    }

    Console.WriteLine("TELEGRAM_CDP_URL не задан. Запускаю отдельный экземпляр Яндекс Браузера.");
    Console.WriteLine("Чтобы работать в уже открытом браузере, запустите его с --remote-debugging-port и задайте TELEGRAM_CDP_URL.");

    var yandexBrowserPath = ResolveYandexBrowserPath();
    Console.WriteLine($"Использую browser executable: {yandexBrowserPath}");

    return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = false,
        SlowMo = 60,
        ExecutablePath = yandexBrowserPath,
        Args =
        [
            "--start-maximized"
        ]
    });
}

static async Task<IBrowserContext> GetWorkingContextAsync(IBrowser browser, string? cdpUrl)
{
    if (!string.IsNullOrWhiteSpace(cdpUrl))
    {
        var existing = browser.Contexts.FirstOrDefault();
        if (existing is not null)
        {
            Console.WriteLine("Использую существующий контекст браузера (не инкогнито).");
            return existing;
        }
    }

    return await browser.NewContextAsync(new BrowserNewContextOptions
    {
        ViewportSize = new ViewportSize
        {
            Width = 1440,
            Height = 900
        }
    });
}

static async Task<JsonElement> CollectStructuredDataAsync(IPage page)
{
    return await page.EvaluateAsync<JsonElement>("""
() => {
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();

  const lastMessageNode = Array.from(document.querySelectorAll('.bubble, .message')).pop() || null;
  const lastMessageText = text(lastMessageNode);
  const lastMessageTime = text(lastMessageNode?.querySelector('time, .time, .message-time'));

  const buttonCandidates = Array.from(document.querySelectorAll('button, [role="button"], .reply-markup-button, .Button'));
  const visibleButtons = buttonCandidates
    .filter((btn) => {
      const rect = btn.getBoundingClientRect();
      const style = getComputedStyle(btn);
      return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
    })
    .map((btn, index) => ({
      index,
      label: text(btn),
      ariaLabel: btn.getAttribute('aria-label') || '',
      className: btn.className || '',
      dataTestId: btn.getAttribute('data-testid') || ''
    }))
    .filter((btn) => btn.label.length > 0 || btn.ariaLabel.length > 0);

  const amountMatches = lastMessageText.match(/(?:\$|₽|€|₴)?\s?\d+[\d\s.,]*(?:\s?(?:USDT|BTC|ETH|TON|RUB|USD|EUR))?/gi) || [];
  const linkMatches = lastMessageText.match(/https?:\/\/[^\s]+/gi) || [];
  const codeMatches = lastMessageText.match(/\b[A-Z0-9]{6,}\b/g) || [];

  const cryptoBotSignals = {
    hasInvoiceKeyword: /invoice|сч[её]т|оплат|pay/i.test(lastMessageText),
    hasCheckKeyword: /check|чек/i.test(lastMessageText),
    hasBalanceKeyword: /balance|баланс/i.test(lastMessageText),
    hasSendKeyword: /send|перевод|transfer/i.test(lastMessageText),
    hasReceiveKeyword: /receive|получ/i.test(lastMessageText),
  };

  const activeChatTitle = text(document.querySelector('.chat-info .title, .chat-info-wrapper .title, .topbar .title, header .title'));

  return {
    extractedAt: new Date().toISOString(),
    pageTitle: document.title,
    url: location.href,
    activeChatTitle,
    botUi: {
      visibleButtonCount: visibleButtons.length,
      visibleButtons
    },
    lastMessage: {
      text: lastMessageText,
      time: lastMessageTime,
      detectedAmounts: amountMatches,
      detectedLinks: linkMatches,
      detectedCodes: codeMatches,
      cryptoBotSignals
    }
  };
}
""");
}

static void PrintExtractionToConsole(JsonElement result)
{
    Console.WriteLine();
    Console.WriteLine("=== ЧТО СОБРАЛ И КАК СТРУКТУРИРОВАЛ ===");
    Console.WriteLine($"Время сбора: {GetString(result, "extractedAt")}");
    Console.WriteLine($"Страница: {GetString(result, "pageTitle")}");
    Console.WriteLine($"URL: {GetString(result, "url")}");
    Console.WriteLine($"Активный чат: {GetString(result, "activeChatTitle")}");

    if (result.TryGetProperty("botUi", out var botUi) &&
        botUi.TryGetProperty("visibleButtonCount", out var countEl))
    {
        Console.WriteLine();
        Console.WriteLine($"Найдено видимых кнопок: {countEl.GetInt32()}");
    }

    if (result.TryGetProperty("botUi", out botUi) &&
        botUi.TryGetProperty("visibleButtons", out var buttonsEl) &&
        buttonsEl.ValueKind == JsonValueKind.Array)
    {
        Console.WriteLine("Кнопки:");
        foreach (var button in buttonsEl.EnumerateArray())
        {
            var index = button.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : -1;
            var label = GetString(button, "label");
            var aria = GetString(button, "ariaLabel");
            Console.WriteLine($"  [{index}] label='{label}', aria='{aria}'");
        }
    }

    if (result.TryGetProperty("lastMessage", out var lastMessage))
    {
        Console.WriteLine();
        Console.WriteLine("Последнее сообщение:");
        Console.WriteLine($"  Время: {GetString(lastMessage, "time")}");
        Console.WriteLine($"  Текст: {GetString(lastMessage, "text")}");

        PrintArray(lastMessage, "detectedAmounts", "  Обнаружены суммы");
        PrintArray(lastMessage, "detectedLinks", "  Обнаружены ссылки");
        PrintArray(lastMessage, "detectedCodes", "  Обнаружены коды");

        if (lastMessage.TryGetProperty("cryptoBotSignals", out var signals))
        {
            Console.WriteLine("  Что понял по сообщению:");
            Console.WriteLine($"    invoice/check: {GetBool(signals, "hasInvoiceKeyword")}/{GetBool(signals, "hasCheckKeyword")}");
            Console.WriteLine($"    balance/send/receive: {GetBool(signals, "hasBalanceKeyword")}/{GetBool(signals, "hasSendKeyword")}/{GetBool(signals, "hasReceiveKeyword")}");
        }
    }

    Console.WriteLine("=== КОНЕЦ ОТЧЕТА ===");
}

static async Task RunInteractiveButtonClickLoopAsync(IPage page)
{
    Console.WriteLine();
    Console.WriteLine("Можно нажимать кнопки через мышь Playwright.");
    Console.WriteLine("Введите индекс кнопки (как в списке выше) и ENTER.");
    Console.WriteLine("Введите R чтобы обновить сбор данных, Q чтобы закончить.");

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
            var refreshed = await CollectStructuredDataAsync(page);
            PrintExtractionToConsole(refreshed);
            continue;
        }

        if (!int.TryParse(input, out var buttonIndex))
        {
            Console.WriteLine("Некорректный ввод. Укажите число, R или Q.");
            continue;
        }

        var target = await page.EvaluateAsync<ClickTarget?>("""
(index) => {
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();
  const buttonCandidates = Array.from(document.querySelectorAll('button, [role="button"], .reply-markup-button, .Button'));
  const visibleButtons = buttonCandidates.filter((btn) => {
    const rect = btn.getBoundingClientRect();
    const style = getComputedStyle(btn);
    return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
  });

  const target = visibleButtons[index];
  if (!target) return null;

  const rect = target.getBoundingClientRect();
  return {
    x: rect.left + rect.width / 2,
    y: rect.top + rect.height / 2,
    label: text(target)
  };
}
""", buttonIndex);

        if (target is null)
        {
            Console.WriteLine($"Кнопка с индексом {buttonIndex} не найдена среди видимых.");
            continue;
        }

        await page.Mouse.MoveAsync((float)target.X, (float)target.Y);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();

        Console.WriteLine($"Нажата кнопка [{buttonIndex}] '{target.Label}'.");
    }
}

static void PrintArray(JsonElement source, string propertyName, string title)
{
    if (!source.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
    {
        return;
    }

    var values = property.EnumerateArray().Select(x => x.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
    if (values.Length == 0)
    {
        Console.WriteLine($"{title}: нет");
        return;
    }

    Console.WriteLine($"{title}: {string.Join(", ", values)}");
}

static string GetString(JsonElement source, string propertyName)
{
    return source.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? string.Empty
        : string.Empty;
}

static bool GetBool(JsonElement source, string propertyName)
{
    return source.TryGetProperty(propertyName, out var value) &&
           (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) &&
           value.GetBoolean();
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

    throw new FileNotFoundException(
        "Не найден executable Яндекс Браузера. Укажите полный путь через переменную окружения YANDEX_BROWSER_PATH.");
}

file sealed class ClickTarget
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required string Label { get; init; }
}
