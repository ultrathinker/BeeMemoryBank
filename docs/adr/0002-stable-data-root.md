# ADR 0002: Стабильный корень данных вне Velopack (P1–P4 — эмпирические пробы)

## Status
Accepted (P1–P4 в этом документе выполнены мной напрямую живыми пробами на throwaway
Velopack-пакете `BmbProbeApp`, отдельном от реальной установки BeeMemoryBank, чтобы не трогать
повторно данные пользователя. P5 — отдельная секция, добавлена параллельно другим агентом).

## Context
Живой репродукцией подтверждено (см. `docs/implementation plans/_СУПЕРПЛАН-МУЛЬТИАККАУНТ.md`
§1): Desktop-приложение хранит `beememorybank.db` внутри `current\data\` — версионированной
папки Velopack. Перед тем как выбирать новый стабильный корень, нужно эмпирически проверить, а
не предполагать, как Velopack реально обращается с посторонними файлами при update/uninstall/
repair, и работает ли `VelopackApp.Build().Run()`.

## Методология проб
Throwaway .NET-приложение `BmbProbeApp` (win-x64, self-contained), упакованное Velopack с
собственным `packId=BmbProbeApp` (полностью отдельный `%LOCALAPPDATA%\BmbProbeApp`, не трогает
реальную установку `%LOCALAPPDATA%\BeeMemoryBank`). В `current\data\` и в корне установки
(сиблинг `current\`/`packages\`) размещались файлы-маркеры перед каждым циклом.

## P1 — проба update-apply

**Действие:** `vpk pack` v1.0.0 → silent-install → маркеры в `current\data\mymarker.txt` и в
корне `myrootmarker.txt` → `vpk pack` v1.0.1 (с delta) → **реальное применение** через
`Update.exe apply --package <v1.0.1-full.nupkg> --norestart` (это ТОЧНО тот же путь, что
`UpdateManager.ApplyUpdatesAndRestart()` вызывает в проде — не Setup.exe, не Repair).

**Результат:**
- `current\data\mymarker.txt` — **уничтожен**. Каталог `current\data\` после апдейта не
  существует вообще (пересобран с нуля из пакета v1.0.1).
- `myrootmarker.txt` (сиблинг `current\`, на уровне `Update.exe`) — **пережил** update-apply
  без изменений.
- Копии старого `current\` (staging/backup) после apply — **не найдено**; `packages\` содержит
  только nupkg-файлы (полный/делта), сам старый `current\` не сохраняется отдельно.

**Вывод:** обновление трогает только `current\` (и его содержимое целиком, независимо от
глубины вложенности) и `packages\`. Сиблинг-файлы на уровне установки update НЕ удаляет.

## P2 — проба uninstall

**Действие:** `Update.exe uninstall --silent` над установкой с выжившим после P1
`myrootmarker.txt` в корне.

**Результат — решающий для §2.1 плана:**
```
[INFO] Removing directory '"C:\Users\evgeny\AppData\Local\BmbProbeApp"'
...
[INFO] Scheduling removal of install directory...
[INFO] Running: cmd.exe /C choice /C Y /N /D Y /T 3 & rmdir /s /q "C:\Users\evgeny\AppData\Local\BmbProbeApp"
```
Деинсталлятор выполняет **`rmdir /s /q` над ВСЕЙ корневой папкой установки безусловно** — не
«удалить только известные файлы пакета». `myrootmarker.txt`, переживший update (P1), полностью
уничтожен вместе со всей папкой при uninstall.

**Вывод — подтверждает решение §2.1 в пользу FABLE:** сиблинг-подход CODEX
(`%LOCALAPPDATA%\BeeMemoryBank\data` рядом с `current\`) был бы безопасен при ОБНОВЛЕНИИ (P1),
но гарантированно уничтожен при ДЕИНСТАЛЛЯЦИИ (P2) — то есть нарушал бы прямое требование
самого СУПЕРПЛАНа «деинсталляция: data-каталог остаётся». Только полностью отдельный корень
(`%LOCALAPPDATA%\BeeMemoryBankData`, вне `%LOCALAPPDATA%\BeeMemoryBank`) безопасен в обоих
сценариях одновременно.

## P3 — проба repair / установки поверх

Отдельный throwaway-цикл не потребовался: уже есть прямая живая репродукция на РЕАЛЬНОЙ
установке BeeMemoryBank этой же ночью (см. `_СУПЕРПЛАН-МУЛЬТИАККАУНТ.md` §0 и переписку с
пользователем) — после `Repair` на реальном Velopack-инсталляторе BeeMemoryBank
`current\data\beememorybank.db` заменился на новый пустой файл (4096 байт), созданный ровно в
момент Repair. Механизм идентичен P1 (Repair — это тоже install-apply поверх существующего
`current\`). Дополнительная проба избыточна — вывод P1 полностью покрывает и Repair-семантику.

## P4 — спайк `VelopackApp.Build().Run()`

**Действие:** добавлен `Velopack` NuGet (1.2.0) в `BmbProbeApp`, `VelopackApp.Build()
.OnAfterUpdateFastCallback(...).Run()` первой строкой `Main`. Пакет собран **без**
`--skipVeloAppCheck`.

**Результат:**
1. Паковщик подтвердил хук статическим анализом: `Verified VelopackApp.Run() in
   'System.Void Program::<Main>$(System.String)'.` — упаковка проходит успешно, `--skipVeloAppCheck`
   не нужен, если код реально вызывает `VelopackApp.Build().Run()`.
2. Хуки вызываются через специальные CLI-флаги, которые `Update.exe` подставляет при запуске
   приложения (не через отдельный API): подтверждено `--veloapp-updated <version>` (вызывает
   `OnAfterUpdateFastCallback` — проверено прямым вызовом,
   `hook-afterupdate.txt` создан с корректным содержимым `"AfterUpdate 1.0.2"`); в реальном логе
   `Update.exe apply` также замечены `--veloapp-obsolete <version>` (у СТАРОЙ версии, перед
   уходом) и `--veloapp-uninstall <version>` (при удалении). Отдельный флаг для `OnFirstRun` не
   подтверждён прямым вызовом (не первостепенно для этого плана — используется реже, чем
   after-update — не блокирует Этап 3, при необходимости перепроверяется точечно там).

**Вывод:** механизм полностью рабочий. Этап 3 может безопасно добавить
`VelopackApp.Build().Run()` первой строкой `Program.cs` Desktop-приложения и снять
`--skipVeloAppCheck` из `pack-windows.ps1` — упаковка не сломается, хуки реально сработают.

## Итоговое решение (см. также §2.1 СУПЕРПЛАНа)

Стабильный корень данных — **`%LOCALAPPDATA%\BeeMemoryBankData`**, полностью отдельный от
`%LOCALAPPDATA%\BeeMemoryBank` (корень установки Velopack). Обоснование опирается на P1+P2
одновременно: любое место ВНУТРИ корня установки Velopack — хоть внутри `current\`, хоть
сиблингом — гарантированно уничтожается хотя бы одним штатным жизненным циклом Velopack
(update — только `current\`; uninstall — весь корень целиком). Только выход за пределы корня
установки безопасен и при update, и при uninstall одновременно.

---

## P5 — спайк graceful-stop для bmbd

_Секция ниже добавляется параллельным агентом по итогам отдельного спайка (см. задачу
`feat/0-p5-gracefulstop`)._
