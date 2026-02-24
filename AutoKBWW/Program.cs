using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;

var outputDirectory = Path.Combine(AppContext.BaseDirectory, "output");
Directory.CreateDirectory(outputDirectory);

using var playwright = await Playwright.CreateAsync();

Console.WriteLine("Запускаю браузер...");
await using var browser = await LaunchChromiumWithAutoInstallAsync(playwright);

var context = await browser.NewContextAsync(new BrowserNewContextOptions
{
    ViewportSize = new ViewportSize
    {
        Width = 1440,
        Height = 900
    }
});

var page = await context.NewPageAsync();
await page.GotoAsync("https://web.telegram.org/k/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

Console.WriteLine("Открыл Telegram Web.");
Console.WriteLine("1) Войдите в аккаунт вручную.");
Console.WriteLine("2) Откройте нужного бота (например, @CryptoBot).");
Console.WriteLine("3) Нажмите ENTER здесь, когда будете готовы собрать данные...");
Console.ReadLine();

await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

var extractionResult = await page.EvaluateAsync("""
() => {
  const text = (el) => (el?.textContent || '').trim();
  const clean = (value) => value.replace(/\s+/g, ' ').trim();

  const chatItems = Array.from(document.querySelectorAll('.chatlist .chatlist-chat')).map((chat, index) => {
    const title = text(chat.querySelector('.user-title, .peer-title, .fullName'));
    const preview = text(chat.querySelector('.message, .last-message, .subtitle'));
    const time = text(chat.querySelector('time, .time, .message-date'));
    return {
      index,
      title: clean(title),
      preview: clean(preview),
      time: clean(time)
    };
  }).filter(chat => chat.title.length > 0);

  const messageNodes = Array.from(document.querySelectorAll('.bubble, .message'));
  const messages = messageNodes.map((msg, index) => {
    const messageText = text(msg.querySelector('.message, .text-content, .translatable-message')) || text(msg);
    const time = text(msg.querySelector('time, .time, .message-time'));
    const outgoing = msg.classList.contains('is-out') || msg.classList.contains('outgoing') || !!msg.closest('.is-out, .outgoing');

    return {
      index,
      direction: outgoing ? 'outgoing' : 'incoming',
      time: clean(time),
      text: clean(messageText)
    };
  }).filter(message => message.text.length > 0);

  const activeChatTitle = text(document.querySelector('.chat-info .title, .chat-info-wrapper .title, .topbar .title, header .title'));

  return {
    extractedAt: new Date().toISOString(),
    pageTitle: document.title,
    url: location.href,
    activeChatTitle: clean(activeChatTitle),
    chatCount: chatItems.length,
    messageCount: messages.length,
    chats: chatItems,
    messages
  };
}
""");

var options = new JsonSerializerOptions { WriteIndented = true };
var json = JsonSerializer.Serialize(extractionResult, options);

var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
var jsonPath = Path.Combine(outputDirectory, $"telegram_export_{timestamp}.json");
var htmlPath = Path.Combine(outputDirectory, $"telegram_snapshot_{timestamp}.html");

await File.WriteAllTextAsync(jsonPath, json);
await File.WriteAllTextAsync(htmlPath, await page.ContentAsync());

Console.WriteLine();
Console.WriteLine("Сбор данных завершён.");
Console.WriteLine($"JSON: {jsonPath}");
Console.WriteLine($"HTML snapshot: {htmlPath}");
Console.WriteLine();
Console.WriteLine("Нажмите ENTER для закрытия браузера...");
Console.ReadLine();

static async Task<IBrowser> LaunchChromiumWithAutoInstallAsync(IPlaywright playwright)
{
    var launchOptions = new BrowserTypeLaunchOptions
    {
        Headless = false,
        SlowMo = 80
    };

    try
    {
        return await playwright.Chromium.LaunchAsync(launchOptions);
    }
    catch (PlaywrightException ex) when (IsMissingBrowserExecutable(ex))
    {
        Console.WriteLine("Chromium для Playwright не найден. Пытаюсь установить автоматически...");

        var installExitCode = await InstallChromiumAsync();
        if (installExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Не удалось установить Chromium для Playwright (код выхода: {installExitCode}). " +
                "Запустите вручную: playwright install chromium", ex);
        }

        Console.WriteLine("Установка Chromium завершена. Повторный запуск браузера...");
        return await playwright.Chromium.LaunchAsync(launchOptions);
    }
}

static bool IsMissingBrowserExecutable(PlaywrightException ex)
{
    return ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase);
}

static async Task<int> InstallChromiumAsync()
{
    var runner = ResolvePlaywrightInstallCommand();

    var startInfo = new ProcessStartInfo
    {
        FileName = runner.FileName,
        Arguments = runner.Arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = new Process { StartInfo = startInfo };
    process.OutputDataReceived += (_, args) =>
    {
        if (!string.IsNullOrWhiteSpace(args.Data))
        {
            Console.WriteLine(args.Data);
        }
    };
    process.ErrorDataReceived += (_, args) =>
    {
        if (!string.IsNullOrWhiteSpace(args.Data))
        {
            Console.Error.WriteLine(args.Data);
        }
    };

    process.Start();
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    await process.WaitForExitAsync();

    return process.ExitCode;
}

static (string FileName, string Arguments) ResolvePlaywrightInstallCommand()
{
    if (OperatingSystem.IsWindows())
    {
        var localCmd = Path.Combine(AppContext.BaseDirectory, "playwright.cmd");
        if (File.Exists(localCmd))
        {
            return (localCmd, "install chromium");
        }

        return ("playwright.cmd", "install chromium");
    }

    var localSh = Path.Combine(AppContext.BaseDirectory, "playwright.sh");
    if (File.Exists(localSh))
    {
        return ("bash", $"\"{localSh}\" install chromium");
    }

    return ("playwright", "install chromium");
}

