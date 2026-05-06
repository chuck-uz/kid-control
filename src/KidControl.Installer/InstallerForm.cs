using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Drawing.Drawing2D;
using KidControl.Installer.Controls;

namespace KidControl.Installer;

public sealed class InstallerForm : Form
{
    private static class Theme
    {
        public static readonly Color Primary = ColorTranslator.FromHtml("#0078D4");
        public static readonly Color PrimaryAlt = ColorTranslator.FromHtml("#6200EE");
        public static readonly Color BackgroundLight = ColorTranslator.FromHtml("#F3F3F3");
        public static readonly Color BackgroundDark = ColorTranslator.FromHtml("#202020");
        public static readonly Color SurfaceLight = Color.FromArgb(128, 255, 255, 255);
        public static readonly Color SurfaceDark = Color.FromArgb(128, 0, 0, 0);
        public static readonly Color TextPrimary = Color.Black;
        public static readonly Color TextSecondary = ColorTranslator.FromHtml("#666666");
    }

    private const string ServiceName = "KidControlv0.4";
    private const string InstallTaskName = "KidControl.UiHost.Launch";
    private static readonly string ProgramFilesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KidControl");
    private static readonly string ProgramDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KidControl");

    private const string GitHubRepo = "chuck-uz/kid-control";
    private const string AssetServiceHost = "KidControl.ServiceHost.exe";
    private const string AssetUiHost = "KidControl.UiHost.exe";

    /// <summary>RmShutdown часто возвращает ERROR_ACCESS_DENIED (5) в Program Files — не спамим вызовами.</summary>
    private bool _skipRestartManager;

    private readonly Panel _navPanel = new() { Left = 0, Top = 0, Width = 230, Height = 640 };
    private readonly Panel _contentPanel = new() { Left = 230, Top = 0, Width = 670, Height = 640, BackColor = Theme.BackgroundLight };
    private readonly StepNavItem[] _stepNav =
    {
        new() { Text = "1. Управление", Top = 120, Left = 16 },
        new() { Text = "2. Telegram",   Top = 166, Left = 16 },
        new() { Text = "3. Установка",  Top = 212, Left = 16 }
    };
    private readonly Panel[] _steps = new Panel[3];
    private bool _isInstalled;

    private readonly AccentButton _backButton = new() { Text = "Назад", Width = 110, Left = 250, Top = 560, AccentColor = Color.FromArgb(94, 94, 94) };
    private readonly AccentButton _nextButton = new() { Text = "Далее", Width = 110, Left = 370, Top = 560 };
    private int _currentStep;

    private readonly ModernTextBox _tokenText = new() { Left = 28, Top = 140, Width = 590 };
    private readonly ModernTextBox _chatIdsText = new() { Left = 28, Top = 230, Width = 590 };
    private readonly AccentButton _checkTelegramButton = new() { Text = "Проверить", Left = 28, Top = 292, Width = 140 };
    private readonly Label _telegramStatus = new() { Left = 186, Top = 304, Width = 420, AutoEllipsis = true, Font = new Font(InstallerFonts.MessageFontFamily, 10), ForeColor = Theme.TextSecondary };

    // Manage-step controls (shown when an existing installation is detected)
    private readonly Label _manageInstalledLabel = new() { Left = 28, Top = 130, Width = 590, Height = 28, AutoSize = false, Font = new Font(InstallerFonts.MessageFontFamily, 12f) };
    private readonly Label _manageLatestLabel    = new() { Left = 28, Top = 164, Width = 590, Height = 28, AutoSize = false, Font = new Font(InstallerFonts.MessageFontFamily, 12f) };
    private readonly AccentButton _manageUpdateButton    = new() { Text = "Обновить",       Left = 28,  Top = 214, Width = 155 };
    private readonly AccentButton _manageReinstallButton = new() { Text = "Переустановить", Left = 196, Top = 214, Width = 165, AccentColor = Color.FromArgb(94, 94, 94) };
    private readonly AccentButton _manageUninstallButton = new() { Text = "Удалить",        Left = 374, Top = 214, Width = 155, AccentColor = Color.FromArgb(176, 42, 55) };
    private readonly TextBox _manageLogBox = new() { Left = 28, Top = 278, Width = 590, Height = 195, Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.WhiteSmoke, Font = new Font("Consolas", 10f) };

    private readonly AccentButton _installButton = new() { Text = "Установить", Left = 28, Top = 120, Width = 190 };
    private readonly AccentButton _uninstallButton = new() { Text = "Удалить полностью", Left = 232, Top = 120, Width = 190, AccentColor = Color.FromArgb(176, 42, 55) };
    private readonly TextBox _logBox = new() { Left = 28, Top = 180, Width = 590, Height = 270, Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.WhiteSmoke, Font = new Font("Consolas", 10f) };

    public InstallerForm()
    {
        Text = "KidControl Installer";
        Width = 900;
        Height = 640;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = Theme.BackgroundLight;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        BuildShell();
        BuildManageStep();
        BuildTelegramStep();
        BuildInstallStep();
        Load += async (_, _) => await DetectAndOfferAsync();
        ShowStep(1); // default: start at Telegram; DetectAndOfferAsync moves to step 0 if installed
    }

    private void BuildShell()
    {
        _navPanel.BackColor = Theme.BackgroundDark;
        _navPanel.Paint += (_, e) =>
        {
            using var brush = new LinearGradientBrush(_navPanel.ClientRectangle, Color.FromArgb(44, 44, 44), Color.FromArgb(24, 24, 24), 90);
            e.Graphics.FillRectangle(brush, _navPanel.ClientRectangle);
        };
        _navPanel.Controls.Add(new Label
        {
            Text = "KidControl",
            Left = 16,
            Top = 28,
            Width = 180,
            Height = 36,
            Font = new Font(InstallerFonts.MessageFontFamily, 22f, FontStyle.Bold),
            ForeColor = Color.White
        });
        _navPanel.Controls.Add(new Label
        {
            Text = "Windows 11 Installer",
            Left = 18,
            Top = 70,
            Width = 190,
            Height = 24,
            Font = new Font(InstallerFonts.MessageFontFamily, 10f, FontStyle.Regular),
            ForeColor = Color.FromArgb(180, 210, 242)
        });
        foreach (var item in _stepNav)
        {
            _navPanel.Controls.Add(item);
        }

        _contentPanel.Controls.Add(_backButton);
        _contentPanel.Controls.Add(_nextButton);
        _backButton.Click += (_, _) => MoveStep(-1);
        _nextButton.Click += (_, _) => MoveStep(1);

        Controls.Add(_navPanel);
        Controls.Add(_contentPanel);
    }

