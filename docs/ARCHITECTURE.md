# Архитектура HexTactics

Документ описывает фактическое устройство кода. Актуален для версии **v0.7.0** (ТЗ ч.1 + ч.2: flat-top, непрерывные черновики, обязательный финальный взгляд).
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
  docs/                          GAME_SPEC (ТЗ 0.1, историческое), GAME_SPEC_PART2 (ТЗ 0.2, приоритетнее),
                                 ARCHITECTURE, CONTROLS, DECISIONS, releases/vX.Y.Z/
  src/
    Core/                        HexTactics.Core — чистая модель (без Godot)
      Hex/                       HexCoord, OffsetCoord, HexDirection, HexLayout, PlaneVector, HexLine
      Map/                       TileType, TileState, HexGrid, MapDefinition, MapParser
      Units/                     UnitId, Side, UnitState, UnitMotion, GameState (занятость + резервы)
      Pathfinding/               Pathfinder (A*, DistanceField, Reachable)
      Orders/                    Waypoint (+OrderKind), OrderQueue (+FinalFacing), WaypointRules, OrderMode,
                                 OrderDraft, OrderDraftValidator, IndividualOrder
      Spawning/                  SpawnMethod, SpawnRequest, SpawnRules, SpawnError
    Simulation/                  HexTactics.Simulation — GameSimulation, SimulationClock, ISimulationSystem,
                                 MovementSystem, события (без Godot)
    Interaction/                 HexTactics.Interaction — InputStateMachine, DraftView (без Godot)
    GodotClient/                 Godot-проект (project.godot), сборка HexTactics.dll
      scenes/Main.tscn           корневая сцена (узлы строятся кодом в Main.cs)
      scripts/App/               Main (композиционный корень), BuildInfo
      scripts/Presentation/      BoardView, HexFillLayer, HexMeshBuilder, CameraRig, UnitView, UnitsView,
                                 OrdersView (стрелки и ТД), VisualConfig
      scripts/InputLayer/        BoardPicker (экран → плоскость → гекс), PointerInput (события Godot → автомат),
                                 InputBindings (действия InputMap: пробел — пауза, Esc — отмена)
      scripts/UI/                DebugHud, UiText (русские тексты сообщений)
      scripts/Debug/             AutomationRunner (скриншоты/сценарии из командной строки)
      shaders/                   tile.gdshader (границы клеток), overlay.gdshader (подсветки)
      maps/prototype.hexmap      данные карты 12×10
      export_presets.cfg         пресеты "Windows Desktop" и "Linux"
  tests/
    Shared/                      общие хелперы тестов (TestMaps, TestPaths), подключаются ссылкой
    Core.Tests/                  координаты, раскладка, карта, гекс-линия, поиск пути
    Simulation.Tests/            появление, направления, занятость, приказы, движение
    Interaction.Tests/           автомат ввода, приёмочные тесты §8 (AcceptanceTests A01–A12)
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
- **Simulation** — единственная точка изменения состояния (`GameSimulation`): команды, фиксированный тик, пауза,
  системы правил (`ISimulationSystem`, сейчас — движение), события.
- **Interaction** — конечный автомат ввода в координатах карты: получает «образцы указателя» (`PointerSample`), отдаёт команды симуляции и структурированную обратную связь.
- **Presentation** строит визуальные слои по данным модели и никогда не принимает игровых решений.
- **Input glue** (`PointerInput`, `BoardPicker`) переводит события и курсор в `PointerSample`.
- **UI / Debug** — панель HUD, тексты сообщений, автоматизация.

## 4. Координаты (flat-top, с v0.6.0)

- Гексы **flat-top** (плоская сторона вверху), хранение — **odd-q offset** (`OffsetCoord(Col, Row)`, `Col 0..11`
  по горизонтали, `Row 0..9` по вертикали; **нечётные колонки сдвинуты вниз на половину высоты гекса**).
  Pointy-top/odd-r из ТЗ 0.1 запрещены (ТЗ ч.2 §2.5); миграция — DECISIONS «Миграции».
- Все алгоритмы — в **axial/cube** (`HexCoord(Q, R)`, `S = -Q-R`). Конвертация odd-q:
  `q = col`, `r = row - (col - (col & 1)) / 2`; обратно `row = r + (q - (q & 1)) / 2`.
