# CLAUDE.md — правила работы с проектом HexTactics

Этот файл читается Claude Code перед любыми изменениями. ТЗ: `docs/GAME_SPEC.md` (редакция 0.1, историческая,
**не изменять**) и `docs/GAME_SPEC_PART2.md` (редакция 0.2, **переопределяет** противоречащие пункты 0.1).

## Порядок работы

1. Прочитать `CLAUDE.md`, `docs/GAME_SPEC.md`, `docs/GAME_SPEC_PART2.md`, `docs/ARCHITECTURE.md`, `docs/DECISIONS.md`,
   `CHANGELOG.md`, историю Git (`git log --oneline`, `git tag`).
2. Реализовывать **только очередную версию** из плана версий, сохраняя все работающие функции. Выпущенные v0.1.0–v0.5.0
   неизменяемы; план ТЗ ч.2 §13 реализуется версиями v0.6.0–v0.10.0 (соответствие — DECISIONS D-033).
3. Перед кодом — короткий план и список файлов. Не переписывать фундамент без необходимости; несовместимые изменения сначала описать в `docs/DECISIONS.md` (раздел «Миграции»).
4. После изменений: сборка, тесты, сценарии мыши (автоматизация `--hx-script`), экспорт Windows.
5. Версия считается готовой только после проверки запуска.

## Слои и правила кода

| Слой | Где | Правило |
|---|---|---|
| Core (домен) | `src/Core` → `HexTactics.Core` | Чистый C#, **без Godot**. Правда о состоянии игры. |
| Simulation | `src/Simulation` → `HexTactics.Simulation` | Фиксированный тик 1/30 с, приказы, движение, пауза, события; новые правила — через `ISimulationSystem`. Без Godot. |
| Interaction | `src/Interaction` → `HexTactics.Interaction` | Конечный автомат ввода в координатах карты. Без Godot. |
| Presentation / Input glue / UI | `src/GodotClient` (Godot-проект, сборка `HexTactics`) | Только отображение, перевод экран → карта, HUD. **Никаких игровых правил.** |

- Шестисоседняя гекс-система **flat-top, odd-q offset**, вычисления в axial/cube; направления — по рёбрам
  (N, NE, SE, S, SW, NW). Pointy-top/odd-r запрещены (ТЗ ч.2 §2.5). Не использовать TileMap/прямоугольный pathfinding.
- Стрелки и маркеры приказов — в верхнем слое без теста глубины (поверх всех объектов сцены). Личные — белые, командные — голубые.
- Проходимость — только из данных (`TileState`), не из цвета/материала.
- Маршрут хранится в модели, а не в визуальных линиях. Правила не зависят от FPS.
- Боевые механики, ИИ, экономику и т. п. **не добавлять** без нового этапа ТЗ.
- Комментарии в коде — на английском, документация — на русском.

## Команды

```bash
dotnet build HexTactics.sln                 # сборка всех проектов
dotnet test HexTactics.sln                  # автотесты
godot --headless --path src/GodotClient --import
godot --path src/GodotClient                # запуск из исходников
# скриншот / сценарий (аргументы после --):
godot --path src/GodotClient --windowed --resolution 1920x1080 -- --hx-script="hover:5,5" --hx-screenshot=shot.png
# сценарий с проверками (код выхода 1 при провале), шаги — docs/ARCHITECTURE.md «Автоматизация»:
godot --path src/GodotClient -- --hx-script="spawn:base;drag:0,4>3,2;key:Space;expect:paused:true" --hx-quit
```

Godot: **4.7.2 stable .NET** (не переходить на beta/dev). .NET SDK 8, `TargetFramework net8.0`.
Рендерер — Compatibility (OpenGL 3.3); он затеняет в sRGB-пространстве (см. `VisualConfig.ToRenderColor`).

## Версии и релизы

- Версия задаётся в двух местах и должна совпадать: `Directory.Build.props` (`HexTacticsVersion`) и `src/GodotClient/project.godot` (`config/version`).
- Процедура релиза vX.Y.Z:
  1. Обновить версию, `CHANGELOG.md`, `README.md`, `docs/ARCHITECTURE.md`, создать `docs/releases/vX.Y.Z/README.md`.
  2. Коммит, затем аннотированный тег: `git tag -a vX.Y.Z -m "HexTactics vX.Y.Z"`. Теги неизменяемы — не удалять и не переносить.
  3. `pwsh tools/build_release.ps1` (тесты + экспорт + ZIP + SHA-256 + BUILD_INFO) → `release_archives/vX.Y.Z/`.
  4. `pwsh tools/verify_release.ps1 -Version X.Y.Z -Run` (на Linux: `-Launcher wine64` под `xvfb-run`,
     `WINEDLLOVERRIDES="dinput8=d"`) — сквозной регрессионный сценарий в самой сборке.
  5. Отдельным коммитом добавить архив (`release_archives/**`, обычные файлы Git, без LFS — см. D-010), скриншот
     `docs/releases/vX.Y.Z/screenshot.png`; push ветки и тега. Если push тега запрещён средой (облачная сессия),
     тег восстанавливается позже `tools/create_release_tags.ps1` по `BUILD_INFO.txt` (D-012).
- Никогда не перезаписывать существующие архивы и не переписывать опубликованную историю.
- В Git не коммитить `.godot/`, `bin/`, `obj/`, `build/`, `staging/`.

## Отчёт после версии

Кратко: что сделано, что проверено (тесты, сценарии, экспорт), какие файлы затронуты, где архив, как откатиться.
