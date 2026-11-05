# Архитектура HexTactics

Документ описывает фактическое устройство кода. Актуален для версии **v0.1.0**.

## 1. Технологии

| Компонент | Версия |
|---|---|
| Движок | Godot **4.7.2 stable .NET** (`4.7.2.stable.mono.official.ed1daf0bf`) |
| Язык / рантайм | C# 12, .NET SDK 8.0.133, `net8.0` |
| Рендерер | Compatibility (OpenGL 3.3), MSAA 4× |
| Тесты | xUnit 2.9 |
| Сборка релизов | PowerShell 7 / Windows PowerShell 5.1 (`tools/*.ps1`) |

## 2. Структура репозитория

```text
TactickGame/                     корень репозитория (логический «HexTactics/» из ТЗ)
  HexTactics.sln                 решение: Debug / ExportDebug / ExportRelease
  Directory.Build.props          единая версия продукта для всех .NET-проектов
  CLAUDE.md README.md CHANGELOG.md
  docs/                          GAME_SPEC, ARCHITECTURE, CONTROLS, DECISIONS, releases/vX.Y.Z/
  src/
    Core/                        HexTactics.Core — чистая модель (без Godot)
      Hex/                       HexCoord, OffsetCoord, HexDirection, HexLayout, PlaneVector
      Map/                       TileType, TileState, HexGrid, MapDefinition, MapParser
    GodotClient/                 Godot-проект (project.godot), сборка HexTactics.dll
      scenes/Main.tscn           корневая сцена (узлы строятся кодом в Main.cs)
      scripts/App/               Main (композиционный корень), BuildInfo
      scripts/Presentation/      BoardView, HexFillLayer, HexMeshBuilder, CameraRig, VisualConfig
      scripts/InputLayer/        BoardPicker (экран → плоскость → гекс)
      scripts/UI/                DebugHud
      scripts/Debug/             AutomationRunner (скриншоты/сценарии из командной строки)
      shaders/                   tile.gdshader (границы клеток), overlay.gdshader (подсветки)
      maps/prototype.hexmap      данные карты 12×10
      export_presets.cfg         пресеты "Windows Desktop" и "Linux"
  tests/Core.Tests/              HexTactics.Core.Tests (xUnit)
  tools/                         build_release.ps1, verify_release.ps1, restore_version.ps1,
                                 create_release_tags.ps1, common.ps1
  release_archives/vX.Y.Z/       ZIP Windows-сборки + .sha256 + BUILD_INFO.txt (вне папки игры)
```

Соответствие логической схеме ТЗ: `src/Core` = Core, `src/GodotClient` = GodotClient. Слой
`Simulation` появится в v0.2.0, `Interaction` (автомат ввода) — в v0.3.0, каждый отдельной сборкой
без зависимости от Godot.

## 3. Слои и зависимости

```text
GodotClient (HexTactics.dll) ──► Core (HexTactics.Core.dll)
tests/Core.Tests ──────────────► Core
```

- **Core** хранит правду о карте: тип клетки, проходимость, зоны появления, база. Ничего не знает о камере и цветах.
- **Presentation** строит визуальные слои по данным модели и никогда не принимает игровых решений.
- **Input** переводит курсор в координаты карты через проекцию камеры; правил не содержит.
- **UI / Debug** — панель HUD (версия, клетка под курсором, легенда) и автоматизация.

## 4. Координаты

- Гексы **pointy-top**, хранение — **odd-r offset** (`OffsetCoord(Col, Row)`, `Col 0..11`, `Row 0..9`;
  нечётные ряды сдвинуты вправо на половину ширины).
- Все алгоритмы — в **axial/cube** (`HexCoord(Q, R)`, `S = -Q-R`). Конвертация:
  `q = col - (row - (row & 1)) / 2`, `r = row`.
- Шесть направлений `HexDirection` (против часовой от востока): `East, NorthEast, NorthWest, West, SouthWest, SouthEast`.
  Этот порядок — фиксированный порядок обхода соседей (важно для детерминизма поиска пути).
  `HexDirection` и есть тип направления взгляда (`FacingDirection` из ТЗ).
- Плоскость карты: `PlaneVector(X, Z)`; X — восток (вправо), Z — юг (к камере). В Godot: `Vector3(X, y, Z)`.
- `HexLayout(size)`: центр гекса `x = size·(√3·q + √3/2·r)`, `z = size·1.5·r`; обратное преобразование
  с округлением в cube-координатах. Размер гекса в клиенте — 1.0 (сторона = радиус).

