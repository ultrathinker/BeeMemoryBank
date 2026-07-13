# ADR 0002: Stable Data Root and Node Management

## P5 — graceful-stop для bmbd

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
