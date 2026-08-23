# KidControl — чек-лист релиза (тег `v2.0.0`)

Релиз полностью автоматизирован: **пуш тега `vX.Y.Z` → workflow `Release`** соберёт,
прогонит тесты, подпишет бинарники, посчитает контрольные суммы и создаст GitHub
Release с артефактами. Ниже — что проверить до, во время и после.

---

## 0. Одноразовая настройка (перед самым первым релизом)

- [ ] **Сертификат подписи кода.** Получи code-signing `.pfx` (OV/EV). Он критичен:
      self-update запускает скачанный инсталлятор **под SYSTEM**, и `UpdateConfig.RequireSignature`
      доверяет только подписи с нужным отпечатком.
- [ ] **Секреты репозитория** (Settings → Secrets and variables → Actions):
  - [ ] `CODE_SIGNING_PFX_BASE64` — `.pfx` в base64 (`[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))`).
  - [ ] `CODE_SIGNING_PFX_PASSWORD` — пароль от `.pfx`.
  - Без них workflow соберёт **неподписанные** бинарники (с предупреждением) — для боевого релиза так нельзя.
- [ ] **SHA-256 отпечаток** сертификата запиши — он пойдёт в установки как
      `--thumbprint` (тихая установка) или в поле мастера, попадёт в `UpdateConfig.TrustedThumbprint`
      и позволит будущим self-update принимать твой подписанный инсталлятор.
- [ ] Реши видимость Release (репозиторий приватный → релизы тоже видны только тебе).

## 1. Pre-flight (перед каждым релизом)

- [ ] Код влит в ветку `v2` (или откуда релизишь), **CI зелёный** (build + tests на windows-latest).
- [ ] Обновлены `docs/` / `REWRITE-NOTES.md`, если менялось поведение.
- [ ] В индексе **нет реального `appsettings.json`** (только `appsettings.template.json`; `.gitignore` это ловит).
- [ ] Версию руками не бампаешь — её даёт **MinVer из тега** (`MinVerTagPrefix=v`).
- [ ] `Directory.Packages.props` восстанавливается (нет несуществующих версий пакетов).

## 2. Тегирование и запуск

- [ ] Встань на нужный коммит: `git checkout v2 && git pull`.
- [ ] Поставь **аннотированный** тег на этот коммит:
      ```bash
      git tag -a v2.0.0 -m "KidControl 2.0.0"
      ```
- [ ] Запушь тег (именно это запускает Release-workflow):
      ```bash
      git push origin v2.0.0
      ```
- [ ] Workflow собирает **из отмеченного коммита** — тег должен стоять на правильном коммите.

## 3. Проверка сборки релиза

- [ ] Actions → run **Release** зелёный.
- [ ] Шаг **Validate tag format** прошёл (тег вида `v2.0.0`).
- [ ] Шаг **Verify stamped version matches tag** прошёл (бинарь = `2.0.0`, значит MinVer сработал).
- [ ] Шаг **Sign executables** НЕ вывел «publishing UNSIGNED binaries» (иначе секрет не задан).
- [ ] На странице Release появились артефакты:
  - [ ] `KidControl-Setup-v2.0.0.zip` (инсталлятор + payload-бинарники, всё в одном),
  - [ ] `KidControl.Installer.exe`, `KidControl.ServiceHost.exe`, `KidControl.UiHost.exe`,
  - [ ] `SHA256SUMS.txt`.
- [ ] Подпись валидна (на Windows): `signtool verify /pa KidControl.Installer.exe`
      или Свойства → Цифровые подписи.

## 4. Smoke-тест на чистой Windows-машине

- [ ] Вариант А: `deploy.bat` c `KC_SOURCE_MODE=release` (теперь релиз существует) → ставит.
- [ ] Вариант Б: скачать `KidControl-Setup-v2.0.0.zip`, распаковать, запустить `KidControl.Installer.exe`.
- [ ] `sc query KidControlService` → RUNNING.
- [ ] На экране появился виджет таймера; по истечении — оверлей перерыва.
- [ ] Telegram-бот отвечает на `/status`, команды применяются.
- [ ] **Персистентность:** перезагрузить ПК → таймер восстановился.

## 5. Проверка self-update (после второго релиза)

- [ ] Установка сделана с корректным `TrustedThumbprint` (= отпечаток твоего сертификата).
- [ ] Выпусти следующий тег (`v2.0.1`).
- [ ] Работающая служба в течение `UpdateConfig.CheckInterval` (6 ч; для теста можно уменьшить)
      пришлёт в Telegram уведомление о новой версии.
- [ ] Установка обновления проходит проверку подписи+отпечатка перед запуском.
- ⚠️ **Важное ограничение сейчас:** `UpdateService` скачивает **один `.exe`-ассет** и запускает
      его. Одинокий `KidControl.Installer.exe` не несёт payload-бинарников (ServiceHost/UiHost),
      поэтому полностью автоматический self-update пока рассчитан на инсталлятор, который либо
      (а) содержит payload встроенно, либо (б) на `UpdateService`, скачивающий и распаковывающий
      `KidControl-Setup-*.zip`. Ручное обновление (`update.bat` / скачать zip → `/update`) работает
      полностью. **Следующий шаг для боевого self-update — дописать `UpdateService` под zip-ассет.**

## 6. Откат

- [ ] Плохой релиз: удали Release в UI и тег
      ```bash
      git push --delete origin v2.0.0
      ```
      затем поправь и перетегируй. (Учти: если кто-то уже скачал — удаление тега/релиза разрушительно.)
- [ ] Или **накати вперёд** патчем `v2.0.1`.
- [ ] На установленных машинах: откат к прошлой версии — через Telegram
      (`UpdateService.StartRollbackAsync` → выбор предыдущего релиза).

## 7. Частые грабли

- MinVer не распознаёт тег → версия `0.0.0-alpha`: убедись, что `MinVerTagPrefix=v` (задан) и
  checkout с `fetch-depth: 0` (задан в workflow). Verify-шаг это ловит.
- Служба не стартует на цели → нет **.NET 8 Desktop Runtime** машинно (`deploy.bat` ставит через winget).
- Self-update отклоняет обновление → релиз неподписан или `TrustedThumbprint` не совпадает.
