# ADR 0002: Стабильный корень данных вне Velopack + graceful-stop bmbd (P1–P5)

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

### Решение
**Вариант А:** Переиспользовать существующий механизм `StdinLifeline` («stdin-lifeline»). 
При остановке ноды Desktop-приложение должно запускать `bmbd` (процесс ноды) с перенаправлением стандартного ввода (`RedirectStandardInput = true`), передавать переменную окружения `BMB_STDIN_LIFELINE=1` и для graceful shutdown закрывать его `StandardInput` (посылая EOF) вместо принудительного уничтожения процесса через `Process.Kill()`.

### Результаты живого эксперимента
В рамках спайка P5 в коде `bmbd` (`desktop/BeeMemoryBank.Node`) был успешно реализован и протестирован механизм `StdinLifeline`.
Проведённый живой эксперимент и интеграционный тест `E2E_GracefulStop_ViaStdinLifeline` показали:
1. При получении EOF в стандартном потоке ввода главный процесс `bmbd` перехватывает сигнал.
2. Процесс корректно запускает процедуру graceful shutdown, последовательно останавливая Kestrel (front app) и вызывая `NodeOrchestrator.StopAsync()`.
3. `NodeOrchestrator.StopAsync()` в свою очередь закрывает `StandardInput` для дочерних процессов `BeeMemoryBank.Api` и `BeeMemoryBank.Web`, которые реагируют на EOF и штатно завершают свою работу (в логах фиксируется `Stdin closed, exiting with code 0`).
4. Файлы статуса ноды (`node.status.json` и `.runtime.json`) успешно удаляются с диска, освобождается монопольный файловый замок на `node.lock`.
5. Процесс ноды завершается с кодом возврата `0`.

### Что было изменено в кодовой базе (ветка `feat/0-p5-gracefulstop`)

1. **`desktop/BeeMemoryBank.Node/Program.cs`**
   - **Строка ~198 (в начале `RunOrchestratorAsync`)**: Добавлена инициализация `StdinLifeline` при наличии переменной окружения `BMB_STDIN_LIFELINE=1`. Callback-функция при наступлении EOF выполняет асинхронную остановку `app` (если он запущен) и `orchestrator`, после чего устанавливает результат `tcs.TrySetResult(0)`.
   - **Строки ~317, ~327, ~342 (внутри `RunOrchestratorAsync` в блоках инициализации и повторного бинда Kestrel)**: Добавлены проверки `tcs.Task.IsCompleted` для исключения гонки — если сигнал EOF пришёл до окончания бинда портов Kestrel, Node не пытается инициализировать/пересобирать фронт-сервер, а сразу выходит из функции.
   - **Строка ~385 (в блоке `finally` метода `RunOrchestratorAsync`)**: Добавлен вызов `lifeline?.Dispose();` для своевной очистки ресурсов.

2. **`tests/BeeMemoryBank.Node.Tests/EndToEndIntegrationTests.cs`**
   - **Строки ~174-318**: Добавлен полноценный интеграционный тест `E2E_GracefulStop_ViaStdinLifeline`, который эмулирует полный жизненный цикл ноды (запуск дочерних процессов, перевод в статус `Ready`, закрытие `StandardInput`, верификация graceful shutdown ноды и детей с кодом 0, проверка очистки файлов статуса). Тест успешно проходит и подтверждает стабильность решения.

### Дополнение (Этап 1, найдено при повторных прогонах): реальный баг гонки + остаточная нестабильность

При многократных повторных прогонах `E2E_GracefulStop_ViaStdinLifeline` (не в единичном запуске — баг проявлялся только при закрытии stdin практически СРАЗУ после готовности, повторно и подряд) обнаружен и подтверждён живой репродукцией (напрямую, без `dotnet test`/vstest, чтобы исключить влияние тестовой инфраструктуры) настоящий, серьёзный баг гонки в `NodeOrchestrator.WaitForAllReadyOrFailureAsync`:

- `StopAsync()` безусловно выставляет `AllReady = false` как часть остановки. Если остановка (через stdin EOF) стартует практически одновременно с моментом готовности — до того как поллинг-цикл `WaitForAllReadyOrFailureAsync` успевает увидеть `AllReady == true` — цикл продолжает ждать условие, которое **структурно никогда больше не станет true**, а `cancellationToken` в этом сценарии (`CancellationToken.None`) никогда не отменяется → **бесконечное зависание процесса**, при этом сам оркестратор уже успешно и полностью остановил детей и залогировал «Stopped successfully».
- **Исправлено:** добавлена проверка `_isStopping` внутри цикла (`NodeOrchestrator.cs`, `WaitForAllReadyOrFailureAsync`) — если остановка уже начата, цикл бросает `OperationCanceledException` вместо бесконечного ожидания. В `Program.cs`: внешний `catch` теперь распознаёт этот случай и возвращает результат из `tcs` (корректный exit code штатной остановки), а не считает это ошибкой запуска (`return 3`).
- **Дополнительно:** `Environment.Exit(exitCode)` в конце `Main` как защитный пояс-и-подтяжки (наблюдалось, что возврат из `Main` не всегда надёжно завершает сам OS-процесс).
- **Результат после первого фикса:** резко возросшая надёжность (было near-0% при повторных прогонах подряд → стало ~80-90%).
- **Второй баг гонки (найден узким Codex-ревью Этапов 0+1, тоже исправлен):** `StopAsync()` отменял `_lifecycleCts` **ДО** закрытия stdin детей — child-lifecycle-петля (`RunChildLifecycleAsync`'s `await exitTask` = `WaitForExitAsync(process, stoppingToken)`) видела отмену токена и **немедленно хардкиллила** процесс, опережая собственную graceful-последовality `StopAsync`'а (закрыть stdin → подождать 5с → kill-фолбэк). Именно эта гонка почти наверняка была источником оставшейся ~1-из-5-8 нестабильности. **Исправлено:** `cts.Cancel()` перенесён на конец `StopAsync()` — ПОСЛЕ `await Task.WhenAll(stopTasks)` (после того как дети уже гарантированно остановлены штатно или через kill-фолбэк). Живая проверка: **10/10** повторных прогонов `E2E_GracefulStop_ViaStdinLifeline` подряд, стабильно ~600-900мс каждый — race полностью устранена.
- **Важно — что этот фикс НЕ покрывает:** реальный путь выхода Desktop-оболочки (`MainWindow.axaml.cs`, `StopNodeProcess`) до сих пор запускает `bmbd` с `RedirectStandardInput = false` и останавливает его голым `Process.Kill(entireProcessTree: true)` — stdin-lifeline вообще не используется на этом пути. Это **не регрессия и не пробел Этапов 0/1** — интеграция graceful-stop в реальный Desktop-жизненный цикл явно запланирована как часть Этапа 4 (`_СУПЕРПЛАН-МУЛЬТИАККАУНТ.md` §4.3, `NodeLifecycleService`: «Graceful-останов вместо голого Kill… Это чинит и сегодняшний Exit»). До Этапа 4 реальный UX выхода из трея остаётся хардкилльным, как и был.