    private void BuildManageStep()
    {
        var panel = NewStepPanel();
        AddStepHeader(panel, "Управление KidControl", "Обнаружена существующая установка.");
        panel.Controls.Add(_manageInstalledLabel);
        panel.Controls.Add(_manageLatestLabel);
        panel.Controls.Add(_manageUpdateButton);
        panel.Controls.Add(_manageReinstallButton);
        panel.Controls.Add(_manageUninstallButton);
        panel.Controls.Add(_manageLogBox);
        _manageUpdateButton.Click    += async (_, _) => await HandleManageUpdateAsync();
        _manageReinstallButton.Click += async (_, _) => await HandleManageReinstallAsync();
        _manageUninstallButton.Click += async (_, _) => await HandleManageUninstallAsync();
        _steps[0] = panel;
    }

    private void BuildTelegramStep()
    {
        var panel = NewStepPanel();
        AddStepHeader(panel, "Настройка Telegram", "Свяжите систему с ботом и списком администраторов.");
        panel.Controls.Add(new Label { Left = 28, Top = 116, Width = 400, Text = "Bot Token", Font = new Font(InstallerFonts.MessageFontFamily, 10.5f) });
        panel.Controls.Add(_tokenText);
        panel.Controls.Add(new Label { Left = 28, Top = 208, Width = 590, Text = "Admin Chat IDs (введите через запятую)", Font = new Font(InstallerFonts.MessageFontFamily, 10.5f) });
        panel.Controls.Add(_chatIdsText);
        panel.Controls.Add(_checkTelegramButton);
        panel.Controls.Add(_telegramStatus);
        panel.Controls.Add(CreateIllustrationPanel("Telegram"));
        _checkTelegramButton.Click += async (_, _) => await CheckTelegramAsync();
        _steps[1] = panel;
    }

    private void BuildInstallStep()
    {
        var panel = NewStepPanel();
        AddStepHeader(panel, "Установка и удаление", "Запустите установку или полное удаление. Логи службы: папка ProgramData\\KidControl\\logs");
        panel.Controls.Add(_installButton);
        panel.Controls.Add(_uninstallButton);
        panel.Controls.Add(_logBox);
        _installButton.Click   += async (_, _) => await InstallAsync();
        _uninstallButton.Click += async (_, _) => await UninstallAsync();
        _steps[2] = panel;
    }

    private Panel NewStepPanel()
    {
        var panel = new Panel { Left = 0, Top = 0, Width = 670, Height = 560, Visible = false, BackColor = Theme.BackgroundLight };
        _contentPanel.Controls.Add(panel);
        return panel;
    }

    private static void AddStepHeader(Panel panel, string title, string subtitle)
    {
        panel.Controls.Add(new Label
        {
            Left = 28,
            Top = 26,
            Width = 610,
            Height = 42,
            Font = new Font(InstallerFonts.MessageFontFamily, 22f, FontStyle.Bold),
            Text = title
        });
        panel.Controls.Add(new Label
        {
            Left = 30,
            Top = 70,
            Width = 610,
            Height = 28,
            Font = new Font(InstallerFonts.MessageFontFamily, 10.5f, FontStyle.Regular),
            ForeColor = Theme.TextSecondary,
            Text = subtitle
        });
    }