- Шесть направлений `HexDirection` — **по рёбрам** flat-top гекса, по часовой стрелке от верхнего ребра:
  `North, NorthEast, SouthEast, South, SouthWest, NorthWest` (углы на плоскости 90°, 30°, −30°, −90°, −150°, 150°).
  Это и фиксированный порядок обхода соседей, и тип направления взгляда (`FacingDirection` из ТЗ).
  Axial-смещения: N (0,−1), NE (+1,−1), SE (+1,0), S (0,+1), SW (−1,+1), NW (−1,0). Колонка — прямая вертикальная линия.
- Плоскость карты: `PlaneVector(X, Z)`; X — восток (вправо), Z — юг (к камере). В Godot: `Vector3(X, y, Z)`.
- `HexLayout(size)` (= `HexLayoutFlatTop` из ТЗ ч.2 §10): центр `x = size·1.5·q`, `z = size·√3·(r + q/2)`;
  обратное — `q = ⅔·x/size`, `r = (−⅓·x + √3/3·z)/size` + округление в cube. Вершина 0 — восточная, ребро вершин
  1–2 — верхнее (плоское). Ширина гекса 2·size, высота √3·size, шаг колонок 1,5·size, расстояние между центрами √3·size.
  Поле 12×10: 18,5 × 10,5√3 ≈ 18,5 × 18,2 единиц.
- Направление на плоскости → одно из шести: `HexDirections.FromPlaneVector(x, z)` (угол в плоскости карты, не экранный).

## 5. Формат карты (`*.hexmap`)

```text
// комментарий
@layout flat-top odd-q   обязательно; другие ориентации (в т. ч. старые pointy-top) отклоняются
@name Прототип 12×10
@size 12 10              колонки ряды
@base База игрока        имя базы
@zone 1 Северный край    id (1..9) и имя зоны появления
. . . . . . 1 1 1 . . .  по одной строке на ряд, токены через пробел (нечётные колонки — на полгекса ниже)
```

Токены: `.` проходимая, `#` препятствие, `B` база игрока, `D` зона выставления (ростер, с v0.8.0),
`1`..`9` зона появления с этим id (симуляционные/отладочные спавны с края; игроку из ростера не разрешена).
Клетки зон появления обязаны лежать на внешнем крае карты (проверяется парсером).
Порядок клеток зоны/базы/выставления — построчный (row-major); он используется как детерминированный порядок.

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

## 7. Автомат ввода (`InputStateMachine`, с v0.7.0 — по ТЗ ч.2)

Режимы: `Idle`, `UnitSelected`, `SingleDraft` (открыт черновик), отладочные `Placement`, `ObstacleEdit`.

| Состояние | Событие | Результат |
|---|---|---|
| `Idle` / `UnitSelected` | клик ЛКМ по юниту (корпус или его логическая клетка) | `UnitSelected` |
| `UnitSelected` | клик ЛКМ в секторе текущей клетки выбранного юнита (вне нейтральной зоны) | `ApplyOrder(поворот на месте)` |
| `Idle` / `UnitSelected` | ЛКМ на юните + смещение > 8 px | `SingleDraft` (`Held`), источник — `CommandAnchor` |
| `Idle` / `UnitSelected` | ЛКМ на **последней** подтверждённой ТД + смещение | `SingleDraft` с оставшимися ТД (продолжение, D-042) |
| `SingleDraft` | отпускание первого протягивания / клик ЛКМ: зелёная клетка | +ТД, черновик остаётся открытым, живая стрелка от новой ТД |
| `SingleDraft` | … в секторе исходной клетки | `ApplyOrder(ТД…, финальный взгляд)` → `UnitSelected` |
| `SingleDraft` | … в нейтральной зоне исходной клетки | ничего |
| `SingleDraft` | … на клетке врага | `AttackTargetReserved` (резерв, D-044), черновик без изменений |
| `SingleDraft` | … на красной клетке (вне поля, препятствие, занято, изолировано) | отмена черновика (`InvalidTarget`) |
| `SingleDraft` | наведение на красную клетку | только показ (красная подсветка/стрелка) |
| `SingleDraft` | ПКМ по ТД черновика | удалить её и следующие |
| `SingleDraft` | ПКМ в другом месте / Esc | отмена черновика (`ByUser`) |
| `UnitSelected` | ПКМ по подтверждённой ТД выбранного юнита | `TruncateOrders` (её и следующие) |

- `DraftView` (каждый кадр, `Refresh()`): `Waypoints`, `Anchor`, `Source`, `Pointer`, `TargetHex`, `Status`
  (`Valid`, `FacingSector`, `Neutral`, `AttackTarget`, `OutOfBounds`, `NotWalkable`, `Occupied`, `Unreachable`, `NoTarget`),
  `SectorFacing`, `UnitFacingPreview` (первый шаг пути или сектор поворота), `Reachable`, `Path`, `Held`, `IsEdit`.
