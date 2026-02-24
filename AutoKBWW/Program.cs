using System.Text.Json;
using Microsoft.Playwright;

var outputDirectory = Path.Combine(AppContext.BaseDirectory, "output");
Directory.CreateDirectory(outputDirectory);

using var playwright = await Playwright.CreateAsync();

Console.WriteLine("Запускаю Яндекс Браузер...");
var yandexBrowserPath = ResolveYandexBrowserPath();
Console.WriteLine($"Использую browser executable: {yandexBrowserPath}");

await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = false,
    SlowMo = 60,
    ExecutablePath = yandexBrowserPath,
    Args =
    [
        "--start-maximized"
    ]
});

var context = await browser.NewContextAsync(new BrowserNewContextOptions
{
    ViewportSize = new ViewportSize
    {
        Width = 1440,
        Height = 900
    }
});

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

var extractionResult = await page.EvaluateAsync<JsonElement>("""
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
Console.WriteLine();
Console.WriteLine("Нажмите ENTER для закрытия браузера...");
Console.ReadLine();

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
