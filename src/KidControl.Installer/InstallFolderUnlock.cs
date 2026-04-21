using System.Runtime.InteropServices;

namespace KidControl.Installer;

/// <summary>
/// Снимает блокировки каталога установки: Restart Manager (в т.ч. Проводник, conhost)
/// и планирует удаление файлов при перезагрузке через MoveFileEx.
/// </summary>
internal static class InstallFolderUnlock
{
    private const uint RmForceShutdown = 0x1;
    private const int MovFileDelayUntilReboot = 0x4;

    /// <summary>
    /// Возвращает код RmShutdown (0 = успех) или код ошибки RmStartSession/RmRegisterResources, если до RmShutdown не дошли.
    /// Код 5 (ERROR_ACCESS_DENIED) для RmShutdown на Program Files встречается часто — тогда опираемся на takeown и MoveFileEx.
    /// </summary>
    public static int TryRestartManagerForceRelease(string directoryPath, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return 0;
        }

        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        uint session = 0;
        var sessionKey = Guid.NewGuid().ToString("N");
        var err = RmStartSession(out session, 0, sessionKey);
        if (err != 0)
        {
            log?.Invoke($"Restart Manager: не удалось начать сессию (код {err}).");
            return err;
        }

        try
        {
            var paths = new[] { dir };
            err = RmRegisterResources(session, (uint)paths.Length, paths, 0, IntPtr.Zero, 0, IntPtr.Zero);
            if (err != 0)
            {
                log?.Invoke($"Restart Manager: не удалось зарегистрировать путь (код {err}).");
                return err;
            }

            log?.Invoke("Restart Manager: запрос на освобождение блокировок (RmShutdown)…");
            err = RmShutdown(session, RmForceShutdown, IntPtr.Zero);
            if (err != 0)
            {
                var hint = err == 5
                    ? " — ERROR_ACCESS_DENIED: типично для Program Files; дальше используем takeown и удаление после перезагрузки."
                    : string.Empty;
                log?.Invoke($"Restart Manager: RmShutdown завершился с кодом {err}{hint}");
            }

            return err;
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    /// <summary>
    /// Помечает все файлы в дереве на удаление при следующей перезагрузке Windows.
    /// </summary>
    /// <returns>true, если удалось пометить хотя бы часть файлов или дерево пустое.</returns>
    public static bool TryScheduleRecursiveDeleteAtNextBoot(string rootDir, Action<string>? log)
    {
        if (!Directory.Exists(rootDir))
        {
            return true;
        }

        var root = Path.GetFullPath(rootDir);
        var scheduled = 0;
        var failed = 0;
        var fileCount = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                fileCount++;
                var extended = ToWin32ExtendedPath(file);
                var target = extended ?? file;
                if (MoveFileEx(target, null, MovFileDelayUntilReboot))
                {
                    scheduled++;
                }
                else
                {
                    failed++;
                    log?.Invoke($"Не удалось запланировать удаление после перезагрузки: {file} (Win32: {Marshal.GetLastWin32Error()})");
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Обход файлов для отложенного удаления: {ex.Message}");
        }

        if (scheduled > 0)
        {
            log?.Invoke($"Запланировано удаление после перезагрузки: {scheduled} файл(ов).");
        }

        // Нет файлов в дереве — отложенное удаление не применимо; пусть вызывающий попробует rd / обычный Delete.
        if (fileCount == 0)
        {
            return false;
        }

        return scheduled > 0;
    }

    /// <summary>Длинные пути и согласованность с API удаления в ядре.</summary>
    private static string? ToWin32ExtendedPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return full;
        }

        // UNC — не добавляем \\?\
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        return @"\\?\" + full;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmEndSession(uint dwSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint dwSessionHandle,
        uint nFiles,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)]
        string[] rgsFileNames,
        uint nApplications,
        IntPtr rgApplications,
        uint nServices,
        IntPtr rgsServiceNames);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmShutdown(uint dwSessionHandle, uint lActionFlags, IntPtr fnStatus);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "MoveFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string? lpExistingFileName, string? lpNewFileName, int dwFlags);
}
