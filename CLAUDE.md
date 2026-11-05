# CLAUDE.md — правила работы с проектом HexTactics

Этот файл читается Claude Code перед любыми изменениями. Актуальное ТЗ — `docs/GAME_SPEC.md`.

## Порядок работы

1. Прочитать `CLAUDE.md`, `docs/GAME_SPEC.md`, `docs/ARCHITECTURE.md`, `docs/DECISIONS.md`, `CHANGELOG.md`, историю Git (`git log --oneline`, `git tag`).
2. Реализовывать **только очередную версию** из схемы версий ТЗ (§1.1), сохраняя все работающие функции.
3. Перед кодом — короткий план и список файлов. Не переписывать фундамент без необходимости; несовместимые изменения сначала описать в `docs/DECISIONS.md` (раздел «Миграции»).
4. После изменений: сборка, тесты, сценарии мыши (автоматизация `--hx-script`), экспорт Windows.
5. Версия считается готовой только после проверки запуска.

## Слои и правила кода

| Слой | Где | Правило |
|---|---|---|
| Core (домен) | `src/Core` → `HexTactics.Core` | Чистый C#, **без Godot**. Правда о состоянии игры. |
| Simulation | `src/Simulation` → `HexTactics.Simulation` (с v0.2) | Фиксированный тик, приказы, пауза, события. Без Godot. |
| Interaction | `src/Interaction` → `HexTactics.Interaction` (с v0.3) | Конечный автомат ввода в мировых координатах. Без Godot. |
| Presentation / Input glue / UI | `src/GodotClient` (Godot-проект, сборка `HexTactics`) | Только отображение, перевод экран → карта, HUD. **Никаких игровых правил.** |

- Шестисоседняя гекс-система (pointy-top, odd-r offset, вычисления в axial/cube). Не использовать TileMap/прямоугольный pathfinding.
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
```

Godot: **4.7.2 stable .NET** (не переходить на beta/dev). .NET SDK 8, `TargetFramework net8.0`.
Рендерер — Compatibility (OpenGL 3.3); он затеняет в sRGB-пространстве (см. `VisualConfig.ToRenderColor`).

## Версии и релизы

- Версия задаётся в двух местах и должна совпадать: `Directory.Build.props` (`HexTacticsVersion`) и `src/GodotClient/project.godot` (`config/version`).
- Процедура релиза vX.Y.Z:
  1. Обновить версию, `CHANGELOG.md`, `README.md`, `docs/ARCHITECTURE.md`, создать `docs/releases/vX.Y.Z/README.md`.
  2. Коммит, затем аннотированный тег: `git tag -a vX.Y.Z -m "HexTactics vX.Y.Z"`. Теги неизменяемы — не удалять и не переносить.
  3. `pwsh tools/build_release.ps1` (тесты + экспорт + ZIP + SHA-256 + BUILD_INFO) → `release_archives/vX.Y.Z/`.
  4. `pwsh tools/verify_release.ps1 -Version X.Y.Z [-Run]`.
  5. Отдельным коммитом добавить архив (`release_archives/**` хранится через Git LFS), push ветки и тега.
- Никогда не перезаписывать существующие архивы и не переписывать опубликованную историю.
- В Git не коммитить `.godot/`, `bin/`, `obj/`, `build/`, `staging/`.

## Отчёт после версии

Кратко: что сделано, что проверено (тесты, сценарии, экспорт), какие файлы затронуты, где архив, как откатиться.