- `HoverTurnFacing` — сектор текущей клетки выбранного юнита под курсором (предпросмотр поворота без черновика).
- Сектор = ближайшее из 6 направлений к вектору «центр → курсор» в плоскости карты; нейтральная зона r = 0,3 (D-040).
- Валидность клетки = `WaypointRules.ValidateTarget(..., reachable)`; применение = `OrderDraftValidator.ValidateChain`
  в `ApplyOrder` — одни и те же правила (зелёное на экране = будет принято).
- Нажатия над HUD не попадают в автомат; отпускания передаются всегда, чтобы жест не «зависал».

## 7.1. Приказы движения в модели

- `IndividualOrder(Unit, Waypoints, FinalFacing)` — подтверждённый приказ; без ТД — поворот на месте.
- `GameSimulation.ApplyOrder` — транзакция (D-038): вся цепочка от `CommandAnchor` проверяется, при ошибке
  `OrderApplyRejected(FailedIndex, Error)` и старая очередь не меняется; иначе очередь заменяется, `FinalFacing` сохраняется.
- `GameSimulation.TruncateOrders(unit, waypointId)` — ПКМ по подтверждённой ТД.
- `MovementSystem`: когда очередь выполнена, применяется `FinalFacing` (`FinalFacingApplied`), D-043.


- `OrderQueue` юнита: `Waypoint(Id, Sequence, Cell)` в порядке исполнения. Изменяется только через `GameState`
  (`AppendWaypoint`, `ClearWaypoints`, `CompleteCurrentWaypoint`), версия очереди растёт при каждом изменении.
- `GameSimulation.IssueMoveOrder(unit, target, mode)`: источник сегмента — `CommandAnchor` (Replace) или последняя ТД
  (Append); при ошибке очередь не меняется (`OrderRejected`), при успехе `OrderIssued`.
- `RotateInPlace` разрешён только без ТД и не во время шага. Исполнение — §7.2.

## 7.2. Поиск пути и движение

- **A\*** (`Pathfinder.FindPath`): шестисоседняя сетка, эвристика cube distance, стоимость входа в клетку `MoveCost`
  (сейчас 1 → кратчайший = минимум шагов). Препятствия: непроходимые клетки + логические клетки других юнитов
  (`GameState.ObstaclesFor(unit)`); стартовая клетка препятствием не считается. Детерминизм — D-024.
- **Достижимость** (`Pathfinder.Reachable`, Дейкстра/BFS) — для подсветки всех клеток при протягивании.
- **Тик**: `GameSimulation.Advance(realDelta)` → `SimulationClock` выдаёт целое число тиков по 1/30 с (максимум 8 за вызов),
  `Step()` выполняет один тик: `MovementSystem.Tick`. `InterpolationAlpha` — доля следующего тика (только для отрисовки).
- **Шаг** (`UnitMotion`): в центре гекса — завершение достигнутых ТД, при необходимости перепланирование пути до текущей ТД,
  резерв следующей клетки, поворот взгляда по направлению шага. `Progress += speed × dt`; при 0,5 — `GameState.MoveUnit`
  (единственный момент смены логической клетки), исходная клетка остаётся в резерве до конца шага (D-025).
- **Нет пути** → `Blocked` + `UnitBlocked(reason)`, приказы сохраняются; при изменении топологии — новая попытка,
  `UnitUnblocked` (D-026). Занятая следующая клетка → перепланирование, зарезервированная → `Waiting`.
- **Команды на ходу**: `CommandAnchor` = `StepTo ?? Cell` (D-027).
- События: `StepStarted`, `UnitEnteredCell`, `WaypointReached`, `UnitBlocked`, `UnitUnblocked`, `MovementFinished`,
  `FacingChanged`, `TerrainChanged`.

## 7.3. Тактическая пауза и игровой цикл

- Кадр Godot: `GameSimulation.Advance(delta)` → 0…8 фиксированных тиков → события → ввод/представление.
  Игровое время = `Tick × 1/30 с` и меняется только тиками; время отрисовки/интерфейса от него не зависит.
- Пауза: `SetPaused(true)` — `Advance` ничего не делает и **не накапливает** время; снятие паузы продолжает с того же
  `Progress` шага (без скачка). Пробел — действие `hx_toggle_pause` (`InputBindings`), обрабатывается вне автомата
  приказов. На паузе работают выбор, протягивание, подсветка, построение и замена цепочек (D-030).
