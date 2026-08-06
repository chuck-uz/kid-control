using System.Runtime.Versioning;
using KidControl.Installer.Core;

namespace KidControl.Installer;

/// <summary>
/// Thin install/uninstall wizard. It collects the operator's inputs, streams progress,
/// and delegates every side effect to <see cref="InstallOrchestrator"/>. There is no
/// HTTP, no ACL code, no P/Invoke and no service management here — that all lives in
/// Installer.Core, which keeps this form small and the logic unit-testable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstallerForm : Form
{
    private readonly TextBox _tokenBox = new() { Left = 24, Top = 70, Width = 540, UseSystemPasswordChar = true };
    private readonly TextBox _chatIdsBox = new() { Left = 24, Top = 138, Width = 540 };
    private readonly TextBox _nightStartBox = new() { Left = 24, Top = 206, Width = 120, Text = "22:00:00" };
    private readonly TextBox _nightEndBox = new() { Left = 200, Top = 206, Width = 120, Text = "07:00:00" };

    private readonly Button _installButton = new() { Left = 24, Top = 262, Width = 170, Height = 34, Text = "Install" };
    private readonly Button _uninstallButton = new() { Left = 210, Top = 262, Width = 170, Height = 34, Text = "Uninstall" };

    private readonly TextBox _log = new()
    {
        Left = 24,
        Top = 316,
        Width = 540,
        Height = 210,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White,
    };

    private readonly InstallOrchestrator _orchestrator = InstallOrchestrator.CreateDefault();

    public InstallerForm()
    {
        Text = "KidControl Installer";
        Width = 600;
        Height = 590;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        BuildLayout();

        _installButton.Click += async (_, _) => await RunAsync(InstallAsync);
        _uninstallButton.Click += async (_, _) => await RunAsync(UninstallAsync);
    }

    private void BuildLayout()
    {
        Controls.Add(Label("Bot token", 24, 48));
        Controls.Add(_tokenBox);
        Controls.Add(Label("Admin chat IDs (comma-separated)", 24, 116));
        Controls.Add(_chatIdsBox);
        Controls.Add(Label("Night start", 24, 186));
        Controls.Add(Label("Night end", 200, 186));
        Controls.Add(_nightStartBox);
        Controls.Add(_nightEndBox);
        Controls.Add(_installButton);
        Controls.Add(_uninstallButton);
        Controls.Add(Label("Progress", 24, 296));
        Controls.Add(_log);
    }

    private static Label Label(string text, int left, int top) =>
        new() { Text = text, Left = left, Top = top, Width = 400, AutoSize = true };

    private async Task RunAsync(Func<Task> action)
    {
        SetBusy(true);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task InstallAsync()
    {
        InstallSettings settings;
        try
        {
            settings = new InstallSettings
            {
                BotToken = _tokenBox.Text.Trim(),
                AdminChatIds = ChatIds.Parse(_chatIdsBox.Text),
                NightStart = ParseTime(_nightStartBox.Text, TimeSpan.FromHours(22)),
                NightEnd = ParseTime(_nightEndBox.Text, TimeSpan.FromHours(7)),
            };
            settings.Validate();
        }
        catch (ArgumentException ex)
        {
            Log(ex.Message);
            return;
        }

        // Payload binaries (ServiceHost.exe + UiHost.exe) ship next to the installer.
        var source = AppContext.BaseDirectory;
        var request = new InstallRequest { SourceDirectory = source, Settings = settings };

        Log("Starting installation…");
        await Task.Run(() => _orchestrator.Install(request, Log));
    }

    private async Task UninstallAsync()
    {
        var confirm = MessageBox.Show(
            this,
            "Remove KidControl, its service, and all of its data?",
            "Confirm uninstall",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.Yes)
        {
            return;
        }

        Log("Starting uninstall…");
        await Task.Run(() => _orchestrator.Uninstall(Log));
    }

    private static TimeSpan ParseTime(string value, TimeSpan fallback) =>
        TimeSpan.TryParse(value, out var parsed) ? parsed : fallback;

    private void SetBusy(bool busy)
    {
        _installButton.Enabled = !busy;
        _uninstallButton.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    /// <summary>Thread-safe append: orchestrator progress arrives from a background thread.</summary>
    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(message));
            return;
        }

        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}
