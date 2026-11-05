# Архитектура HexTactics

Документ описывает фактическое устройство кода. Актуален для версии **v0.2.0**.
Состояние документа для прошлых версий — в соответствующих Git-тегах.

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
      Units/                     UnitId, Side, UnitState, GameState
      Orders/                    Waypoint, OrderQueue
      Spawning/                  SpawnMethod, SpawnRequest, SpawnRules, SpawnError
    Simulation/                  HexTactics.Simulation — GameSimulation, события, настройки (без Godot)
    Interaction/                 HexTactics.Interaction — InputStateMachine (без Godot)
    GodotClient/                 Godot-проект (project.godot), сборка HexTactics.dll
      scenes/Main.tscn           корневая сцена (узлы строятся кодом в Main.cs)
      scripts/App/               Main (композиционный корень), BuildInfo
      scripts/Presentation/      BoardView, HexFillLayer, HexMeshBuilder, CameraRig, UnitView, UnitsView, VisualConfig
      scripts/InputLayer/        BoardPicker (экран → плоскость → гекс), PointerInput (события Godot → автомат)
      scripts/UI/                DebugHud, UiText (русские тексты сообщений)
      scripts/Debug/             AutomationRunner (скриншоты/сценарии из командной строки)
      shaders/                   tile.gdshader (границы клеток), overlay.gdshader (подсветки)
      maps/prototype.hexmap      данные карты 12×10
      export_presets.cfg         пресеты "Windows Desktop" и "Linux"
  tests/
    Shared/                      общие хелперы тестов (TestMaps, TestPaths), подключаются ссылкой
    Core.Tests/                  координаты, раскладка, карта
    Simulation.Tests/            появление, направления, занятость
    Interaction.Tests/           автомат ввода
  tools/                         build_release.ps1, verify_release.ps1, restore_version.ps1,
                                 create_release_tags.ps1, common.ps1
  release_archives/vX.Y.Z/       ZIP Windows-сборки + .sha256 + BUILD_INFO.txt (вне папки игры)
```

Соответствие логической схеме ТЗ: `src/Core` = Core, `src/Simulation` = Simulation, `src/GodotClient` = GodotClient.
Конечный автомат ввода вынесен в отдельную чистую сборку `src/Interaction`, чтобы тестировать сценарии мыши без движка;
связка с событиями Godot (`PointerInput`) остаётся в клиенте.

## 3. Слои и зависимости

```text
GodotClient (HexTactics.dll) ──► Interaction ──► Simulation ──► Core
           └──────────────────────────────────► Simulation, Core