- Системы правил: `ISimulationSystem.Tick(SimulationContext)` для каждой зарегистрированной системы по порядку;
  контекст даёт `State`, `Tick`, `DeltaSeconds`, `Emit(event)` (D-031).

## 8. Визуальные слои

| Порядок | Слой | Реализация |
|---|---|---|
| 0 | Поле | `BoardView`: один `ArrayMesh` — шестигранные призмы без зазоров; препятствия — поднятые колонны |
| 1 | Проходимость | `HexFillLayer` (MultiMesh, полупрозрачная заливка, `render_priority = 1`): при протягивании все 120 клеток — зелёные (можно поставить ТД) / красные (препятствие, занято, изолировано), исходная — жёлтая; в режиме выставления — зелёные/красные по правилам появления |
| 2 | Маршрут/стрелки/ТД — **верхний слой** | `OrdersView`, все материалы **без теста глубины** (`NoDepthTest`), т. е. поверх юнитов, препятствий и любых объектов сцены (ТЗ ч.2 §3.1); порядок внутри слоя — `render_priority` 20…28: цепочки «юнит → ТД1 → ТД2…» **белыми** толстыми прямыми стрелками (ширина ½ стороны гекса в плоскости поля), стрелка протягивания, тонкие линии путей (тёмная — маршрут, зелёная — предпросмотр), белые маркеры ТД с тёмной каймой и номерами. HUD (CanvasLayer) — над ними |
| 3 | Юниты | `UnitsView` → `UnitView`: цилиндр (r 0,28), **белая мини-стрелка у ребра гекса** в направлении взгляда с тёмной обводкой (видна всегда, не только при выделении), тор выделения, красный «!» при остановке; позиция интерполируется между центрами гексов |
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
- **Направление взгляда**: маленький белый «дротик» с выемкой лежит на земле у середины соответствующего ребра гекса
  (0,58…0,80 от центра при апофеме 0,866), с тёмной обводкой. Визуальный поворот плавный, данные — дискретные.

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
| `--hx-quit` | выйти после шагов (смоук-тест); код выхода 1, если провалилась хоть одна проверка `expect:` |

Шаги: `hover:c,r`, `mouse:x,y`, `click:c,r`, `rclick:c,r`, `press:P`, `move:P`, `release:P`, `drag:P1>P2>…`
(события мыши через `Input.ParseInputEvent` — тот же путь, что у реальной мыши), `spawn:base`, `spawn:zone:N`,
`spawn:cell:c,r`, `place`, `obstacle:c,r` (переключить препятствие), `select:id`, `key:Escape`, `key:Space`,
`face:NorthEast`, `wait:N`, `pause[:on|off]`, `mark`, `ticks:N`, `waitidle:ID[:maxFrames]` (ждать, пока юнит выполнит ТД).

Шаги v0.7: `click:P`/`rclick:P` принимают точку с `@Direction:dist` (клик в секторе), `rpress:P`, `draft` (открыть
черновик выбранного юнита). Проверки: `expect:draft:none|N`, `expect:units:N`, `expect:cell:ID:c,r`, `expect:waypoints:ID:N`, `expect:facing:ID:Dir`,
`expect:status:ID:Idle|Moving|Waiting|Blocked`, `expect:paused:true|false`, `expect:same-tick` (тик не изменился
после `mark`). Итог печатается как `[automation] checks passed: N` или `FAILED k of N`.
Регрессионные сценарии релиза — `tools/verify_release.ps1` (`$RegressionScenarioV05` для v0.5–v0.6, `$RegressionScenarioV07` для v0.7+). Точка `P` — `c,r` (центр гекса) или
`c,r@Direction:dist` (смещение от центра в плоскости карты, в размерах гекса), например `drag:9,8>9,8@NorthWest:0.6`.

## 12. Версия и сборка

- `Directory.Build.props` → `Version`; .NET SDK добавляет хеш коммита в `AssemblyInformationalVersion`
  (`0.7.0+<sha>`), HUD показывает версию и короткий коммит.
- `project.godot` → `config/version` (метаданные exe). Скрипт релиза проверяет совпадение версий и тега.
- Экспорт: `godot --headless --path src/GodotClient --export-release "Windows Desktop" <out>/HexTactics.exe`
  → `HexTactics.exe`, `HexTactics.console.exe` (с консолью/логом), `HexTactics.pck`,
  `data_HexTactics_windows_x86_64/` (самодостаточный .NET 8 рантайм и сборки игры).
