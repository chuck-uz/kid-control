# KidControl v2 — заметки к переписыванию

Это переосмысленная версия [chuck-uz/kid-control](https://github.com/chuck-uz/kid-control),
написанная с нуля «как я вижу этот продукт»: та же функциональность (родительский
тайм-контроль Windows с управлением через Telegram), но с исправлением всех проблем,
найденных при разборе v1, и с архитектурой, заточенной под тестируемость,
безопасность и сопровождение.

> ✅ **Статус сборки:** решение собрано и протестировано на macOS (.NET SDK 8.0.423,
> osx-arm64). Полная сборка всех 12 проектов — **0 ошибок**; тесты — **149/149**
> (Domain 90, Application 54, Infrastructure 5). WPF/WinForms-проекты собираются на
> macOS в compile-режиме через флаг `-p:EnableWindowsTargeting=true`; **исполнять и
> публиковать (`win-x64`, single-file) — только на Windows**. CI-гейт — на
> `windows-latest`.
>
> Команды:
> ```bash
> dotnet build KidControl.sln -c Release -p:EnableWindowsTargeting=true
> dotnet test  KidControl.sln -c Release -p:EnableWindowsTargeting=true
> ```

## Философия

Модель угроз родительского контроля — **противник это сам ребёнок** (физический
доступ, иногда админ). Поэтому обходоустойчивость, безопасность канала обновлений
и надёжность — это не «качество кода», а сам продукт. v2 строится вокруг этого.

## Что изменилось: v1 → v2 (трассируемость)

### Безопасность
| Проблема v1 | Решение v2 |
|---|---|
| Обновление скачивало `.exe` и запускало под SYSTEM **без проверок**; репозиторий переопределялся через перезаписываемый ProgramData-конфиг | `UpdateService`: проверка Authenticode-подписи + пиннинг SHA-256 отпечатка (`UpdateConfig.RequireSignature/TrustedThumbprint`), сверка длины с `AssetSize`, allow-list хостов, запуск инсталлятора **только** после верификации; owner/repo фиксируются в конфиге с безопасными дефолтами |
| Command-pipe открыт `Authenticated Users` (ребёнку); OTP 4 знака, без лимита попыток → брутфорс за секунды | `PipeAccess`: DACL только SYSTEM+Administrators; `EmergencyOtpService`: 6 знаков, 5 попыток, burn кода, окно 5 мин, cooldown переиздачи, constant-time сравнение |
| Command-pipe: одно соединение за итерацию, блокирующее чтение без таймаута → DoS канала | `CommandPipeServer`: read-timeout 5 c, связанный со стоп-токеном; зависший клиент не вешает сервер |
| Оставленная отладочная телеметрия в `C:\kid-control\...\debug-9d75ca.log` и temp, в 3 файлах | Удалена полностью; логи только в защищённый `%ProgramData%\KidControl\logs` |
| Path traversal через `tag`; HTML-инъекция в Telegram | `SanitizeTag`/`SanitizeFileName` (отклоняют `..`/разделители); HTML-escape тега и release notes |

### Надёжность
| Проблема v1 | Решение v2 |
|---|---|
| `RtlSetProcessIsCritical` снимался только при штатном выходе → BSOD на любом краше | `Platform.ProcessProtection.RunCriticalAsync`: флаг снимается в `finally` + `ProcessExit`/`UnhandledException`; критичность выключена по умолчанию (`ProtectionConfig.CriticalProcess=false`) |
| После неудачного апдейта служба всё равно гасила себя (не проверялся `Process.Start`) | Остановка службы только если `Process.Start` вернул не-null |
| Неатомарная запись `session_state.json` → потеря состояния | `JsonSessionStore.Save`: temp-файл + атомарная замена |
| Telegram переигрывал устаревшие команды при рестарте | Сброс backlog при старте поллинга |
| `schtasks`: `WaitForExit` до вычитывания stdout → возможный deadlock | Асинхронный дренаж обоих потоков параллельно с ожиданием |
| `TamperDetector`: нет `Error`, нерекурсивно, кривой предикат | Рекурсивно, `Error` с ре-армингом, чистая логика |

### Архитектура и тестируемость
| Проблема v1 | Решение v2 |
|---|---|
| `SessionOrchestrator` — 979 строк, всё в одном | Разнесено: доменный `Session` (ночной снапшот теперь в домене), `SessionService` (координация), `EmergencyOtpService`, `CommandParser` (чистый парсинг), `ISystemController` (side effects) |
| `InstallerForm` — God-class 1900 строк, silent-режим зависел от `Form` | `Installer.Core`: `ServiceInstaller`, `AclManager`, `ProcessKiller`, `RegistryProtector`, `AppSettingsWriter`, `InstallOrchestrator` (прогресс через `Action<string>` — headless без окна); UI — тонкий (~180 строк) |
| `DateTimeOffset.Now` повсюду → нетестируемо | Порт `IClock` + `FakeClock` в тестах; ночная логика и OTP теперь детерминированно тестируются |
| Дублированный DACL/P-Invoke в UI и службе | Единый `KidControl.Platform.ProcessProtection` |
| Имя службы с версией (`KidControlv0.4`) → сирота при апдейте | `KidControlNames.ServiceName` = `KidControlService`, версия-независимо; все имена в одном месте |
| 180-строчный `switch` callback'ов | Таблично-управляемый диспатч |

### Тесты и CI
| Проблема v1 | Решение v2 |
|---|---|
| `Infrastructure.Tests` — пустой класс без тест-пакетов | Реальные тесты `JsonSessionStore` (round-trip, устойчивость к битому файлу, атомарность); тест-стек подключён |
| ~150 доменных/прикладных кейсов отсутствовали | ~150 кейсов: `Session`, `ScheduleRule`, `NightWindow`, `CommandParser`, `EmergencyOtpService` (явная защита от брутфорса), `SessionService` |
| CI не запускал тесты, триггер только на теги, без подписи | `ci.yml` (push/PR, build+test+coverage); `release.yml` (тесты до упаковки, подпись `signtool`, `SHA256SUMS.txt`) |
| `10.0.x`-пакеты на net8, без центрального управления, плавающий MinVer | `Directory.Packages.props` — все версии выровнены под net8, зафиксированы |

## Слои (правила зависимостей)

```
Domain         ← чистый, без зависимостей (Session, ScheduleRule, NightWindow)
Contracts      ← DTO/константы IPC, без зависимостей
Application    ← зависит только от Domain + Contracts; порты + сервисы + парсер
Platform       ← общий Win32 (DACL, critical process)
Infrastructure ← реализует порты Application (Telegram, IPC, Update, Persistence, Windows)
ServiceHost    ← хост службы: связывает всё, гоняет таймер и watchdog
UiHost         ← WPF-виджет/оверлей; знает только Contracts + Platform
Installer.*    ← декомпозированная установка; ядро без WinForms
```

Подробности — в [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) и [docs/SECURITY.md](docs/SECURITY.md).

## Известные ограничения / сознательные упрощения

- **Не компилировалось.** Первый шаг на Windows — `dotnet build -c Release` и `dotnet test`; ждём мелкие правки под реальный API Telegram.Bot и точные версии пакетов.
- **Нет отдельного `Unlocker.exe`.** Command-pipe (`CommandPipeServer`) готов, но клиента к нему в v2 не собирал — аварийное выключение уже доступно через Telegram-OTP. Клиент-утилиту легко добавить отдельным проектом.
- **`GitHubReleaseClient`** — typed HttpClient, захваченный singleton `UpdateService` (captive dependency). Для поллинга раз в 6 часов это несущественно.
- **GitHub API** не отдаёт SHA-256 ассета — `UpdateInfo.Sha256` обычно null; подпись Authenticode остаётся основной гарантией, хэш сверяется если задан.
- **Ночное окно** из `appsettings` применяется как seed для новой сессии; после первой правки через Telegram источник правды — персистентный снапшот.
- Тонкости P/Invoke (`ProcessWatchdog`, `CreateProcessAsUser`) перенесены из рабочего v1, но их поведение в Session 0 нужно проверить вживую.

## Первый запуск на Windows

```powershell
# 1) собрать + тесты
dotnet build -c Release
dotnet test  -c Release

# 2) опубликовать артефакты
./build.ps1

# 3) заполнить секреты (НЕ в git): %ProgramData%\KidControl\appsettings.json
#    по образцу src/KidControl.ServiceHost/appsettings.template.json
#    (BotToken, AdminChatIds, при желании TrustedThumbprint для подписи апдейтов)

# 4) установить от администратора
./publish/KidControl.Installer.exe
```
