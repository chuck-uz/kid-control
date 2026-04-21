using System.Diagnostics;
using System.Reflection;
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

    /// <summary>RmShutdown часто возвращает ERROR_ACCESS_DENIED (5) в Program Files — не спамим вызовами.</summary>
    private bool _skipRestartManager;

    private readonly Panel _navPanel = new() { Left = 0, Top = 0, Width = 230, Height = 640 };
    private readonly Panel _contentPanel = new() { Left = 230, Top = 0, Width = 670, Height = 640, BackColor = Theme.BackgroundLight };
    private readonly StepNavItem[] _stepNav =
    {
        new() { Text = "1. Приветствие", Top = 120, Left = 16 },
        new() { Text = "2. Telegram", Top = 166, Left = 16 },
        new() { Text = "3. Опции", Top = 212, Left = 16 },
        new() { Text = "4. Установка/Удаление", Top = 258, Left = 16 }
    };
    private readonly Panel[] _steps = new Panel[4];

    private readonly AccentButton _backButton = new() { Text = "Назад", Width = 110, Left = 250, Top = 560, AccentColor = Color.FromArgb(94, 94, 94) };
    private readonly AccentButton _nextButton = new() { Text = "Далее", Width = 110, Left = 370, Top = 560 };
    private int _currentStep;

    private readonly ModernTextBox _tokenText = new() { Left = 28, Top = 140, Width = 590 };
    private readonly ModernTextBox _chatIdsText = new() { Left = 28, Top = 230, Width = 590 };
    private readonly AccentButton _checkTelegramButton = new() { Text = "Проверить", Left = 28, Top = 292, Width = 140 };
    private readonly Label _telegramStatus = new() { Left = 186, Top = 304, Width = 420, AutoEllipsis = true, Font = new Font(InstallerFonts.MessageFontFamily, 10), ForeColor = Theme.TextSecondary };

    private readonly CheckBox _persistenceCheck = new() { Left = 28, Top = 150, Width = 590, Text = "Включить защиту от перезагрузки", Font = new Font(InstallerFonts.MessageFontFamily, 11f) };
    private readonly CheckBox _autostartCheck = new() { Left = 28, Top = 190, Width = 590, Text = "Служба Windows: зарегистрировать и запустить (автозапуск)", Checked = true, Font = new Font(InstallerFonts.MessageFontFamily, 11f) };

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
        BuildStepOne();
        BuildStepTwo();
        BuildStepThree();
        BuildStepFour();
        ShowStep(0);
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

    private void BuildStepOne()
    {
        var panel = NewStepPanel();
        AddStepHeader(panel, "Лицензия и приветствие", "Добро пожаловать в современную установку KidControl.");
        panel.Controls.Add(new Label
        {
            Left = 28,
            Top = 130,
            Width = 590,
            Height = 140,
            Font = new Font(InstallerFonts.MessageFontFamily, 11f),
            Text = "Продолжая установку, вы подтверждаете ответственность за настройку ограничений доступа и управление приложением через Telegram."
        });
        panel.Controls.Add(CreateIllustrationPanel("Security"));
        _steps[0] = panel;
    }

    private void BuildStepTwo()
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

    private void BuildStepThree()
    {
        var panel = NewStepPanel();
        AddStepHeader(panel, "Опции безопасности", "Выберите поведение системы при запуске.");
        panel.Controls.Add(_persistenceCheck);
        panel.Controls.Add(_autostartCheck);
        panel.Controls.Add(CreateIllustrationPanel("Shield"));
        _steps[2] = panel;
    }

    private void BuildStepFour()
    {
        var panel = NewStepPanel();
        AddStepHeader(panel, "Установка и удаление", "Запустите установку или полное удаление. Логи службы: папка ProgramData\\KidControl\\logs");
        panel.Controls.Add(_installButton);
        panel.Controls.Add(_uninstallButton);
        panel.Controls.Add(_logBox);
        _installButton.Click += async (_, _) => await InstallAsync();
        _uninstallButton.Click += async (_, _) => await UninstallAsync();
        _steps[3] = panel;
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
        var next = Math.Clamp(_currentStep + delta, 0, _steps.Length - 1);
        ShowStep(next);
    }

    private void ShowStep(int index)
    {
        _currentStep = index;
        for (var i = 0; i < _steps.Length; i++)
        {
            _steps[i].Visible = i == index;
        }

        _backButton.Enabled = index > 0;
        _nextButton.Enabled = index < _steps.Length - 1;
        _nextButton.Visible = index < _steps.Length - 1;
        _backButton.Visible = index < _steps.Length - 1;
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

            ExtractPayload(tempPath);
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
            File.Copy(Path.Combine(tempPath, "KidControl.Unlocker.exe"), Path.Combine(ProgramFilesPath, "KidControl.Unlocker.exe"), true);

            if (_persistenceCheck.Checked)
            {
                EnsureProgramDataFolderProtected();
                Log("Папка ProgramData создана и защищена.");
            }

            EnsureProgramDataLogsDirectory();
            ResetPersistedSessionState();
            WriteAppSettings();
            Log("Файлы установлены.");
            Log($"Логи службы и UI: {Path.Combine(ProgramDataPath, "logs")}");

            RegisterUiTask();
            Log("Задача планировщика для UI обновлена.");

            if (_autostartCheck.Checked)
            {
                RegisterService();
                StartService();
                Log("Служба зарегистрирована и запущена.");
            }

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

            StopAndDeleteService(ServiceName);
            DeleteUiTask();
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

    private void ExtractPayload(string tempPath)
    {
        Extract("Payload.KidControl.ServiceHost.exe", Path.Combine(tempPath, "KidControl.ServiceHost.exe"));
        Extract("Payload.KidControl.UiHost.exe", Path.Combine(tempPath, "KidControl.UiHost.exe"));
        Extract("Payload.KidControl.Unlocker.exe", Path.Combine(tempPath, "KidControl.Unlocker.exe"));
    }

    private static void Extract(string logicalName, string destinationPath)
    {
        // Single-file publish: GetExecutingAssembly() may not be the project assembly that holds EmbeddedResource.
        var assembly = typeof(InstallerForm).Assembly;
        var fullName = $"{typeof(InstallerForm).Namespace}.{logicalName}";
        var resourceNames = assembly.GetManifestResourceNames();
        var actualResourceName = resourceNames.FirstOrDefault(name =>
            string.Equals(name, fullName, StringComparison.OrdinalIgnoreCase))
            ?? resourceNames.FirstOrDefault(name =>
                name.EndsWith(logicalName, StringComparison.OrdinalIgnoreCase))
            ?? resourceNames.FirstOrDefault(name =>
                name.EndsWith(Path.GetFileName(logicalName), StringComparison.OrdinalIgnoreCase));

        using var stream = actualResourceName is null
            ? null
            : assembly.GetManifestResourceStream(actualResourceName);

        if (stream is null)
        {
            var fileName = Path.GetFileName(logicalName);
            if (TryCopyPayloadFromDisk(fileName, destinationPath))
            {
                return;
            }

            var knownResources = resourceNames.Length == 0
                ? "(нет встроенных ресурсов в этой сборке — часто признак single-file + неверный Assembly)"
                : string.Join(", ", resourceNames);
            throw new InvalidOperationException(
                $"Ресурс не найден: {fullName}. Доступные ресурсы: {knownResources}. " +
                "Рядом с инсталлером должна быть папка Artifacts с тремя .exe (полная сборка через build.ps1).");
        }

        using var file = File.Create(destinationPath);
        stream.CopyTo(file);
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
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "publish", "Unlocker", fileName));

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
        File.WriteAllText(Path.Combine(ProgramFilesPath, "appsettings.json"), json, Encoding.UTF8);
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

        // 1) Shell execute directly.
        if (TryStartUiViaShell(uiExe))
        {
            return;
        }

        // 2) Explorer fallback in interactive shell.
        if (TryStartUiViaExplorer(uiExe))
        {
            return;
        }

        // 3) Run scheduled task now (task already created on install).
        if (TryRunUiTaskNow())
        {
            return;
        }

        MessageBox.Show(
            this,
            "Не удалось запустить UI автоматически. Откройте вручную: C:\\Program Files\\KidControl\\KidControl.UiHost.exe",
            "KidControl",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
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
                $"\"{rootPath}\" /grant \"Administrators:(OI)(CI)F\" /T /C",
                waitMs: 120_000);
            RunExternal(
                "icacls.exe",
                $"\"{rootPath}\" /grant \"SYSTEM:(OI)(CI)F\" /T /C",
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

    private void TryDeleteDirectoryWithRetry(string path, int attempts = 12, int delayMs = 900)
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
            catch (IOException) when (i < attempts - 1)
            {
                Log($"Каталог занят процессом… ({i + 1}/{attempts}) — Restart Manager + завершение процессов KidControl");
                if (i >= 1)
                {
                    TakeOwnershipAndResetAttributes(path);
                }

                MaybeRestartManagerRelease(path);
                KillAllKidControlProcessesBlockingUninstall();
                Thread.Sleep(delayMs);
            }
            catch (UnauthorizedAccessException) when (i < attempts - 1)
            {
                Log($"Нет доступа к каталогу… ({i + 1}/{attempts}) — Restart Manager + процессы KidControl");
                TakeOwnershipAndResetAttributes(path);
                MaybeRestartManagerRelease(path);
                KillAllKidControlProcessesBlockingUninstall();
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
        Run("icacls.exe", $"\"{path}\" /grant \"Administrators:(OI)(CI)F\" /T /C", false);
        Run("icacls.exe", $"\"{path}\" /grant \"SYSTEM:(OI)(CI)F\" /T /C", false);
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
        Run("sc.exe", $"failure {ServiceName} reset= 0 actions= restart/5000", false);
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

    /// <summary>
    /// Завершает все процессы, у которых исполняемый файл лежит в каталоге установки (и известные имена по всей системе),
    /// чтобы снять блокировку с Program Files\KidControl.
    /// </summary>
    private void KillAllKidControlProcessesBlockingUninstall()
    {
        Run("taskkill.exe", "/F /IM KidControl.UiHost.exe", false);
        Run("taskkill.exe", "/F /IM KidControl.ServiceHost.exe", false);
        Run("taskkill.exe", "/F /IM KidControl.Unlocker.exe", false);

        StopProcessesUnderInstallFolderViaPowerShellCim();
        KillProcessesWhoseImagePathIsUnderInstallFolder();
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
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
    }
}
