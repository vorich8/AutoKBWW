using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;

var outputDirectory = Path.Combine(AppContext.BaseDirectory, "output");
Directory.CreateDirectory(outputDirectory);

using var playwright = await Playwright.CreateAsync();

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

var extractionResult = await CollectMenuDataAsync(page);
PrintMenuToConsole(extractionResult);
await SaveSnapshotAsync(page, extractionResult, outputDirectory);

await RunInteractiveLoopAsync(page, outputDirectory);

Console.WriteLine();
Console.WriteLine("Нажмите ENTER для закрытия браузера...");
Console.ReadLine();

static async Task RunInteractiveLoopAsync(IPage page, string outputDirectory)
{
    Console.WriteLine();
    Console.WriteLine("Команды: индекс кнопки (0..), AUTO - автосценарий P2P, R - перескан, Q - выход.");

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
            var refreshed = await CollectMenuDataAsync(page);
            PrintMenuToConsole(refreshed);
            await SaveSnapshotAsync(page, refreshed, outputDirectory);
            continue;
        }

        if (string.Equals(input, "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            await RunP2PAutomationAsync(page, outputDirectory);
            continue;
        }

        if (!int.TryParse(input, out var displayIndex))
        {
            Console.WriteLine("Некорректный ввод. Укажите индекс, AUTO, R или Q.");
            continue;
        }

        var clicked = await ClickVisibleButtonByIndexAsync(page, displayIndex);
        if (!clicked)
        {
            Console.WriteLine($"Кнопка с индексом {displayIndex} не найдена в последних 30.");
            continue;
        }

        await page.WaitForTimeoutAsync(500);
        var refreshedAfterClick = await CollectMenuDataAsync(page);
        PrintMenuToConsole(refreshedAfterClick);
        await SaveSnapshotAsync(page, refreshedAfterClick, outputDirectory);
    }
}

static async Task RunP2PAutomationAsync(IPage page, string outputDirectory)
{
    Console.WriteLine("Запускаю автоматизацию: P2P -> Купить -> Tether (USDT) -> СБП");

    var sequence = new[] { "P2P", "Купить", "Tether (USDT)", "СБП" };

    foreach (var expected in sequence)
    {
        var clicked = await ClickVisibleButtonByTextAsync(page, expected);
        if (!clicked)
        {
            Console.WriteLine($"Автоматизация остановлена: кнопка '{expected}' не найдена.");
            return;
        }

        Console.WriteLine($"Авто-нажатие: '{expected}' выполнено. Жду 3 сек...");
        await page.WaitForTimeoutAsync(3000);

        var stepScan = await CollectMenuDataAsync(page);
        PrintMenuToConsole(stepScan);
        await SaveSnapshotAsync(page, stepScan, outputDirectory);
    }

    var result = await CollectMenuDataAsync(page);
    var offers = ParseOffers(result);
    PrintBestOffer(offers);
}

