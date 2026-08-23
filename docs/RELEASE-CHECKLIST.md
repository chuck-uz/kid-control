# KidControl — чек-лист релиза (тег `v2.0.0`)

Релиз полностью автоматизирован: **пуш тега `vX.Y.Z` → workflow `Release`** соберёт,
прогонит тесты, подпишет бинарники, посчитает контрольные суммы и создаст GitHub
Release с артефактами. Ниже — что проверить до, во время и после.

---

## 0. Одноразовая настройка (перед самым первым релизом)

**Быстрый путь (Variant A, самоподпись):** на Windows-dev-машине запусти
```powershell
pwsh -File setup-signing.ps1 -Repo chuck-uz/kid-control
```
Скрипт создаст самоподписанный серт, экспортирует `signing/kidcontrol-codesign.pfx` (приватный) и
`signing/kidcontrol-codesign.cer` (публичный), посчитает **правильный** SHA-256-отпечаток
(над DER-байтами — именно его пинует верификатор), и заведёт секреты
`CODE_SIGNING_PFX_BASE64` / `CODE_SIGNING_PFX_PASSWORD` в репозиторий. Отпечаток он распечатает.

Ручной путь / детали:
- [ ] **Сертификат.** Variant A — `setup-signing.ps1` (бесплатно, приватное использование).
      Variant B — купить OV/EV code-signing (нет предупреждений SmartScreen, чистая цепочка).
- [ ] **Секреты репозитория** (Settings → Secrets → Actions): `CODE_SIGNING_PFX_BASE64`,
      `CODE_SIGNING_PFX_PASSWORD`. Без них workflow соберёт **неподписанные** бинарники (с предупреждением).
- [ ] **SHA-256 отпечаток** запиши — он идёт в установку (`deploy.bat`: `KC_THUMBPRINT=...`,
      или `--thumbprint`), попадает в `Update.TrustedThumbprint`, и self-update принимает только твой релиз.
- [ ] **Variant A на целевых ПК:** `.cer` нужно доверить. `deploy.bat` делает это сам, если задать
      `KC_CERT_FILE=<путь к kidcontrol-codesign.cer>` (импорт в Trusted Root + Trusted Publisher).
- [ ] Реши видимость Release (репозиторий приватный → релизы тоже видны только тебе).

> ⚠️ **НЕ коммить `signing/` и `*.pfx`** — приватный ключ. Папка уже в `.gitignore`.

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
- [ ] **Автообновление (по умолчанию `Update.AutoInstall=true`):** в течение
      `UpdateConfig.CheckInterval` (6 ч; для теста уменьши) работающая служба сама находит
      релиз, шлёт в Telegram «⬇️ Устанавливаю обновление …», ставит его и перезапускается уже
      на новой версии — без ручных действий. Если `AutoInstall=false` — только уведомление.
- [ ] Обновление проходит полностью автоматически: `UpdateService` выбирает setup-**zip**,
      сверяет размер и (если задан) SHA-256, распаковывает его с защитой от zip-slip,
      проверяет Authenticode-подпись+отпечаток **всех** `KidControl*.exe`, и только затем
      запускает `KidControl.Installer.exe /update --source <распакованное>` (конфиг и таймер
      сохраняются). Ручной путь (`update.bat`) — тоже рабочий.
- [ ] Релиз содержит `KidControl-Setup-<tag>.zip` (иначе `UpdateService` откатится на bare `.exe`,
      у которого нет payload-бинарников — self-update не применит обновление).

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
