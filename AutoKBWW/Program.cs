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

Console.WriteLine("Telegram Web открыт.");
Console.WriteLine("1) Войдите в аккаунт вручную.");
Console.WriteLine("2) Откройте CryptoBot.");
Console.WriteLine("3) Нажмите ENTER, когда меню бота открыто.");
Console.ReadLine();

await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 0 });

var extractionResult = await CollectMenuDataAsync(page);
PrintMenuToConsole(extractionResult);
await SaveSnapshotAsync(page, extractionResult, outputDirectory);

await RunInteractiveButtonClickLoopAsync(page, outputDirectory);

Console.WriteLine();
Console.WriteLine("Нажмите ENTER для закрытия браузера...");
Console.ReadLine();

static async Task<JsonElement> CollectMenuDataAsync(IPage page)
{
    return await page.EvaluateAsync<JsonElement>("""
() => {
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();

  const bubbles = Array.from(document.querySelectorAll('.bubble, .message'));
  const lastBubble = bubbles.at(-1) || null;
  const menuButtons = Array.from(document.querySelectorAll('button, [role="button"], .reply-markup-button, .Button'))
    .filter((btn) => {
      const rect = btn.getBoundingClientRect();
      const style = getComputedStyle(btn);
      const visible = rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
      if (!visible) return false;
      const t = text(btn);
      return t.length > 0 || (btn.getAttribute('aria-label') || '').trim().length > 0;
    })
    .map((btn, index) => ({
      index,
      label: text(btn),
      ariaLabel: btn.getAttribute('aria-label') || ''
    }));

  // Пытаемся взять текст сообщения, к которому относится текущее меню кнопок.
  // Обычно это последнее сообщение в открытом чате (над reply клавиатурой).
  const messageAboveButtons = text(lastBubble?.querySelector('.message, .text-content, .translatable-message')) || text(lastBubble);
  const messageTime = text(lastBubble?.querySelector('time, .time, .message-time'));

  const activeChatTitle = text(document.querySelector('.chat-info .title, .chat-info-wrapper .title, .topbar .title, header .title'));

  return {
    extractedAt: new Date().toISOString(),
    activeChatTitle,
    messageAboveButtons,
    messageTime,
    visibleButtonCount: menuButtons.length,
    visibleButtons: menuButtons
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
    Console.WriteLine($"Кнопок найдено: {GetInt(result, "visibleButtonCount")}");

    if (result.TryGetProperty("visibleButtons", out var buttons) && buttons.ValueKind == JsonValueKind.Array)
    {
        foreach (var button in buttons.EnumerateArray())
        {
            var index = GetInt(button, "index");
            var label = GetString(button, "label");
            var aria = GetString(button, "ariaLabel");
            Console.WriteLine($"  [{index}] label='{label}', aria='{aria}'");
        }
    }

    Console.WriteLine("=== КОНЕЦ МЕНЮ ===");
}

static async Task SaveSnapshotAsync(IPage page, JsonElement result, string outputDirectory)
{
    var options = new JsonSerializerOptions { WriteIndented = true };
    var json = JsonSerializer.Serialize(result, options);
    var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");

    var jsonPath = Path.Combine(outputDirectory, $"cryptobot_menu_{timestamp}.json");
    var htmlPath = Path.Combine(outputDirectory, $"telegram_snapshot_{timestamp}.html");

    await File.WriteAllTextAsync(jsonPath, json);
    await File.WriteAllTextAsync(htmlPath, await page.ContentAsync());

    Console.WriteLine($"JSON: {jsonPath}");
    Console.WriteLine($"HTML snapshot: {htmlPath}");
}

static async Task RunInteractiveButtonClickLoopAsync(IPage page, string outputDirectory)
{
    Console.WriteLine();
    Console.WriteLine("Введите индекс кнопки для нажатия.");
    Console.WriteLine("После каждого нажатия скрипт автоматически пересканирует меню.");
    Console.WriteLine("Введите Q для выхода.");

    while (true)
    {
        Console.Write("Ваш выбор: ");
        var input = Console.ReadLine()?.Trim();

        if (string.Equals(input, "Q", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        if (!int.TryParse(input, out var buttonIndex))
        {
            Console.WriteLine("Некорректный ввод. Укажите индекс кнопки или Q.");
            continue;
        }

        var target = await page.EvaluateAsync<ClickTarget?>("""
(index) => {
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();
  const visibleButtons = Array.from(document.querySelectorAll('button, [role="button"], .reply-markup-button, .Button'))
    .filter((btn) => {
      const rect = btn.getBoundingClientRect();
      const style = getComputedStyle(btn);
      const visible = rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
      if (!visible) return false;
      const t = text(btn);
      return t.length > 0 || (btn.getAttribute('aria-label') || '').trim().length > 0;
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

        await page.WaitForTimeoutAsync(500);
        var refreshed = await CollectMenuDataAsync(page);
        PrintMenuToConsole(refreshed);
        await SaveSnapshotAsync(page, refreshed, outputDirectory);
    }
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

static async Task<IBrowser> OpenBrowserAsync(IPlaywright playwright, string? cdpUrl)
{
    if (!string.IsNullOrWhiteSpace(cdpUrl))
    {
        Console.WriteLine($"Подключаюсь к уже открытому браузеру по CDP: {cdpUrl}");
        return await playwright.Chromium.ConnectOverCDPAsync(cdpUrl);
    }

    Console.WriteLine("TELEGRAM_CDP_URL не задан. Запускаю отдельный экземпляр Яндекс Браузера.");
    var yandexBrowserPath = ResolveYandexBrowserPath();
    Console.WriteLine($"Использую browser executable: {yandexBrowserPath}");

    return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = false,
        SlowMo = 60,
        ExecutablePath = yandexBrowserPath,
        Args = ["--start-maximized"]
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
        ViewportSize = new ViewportSize { Width = 1440, Height = 900 }
    });
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