tests/*  ──► соответствующие сборки
```

- **Core** хранит правду о состоянии: карта, юниты, занятость, очереди приказов, правила появления. Ничего не знает о камере, цветах и мыши.
- **Simulation** — единственная точка изменения состояния (`GameSimulation`): команды, события. Игровой тик и движение — с v0.4.
- **Interaction** — конечный автомат ввода в координатах карты: получает «образцы указателя» (`PointerSample`), отдаёт команды симуляции и структурированную обратную связь.
- **Presentation** строит визуальные слои по данным модели и никогда не принимает игровых решений.
- **Input glue** (`PointerInput`, `BoardPicker`) переводит события и курсор в `PointerSample`.
- **UI / Debug** — панель HUD, тексты сообщений, автоматизация.

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
- Направление на плоскости → одно из шести: `HexDirections.FromPlaneVector(x, z)` (угол в плоскости карты, не экранный).

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

## 6. Состояние игры, юниты и появление

- `GameState`: карта, юниты (`SortedDictionary` по `UnitId` — детерминированный порядок), занятость `HexCoord → UnitId`.
  Инварианты: юнит стоит ровно в одной проходимой клетке поля; в клетке не больше одного юнита.
  `MoveUnit` — единственное место смены логической клетки (атомарный перенос занятости).
  `OccupancyVersion`/`TopologyVersion` растут при любом изменении занятости/рельефа — кэши (подсветка, достижимость) сравнивают их.
- `UnitState`: `Id` (уникален, не переиспользуется), `Side`, `Cell`, `Facing` (одно из 6), `SpeedHexesPerSecond`
  (по умолчанию 2), `Orders` (`OrderQueue` из `Waypoint`). Изменение — только через `GameState`/`GameSimulation`.
- `SpawnRules.Resolve(state, request)`:
  - **база** — первая свободная клетка базы в порядке карты (или указанная клетка базы);
  - **зона** — первая свободная клетка указанной зоны (или указанная клетка этой зоны);
  - **прямое выставление** — ровно указанная клетка.
  Общая проверка `ValidateCell`: в пределах поля → проходима → свободна. Ошибки: `OutOfBounds`, `NotWalkable`,
  `Occupied`, `BaseFull`, `ZoneFull`, `UnknownZone`, `NoPlayerBase`, `CellNotInZone`, `MissingCell`.
  При ошибке состояние не меняется: никто не удаляется и не переносится.
- Направление при появлении — к центру карты (квантуется до 6 направлений), если не задано явно.
- `GameSimulation.Spawn/SetFacing` порождают события `UnitSpawned`, `SpawnRejected`, `FacingChanged`
  (`DrainEvents()`); будущие системы добавляют свои события, а не вмешиваются в обработчики ввода.

## 7. Автомат ввода (`InputStateMachine`)

| Состояние | Событие | Результат |
|---|---|---|
| `Idle` / `UnitSelected` | щелчок ЛКМ по юниту (корпус или клетка) | `UnitSelected(юнит)` |
| `Idle` / `UnitSelected` | щелчок ЛКМ по пустой клетке / вне поля | `Idle` |
| любое | `EnterPlacementMode` (кнопка, P) | `Placement` |
| `Placement` | щелчок ЛКМ по клетке | `GameSimulation.Spawn(AtCell)`; успех → `UnitSelected(новый)`, отказ → остаётся `Placement` + сообщение |
| `Placement` | Esc / щелчок ПКМ | прежнее выделение (`UnitSelected`) или `Idle` |

- Щелчок = отпускание той же кнопки при смещении ≤ `DragThresholdPixels` (8 px); большее смещение — жест перетаскивания (v0.3).
- Нажатия над HUD не попадают в автомат; отпускания передаются всегда, чтобы жест не «зависал».
- Обратная связь: `SelectionChanged`, `PlacementModeChanged`, `SpawnAttempted` → тексты формирует `UiText` в клиенте.

## 8. Визуальные слои

| Порядок | Слой | Реализация |
|---|---|---|
| 0 | Поле | `BoardView`: один `ArrayMesh` — шестигранные призмы без зазоров; препятствия — поднятые колонны |
| 1 | Проходимость | `HexFillLayer` (MultiMesh, полупрозрачная заливка, `render_priority = 1`): в режиме выставления зелёный/красный |
| 2 | Маршрут/стрелки/ТД (с v0.3) | — |
| 3 | Юниты | `UnitsView` → `UnitView`: цилиндр, «дротик» на крышке и стрелка на земле (взгляд), тор выделения |
| 3+ | Курсорный гекс | `HexFillLayer` с белой заливкой α = 0.22, `render_priority = 3` |
| UI | HUD | `CanvasLayer` слева, масштабируется по высоте окна |

- **Границы клеток** рисует шейдер `tile.gdshader`: UV.x вершины верхней грани = расстояние до края
  гекса в мировых единицах (апофема в центре, 0 на ребре). Ширина линии задаётся в мировых
  единицах (`line_half_width = 0.011` на каждую сторону ≈ 1,5–2 px при 1920×1080), с нижним порогом
  в пикселях через `fwidth`. Сглаживание — `smoothstep`.
- **Подсветки** никогда не меняют материал клетки: отдельные полупрозрачные шестиугольники (α 0.15),
  приподнятые на 0,004 над поверхностью (без z-fighting), `depth_draw_never`, без теней.
- **Цвета**: Compatibility-рендерер затеняет в sRGB-пространстве, поэтому цвета вершин передаются как есть
  (`VisualConfig.ToRenderColor` переключится на линейные при смене рендерера).
- **Направление взгляда**: «дротик» с выемкой (однозначная форма) на крышке цилиндра виден при любом из 6 направлений;
  стрелка на земле указывает на соседний гекс. Визуальный поворот плавный, данные — дискретные.

## 9. Камера

`CameraRig`: ортографическая `Camera3D`, поворот `-60°` вокруг X — **возвышение 60° над плоскостью**
(90° = строго сверху). При каждом изменении размера окна/HUD камера вписывает AABB поля (с запасом
по высоте под юниты) в свободную область окна справа от HUD: `Camera.Size = k·H`, где `k` — мировых единиц на
пиксель; затем камера сдвигается в своей плоскости так, чтобы поле было по центру свободной области.

## 10. Ввод: экран → карта

- `BoardPicker`: луч камеры пересекается сначала с плоскостью верха препятствий (они загораживают то,
  что за ними), затем с плоскостью земли `y = 0`; точка плоскости → `HexLayout.PlaneToHex`. Клетка возвращается
  и вне поля — границы проверяет автомат/модель.
- `UnitsView.PickUnit`: отрезок луча между высотой корпуса и землёй проецируется на плоскость; попадание —
  если расстояние до оси юнита ≤ 0,42. Берётся ближайший к камере.
- Метод не зависит от угла и положения камеры: будущие зум/панорамирование не потребуют изменений.

## 11. Автоматизация (скриншоты, смоук-тесты)

Аргументы пользователя после `--` (работают и в экспортированной сборке):

| Аргумент | Действие |
|---|---|
| `--hx-script="a;b;c"` | последовательность шагов (см. ниже) |
| `--hx-frames=N` | минимальное число кадров до завершения |
| `--hx-screenshot=path.png` | сохранить кадр и выйти (если нет `--hx-stay`) |
| `--hx-quit` | выйти после шагов (смоук-тест) |

Шаги: `hover:c,r`, `mouse:x,y`, `click:c,r`, `rclick:c,r` (события мыши через `Input.ParseInputEvent` — тот же путь,
что у реальной мыши), `spawn:base`, `spawn:zone:N`, `spawn:cell:c,r`, `place`, `key:Escape`, `face:NorthEast`, `wait:N`.

## 12. Версия и сборка

- `Directory.Build.props` → `Version`; .NET SDK добавляет хеш коммита в `AssemblyInformationalVersion`
  (`0.2.0+<sha>`), HUD показывает версию и короткий коммит.
- `project.godot` → `config/version` (метаданные exe). Скрипт релиза проверяет совпадение версий и тега.
- Экспорт: `godot --headless --path src/GodotClient --export-release "Windows Desktop" <out>/HexTactics.exe`
  → `HexTactics.exe`, `HexTactics.console.exe` (с консолью/логом), `HexTactics.pck`,
  `data_HexTactics_windows_x86_64/` (самодостаточный .NET 8 рантайм и сборки игры).