## 5. Формат карты (`*.hexmap`)

```text
// комментарий
@name Прототип 12×10
@size 12 10              колонки ряды
@base База игрока        имя базы
@zone 1 Северный край    id (1..9) и имя зоны появления
. . . . . . . 1 1 1 . .  по одной строке на ряд, токены через пробел
```

Токены: `.` проходимая, `#` препятствие, `B` база игрока, `1`..`9` зона появления с этим id.
Клетки зон появления обязаны лежать на внешнем крае карты (проверяется парсером).
Порядок клеток зоны/базы — построчный (row-major); он используется как детерминированный порядок.

## 6. Визуальные слои

| Порядок | Слой | Реализация |
|---|---|---|
| 0 | Поле | `BoardView`: один `ArrayMesh` — шестигранные призмы без зазоров; препятствия — поднятые колонны |
| 1 | Проходимость (с v0.4) | `HexFillLayer` (MultiMesh, полупрозрачная заливка, `render_priority`) |
| 2 | Маршрут/стрелки/ТД (с v0.3) | — |
| 3 | Юниты (с v0.2) | — |
| 3+ | Курсорный гекс | `HexFillLayer` с белой заливкой α = 0.22 |
| UI | HUD | `CanvasLayer` слева, масштабируется по высоте окна |

- **Границы клеток** рисует шейдер `tile.gdshader`: UV.x вершины верхней грани = расстояние до края
  гекса в мировых единицах (апофема в центре, 0 на ребре). Ширина линии задаётся в мировых
  единицах (`line_half_width = 0.011` на каждую сторону ≈ 1,5–2 px при 1920×1080), с нижним порогом
  в пикселях через `fwidth`, чтобы линии не пропадали в маленьком окне. Сглаживание — `smoothstep`.
- **Подсветки** никогда не меняют материал клетки: отдельные полупрозрачные шестиугольники, приподнятые на
  0,004 над поверхностью (без z-fighting), `depth_draw_never`, без теней.
- **Цвета**: Compatibility-рендерер затеняет в sRGB-пространстве, поэтому цвета вершин передаются как есть
  (`VisualConfig.ToRenderColor` переключится на линейные при смене рендерера).

## 7. Камера

`CameraRig`: ортографическая `Camera3D`, поворот `-60°` вокруг X — **возвышение 60° над плоскостью**
(90° = строго сверху). При каждом изменении размера окна/HUD камера вписывает AABB поля (включая запас
по высоте под юниты) в свободную область окна справа от HUD: `Camera.Size = k·H`, где `k` — мировых единиц на
пиксель; затем камера сдвигается в своей плоскости так, чтобы поле было по центру свободной области.

## 8. Ввод: экран → гекс

`BoardPicker`: луч камеры (`ProjectRayOrigin/Normal`) пересекается сначала с плоскостью верха
препятствий (они загораживают то, что за ними), затем с плоскостью земли `y = 0`; точка плоскости →
`HexLayout.PlaneToHex` → проверка `HexGrid.Contains`. Метод не зависит от угла и положения камеры,
поэтому будущие зум/панорамирование не потребуют изменений.

## 9. Автоматизация (скриншоты, смоук-тесты)

Аргументы пользователя после `--` (работают и в экспортированной сборке):

| Аргумент | Действие |
|---|---|
| `--hx-script="a;b;c"` | шаги: `hover:col,row`, `mouse:x,y`, `wait:N` |
| `--hx-frames=N` | минимальное число кадров до завершения |
| `--hx-screenshot=path.png` | сохранить кадр и выйти (если нет `--hx-stay`) |
| `--hx-quit` | выйти после шагов (смоук-тест) |

## 10. Версия и сборка

- `Directory.Build.props` → `Version`; .NET SDK добавляет хеш коммита в `AssemblyInformationalVersion`
  (`0.1.0+<sha>`), HUD показывает версию и короткий коммит.
- `project.godot` → `config/version` (метаданные exe). Скрипт релиза проверяет совпадение версий и тега.
- Экспорт: `godot --headless --path src/GodotClient --export-release "Windows Desktop" <out>/HexTactics.exe`
  → `HexTactics.exe`, `HexTactics.console.exe` (с консолью/логом), `HexTactics.pck`,
  `data_HexTactics_windows_x86_64/` (самодостаточный .NET 8 рантайм и сборки игры).
