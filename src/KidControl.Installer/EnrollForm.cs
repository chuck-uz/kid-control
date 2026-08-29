using System.Runtime.Versioning;
using KidControl.Installer.Core;

namespace KidControl.Installer;

/// <summary>
/// Post-install "bind this PC to the server" dialog (managed mode). The operator enters the
/// backend URL (pre-filled) and the one-time enroll code from the bot; on Bind it writes the
/// Fleet section into appsettings, restarts the service, and waits for the agent to enroll.
/// Kept thin — the config write lives in <see cref="FleetConfigWriter"/> and the service
/// control in <see cref="ServiceInstaller"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EnrollForm : Form
{
    private readonly TextBox _urlBox = new() { Left = 24, Top = 70, Width = 520 };
    private readonly TextBox _codeBox = new() { Left = 24, Top = 138, Width = 260, CharacterCasing = CharacterCasing.Upper };
    private readonly Button _bindButton = new() { Left = 24, Top = 190, Width = 200, Height = 36, Text = "Привязать" };
    private readonly Label _status = new() { Left = 24, Top = 240, Width = 520, Height = 60, AutoSize = false };

    private readonly FleetConfigWriter _config = new();
    private readonly ServiceInstaller _service = new();

    public EnrollForm()
    {
        Text = "KidControl — привязка к серверу";
        Width = 590;
        Height = 360;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        Controls.Add(Label("Адрес сервера", 24, 48));
        Controls.Add(_urlBox);
        Controls.Add(Label("Код привязки (из бота: /enroll)", 24, 116));
        Controls.Add(_codeBox);
        Controls.Add(_bindButton);
        Controls.Add(_status);

        _urlBox.Text = _config.ReadBackendUrl();
        _bindButton.Click += async (_, _) => await BindAsync();
    }

    private static Label Label(string text, int left, int top) =>
        new() { Text = text, Left = left, Top = top, AutoSize = true };

    private async Task BindAsync()
    {
        var url = _urlBox.Text.Trim();
        var code = _codeBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url)) { SetStatus("Укажите адрес сервера."); return; }
        if (string.IsNullOrWhiteSpace(code)) { SetStatus("Введите код привязки."); return; }

        SetBusy(true);
        try
        {
            SetStatus("Сохраняю настройки…");
            _config.Write(url, code);

            SetStatus("Перезапускаю службу…");
            await Task.Run(RestartService);

            SetStatus("Ожидаю привязку устройства…");
            var enrolled = await WaitForEnrollAsync(TimeSpan.FromSeconds(40));

            SetStatus(enrolled
                ? "✓ Устройство привязано. Оно появилось в боте — можно управлять."
                : "Настройки применены. Устройство привяжется в течение минуты; проверьте список устройств в боте. Если нет — код мог истечь: возьмите новый (/enroll) и повторите.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RestartService()
    {
        if (!_service.IsInstalled())
            throw new InvalidOperationException("Служба KidControlService не установлена. Сначала запустите deploy.bat.");
        try { _service.StopAndWait(TimeSpan.FromSeconds(30)); } catch { /* already stopped */ }
        _service.Start();
    }

    private async Task<bool> WaitForEnrollAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_config.IsEnrolled())
                return true;
            await Task.Delay(2000);
        }
        return _config.IsEnrolled();
    }

    private void SetBusy(bool busy)
    {
        _bindButton.Enabled = !busy;
        _urlBox.Enabled = !busy;
        _codeBox.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void SetStatus(string message)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStatus(message)); return; }
        _status.Text = message;
    }
}