static List<P2POffer> ParseOffers(JsonElement menu)
{
    var result = new List<P2POffer>();

    if (!menu.TryGetProperty("visibleButtons", out var buttons) || buttons.ValueKind != JsonValueKind.Array)
    {
        return result;
    }

    foreach (var button in buttons.EnumerateArray())
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
            // Формат без ника: "79.8₽ · 16.75K"
            seller = "(не указан)";
            rawPrice = parts[0];
            rawVolume = parts[1];
        }
        else
        {
            // Формат с ником: "Seller · 79.9₽ · 9.35K" либо "Seller · 80₽ · 7K - 7.50K"
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

static void PrintBestOffer(List<P2POffer> offers)
{
    if (offers.Count == 0)
    {
        Console.WriteLine("Подходящие P2P офферы не распознаны на текущем экране.");
        return;
    }

    var best = offers.OrderBy(x => x.Price).First();
    Console.WriteLine();
    Console.WriteLine("=== ЛУЧШЕЕ ПРЕДЛОЖЕНИЕ (минимальная цена) ===");
    Console.WriteLine($"Продавец: {best.Seller}");
    Console.WriteLine($"Цена: {best.RawPrice} (число: {best.Price.ToString(CultureInfo.InvariantCulture)})");
    Console.WriteLine($"Объем: {best.Volume}");
    if (best.VolumeMin is not null)
    {
        var maxPart = best.VolumeMax is not null ? $" - {best.VolumeMax.Value.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        Console.WriteLine($"Объем (число): {best.VolumeMin.Value.ToString(CultureInfo.InvariantCulture)}{maxPart}");
    }
    Console.WriteLine($"Сырая строка: {best.SourceLabel}");
    Console.WriteLine("=== КОНЕЦ ===");
}

static double? TryParseFlexibleNumber(string source)
{
    if (string.IsNullOrWhiteSpace(source))
    {
        return null;
    }

    var hasK = source.Contains('K', StringComparison.OrdinalIgnoreCase);
    var filtered = new string(source.Where(ch => char.IsDigit(ch) || ch == '.' || ch == ',').ToArray());
    if (string.IsNullOrWhiteSpace(filtered))
    {
        return null;
    }

    filtered = filtered.Replace(',', '.');
    if (!double.TryParse(filtered, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
    {
        return null;
    }

    return hasK ? value * 1000d : value;
}

static (double? Min, double? Max) TryParseVolumeRange(string rawVolume)
{
    if (string.IsNullOrWhiteSpace(rawVolume))
    {
        return (null, null);
    }

    var parts = rawVolume.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 1)
    {
        var single = TryParseFlexibleNumber(parts[0]);
        return (single, single);
    }

    var min = TryParseFlexibleNumber(parts[0]);
    var max = TryParseFlexibleNumber(parts[1]);
    return (min, max);
}

static async Task<bool> ClickVisibleButtonByTextAsync(IPage page, string expectedText)
{
    var target = await page.EvaluateAsync<ClickTarget?>("""
(expectedText) => {
  const normalize = (s) => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();

  const allVisibleButtons = Array.from(document.querySelectorAll('button, [role="button"], .reply-markup-button, .Button'))
    .filter((btn) => {
      const rect = btn.getBoundingClientRect();
      const style = getComputedStyle(btn);
      const visible = rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
      if (!visible) return false;
      const t = text(btn);
      return t.length > 0 || (btn.getAttribute('aria-label') || '').trim().length > 0;
    });

  const expected = normalize(expectedText);
  const hit = allVisibleButtons.find((btn) => normalize(text(btn)).includes(expected));
  if (!hit) return null;

  const rect = hit.getBoundingClientRect();
  return {
    x: rect.left + rect.width / 2,
    y: rect.top + rect.height / 2,
    label: text(hit)
  };
}
""", expectedText);

    if (target is null)
    {
        return false;
    }

    await page.Mouse.MoveAsync((float)target.X, (float)target.Y);
    await page.Mouse.DownAsync();
    await page.Mouse.UpAsync();
    return true;
}

static async Task<bool> ClickVisibleButtonByIndexAsync(IPage page, int displayIndex)
{
    var target = await page.EvaluateAsync<ClickTarget?>("""
(displayIndex) => {
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();
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
  const chosen = last30[displayIndex];
  if (!chosen) return null;

  const rect = chosen.btn.getBoundingClientRect();
  return {
    x: rect.left + rect.width / 2,
    y: rect.top + rect.height / 2,
    label: text(chosen.btn)
  };
}
""", displayIndex);

    if (target is null)
    {
        return false;
    }

    await page.Mouse.MoveAsync((float)target.X, (float)target.Y);
    await page.Mouse.DownAsync();
    await page.Mouse.UpAsync();
    Console.WriteLine($"Нажата кнопка [{displayIndex}] '{target.Label}'.");
    return true;
}

static async Task<JsonElement> CollectMenuDataAsync(IPage page)
{
    return await page.EvaluateAsync<JsonElement>("""
() => {
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();

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

  const messageAboveButtons = text(lastBubble?.querySelector('.message, .text-content, .translatable-message')) || text(lastBubble);
  const messageTime = text(lastBubble?.querySelector('time, .time, .message-time'));
  const activeChatTitle = text(document.querySelector('.chat-info .title, .chat-info-wrapper .title, .topbar .title, header .title'));

  return {
    extractedAt: new Date().toISOString(),
    activeChatTitle,
    messageAboveButtons,
    messageTime,
    totalVisibleButtonCount: allVisibleButtons.length,
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
    public required string Seller { get; init; }
    public required double Price { get; init; }
    public required string RawPrice { get; init; }
    public required string Volume { get; init; }
    public double? VolumeMin { get; init; }
    public double? VolumeMax { get; init; }
    public required string SourceLabel { get; init; }
}