    private static Panel CreateIllustrationPanel(string mode)
    {
        var panel = new Panel { Left = 28, Top = 340, Width = 590, Height = 170, BackColor = Color.Transparent };
        panel.Paint += (_, e) =>
        {
            var rect = panel.ClientRectangle;
            using var bg = new LinearGradientBrush(rect, Color.FromArgb(216, 234, 250), Color.FromArgb(231, 224, 255), 20);
            using var cardPath = RoundedRect(rect, 12);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(bg, cardPath);
            using var border = new Pen(Color.FromArgb(120, 255, 255, 255), 1f);
            e.Graphics.DrawPath(border, cardPath);

            var iconRect = new Rectangle(28, 35, 68, 68);
            using var accent = new SolidBrush(Theme.Primary);
            e.Graphics.FillEllipse(accent, iconRect);
            using var whitePen = new Pen(Color.White, 3f);
            if (mode == "Telegram")
            {
                e.Graphics.DrawLine(whitePen, 45, 67, 75, 49);
                e.Graphics.DrawLine(whitePen, 75, 49, 61, 82);
            }
            else if (mode == "Shield")
            {
                e.Graphics.DrawArc(whitePen, 45, 54, 32, 30, 180, 180);
                e.Graphics.DrawLine(whitePen, 45, 69, 61, 88);
                e.Graphics.DrawLine(whitePen, 77, 69, 61, 88);
            }
            else
            {
                e.Graphics.DrawEllipse(whitePen, 47, 50, 28, 28);
                e.Graphics.DrawLine(whitePen, 61, 64, 61, 54);
                e.Graphics.DrawLine(whitePen, 61, 64, 71, 68);
            }

            using var textBrush = new SolidBrush(Color.FromArgb(43, 43, 43));
            using var titleFont = new Font(InstallerFonts.MessageFontFamily, 14f, FontStyle.Bold);
            using var bodyFont = new Font(InstallerFonts.MessageFontFamily, 10.5f, FontStyle.Regular);
            var title = mode switch
            {
                "Telegram" => "Подключение к Telegram",
                "Shield" => "Защита состояния",
                _ => "Контроль установки"
            };
            var body = mode switch
            {
                "Telegram" => "Проверьте токен и список администраторов перед финальной установкой.",
                "Shield" => "Опции безопасности применяются автоматически при создании сервиса.",
                _ => "Мастер установки поддерживает полную установку и безопасное удаление."
            };
            e.Graphics.DrawString(title, titleFont, textBrush, new PointF(120, 44));
            e.Graphics.DrawString(body, bodyFont, textBrush, new RectangleF(120, 76, 430, 70));
        };
        return panel;
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void MoveStep(int delta)
    {
        var firstVisible = _isInstalled ? 0 : 1;
        var next = Math.Clamp(_currentStep + delta, firstVisible, _steps.Length - 1);
        ShowStep(next);
    }

    private void ShowStep(int index)
    {
        _currentStep = index;
        for (var i = 0; i < _steps.Length; i++)
        {
            _steps[i].Visible = i == index;
        }

        // Step 0 (manage): no Back/Next — actions are self-contained buttons.
        // When not installed, step 1 is the first visible step so Back is hidden there too.
        var firstVisible = _isInstalled ? 0 : 1;
        var lastVisible  = _steps.Length - 1;
        _backButton.Enabled = index > firstVisible;
        _backButton.Visible = index > firstVisible;
        _nextButton.Enabled = index < lastVisible;
        _nextButton.Visible = index < lastVisible;

        for (var i = 0; i < _stepNav.Length; i++)
        {
            _stepNav[i].IsActive = i == index;
        }
    }

    private async Task CheckTelegramAsync()
    {
        _telegramStatus.Text = "Проверка...";
        var token = _tokenText.Text.Trim();
        var chatIds = ParseChatIds(_chatIdsText.Text);
        if (string.IsNullOrWhiteSpace(token) || chatIds.Count == 0)
        {
            _telegramStatus.Text = "Введите Bot Token и хотя бы один Chat ID.";
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            foreach (var chatId in chatIds)
            {
                var payload = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["chat_id"] = chatId.ToString(),
                    ["text"] = "Попытка установки системы..."
                });
                var response = await http.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", payload);
                response.EnsureSuccessStatusCode();
            }

            _telegramStatus.Text = "Проверка успешна: сообщение отправлено.";
        }
        catch (Exception ex)
        {
            _telegramStatus.Text = $"Ошибка проверки: {ex.Message}";
        }
    }

    private async Task InstallAsync()
    {
        if (!EnsureAdmin())
        {
            return;
        }

        try
        {
            Log("Начинаю установку...");
            var tempPath = Path.Combine(Path.GetTempPath(), $"KidControlInstaller-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempPath);

            Log("Получаю последнюю версию с GitHub...");
            var tag = await GetLatestGitHubTagAsync()
                      ?? throw new InvalidOperationException("Не удалось определить последнюю версию на GitHub. Проверьте подключение к интернету.");
            await DownloadPayloadAsync(tag, tempPath);
            Directory.CreateDirectory(ProgramFilesPath);

            StopAndDeleteService(ServiceName);
            KillAllKidControlProcessesBlockingUninstall();
            Thread.Sleep(500);

            // Старые версии оставляли только SYSTEM на ProgramData\KidControl — администратор не мог создать logs.
            if (Directory.Exists(ProgramDataPath))
            {
                GrantAdministratorsAndSystemRecursive(ProgramDataPath);
            }

            File.Copy(Path.Combine(tempPath, "KidControl.ServiceHost.exe"), Path.Combine(ProgramFilesPath, "KidControl.ServiceHost.exe"), true);
            File.Copy(Path.Combine(tempPath, "KidControl.UiHost.exe"), Path.Combine(ProgramFilesPath, "KidControl.UiHost.exe"), true);

            EnsureProgramDataFolderProtected();
            Log("Папка ProgramData создана и защищена.");

            EnsureProgramDataLogsDirectory();
            ResetPersistedSessionState();
            WriteAppSettings();
            Log("Файлы установлены.");

            // Register and start the service before locking the folder ACL
            // so the service binary can be accessed by the SCM during first start.
            RegisterService();
            StartService();
            Log("Служба зарегистрирована и запущена.");

            LockInstallFolderAcl();
            Log($"Логи службы и UI: {Path.Combine(ProgramDataPath, "logs")}");

            RegisterUiTask();
            RegisterServiceRestartTask();
            Log("Задачи планировщика для UI и перезапуска службы обновлены.");

            ProtectServiceRegistryKey();
            Log("Ключ реестра службы защищён.");

            HideFromUninstallList();
            Log("Приложение скрыто из списка установленных программ.");

            // Пытаемся поднять UI сразу после установки, чтобы не ждать следующего входа пользователя.
            TryLaunchUiInUserSession();
            Log("Попытка запуска UI выполнена.");

            Directory.Delete(tempPath, true);
            Log("Установка завершена.");
        }
        catch (Exception ex)
        {
            Log($"Ошибка установки: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private async Task UninstallAsync()
    {
        if (!EnsureAdmin())
        {
            return;
        }

        TryEnableSeDebugPrivilege();

        try
        {
            Log("Начинаю полное удаление…");
            _skipRestartManager = false;
            if (IsInstallerRunningFromInstallFolder())
            {
                Log("Внимание: инсталлятор запущен из папки установки — скопируйте .exe в другое место (например Рабочий стол) и удалите снова, иначе Windows не даст удалить каталог.");
                MessageBox.Show(
                    this,
                    "Инсталлятор запущен из C:\\Program Files\\KidControl. Скопируйте KidControl.Installer.exe на Рабочий стол или во временную папку и запустите удаление оттуда — иначе папка установки остаётся заблокированной.",
                    "KidControl",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            UnprotectServiceRegistryKey();
            StopAndDeleteService(ServiceName);
            DeleteUiTask();
            DeleteServiceRestartTask();
            RemoveFromUninstallList();
            KillAllKidControlProcessesBlockingUninstall();
            Thread.Sleep(1200);
            KillAllKidControlProcessesBlockingUninstall();
            Thread.Sleep(800);

            if (Directory.Exists(ProgramDataPath))
            {
                RelaxProgramDataAcl();
            }

            if (Directory.Exists(ProgramFilesPath))
            {
                UnlockInstallFolderAcl();
                Log("Restart Manager: снятие блокировок с каталога установки…");
                MaybeRestartManagerRelease(ProgramFilesPath);
                Thread.Sleep(1500);
            }

            if (Directory.Exists(ProgramFilesPath))
            {
                TryDeleteDirectoryWithRetry(ProgramFilesPath);
            }

            if (Directory.Exists(ProgramDataPath))
            {
                TryDeleteDirectoryWithRetry(ProgramDataPath);
            }

            Log("Удаление завершено.");
        }
        catch (Exception ex)
        {
            Log($"Ошибка удаления: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    // ─── GitHub helpers ────────────────────────────────────────────────────────

    private static string GetInstalledVersion(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            return info.ProductVersion ?? info.FileVersion ?? "неизвестно";
        }
        catch
        {
            return "неизвестно";
        }
    }

    private static async Task<string?> GetLatestGitHubTagAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "KidControl-Installer");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tag_name", out var tag))
                return tag.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Downloads ServiceHost.exe and UiHost.exe for the given release tag into destDir.
    /// Falls back to files next to the installer (offline usage) before attempting GitHub.
    /// </summary>
    private async Task DownloadPayloadAsync(string tag, string destDir)
    {
        var svcDest = Path.Combine(destDir, AssetServiceHost);
        var uiDest  = Path.Combine(destDir, AssetUiHost);

        // Offline fallback: files sitting next to the installer exe.
        if (TryCopyPayloadFromDisk(AssetServiceHost, svcDest) && TryCopyPayloadFromDisk(AssetUiHost, uiDest))
        {
            Log("Файлы найдены рядом с установщиком (офлайн-режим).");
            return;
        }

        Log($"Скачиваю бинарники версии {tag} с GitHub...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "KidControl-Installer");

        var releaseJson = await http.GetStringAsync(
            $"https://api.github.com/repos/{GitHubRepo}/releases/tags/{tag}");
        using var doc = JsonDocument.Parse(releaseJson);
        var assets = doc.RootElement.GetProperty("assets");

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            var url  = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;

            string? destFile = null;
            if (name == AssetServiceHost) destFile = svcDest;
            else if (name == AssetUiHost) destFile = uiDest;
            else continue;

            Log($"Скачиваю {name}...");
            var bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(destFile, bytes);
            Log($"{name} скачан ({bytes.Length / 1024} КБ).");
        }

        if (!File.Exists(svcDest))
            throw new InvalidOperationException($"Не удалось получить {AssetServiceHost} из релиза {tag}.");
        if (!File.Exists(uiDest))
            throw new InvalidOperationException($"Не удалось получить {AssetUiHost} из релиза {tag}.");
    }

    private static bool TryCopyPayloadFromDisk(string fileName, string destinationPath)
    {
        static void AddUnique(List<string> list, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(path);
                if (!list.Exists(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(full);
                }
            }
            catch
            {
                // ignore invalid paths
            }
        }

        var searchRoots = new List<string>();
        AddUnique(searchRoots, AppContext.BaseDirectory);
        try
        {
            AddUnique(searchRoots, Path.GetDirectoryName(Environment.ProcessPath));
        }
        catch
        {
            // Environment.ProcessPath unavailable on some hosts
        }

        AddUnique(searchRoots, Path.GetDirectoryName(Application.ExecutablePath));
        AddUnique(searchRoots, Directory.GetCurrentDirectory());

        // Walk up from each root (USB / copied folder / Build/v0.4 / etc.)
        var expandedRoots = new List<string>();
        foreach (var root in searchRoots.ToArray())
        {
            var walk = root;
            for (var i = 0; i < 10 && !string.IsNullOrEmpty(walk); i++)
            {
                AddUnique(expandedRoots, walk);
                walk = Directory.GetParent(walk)?.FullName ?? string.Empty;
            }
        }

        var candidates = new List<string>();
        foreach (var root in expandedRoots)
        {
            candidates.Add(Path.Combine(root, "Artifacts", fileName));
            candidates.Add(Path.Combine(root, fileName));
        }

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "src", "KidControl.Installer", "Artifacts", fileName));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "publish", "ServiceHost", fileName));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "publish", "UiHost", fileName));
        var sourcePath = candidates.FirstOrDefault(File.Exists);
        if (sourcePath is null)
        {
            return false;
        }

        File.Copy(sourcePath, destinationPath, true);
        return true;
    }

    private void WriteAppSettings()
    {
        var token = _tokenText.Text.Trim();
        var chatIds = ParseChatIds(_chatIdsText.Text);
        var logDir = Path.Combine(ProgramDataPath, "logs");
        var textLog = Path.Combine(logDir, "kidcontrol-.log");
        var jsonLog = Path.Combine(logDir, "kidcontrol-ai-.json");
        var model = new
        {
            TelegramConfig = new
            {
                BotToken = token,
                AdminChatIds = chatIds,
                NightModeStart = "22:00",
                NightModeEnd = "07:00"
            },
            Protection = new
            {
                CriticalProcess = true
            },
            UpdateConfig = new
            {
                Repository = "chuck-uz/kid-control",
                CheckIntervalHours = 6,
                AutoCheckEnabled = true,
                AssetName = "KidControl.Installer.exe"
            },
            Serilog = new
            {
                Using = new[] { "Serilog.Sinks.Console", "Serilog.Sinks.File", "Serilog.Formatting.Compact" },
                MinimumLevel = new
                {
                    Default = "Information",
                    Override = new { Microsoft = "Warning", System = "Warning" }
                },
                WriteTo = new object[]
                {
                    new { Name = "Console" },
                    new { Name = "File", Args = new { path = textLog, rollingInterval = "Day", retainedFileCountLimit = 14, shared = true } },
                    new { Name = "File", Args = new { path = jsonLog, rollingInterval = "Day", retainedFileCountLimit = 14, shared = true, formatter = "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact" } }
                },
                Enrich = new[] { "FromLogContext" }
            }
        };

        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
        // Write to ProgramData (protected folder) so children cannot delete the config.
        File.WriteAllText(Path.Combine(ProgramDataPath, "appsettings.json"), json, Encoding.UTF8);
    }

    private static void EnsureProgramDataLogsDirectory()
    {
        Directory.CreateDirectory(Path.Combine(ProgramDataPath, "logs"));
    }

    /// <summary>
    /// Seed default state only when it does not exist (first install).
    /// Existing state must survive updates/reboots.
    /// </summary>
    private void ResetPersistedSessionState()
    {
        try
        {
            var statePath = Path.Combine(ProgramDataPath, "session_state.json");
            if (File.Exists(statePath))
            {
                Log("Сохраненное состояние таймера найдено и сохранено (session_state.json).");
                return;
            }

            var freshState = """
            {
              "timeRemaining": "00:40:00",
              "currentStatus": 0,
              "lastUpdateTimestamp": "__NOW__",
              "playMinutes": 40,
              "restMinutes": 20,
              "nightModeStart": "22:00:00",
              "nightModeEnd": "07:00:00"
            }
            """.Replace("__NOW__", DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

            File.WriteAllText(statePath, freshState, Encoding.UTF8);
            Log("Инициализировано стартовое состояние таймера (первый запуск): Active 40/20.");
        }
        catch (Exception ex)
        {
            Log($"Не удалось сбросить сохраненное состояние таймера: {ex.Message}");
        }
    }

    /// <summary>
    /// Try several launch paths because elevated context and session isolation can block a direct start.
    /// </summary>
    private void TryLaunchUiInUserSession()
    {
        var uiExe = Path.Combine(ProgramFilesPath, "KidControl.UiHost.exe");
        if (!File.Exists(uiExe))
        {
            MessageBox.Show(this, "Сначала установите файлы (кнопка «Установить»).", "KidControl", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Use the scheduled task (already registered with /RL LIMITED) — this is the
        // only reliable way to launch a non-elevated process from an elevated installer.
        if (TryRunUiTaskNow())
        {
            return;
        }

        // Last resort: direct shell execute.
        TryStartUiViaShell(uiExe);
    }

    private bool TryStartUiViaShell(string uiExe)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uiExe,
                UseShellExecute = true,
                WorkingDirectory = ProgramFilesPath
            });
            Log("Запуск UI: прямой shell start выполнен.");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Запуск UI (shell) не удался: {ex.Message}");
            return false;
        }
    }

    private bool TryStartUiViaExplorer(string uiExe)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{uiExe}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = ProgramFilesPath
            });
            Log("Запуск UI: через explorer.exe выполнен.");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Запуск UI (explorer) не удался: {ex.Message}");
            return false;
        }
    }

    private bool TryRunUiTaskNow()
    {
        try
        {
            Run("schtasks.exe", $"/Run /TN \"{InstallTaskName}\"", false);
            Log("Запуск UI: отправлена команда schtasks /Run.");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Запуск UI через задачу не удался: {ex.Message}");
            return false;
        }
    }

    private static List<long> ParseChatIds(string input)
    {
        return input
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => long.TryParse(part, out var id) ? id : 0)
            .Where(id => id != 0)
            .Distinct()
            .ToList();
    }

    private void EnsureProgramDataFolderProtected()
    {
        Directory.CreateDirectory(ProgramDataPath);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        void addFull(SecurityIdentifier sid)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        addFull(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        // Инсталлятор и обслуживание идут от имени встроенных администраторов, не как SYSTEM.
        addFull(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        new DirectoryInfo(ProgramDataPath).SetAccessControl(security);
    }

    private void RelaxProgramDataAcl()
    {
        if (!Directory.Exists(ProgramDataPath))
        {
            return;
        }

        GrantAdministratorsAndSystemRecursive(ProgramDataPath);
    }

    /// <summary>
    /// Восстанавливает доступ администраторов и SYSTEM ко всему дереву (нужно после старых ACL только с SYSTEM и для удаления).
    /// </summary>
    private static void GrantAdministratorsAndSystemRecursive(string rootPath)
    {
        try
        {
            RunExternal(
                "icacls.exe",
                $"\"{rootPath}\" /grant \"*S-1-5-32-544:(OI)(CI)F\" /T /C",
                waitMs: 120_000);
            RunExternal(
                "icacls.exe",
                $"\"{rootPath}\" /grant \"*S-1-5-18:(OI)(CI)F\" /T /C",
                waitMs: 120_000);
        }
        catch
        {
            // icacls может вернуть ненулевой код при частично заблокированных файлах (/C продолжает) — всё равно продолжаем.
        }
    }

    private static void RunExternal(string fileName, string arguments, int waitMs = 30_000)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        });
        process?.WaitForExit(waitMs);
    }

    private void TryDeleteDirectoryWithRetry(string path, int attempts = 4, int delayMs = 900)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, true);
                return;
            }
            catch (IOException)
            {
                if (i >= attempts - 1) break;
                Log($"Каталог занят процессом… ({i + 1}/{attempts}) — повтор через {delayMs} мс");
                if (i >= 1)
                {
                    TakeOwnershipAndResetAttributes(path);
                }

                Thread.Sleep(delayMs);
            }
            catch (UnauthorizedAccessException)
            {
                if (i >= attempts - 1) break;
                Log($"Нет доступа к каталогу… ({i + 1}/{attempts}) — повтор через {delayMs} мс");
                TakeOwnershipAndResetAttributes(path);
                Thread.Sleep(delayMs);
            }
        }

        TrySalvageDeleteDirectory(path);
    }

    /// <summary>
    /// Последняя попытка: Restart Manager, процессы, rd /s /q, затем удаление файлов после перезагрузки.
    /// </summary>
    private void TrySalvageDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Log("Финальная очистка каталога (владение, права, процессы)…");
        TakeOwnershipAndResetAttributes(path);
        MaybeRestartManagerRelease(path);
        KillAllKidControlProcessesBlockingUninstall();
        Thread.Sleep(2000);

        try
        {
            Directory.Delete(path, true);
            return;
        }
        catch (Exception ex)
        {
            Log($"Directory.Delete не удался: {ex.Message}");
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        Run("cmd.exe", $"/c rd /s /q \"{path}\"", false);
        Thread.Sleep(500);

        if (!Directory.Exists(path))
        {
            return;
        }

        TakeOwnershipAndResetAttributes(path);
        Log("Планирую удаление файлов после перезагрузки Windows (MoveFileEx)…");
        if (InstallFolderUnlock.TryScheduleRecursiveDeleteAtNextBoot(path, Log))
        {
            var where = string.Equals(path, ProgramFilesPath, StringComparison.OrdinalIgnoreCase)
                ? "каталог установки в Program Files"
                : "каталог данных в ProgramData";
            MessageBox.Show(
                this,
                "Каталог всё ещё занят (часто Проводник, индекс поиска или антивирус). "
                + "Файлы KidControl помечены на удаление при следующей перезагрузке. "
                + $"Перезагрузите компьютер — {where} обычно пропадает.",
                "KidControl",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex)
        {
            Log($"Каталог не удалось стереть сейчас: {path}. После перезагрузки удалите вручную или снова нажмите «Удалить полностью». {ex.Message}");
            MessageBox.Show(
                this,
                "Не удалось ни удалить каталог сразу, ни пометить все файлы на удаление при перезагрузке. "
                + "Закройте окно Проводника с этой папкой, перезагрузите ПК и повторите удаление либо удалите папку вручную.",
                "KidControl",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void MaybeRestartManagerRelease(string path)
    {
        if (_skipRestartManager)
        {
            return;
        }

        var code = InstallFolderUnlock.TryRestartManagerForceRelease(path, Log);
        if (code == 5)
        {
            _skipRestartManager = true;
            Log("Restart Manager больше не вызывается в этой сессии (RmShutdown: отказ в доступе — типично для Program Files).");
        }
    }

    private void TakeOwnershipAndResetAttributes(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        Log("Владение и права: takeown + icacls + снятие read-only…");
        Run("takeown.exe", $"/F \"{path}\" /R /D Y", false);
        Run("icacls.exe", $"\"{path}\" /grant \"*S-1-5-32-544:(OI)(CI)F\" /T /C", false);
        Run("icacls.exe", $"\"{path}\" /grant \"*S-1-5-18:(OI)(CI)F\" /T /C", false);
        Run("cmd.exe", $"/c attrib -R -S -H \"{path}\" /S /D", false);
    }

    private bool IsInstallerRunningFromInstallFolder()
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (string.IsNullOrEmpty(exeDir))
            {
                return false;
            }

            return string.Equals(
                Path.TrimEndingDirectorySeparator(exeDir),
                Path.TrimEndingDirectorySeparator(ProgramFilesPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void RegisterService()
    {
        var serviceExe = Path.Combine(ProgramFilesPath, "KidControl.ServiceHost.exe");
        Run("sc.exe", $"create {ServiceName} binPath= \"\\\"{serviceExe}\\\"\" start= auto obj= LocalSystem", true);
        Run("sc.exe", $"failure {ServiceName} reset= 0 actions= restart/1000/restart/1000/restart/1000", false);
    }

    private void StartService()
    {
        Run("sc.exe", $"start {ServiceName}", false);
    }

    private void StopAndDeleteService(string serviceName)
    {
        Run("sc.exe", $"stop {serviceName}", false);
        WaitForServiceStoppedOrMissing(serviceName, TimeSpan.FromSeconds(60));
        Run("sc.exe", $"delete {serviceName}", false);
    }

    private static void WaitForServiceStoppedOrMissing(string serviceName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query {serviceName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                Thread.Sleep(400);
                continue;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            if (process.ExitCode != 0 && stdout.Contains("1060", StringComparison.Ordinal))
            {
                return;
            }

            if (stdout.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Thread.Sleep(500);
        }
    }

    private void RegisterUiTask()
    {
        var uiPath = Path.Combine(ProgramFilesPath, "KidControl.UiHost.exe");
        Run("schtasks.exe", BuildSchtasksCreateUiLaunchArgs(InstallTaskName, uiPath), true);
    }

    /// <summary>
    /// Builds schtasks /Create arguments. Do not use /RU INTERACTIVE — it is rejected on modern Windows (exit code 1).
    /// Keep /TR quoting exactly as schtasks expects for paths with spaces.
    /// </summary>
    private static string BuildSchtasksCreateUiLaunchArgs(string taskName, string uiExePath)
    {
        var escapedExe = uiExePath.Replace("\"", "\\\"", StringComparison.Ordinal);
        // schtasks expects nested quotes in /TR for executable paths with spaces.
        var tr = $"\\\"{escapedExe}\\\"";
        return $"/Create /TN \"{taskName}\" /TR \"{tr}\" /SC ONLOGON /RL LIMITED /F";
    }

    private void DeleteUiTask()
    {
        Run("schtasks.exe", $"/Delete /TN \"{InstallTaskName}\" /F", false);
    }

    // ─── Protection helpers ────────────────────────────────────────────────

    /// <summary>
    /// Locks C:\Program Files\KidControl so that only SYSTEM has write/delete rights.
    /// Administrators and Users retain Read+Execute only.
    /// Uses the .NET DirectorySecurity API with well-known SIDs to avoid icacls
    /// locale/SID-resolution issues that silently leave only SYSTEM:F on files.
    /// </summary>
    private void LockInstallFolderAcl()
    {
        try
        {
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var usersSid  = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            ApplyAclToDirectoryTree(ProgramFilesPath, systemSid, adminsSid, usersSid);
            Log("Права на папку установки ограничены (только SYSTEM может изменять файлы).");
        }
        catch (Exception ex)
        {
            Log($"Предупреждение: не удалось установить ACL на папку установки: {ex.Message}");
        }
    }

    private static void ApplyAclToDirectoryTree(
        string root,
        SecurityIdentifier systemSid,
        SecurityIdentifier adminsSid,
        SecurityIdentifier usersSid)
    {
        // Apply to the root folder itself.
        var dirInfo = new DirectoryInfo(root);
        var dirSec = dirInfo.GetAccessControl();
        dirSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        dirSec.AddAccessRule(new FileSystemAccessRule(systemSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        dirSec.AddAccessRule(new FileSystemAccessRule(adminsSid,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        dirSec.AddAccessRule(new FileSystemAccessRule(usersSid,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        dirInfo.SetAccessControl(dirSec);

        // Apply to every file recursively (files don't inherit automatically when
        // inheritance is disabled on the folder with SetAccessRuleProtection).
        foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                var fileSec = file.GetAccessControl();
                fileSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                fileSec.AddAccessRule(new FileSystemAccessRule(systemSid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                fileSec.AddAccessRule(new FileSystemAccessRule(adminsSid,
                    FileSystemRights.ReadAndExecute,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                fileSec.AddAccessRule(new FileSystemAccessRule(usersSid,
                    FileSystemRights.ReadAndExecute,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                file.SetAccessControl(fileSec);
            }
            catch { /* skip locked files (e.g. running service EXE) */ }
        }
    }

    /// <summary>
    /// Restores full-control access for Administrators so the uninstaller can delete files.
    /// </summary>
    private void UnlockInstallFolderAcl()
    {
        try
        {
            var dirInfo = new DirectoryInfo(ProgramFilesPath);
            if (!dirInfo.Exists) return;

            var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            void grantFull(FileSystemSecurity sec, bool isDir)
            {
                sec.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
                sec.AddAccessRule(new FileSystemAccessRule(adminsSid,
                    FileSystemRights.FullControl,
                    isDir
                        ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                        : InheritanceFlags.None,
                    PropagationFlags.None, AccessControlType.Allow));
            }

            var dirSec = dirInfo.GetAccessControl();
            grantFull(dirSec, isDir: true);
            dirInfo.SetAccessControl(dirSec);

            foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    var fileSec = file.GetAccessControl();
                    grantFull(fileSec, isDir: false);
                    file.SetAccessControl(fileSec);
                }
                catch { /* skip locked files */ }
            }

            Log("ACL на папку установки снят для удаления.");
        }
        catch (Exception ex)
        {
            Log($"Предупреждение: не удалось снять ACL папки установки: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a scheduled task KidControl.Service.Restart that runs as SYSTEM and
    /// starts the service via sc.exe. UiHost's ServiceWatchdog triggers this task
    /// if it detects the service process is gone.
    /// </summary>
    private void RegisterServiceRestartTask()
    {
        const string restartTaskName = "KidControl.Service.Restart";
        var args = $"/Create /TN \"{restartTaskName}\" /TR \"sc.exe start {ServiceName}\" /SC ONDEMAND /RU SYSTEM /RL HIGHEST /F";
        Run("schtasks.exe", args, false);
    }

    private void DeleteServiceRestartTask()
    {
        Run("schtasks.exe", "/Delete /TN \"KidControl.Service.Restart\" /F", false);
    }

    /// <summary>
    /// Writes registry values that hide KidControl from "Apps &amp; Features" /
    /// "Add or Remove Programs". SystemComponent=1 hides from the modern Settings
    /// app; NoRemove and NoModify disable the Remove/Change buttons in classic
    /// Control Panel.
    /// </summary>
    private void HideFromUninstallList()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\KidControl",
                writable: true);
            if (key is null) return;
            key.SetValue("DisplayName", "KidControl", Microsoft.Win32.RegistryValueKind.String);
            key.SetValue("SystemComponent", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("NoRemove", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            Log("Запись об установке скрыта из списка программ.");
        }
        catch (Exception ex)
        {
            Log($"Предупреждение: не удалось скрыть из списка программ: {ex.Message}");
        }
    }

    private void RemoveFromUninstallList()
    {
        try
        {
            Microsoft.Win32.Registry.LocalMachine.DeleteSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\KidControl",
                throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Log($"Предупреждение: не удалось удалить запись из реестра: {ex.Message}");
        }
    }

    /// <summary>
    /// Restricts the service registry key so ordinary users and non-SYSTEM admins
    /// cannot delete or modify the service registration.
    /// </summary>
    private void ProtectServiceRegistryKey()
    {
        try
        {
            var keyPath = $@"SYSTEM\CurrentControlSet\Services\{ServiceName}";
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
            if (key is null)
            {
                Log($"Предупреждение: ключ реестра службы не найден для защиты: {keyPath}");
                return;
            }

            var acl = key.GetAccessControl();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var system = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
            var admins = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);

            acl.AddAccessRule(new System.Security.AccessControl.RegistryAccessRule(
                system,
                System.Security.AccessControl.RegistryRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));

            acl.AddAccessRule(new System.Security.AccessControl.RegistryAccessRule(
                admins,
                System.Security.AccessControl.RegistryRights.ReadKey,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));

            key.SetAccessControl(acl);
            Log("Ключ реестра службы защищён (только SYSTEM может изменять).");
        }
        catch (Exception ex)
        {
            Log($"Предупреждение: не удалось защитить ключ реестра службы: {ex.Message}");
        }
    }

    /// <summary>
    /// Restores inherited ACL on the service registry key so sc.exe can delete it
    /// during uninstall.
    /// </summary>
    private void UnprotectServiceRegistryKey()
    {
        try
        {
            var keyPath = $@"SYSTEM\CurrentControlSet\Services\{ServiceName}";
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
            if (key is null) return;

            var acl = key.GetAccessControl();
            acl.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);

            var admins = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            acl.AddAccessRule(new System.Security.AccessControl.RegistryAccessRule(
                admins,
                System.Security.AccessControl.RegistryRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));

            key.SetAccessControl(acl);
        }
        catch
        {
            // Best effort — sc.exe delete may still work via SYSTEM context.
        }
    }

    /// <summary>
    /// Завершает все процессы, у которых исполняемый файл лежит в каталоге установки (и известные имена по всей системе),
    /// чтобы снять блокировку с Program Files\KidControl.
    /// </summary>
    private void KillAllKidControlProcessesBlockingUninstall()
    {
        KillKidControlProcessesAsSystem();
        Run("taskkill.exe", "/F /IM KidControl.UiHost.exe", false);
        Run("taskkill.exe", "/F /IM KidControl.ServiceHost.exe", false);
        StopProcessesUnderInstallFolderViaPowerShellCim();
        KillProcessesWhoseImagePathIsUnderInstallFolder();
    }

    /// <summary>
    /// Завершает KidControl-процессы синхронно, используя SeDebugPrivilege для обхода DACL-запрета
    /// PROCESS_TERMINATE для Admins. TryEnableSeDebugPrivilege() должен быть вызван заранее.
    /// </summary>
    private void KillKidControlProcessesAsSystem()
    {
        foreach (var name in new[] { "KidControl.UiHost", "KidControl.ServiceHost" })
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                using (proc)
                {
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(3000);
                        Log($"Процесс {name}.exe завершён (SeDebugPrivilege).");
                    }
                    catch (Exception ex)
                    {
                        Log($"Предупреждение: не удалось завершить {name}.exe: {ex.Message}");
                    }
                }
            }
        }
    }

    // ─── SeDebugPrivilege P/Invoke ─────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privileges; }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr hProcess, uint desiredAccess, out IntPtr hToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr hToken, bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint bufLen, IntPtr prevState, IntPtr retLen);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Активирует SeDebugPrivilege в текущем процессе. После этого Process.Kill() работает
    /// на любых процессах вне зависимости от их DACL.
    /// </summary>
    private static void TryEnableSeDebugPrivilege()
    {
        const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        const uint TOKEN_QUERY = 0x0008;
        const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var hToken))
            return;
        try
        {
            if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid))
                return;
            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED }
            };
            AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(hToken);
        }
    }

    private void StopProcessesUnderInstallFolderViaPowerShellCim()
    {
        try
        {
            var dir = Path.GetFullPath(ProgramFilesPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dirPs = dir.Replace("'", "''", StringComparison.Ordinal);
            var self = Environment.ProcessId;
            var script =
                "$d='" + dirPs + "';" +
                "Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.ExecutablePath -and ($_.ExecutablePath.StartsWith($d+'\\',[System.StringComparison]::OrdinalIgnoreCase)) } | " +
                "ForEach-Object { if ($_.ProcessId -ne " + self + ") { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } }";

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", false);
        }
        catch
        {
            // PowerShell может быть отключён политикой — тогда остаётся обход через MainModule.
        }
    }

    private void KillProcessesWhoseImagePathIsUnderInstallFolder()
    {
        var root = Path.GetFullPath(ProgramFilesPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var self = Environment.ProcessId;

        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                try
                {
                    if (proc.Id == self)
                    {
                        continue;
                    }

                    string? exe;
                    try
                    {
                        exe = proc.MainModule?.FileName;
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(exe) || !IsExecutableUnderDirectory(exe, root))
                    {
                        continue;
                    }

                    try
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(8000);
                    }
                    catch
                    {
                        Run("taskkill.exe", $"/F /PID {proc.Id} /T", false);
                    }
                }
                catch
                {
                    // нет прав на MainModule и т.п.
                }
            }
        }
    }

    private static bool IsExecutableUnderDirectory(string executablePath, string directoryPath)
    {
        var exe = Path.GetFullPath(executablePath);
        var dir = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (exe.Length <= dir.Length + 1)
        {
            return false;
        }

        if (!exe.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return exe[dir.Length] is '\\' or '/';
    }

    private void Run(string fileName, string arguments, bool throwOnError)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        });
        process?.WaitForExit(10000);
        var stderr = process?.StandardError.ReadToEnd().Trim() ?? string.Empty;
        var stdout = process?.StandardOutput.ReadToEnd().Trim() ?? string.Empty;

        if (throwOnError && (process is null || process.ExitCode != 0))
        {
            var detail = string.IsNullOrEmpty(stderr) ? stdout : stderr;
            if (!string.IsNullOrEmpty(detail))
            {
                throw new InvalidOperationException($"{fileName} завершился с ошибкой: {process?.ExitCode}. {detail}");
            }

            throw new InvalidOperationException($"{fileName} завершился с ошибкой: {process?.ExitCode}");
        }
    }

    private bool EnsureAdmin()
    {
        var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            return true;
        }

        MessageBox.Show(this, "Запустите инсталлер от имени администратора.", "Недостаточно прав", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private void Log(string text)
    {
        var target = _currentStep == 0 ? _manageLogBox : _logBox;
        target.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
    }

    // ─── Detect installed version & manage step ───────────────────────────────

    /// <summary>
    /// Called on Form Load. If an existing installation is detected, populates the
    /// manage step (step 0) with version info and switches to it. Otherwise the wizard
    /// stays on step 1 (Telegram) for a fresh install.
    /// </summary>
    private async Task DetectAndOfferAsync()
    {
        var installedExe = Path.Combine(ProgramFilesPath, AssetServiceHost);
        if (!File.Exists(installedExe)) return;

        _isInstalled = true;
        _manageInstalledLabel.Text = $"Установлена версия: {GetInstalledVersion(installedExe)}";
        _manageLatestLabel.Text    = "Последняя версия на GitHub: проверяю...";
        ShowStep(0);

        var latest = await GetLatestGitHubTagAsync();
        _manageLatestLabel.Text = $"Последняя версия на GitHub: {latest ?? "не удалось получить"}";
    }

    private void SetManageButtonsEnabled(bool enabled)
    {
        _manageUpdateButton.Enabled    = enabled;
        _manageReinstallButton.Enabled = enabled;
        _manageUninstallButton.Enabled = enabled;
    }

    private async Task HandleManageUpdateAsync()
    {
        if (!EnsureAdmin()) return;
        SetManageButtonsEnabled(false);
        Log("Получаю последнюю версию с GitHub...");
        try
        {
            var tag = await GetLatestGitHubTagAsync()
                      ?? throw new InvalidOperationException("Не удалось получить последнюю версию с GitHub. Проверьте подключение к интернету.");
            var tempPath = Path.Combine(Path.GetTempPath(), $"KidControlUpdate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempPath);
            await DownloadPayloadAsync(tag, tempPath);
            PerformBinaryUpdate(tempPath, Log);
            Log("Обновление завершено успешно.");
            try { Directory.Delete(tempPath, recursive: true); } catch { }
        }
        catch (Exception ex)
        {
            Log($"Ошибка обновления: {ex.Message}");
        }
        SetManageButtonsEnabled(true);
    }

    private async Task HandleManageReinstallAsync()
    {
        if (!EnsureAdmin()) return;
        SetManageButtonsEnabled(false);
        Log("Удаляю текущую версию для переустановки...");
        await UninstallAsync();
        _isInstalled = false;
        ShowStep(1); // back to Telegram for fresh install
    }

    private async Task HandleManageUninstallAsync()
    {
        if (!EnsureAdmin()) return;
        SetManageButtonsEnabled(false);
        Log("Начинаю удаление...");
        await UninstallAsync();
        Close();
    }

    /// <summary>
    /// Replaces installed binaries with those in <paramref name="tempPath"/>,
    /// re-registers the service, and restores all protection. Does NOT touch
    /// appsettings.json or session_state.json.
    /// </summary>
    private void PerformBinaryUpdate(string tempPath, Action<string> log)
    {
        Directory.CreateDirectory(ProgramFilesPath);

        StopAndDeleteService(ServiceName);
        KillAllKidControlProcessesBlockingUninstall();
        Thread.Sleep(500);

        if (Directory.Exists(ProgramDataPath))
            GrantAdministratorsAndSystemRecursive(ProgramDataPath);

        UnlockInstallFolderAcl();
        Thread.Sleep(300);

        File.Copy(Path.Combine(tempPath, AssetServiceHost), Path.Combine(ProgramFilesPath, AssetServiceHost), overwrite: true);
        File.Copy(Path.Combine(tempPath, AssetUiHost),      Path.Combine(ProgramFilesPath, AssetUiHost),      overwrite: true);
        log("Бинарники заменены.");

        EnsureProgramDataLogsDirectory();
        EnsureProgramDataFolderProtected();

        RegisterService();
        StartService();
        log("Служба перезапущена.");

        LockInstallFolderAcl();

        RegisterUiTask();
        RegisterServiceRestartTask();
        log("Задачи планировщика обновлены.");

        ProtectServiceRegistryKey();
        log("Защита реестра обновлена.");
    }

    // ─── Silent update / rollback ────────────────────────────────────────────

    /// <summary>
    /// Called by Program.Main when /silent is passed. Replaces the running binaries
    /// without showing any UI. Preserves existing appsettings.json and session_state.json.
    /// Must be called from the UI thread (STA) but no Form will be shown.
    /// </summary>
    public void RunSilentUpdate()
    {
        var logPath = Path.Combine(ProgramDataPath, "silent_install.log");
        void SilentLog(string msg)
        {
            var line = $"[{DateTimeOffset.UtcNow:u}] {msg}";
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
        }

        SilentLog("Silent update started.");

        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"KidControlUpdate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempPath);
            SilentLog($"Temp dir: {tempPath}");

            // Determine latest release tag and download payloads.
            var tag = GetLatestGitHubTagAsync().GetAwaiter().GetResult()
                      ?? throw new InvalidOperationException("Could not determine latest GitHub release tag.");
            SilentLog($"Latest tag: {tag}");

            // DownloadPayloadAsync internally calls Log() which writes to the in-memory
            // _logBox — harmless in silent mode since no UI message pump is running.
            DownloadPayloadAsync(tag, tempPath).GetAwaiter().GetResult();
            SilentLog("Payload downloaded.");

            // Intentionally skip WriteAppSettings() and ResetPersistedSessionState() —
            // silent update preserves existing Telegram config and session state.
            PerformBinaryUpdate(tempPath, SilentLog);

            // Clean up temp dir.
            try { Directory.Delete(tempPath, recursive: true); } catch { }

            SilentLog("Silent update completed successfully.");
        }
        catch (Exception ex)
        {
            SilentLog($"Silent update FAILED: {ex}");
            throw;
        }
    }
}
