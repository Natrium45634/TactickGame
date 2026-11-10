# Архитектура HexTactics

Документ описывает фактическое устройство кода. Актуален для версии **v0.36.0** (ТЗ ч.1–3: flat-top, непрерывные
черновики, ростер, команды, плавное движение 1 гекс/с, HP и обычная атака, «Линейный удар» с юнита и из ТД; ТЗ ч.4:
стартовое меню «Играть»/«Разработчик», библиотека контента, каталог разработчика, Undo/Redo, редакторы карт, юнитов,
навыков и типов гексов, ядро эффектов, поверхности, импорт/экспорт объектов и пакетов; ТЗ ч.5: единый расчёт урона,
типы урона, сопротивления, правила защиты, щиты и реакции, «Механики»; ТЗ ч.6: единый реестр определений, каталог ссылок,
зависимости, защита A и правила боя, пассивы, виды, теги и стая; ТЗ ч.7: автоатака, блок, сценарии и обычный бой, маршруты врагов, волны и Tower Defense, простой ИИ и оборона). Формат контента —
`docs/CONTENT_FORMAT.md`.
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
  docs/                          GAME_SPEC (ТЗ 0.1), GAME_SPEC_PART2 (0.2), GAME_SPEC_PART3 (0.3), GAME_SPEC_PART4 (0.4,
                                 актуальная), ARCHITECTURE, CONTROLS, CONTENT_FORMAT, EDITOR_GUIDE, MIGRATIONS, DECISIONS,
                                 RELEASE_CHECKLIST, releases/vX.Y.Z/
  src/
    Core/                        HexTactics.Core — чистая модель (без Godot)
      Hex/                       HexCoord, OffsetCoord, HexDirection, HexLayout, PlaneVector, HexLine
      Map/                       TileType, TileState, HexGrid, MapDefinition, MapParser
      Units/                     UnitId, Side, UnitState, UnitMotion, GameState (занятость + резервы)
      Pathfinding/               Pathfinder (A*, DistanceField, Reachable)
      Orders/                    Waypoint (+OrderKind), OrderQueue (+FinalFacing, GroupId), WaypointRules, OrderMode,
                                 OrderDraft, OrderDraftValidator, IndividualOrder, GroupOrder (+GroupSlotAssignment,
                                 GroupOrderState), GroupRoutePlanner (+GroupStageEvaluator) — с v0.9.0
      Spawning/                  SpawnMethod, SpawnRequest, SpawnRules, SpawnError, PlacementRules (ростер)
      Motion/                    CubicSegment, MotionCurve, StepGeometry, StepPlan, MotionCorridor (с v0.11.0)
      Combat/                    UnitStats, LifeState, AttackStatus, CombatState (с v0.12.0)
      Skills/                    SkillDefinition (+SkillCell), SkillCatalog, SkillAction, WaypointSkillSpec (с v0.13.0/v0.14.0)
      Effects/                   EffectSpec, EffectTargets, EffectTypes (с v0.15.0); StatusEffect, StackingPolicy, Stats,
                                 StatModifiers (с v0.18.0); TileTrigger, TileEffect, TileSides (с v0.19.0)
      Roster/                    RosterState, RosterEntry, RosterEntryState (с v0.8.0)
    Simulation/                  HexTactics.Simulation — GameSimulation, SimulationClock, ISimulationSystem,
                                 MovementSystem, SkillSystem, AttackSystem, TileEffectSystem (с v0.19.0), StatusSystem
                                 (с v0.18.0), EffectResolver,
                                 события (без Godot)
    Content/                     HexTactics.Content (с v0.15.0) — Model (ContentRef, ContentIssue, ContentIds), Schema
                                 (FieldSchema), Kinds (MapData, UnitData, SkillData, TileData, EffectData), Registry
                                 (ContentTypeRegistry, EffectTypeRegistry), Storage (ContentRepository), Library
                                 (ContentLibrary, ContentItem, ContentSerializer, MapChecks), Runtime (RuntimeFactory),
                                 Transfer (ContentExporter, ContentImporter, PackageManifest — с v0.20.0)
    Editor/                      HexTactics.Editor (с v0.15.0) — History (CommandHistory, команды), Library (LibraryBrowser,
                                 LibraryCommands), Maps (MapEditorModel, с v0.16.0), Definitions (DefinitionEditorModel),
                                 Units (UnitEditorModel, CombatPreviewText — с v0.17.0), Skills (SkillEditorModel,
                                 EffectPreview — с v0.18.0), Tiles (TileEditorModel, TilePreview — с v0.19.0),
                                 Transfer (ExportPlan, ImportSession — с v0.20.0)
    Interaction/                 HexTactics.Interaction — InputStateMachine (+ .Group.cs — режим «Команда»), DraftView,
                                 PlacementView, GroupDraftView (без Godot)
    GodotClient/                 Godot-проект (project.godot), сборка HexTactics.dll
      scenes/App.tscn            корневая сцена (с v0.15.0; узлы строятся кодом)
      scripts/App/               AppRoot (стартовое меню, режимы, автоматизация), ContentService (загрузка библиотеки),
                                 BuildInfo
      scripts/MainMenu/          StartMenu («Играть» / «Разработчик» / «Выход»)
      scripts/GameMode/          GameMode (режим «Играть»: матч из библиотеки, бывший Main)
      scripts/DeveloperMode/     DeveloperMode (каталог категорий, список, свойства, сообщения, реестр редакторов, черновики),
                                 Editors/IContentEditor, Editors/DefinitionEditorShell, Editors/Map/MapEditor (с v0.16.0),
                                 Editors/Unit/UnitEditor (с v0.17.0), Editors/Skill/SkillEditor (с v0.18.0),
                                 Editors/Tile/TileEditor (с v0.19.0),
                                 SharedInspector/Inspector
      scripts/Presentation/      BoardView, HexFillLayer, HexMeshBuilder, CameraRig, UnitView, UnitsView,
                                 OrdersView (стрелки и ТД), VisualConfig, TilePalette (вид типов гексов из библиотеки)
      scripts/InputLayer/        BoardPicker (экран → плоскость → гекс), PointerInput (события Godot → автомат),
                                 InputBindings (действия InputMap: пробел — пауза, Esc — отмена)
      scripts/UI/                DebugHud, RosterBar (нижняя панель ростера), WaypointMenuPanel (меню ТД, v0.14.0),
                                 UiText (русские тексты сообщений), UiKit (тема, кнопки, AutomationIds), ModalDialog
      scripts/Debug/             AutomationRunner (скриншоты/сценарии из командной строки), AutomationInput
      shaders/                   tile.gdshader (границы клеток), overlay.gdshader (подсветки)
      data/content/              встроенный контент (JSON): tiles/, units/, skills/, maps/, index.json
      maps/prototype.hexmap      прежний текстовый формат карты — только эталон для тестов (игра его не читает)
      export_presets.cfg         пресеты "Windows Desktop" и "Linux"
  tests/
    Shared/                      общие хелперы тестов (TestMaps, TestPaths), подключаются ссылкой
    Core.Tests/                  координаты, раскладка, карта, гекс-линия, поиск пути
    Simulation.Tests/            появление, направления, занятость, приказы, движение, ростер (RosterTests)
    Interaction.Tests/           автомат ввода, черновики, перетаскивание карточек, приёмочные тесты (A01–A12, P01–P20)
    Content.Tests/               встроенный контент, формат, проверки, хранилище, реестры (+ Support: TempDirectory, ContentTestKit)
    Editor.Tests/                Undo/Redo, каталог и библиотечные команды
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
           ├──────────────────► Editor ──► Content ──────────► Core
           └──────────────────────────────────► Simulation, Content, Core
tests/*  ──► соответствующие сборки
```

- **Content** — определения игрового контента и всё, что с ними делают без движка: формат, реестры, проверки, хранилище,
  `RuntimeFactory` (определение → модель Core). Simulation от Content не зависит.
- **Editor** — логика режима «Разработчик» (история команд, модели каталога и редакторов) без Godot.

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
- `UnitState`: `Id` (уникален, не переиспользуется), `Side`, `Cell`, `Facing` (одно из 6; `null` только у
  выставленного из ростера и ещё не активного юнита), `Lifecycle` (`Active` | `PlacedPendingFacing`), `RosterEntryId`,
  `SpeedHexesPerSecond` (по умолчанию 2), `Orders` (`OrderQueue` из `Waypoint`). Изменение — только через
  `GameState`/`GameSimulation`. `MovementSystem` пропускает неактивных юнитов.
- `SpawnRules.Resolve(state, request)`:
  - **база** — первая свободная клетка базы в порядке карты (или указанная клетка базы);
  - **зона** — первая свободная клетка указанной зоны (или указанная клетка этой зоны);
  - **прямое выставление** — ровно указанная клетка.
  Общая проверка `ValidateCell`: в пределах поля → проходима → свободна. Ошибки: `OutOfBounds`, `NotWalkable`,
  `Occupied`, `BaseFull`, `ZoneFull`, `UnknownZone`, `NoPlayerBase`, `CellNotInZone`, `MissingCell`.
  При ошибке состояние не меняется: никто не удаляется и не переносится.
- Направление при появлении — к центру карты (квантуется до 6 направлений), если не задано явно.
- **Ростер (с v0.8.0, ТЗ ч.2 §5, D-046–D-048).** `GameState.Roster` — `RosterState` с карточками `RosterEntry {Id, Name,
  State, Unit}`; состояния `AvailableInRoster → PlacementPreview → PlacedPendingFacing → Active` (`Unavailable` — резерв).
  `PlacementRules.ValidateCell`: в поле → клетка базы или зоны выставления (`B`/`D`, только из данных карты) → проходима →
  свободна и не зарезервирована; иначе `OutOfBounds`, `NotAllowedHere`, `NotWalkable`, `Occupied`.
  `GameSimulation.BeginRosterPlacement` / `CancelRosterPlacement` / `PlaceFromRoster(entry, cell)` — юнит создаётся с
  `Facing = null` и `PlacedPendingFacing`; первый принятый `ApplyOrder` или `SetFacing` активирует его (`UnitActivated`).
  При отказе ничего не создаётся, карточка снова доступна (`PlacementRejected`).
- `GameSimulation.Spawn/SetFacing` порождают события `UnitSpawned`, `SpawnRejected`, `FacingChanged`
  (`DrainEvents()`); будущие системы добавляют свои события, а не вмешиваются в обработчики ввода.

## 7. Автомат ввода (`InputStateMachine`, с v0.7.0 — по ТЗ ч.2)

Режимы: `Idle`, `UnitSelected`, `SingleDraft` (открыт черновик), `RosterDrag` (перетаскивается карточка ростера, v0.8.0),
`GroupCollect` / `GroupDraft` (режим «Команда», v0.9.0), `SkillAim` (прицеливание навыка, v0.13.0; от ТД — v0.14.0),
отладочные `Placement`, `ObstacleEdit`. Меню ТД (v0.14.0) — не отдельный режим, а `WaypointMenu` поверх `SingleDraft`/`UnitSelected`.

| Состояние | Событие | Результат |
|---|---|---|
| `Idle` / `UnitSelected` | клик ЛКМ по юниту (корпус или его логическая клетка) | `UnitSelected` |
| `UnitSelected` | клик ЛКМ в секторе текущей клетки выбранного юнита (вне нейтральной зоны) | `ApplyOrder(поворот на месте)` |
| `Idle` / `UnitSelected` | ЛКМ на юните + смещение > 8 px | `SingleDraft` (`Held`), источник — `CommandAnchor` |
| `Idle` / `UnitSelected` | ЛКМ на **последней** подтверждённой ТД + смещение | `SingleDraft` с оставшимися ТД (продолжение, D-042) |
| `SingleDraft` | отпускание первого протягивания / клик ЛКМ: зелёная клетка | +ТД, черновик остаётся открытым, живая стрелка от новой ТД |
| `SingleDraft` | … в секторе исходной клетки | `ApplyOrder(ТД…, финальный взгляд)` → `UnitSelected` |
| `SingleDraft` | … в нейтральной зоне исходной клетки | ничего |
| `SingleDraft` | … на живом враге (тело или клетка) | `ApplyOrder(ТД…, AttackTarget)` — приказ атаки (v0.12.0, D-070) |
| `SingleDraft` | короткий клик по центру маркера ТД черновика (r 0,3) | `WaypointMenu` этой ТД, ТД не добавляется (v0.14.0) |
| `SingleDraft` | … на красной клетке (вне поля, препятствие, занято, изолировано) | отмена черновика (`InvalidTarget`) |
| `SingleDraft` | наведение на красную клетку | только показ (красная подсветка/стрелка) |
| `SingleDraft` | ПКМ по ТД черновика | удалить её и следующие |
| `SingleDraft` | ПКМ в другом месте / Esc | отмена черновика (`ByUser`) |
| `UnitSelected` | ПКМ по подтверждённой ТД выбранного юнита | `TruncateOrders` (её и следующие) |
| `UnitSelected` | короткий клик по подтверждённой ТД выбранного юнита | `WaypointMenu` этой ТД (v0.14.0) |
| меню ТД открыто | «✦ Линейный удар» (`BeginWaypointSkillAim`) / «✕» (`ClearWaypointSkillFromMenu`) | `SkillAim` от ТД / навык снят |
| меню ТД открыто | ЛКМ мимо меню, ПКМ, Esc | закрыть только меню |
| `UnitSelected` | «✦ Линейный удар (R)» (`BeginSkillAim`) | `SkillAim` от юнита (v0.13.0) |
| `SkillAim` | движение | `SkillAimView`: направление (1 из 6 или нет в центре), две клетки области |
| `SkillAim` | ЛКМ вне нейтрального центра | от юнита — `UseSkill`; от ТД черновика — `OrderDraft.SetSkill`, возврат в `SingleDraft`; от подтверждённой ТД — `AssignWaypointSkill` |
| `SkillAim` | ПКМ / Esc | отменить только прицеливание (черновик и маршрут не меняются) |
| любой | ЛКМ на доступной карточке ростера (`BeginRosterDrag`) | `RosterDrag`, открытый черновик отменяется |
| `RosterDrag` | движение | `PlacementView` (клетка под курсором, ошибка, разрешённые клетки) |
| `RosterDrag` | отпускание на разрешённой свободной клетке | `PlaceFromRoster` → сразу `SingleDraft` нового юнита |
| `RosterDrag` | отпускание на запрещённой клетке / вне поля / над панелью, ПКМ, Esc | карточка возвращается (`RosterCardReturned`) |
| любой | клик по юниту в `PlacedPendingFacing` или по карточке выставленного | выбор; у ожидающего — черновик открывается сам |
| любой | карточка «Команда» (`ToggleGroupMode`) | `GroupCollect`; прочие черновики/режимы закрываются, выбор снимается |
| `GroupCollect` | клик по своему юниту не из команды | участник (первый — капитан), ≤ 7 |
| `GroupCollect` / `GroupDraft` | клик по участнику | ничего (без дублей, капитан не меняется) |
| `GroupCollect` (есть капитан) / `GroupDraft` | клик по зелёной клетке (команда помещается) | +КТД (`GroupDraft`) |
| `GroupDraft` | клик в секторе последней КТД | `ApplyGroupOrder` → `UnitSelected` (капитан) |
| `GroupCollect` / `GroupDraft` | клик по красной клетке | отмена (`GroupCancelled`, `InvalidTarget`) |
| `GroupDraft` | ПКМ по КТД | удалить её и следующие (без КТД → `GroupCollect`) |
| `GroupCollect` / `GroupDraft` | ПКМ в другом месте / Esc / карточка «Команда» | отмена, режим закрыт |

- `DraftView` (каждый кадр, `Refresh()`): `Waypoints`, `Anchor`, `Source`, `Pointer`, `TargetHex`, `Status`
  (`Valid`, `FacingSector`, `Neutral`, `AttackTarget`, `OutOfBounds`, `NotWalkable`, `Occupied`, `Unreachable`, `NoTarget`),
  `SectorFacing`, `UnitFacingPreview` (первый шаг пути или сектор поворота), `Reachable`, `Path`, `Held`, `IsEdit`.
- `HoverTurnFacing` — сектор текущей клетки выбранного юнита под курсором (предпросмотр поворота без черновика).
- Сектор = ближайшее из 6 направлений к вектору «центр → курсор» в плоскости карты; нейтральная зона r = 0,3 (D-040).
- Валидность клетки = `WaypointRules.ValidateTarget(..., reachable)`; применение = `OrderDraftValidator.ValidateChain`
  в `ApplyOrder` — одни и те же правила (зелёное на экране = будет принято).
- Нажатия над HUD и панелью ростера не попадают в автомат; отпускания передаются всегда, чтобы жест не «зависал».
  Нажатие на карточку обрабатывает UI (`RosterBar.CardPressed` → `BeginRosterDrag`); отпускание после него
  `PointerInput` передаёт автомату, а выборка над панелями в `RosterDrag` не содержит клетки.

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
- **Шаг** (`UnitMotion`): между шагами — завершение достигнутых ТД, при необходимости перепланирование пути до текущей ТД,
  резерв следующей клетки, поворот взгляда по направлению шага, построение **кривой шага** (`StepGeometry.Build`, ниже).
  `Progress += speed × dt` (скорость 1 гекс/с, шаг = 1/скорость с); положение тела `Position` — точка кривой при этом
  прогрессе (равномерно по длине); когда тело оказывается в следующем гексе (`SwitchAt`, середина общего ребра) —
  `GameState.MoveUnit` (единственный момент смены логической клетки), исходная клетка остаётся в резерве до конца шага (D-025).
- **Сглаживание** (`HexTactics.Core.Motion`, ТЗ ч.3 §2, D-064–D-066): кривая шага From → To = часть внутри From до середины
  общего ребра + часть внутри To до точки конца шага. Если путь за To продолжается (`StepLookahead`), конец — середина
  скругления «середина входного ребра → центр To → середина выходного ребра» (квадратичная кривая внутри To), иначе
  (ТД, конец маршрута) — центр To. `MotionCorridor` проверяет всю кривую кругом тела r 0,28 против коридора
  {From, To, следующий}; при провале — откат на прямые через центры. Новый приказ во время шага → `Retarget` остатка от
  текущей точки и касательной. Покой — в центре (или в точке ожидания посреди маршрута); разгон с места — без инерции.
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

## 7.4. Командные приказы (с v0.9.0, ТЗ ч.2 §6, D-050–D-061)

- `GroupOrder(Captain, Members, GroupWaypoints, FinalCaptainFacing)` — капитан всегда `Members[0]`.
- `GroupStageEvaluator(state, members, sources, index).Evaluate(ктд)` → `GroupStagePlan` (ошибка или назначения
  `GroupSlotAssignment(index, unit, destination)`, `TotalCost`, `Conflicts`): КТД проходима, свободна (кроме капитана),
  капитан до неё дойдёт; спутникам — разные свободные достижимые клетки первого кольца; минимум (конфликты порядка
  прихода, суммарная длина), ничьи — по порядку сторон (D-058). Одно дерево кратчайших путей на участника и этап — так же
  дёшево красятся все 120 клеток.
- `GroupRoutePlanner.PlanChain` — этап k стартует с мест этапа k−1; `ValidateMembers` — 1…7, без дублей, одна сторона.
- `GameSimulation.ApplyGroupOrder` — транзакция (как `ApplyOrder`): `GroupOrderState` в `GameState.Groups`, очереди
  участников = их места, `OrderQueue.GroupId`, финальные взгляды (`CompanionFacingRule`, seed, D-057). Завершается для всех
  сразу, когда закончили все (`GroupCoordinator.CompleteFinished`).
- `MovementSystem` (v0.10.0): участник впереди команды по этапам ждёт (`WaitReason.TeamStage`, D-056); путь закрыт только
  своими → `WaitReason.Teammate`, через 0,8 с стоящий на пути отходит обходным шагом (`Waypoint.IsDetour`, `TeammateYielded`)
  и ждёт, пока его пропустят (`WaitReason.Yielding`); некому отойти 6 с → `BlockReason.BlockedByTeammates` (D-059).
- `InputStateMachine.Group` → `GroupDraftView`: участники, якоря, источники следующего этапа, КТД, планы этапов,
  предпросмотр под курсором, сектор взгляда, `NextStage` (оценщик для подсветки).

## 7.5. Бой (с v0.12.0, ТЗ ч.3 §3–4, §7, D-068–D-072)

- `UnitStats` (Core/Combat) из `SimulationSettings.DefaultStats` ← юнит библиотеки (с v0.15.0; раньше `data/balance.json`); `UnitState.CurrentHealth`,
  `LifeState`, `Combat` (`AttackStatus`, таймер, клетка подхода, счётчик ударов).
- Приказ атаки: `IndividualOrder.AttackTarget` → `OrderQueue.AttackTarget` (последняя стадия после ТД), взгляд к цели
  при подтверждении (`HexDirections.Toward`).
- Тик: `MovementSystem` → `AttackSystem` (подход обходным шагом к ближайшей свободной клетке в радиусе / удары по
  таймеру 1/AttackRate / цель потеряна) → `GameState.RemoveDead` (смерти, освобождение клеток, `UnitDied`) →
  завершение команд. На паузе тиков нет — нет ни урона, ни таймеров.
- Клиент: `HealthBarsView` (полоски здоровья в экранных координатах), `CombatFxView` (всплывающий урон, след погибшего),
  `OrdersView.DrawAttackOrders` (оранжевая стрелка атаки и кольцо цели).

## 7.6. Навыки (с v0.13.0, ТЗ ч.3 §5, D-073–D-075)

- `SkillDefinition` (Core/Skills): относительные шаги клеток, урон, правила попадания, поля стоимости/перезарядки/каста;
  `Area(origin, direction, grid)` — две клетки «Линейного удара», без клеток за краем.
- `SkillAction` — запланированное применение: устойчивый id, навык, направление, ТД-источник (с v0.14.0), статус
  `Planned/Executed/Cancelled`; исполняется не более одного раза.
- `GameSimulation.UseSkill` → `UnitState.PendingSkill`; `SkillSystem` (после движения, до атак) исполняет его один раз,
  когда юнит стоит в центре клетки: поворот в направление удара, снимок врагов в области на этом тике, урон, `SkillActivated`.
- Ввод: `InputMode.SkillAim` / `SkillAimView` (`InputStateMachine.Skill.cs`). Клиент: серый слой `SkillAreaLayer`
  (отдельный от зелёно-красного), красная стрелка `OrdersView.DrawSkillAim`, вспышка `CombatFxView.SkillFlash`.
- **Навык в ТД (v0.14.0, ТЗ ч.3 §6, D-076–D-078).** `Waypoint.Skill` (`SkillAction` с `OriginWaypointId`); черновик —
  `OrderDraft.Skills` (индекс ТД → `WaypointSkillSpec`), применяется вместе с приказом (`IndividualOrder.WaypointSkills`);
  подтверждённый маршрут — `AssignWaypointSkill/RemoveWaypointSkill`. ТД с навыком — якорь кривой; `MovementSystem` держит
  юнита в её центре один тик (`WaitReason.Skill`), `SkillSystem` в этом тике исполняет навык от ТД, со следующего тика
  маршрут продолжается. Удаление ТД отменяет её навык. Ввод: `WaypointMenuView` (короткий клик по маркеру ТД),
  `BeginWaypointSkillAim`; клиент: `WaypointMenuPanel` (CanvasLayer над стрелками), красный шестиугольник «✦1» у маркера
  (`OrdersView.SkillBadge`), ярко/приглушённо серые области (`Main.UpdateSkillAreas`); пока открыто меню ТД или идёт
  прицеливание, зелёно-красная подсветка движения скрыта (`Main.MovementFillsAllowed`, D-079).
- **Попадания и статусы (v0.18.0, ТЗ ч.4 §6.3–6.5, D-101–D-104, D-106).** При активации `SkillSystem` строит
  `SkillExecution` — расписание `ScheduledHit` (время, номер попадания, клетка, эффект) и исполняет наступившие попадания
  на каждом тике: цели клетки — на этом тике, эффект — через `EffectResolver` (урон/лечение — сразу, бафф/дебафф/
  периодический — `GameState.AddStatus` с политикой повтора). Пока статус действия `Executing`, юнит стоит в точке;
  по завершении — `SkillCompleted`, отмена (удаление ТД, смерть) — `SkillInterrupted`. `StatusSystem` (после атак) стареет
  статусы, применяет периодические непрерывно (perSecond × время тика), снимает истёкшие и пересчитывает `UnitState.Stats` из `BaseStats`
  (`StatModifiers.Effective` с безопасными минимумами). Порядок систем: Movement → Skills → Attack → Tiles (с v0.19.0) →
  Statuses → смерти.
- **Поверхности (v0.19.0, ТЗ ч.4 §7, D-107–D-110).** `SimulationSettings.TileEffects` (ID типа → `TileEffect`: триггер +
  `EffectSpec`) строит `RuntimeFactory` из библиотеки; клетка карты хранит только `TileState.TileId`. `TileEffectSystem`
  смотрит логический гекс (`UnitState.Cell`) каждого живого юнита: смена — `exit`/`enter`, каждый тик — `stay`
  (`EffectInvocation.Slice` = время тика, периодический применяется сразу на эту долю). Без источника цели фильтрует
  `sides`. `TileState.AllowsWaypoints` проверяют `WaypointRules` и проверка КТД (красная подсветка, путь через гекс разрешён).

## 7.7. Контент, режимы и редакторы (с v0.15.0, ТЗ ч.4, D-080–D-091)

- **Выход (v0.19.1, D-112).** Любой выход — через `AppExit.Quit`: финализаторы ненужных обёрток Godot выполняются, пока
  движок жив; закрытие окна перехвачено в `AppRoot`.
- **Старт.** `AppRoot` (`scenes/App.tscn`) загружает библиотеку (`ContentService`: встроенное из `res://data/content` +
  пользовательское из `user://`), показывает `StartMenu` и создаёт один режим: `GameMode` («Играть») или `DeveloperMode`
  («Разработчик»). Возврат в меню удаляет режим; симуляция в редакторе не работает.
- **Матч из данных.** `RuntimeFactory.BuildGame(library)` → `GameContent` (карта с `TileType.Void` для пустых мест, навыки,
  юнит по умолчанию, вид типов гексов). `GameMode` строит из него `GameSimulation` (характеристики и навыки юнитов —
  из определения) и `TilePalette` (цвет/высота гексов). Тестовый запуск из редакторов (с v0.16.0) — та же фабрика.
- **Эффекты.** Клетка навыка хранит `EffectSpec`; `SkillSystem` для каждой клетки по порядку применяет эффекты к юниту в
  ней через `EffectResolver` — единственный исполнитель (обработчики по типу, проверка целей). Реестр полей эффектов —
  `EffectTypeRegistry` в Content; тест требует обработчик для каждого зарегистрированного типа.
- **Каталог разработчика.** `DeveloperMode` показывает модели `LibraryBrowser` (категории из реестра, строки, поиск,
  сортировка); операции библиотеки — команды `LibraryCommands` в `CommandHistory` (Ctrl+Z / Ctrl+Y); редакторы категорий
  регистрируются в `DeveloperMode.Editors` (появляются с v0.16.0). Диалоги — `ModalDialog` (перехватывает ввод).
- **Редактор карт (с v0.16.0, D-092–D-097).** `MapEditor` (Node3D) показывает `MapEditorModel`: доска строится из
  рабочей копии через `RuntimeFactory.BuildMap` (пустые места — контуры и координаты), выбор/отметки — `HexFillLayer`,
  выбор гекса — `BoardPicker` с учётом высот, панель — `Inspector`. Правки — команды модели, сохранение — через
  библиотеку, черновики — `user://drafts`. «Тест карты»: `DeveloperMode.TestRequested` → `AppRoot` вынимает режим
  разработчика из дерева и запускает `GameMode` на копии; по выходу возвращает его.
- **Редакторы определений (с v0.17.0, D-098–D-100).** `DefinitionEditorShell` — общий каркас (верхняя строка, панели
  свойств и предпросмотра, холст, сообщения, черновики, горячие клавиши); модель — `DefinitionEditorModel` (снимки,
  проверка поля по схеме до применения). `UnitEditor`: поля `UnitData.Fields`, навыки-ссылки, предпросмотр юнита и зоны
  навыка, «В бою». В бою юнит создаётся из `UnitProfile` (ростер/появление), его вид — из определения.
- **Редактор навыков (с v0.18.0, D-104–D-105).** `SkillEditor` — поле 13 × 13 (гексы вне радиуса — пустота), базовый юнит
  с указателем взгляда в центре, заливки клеток `HexFillLayer` (цвет автора; выбранная — золотом), подписи `Label3D`.
  Клик → смещение от центра → `SkillEditorModel.Click` (поворот к хранимому для взгляда на север). Панель: клетка (свои
  эффекты / наследование с переопределениями), поля эффектов из `EffectTypeRegistry`; справа — `Preview()` с выбранной
  силой атаки. «Тест навыка» — `RuntimeFactory.BuildGame(…, testSkill)`.
- **Редактор типов гексов (с v0.19.0, D-111).** `TileEditor`: доска 7 × 7 — тип (несохранённые значения) на центре и
  кольце, обычные гексы вокруг (`TilePalette` с видом рабочей копии); поля `TileData.Fields`; эффекты — поля для гексов
  (`EffectTypeDescriptor.FieldsFor(Tile)`); справа — `TilePreview`. «Тест гекса» — `RuntimeFactory.BuildGame(…, testTile)`.
  Редактор карт берёт типы из библиотеки (свои — с пометкой «(свой)»), панель гекса показывает правила типа словами.
  Значки типов — `BoardView` (Label3D на гексе).
- **Урон (с v0.21.0, ТЗ ч.5, D-120–D-125).** Любой урон — `DamageEvent` → `DamageResolver.Resolve` (`GameSimulation.Damage`):
  допуск → D0 → R (`ResistanceMath`: слои global/семейство/подтип/временное, ±500, пробитие) → HP. Источники: `AttackSystem`
  (тип и область обычной атаки из `UnitStats`), обработчик `damage` (тип, пробитие, доля `EffectInvocation.Scale` при
  `area = split`), срез поверхности, `StatusSystem` (тип периодического статуса). Каталог типов — `SimulationSettings.DamageTypes`
  (`RuntimeFactory.BuildDamageTypes` из вида `damage_type`). Сопротивления — `UnitStats.Resistances`, временные — статусы
  `Stats.Resistance`. Клиент R не показывает; редактор юнита — секция «Сопротивления».
- **Защита и реакции (с v0.22.0, ТЗ ч.5 §4–§5, D-126–D-131).** Стадии `DamageResolver.ResolveOne`: допуск → D0 → иммунитет →
  конверсии из бюджета D0 (правила «получен удар» по `Priority`, затем `Id`) → R → плоское снижение → предел → щиты
  (`UnitState.Shields`, порядок) → HP (`GameState.ApplyDamage`) → ответный удар и вампиризм. Лечение — только
  `DamageResolver.Heal` (множитель `HealingReceived`). Реакции — `Pending` в очереди, `Drain` после удара; ответный пакет
  `Reactive`, `ProcDepth` + 1; пределы `CombatLimits` (`MaxProcDepth` 1, `MaxGeneratedEventsPerRoot` 64, `MaxEventsPerTick`
  4096, `MaxReactionTargets` 32) → `CombatLimitReached`; ответ на непрерывный урон копится и уходит раз в игровую секунду
  (`AfterTick`). Правила — `UnitStats.DefenseRules` (`RuntimeFactory.BuildUnit` разрешает ссылки `defenseRules` через
  библиотеку; правило с ошибками не действует), реакции описывает `DefenseActionRegistry` (Content), формы — из его полей.
  Клиент: `DefenseRuleEditor`, секция юнита «Особые защиты и реакции», `CombatView.Note` («невосприимчив», «щит»).
- **«Механики» (с v0.23.0, ТЗ ч.5 §6, D-132–D-135).** Категории с `ContentKind.Group = mechanics` каталог показывает одной
  позицией с первым экраном из реестра. Наборы сопротивлений — вид `resistance_profile`; `ResistanceLayers` складывает
  основы наборов, наборы юнита и его свои значения в базовые R (`RuntimeFactory.BuildUnit`, редактор юнита, проверка
  библиотеки). Конструктор типа — `DamageTypeEditorModel`; набор — `ResistanceProfileEditorModel`. «Проверить на ударе» —
  `Editor/Mechanics/HitSandbox`: одноразовая `GameSimulation` с `DamageResolver.Trace`; клиент — `SandboxPanel` в колонке
  предпросмотра редакторов правила, типа и юнита.
- **Инспектор юнита (с v0.24.0, ТЗ ч.5 §7, D-136–D-139).** Секции с резюме из `UnitEditorModel.Sections`; таблица — 
  `UnitEditorModel.ResistanceView(поиск, показать все, раскрытые семейства)`; карточки — `DefenseCards`. Вложенный редактор:
  `IContentEditor.NestedRequested` → `DeveloperMode.OpenNested` откладывает родительский узел (`RemoveChild`) и возвращает его в
  `CloseEditor`, передав сохранённый объект. Автоматизация: элемент закрытой секции — `IContentEditor.Reveal(id)` и повтор
  шага. Отладка защиты — только `GameMode` с `isTest` (`UiText.DefenseDebug`).
- **Точка расширения для стихий (с v0.25.0, ТЗ ч.5 §13.4, D-141).** `IDamageStage` в `SimulationSettings.PreDamageStages`
  исполняется в `DamageResolver` до D0 для каждого события; в этой редакции список пуст. Новые категории «Механик» — через
  `ContentKind.Group`, новые реакции — через `DefenseActionRegistry`, новые типы — данными `damage_type`.
- **Единый реестр определений (с v0.26.0, ТЗ ч.6 §2, D-144–D-149).** Реестр = `ContentTypeRegistry` (виды и схемы) +
  `ContentLibrary` (объекты по `kind + id`). `ContentLibrary.Changed` вызывается при каждом изменении (`Bump`), на него
  подписан `DefinitionEditorShell` (перестройка инспектора без сброса черновика). Варианты полей-ссылок —
  `Editor/Library/ReferenceCatalog` (группы по семействам, фильтр `FieldSchema.RefFilter`, архивные и служебные скрыты, пока
  не выбраны, пункт создания `ReferenceCatalog.CreateValue`); инспектор получает их функцией `Inspector.References`, а выбор
  пункта создания — через `Inspector.CreateRequested` → `DefinitionEditorShell.CreateReference` (диалог основы и вложенный
  `DamageTypeEditor`, как правило поверх юнита). Зависимости: `ContentLibrary.ReplaceReferences` (транзакционная перепривязка
  через `RemapReferences`), `SetArchived`; команды отмены `LibraryCommands.Archive/ReplaceAndDelete`; диалог — 
  `DeveloperMode.AskDependents`. Неизвестные поля файла: `ContentSerializer` хранит их в `ContentItem.ExtraEnvelope/ExtraData`
  и дописывает при записи (известные поля вида — `typed.ToJson()` и `ContentKind.OptionalDataKeys`).
- **Защита A и правила боя (с v0.27.0, ТЗ ч.6 §3, D-151–D-153).** `Core/Combat/Armor.cs`: `ArmorSet` (слои как у R),
  `ArmorMath` (`Raw` со статусами `Stats.Armor`, `Effective` с пробитием в очках и %, `Multiplier = K/(K+A)`), `CombatRules`
  (K и K семейства). `DamageResolver.ResolveOne`: R → **A** → плоское снижение → предел → щиты → HP; `EffectiveArmor`, поля
  `DamageResult.ArmorRaw/Armor/DefenseK/AfterArmor`. Пробитие A приходит в `DamageEvent` из обычной атаки (`UnitStats`) и
  эффекта урона (`armorPenetration*`). Данные: `UnitData.Armor` (схема 5), `ResistanceProfileData.Armor` (схема 2),
  `ResistanceLayers.*(…, armor)`, вид `combat_ruleset` (`CombatRulesetData`), `MapData.CombatRuleset` →
  `RuntimeFactory.BuildCombatRules` → `GameContent.CombatRules` → `SimulationSettings.CombatRules`. Редактор: строки
  `ResistanceRow` с A, `Inspector.PairText` («R, % | A»), `CombatRulesetEditorModel`/`CombatRulesetEditor`, выбор правил в
  `MapEditor`, песочница — `SandboxSetup.ArmorPenetration*/RulesetId`, `HitSandbox.Run(…, rulesetDraft)`.
- **Пассивы и движок модификаторов (с v0.28.0, ТЗ ч.6 §4, D-154–D-156).** Данные — `PassiveData` в `UnitData.Passives`,
  операции — реестр `PassiveOperations`. Исполнение — только `Content/Runtime/PassiveEffects`: характеристики (`Stat`), R и A
  слоёв, подключённые правила, `DamageModifiers` → `UnitStats`; редактор берёт оттуда же «откуда взялось»
  (`UnitEditorModel.Breakdown`) и итог «В бою». В бою `DamageResolver`: стадия 1a — исходящие модификаторы атакующего до
  D0 (`ModifierMath.DamageMultiplier`: проценты группы складываются, группы перемножаются, не ниже 0), стадия 5c — входящие
  модификаторы цели после плоского снижения и до предела. Песочница — `SandboxSetup.AttackerUnitId`,
  `HitSandbox.Run(…, attackerDraft)`.
- **Виды, теги, стая, характеристики (с v0.29.0, ТЗ ч.6 §5, D-157–D-159).** Данные: `SpeciesData`, `TraitData`, `UnitStatData`
  (`Content/Kinds/OriginData.cs`), у юнита `Species/Traits/PackId/CustomStats`. `PassiveEffects.Active` собирает эффекты: вид
  (от корня, по ключу эффекта), теги (`ActiveTraits`: повтор, группы, допустимые виды), личные пассивы — с подписью
  источника. `RuntimeFactory.BuildUnit` кладёт в `UnitStats` линию вида, стаю и `CountModifiers`; `PackSystem` (первая система
  тика) пересчитывает `UnitState.PackPercent` при изменении `GameState.RosterVersion`. Клиент: группы каталога (`Groups` в
  `DeveloperMode`), общий список эффектов `DefinitionEditorShell.BuildEffectList`, редакторы `SpeciesEditor`, `TraitEditor`,
  `UnitStatEditor`, «Особенности» юнита.
- **Внезапность и крит (с v0.30.0, ТЗ ч.6 §6–§7, D-160–D-163).** Core `Combat/Chance.cs`: `SurpriseMath.Sector` (сторона
  по геометрии гексов от логического взгляда цели), `CritMath` (две ступени, множители из `CombatRules`), `DeterministicRoll`
  (бросок без состояния по `seed|purpose|key`). `DamageResolver.ResolveOne`, стадия **1b** — после исходящих модификаторов
  (1a) и до 1c/D0: только первичный удар обычной атаки или навыка с живым источником; результат — `DamageResult.Surprise*`,
  `Crit*`. Ключ броска задаёт источник: `AttackSystem` (`atk:…`), эффект урона навыка (`skill:{ActionId}:{HitIndex|s}:{цель|all}`
  через `EffectInvocation`). Правила — `CombatRulesetData` схемы 2 → `RuntimeFactory.BuildCombatRules`; шанс юнита —
  `PassiveEffects.CritChance`; невосприимчивость — операции `crit_immunity`, `surprise_immunity` → `UnitStats`. Редактор:
  `CombatRulesetEditorModel` (зеркальные фланги, проверки, `CritTable`), песочница — `SandboxSetup.Sector/ForceSurprise/
  ForceCrit` (атакующий ставится на выбранную сторону цели).
- **Полный редактор части 6 (с v0.31.0, ТЗ ч.6 §6.3–6.4, §9.2, §9.5, §11, D-165–D-168).** Клиент: `SectorHexagon` — рисунок
  шести сторон в редакторе правил боя (якоря `rules.hex.<n>` для автоматизации), `SandboxPanel.TestFromSide`, seed, серия и
  сброс; назначение тега в `TraitEditor`; `CombatView.HitWords` — слова «Крит!»/«Внезапно!» над целью по
  `DamageApplied.Surprised/Crit`. Editor: `CombatRulesetEditorModel.RollTest` (та же `DeterministicRoll`), `HitSandbox.Batch`
  (ключи `sandbox:i`), `TraitEditorModel.AssignTo` (проверка всех юнитов до записи), `UnitEditorModel.FeaturesSummary`.
  Правила: `CombatRules.SurpriseMeleeOnly` (дистанция 1), `UnitStats.SurpriseChanceBonus` — в стадии 1b `DamageResolver`.
- **Автоматическая базовая атака (с v0.32.0, ТЗ ч.7 §8, D-170, D-171).** Core `Combat/BasicAttack.cs`:
  `BasicAttackDefinition` (область, поворот, выбор целей). `AttackSystem`: без явного приказа — `AutoAttack` по
  `CombatState.AutoTimer`, только при `SimulationSettings.AutoAttacks` (из карты, `GameContent.AutoAttacks`) и
  `UnitStats.AutoAttackEnabled`; удар — тот же `DamageResolver`. Content: `BasicAttackData` (вид `basic_attack`),
  `RuntimeFactory.BuildUnit` кладёт определение в `UnitStats`. Editor: `BasicAttackEditorModel`. Клиент: `BasicAttackEditor`
  (гекс-поле как у навыка), флажок «Автоатака юнитов» карты.
- **Блок (с v0.33.0, ТЗ ч.7 §7, D-172, D-173).** Ёмкость — характеристика реестра `core.stat.block` → `UnitStats.BlockCapacity`
  (+ статусы `Stats.Block`, `BlockMath.Capacity`). Связи `BlockLink` хранятся в `GameState`; `BlockSystem` (после `PackSystem`,
  до `MovementSystem`) снимает недействительные, а `MovementSystem.TryStartStep` спрашивает `HoldsBack` перед каждым шагом:
  враг остаётся на своём гексе с `WaitReason.Blocked`. События `BlockStarted`/`BlockReleased`; клиент показывает слово «блок».
- **Сценарии и цели (с v0.34.0, ТЗ ч.7 §2–§3, D-174–D-177).** Content: вид `scenario` (`ScenarioData`: режим из реестра
  `ScenarioData.Modes`, карта, правила, отряд, враги на карте, переключатели, время), `RuntimeFactory.CheckScenario` (проверка с
  картой и юнитами) и `BuildScenario` → `GameContent.Scenario` (`ScenarioRuntime`: правила целей, расставленные враги, цели
  словами, пауза старта) и `GameContent.Blocking`. Editor: `MatchFactory.Start(GameContent)` — единый старт матча (настройки
  симуляции, ростер, враги, «Подготовка»), им пользуются игра, тест и автотесты; `ScenarioEditorModel`. Simulation:
  `ObjectiveSystem` — последняя система тика (`Packs, Blocks, Movement, Skills, Attack, Tiles, Statuses, Objectives`):
  `Preparing → Running → Victory | Defeat`, событие `ScenarioEnded`, после итога `GameSimulation` стоит на паузе
  (`IsScenarioOver`). Клиент: выбор «Сценарий» в стартовом меню, `ScenarioEditor` (карта сценария на `BoardView`, враги —
  цилиндры с указателем взгляда), цели и «Начать бой» в панели игры, название паузы `DebugHud.SetPaused(bool, string?)`.
- **Маршруты врагов (с v0.35.0, ТЗ ч.7 §4, D-178–D-181).** Core: `EnemyRoute` (упорядоченные посещения), `MapBase`
  (нумерованные базы сторон), `MapDefinition.Bases/Routes`, `RouteCheck` (та же проходимость, что у движения),
  `RouteProgress` юнита (индекс посещения — источник истины). Simulation: `MovementSystem.TryStartRouteStep` — строго к
  следующему посещению, ожидание `RouteAhead` без обхода, `RouteFinished`; `SpawnOnRoute`. Content: `MapData.Routes/Bases`,
  `MapTileData.Base`, `RuntimeFactory.BuildMap` (базы и маршруты), `CheckRoutes`, предупреждения `MapChecks`. Editor:
  `MapEditorModel.Routes.cs` — черновик (`RouteClickAt`, `RouteHover`, `RemoveLastVisit`, `CancelRouteDraft`, `RedrawRoute`),
  карточка, проверка; событие `DraftChanged` перерисовывает только линии. Клиент: `RouteLayer` (полосы с шевронами и
  номерами, без теста глубины), «〰 Маршруты» в `MapEditor` (след курсора `TraceRoute`), раздел «Маршруты врагов» в отладке боя.
- **Волны и Tower Defense (с v0.36.0, ТЗ ч.7 §4.5–4.7, D-182–D-184).** Core: `WaveDefinition`, `SpawnGroupDefinition`,
  `BaseLives`, `DefeatPolicy`, поля Tower Defense в `ScenarioRules`, `GameState.RemoveFromMap`. Simulation: `WaveSystem` — первая
  система тика (`Waves, Packs, Blocks, Movement, Skills, Attack, Tiles, Statuses, Objectives`): старт волн, очереди входов,
  один враг за тик на вход, разрешение волн; `ObjectiveSystem` — утечки (`EnemyLeaked`), жизни баз, политика поражения,
  победа после волн. Content: `WaveData`, `SpawnGroupData`, `BaseLivesData`, `RuntimeFactory.CheckWaves` и `ScenarioRulesOf`.
  Editor: волны, группы, базы, суммы и расписание в `ScenarioEditorModel`. Клиент: разделы «Базы игрока» и «Волны» в
  `ScenarioEditor` (линии маршрутов на поле), строка волн и жизней баз в `GameMode`.
- **Простой ИИ и оборона (с v0.37.0, ТЗ ч.7 §5, D-185–D-188).** Core: `AiProfile` (`AiPreset`, `AiPriority`, дальность,
  пересмотр, радиус охраны, реакция на блок), `AiState` юнита (дом, фаза, цель, следующий пересмотр, пропущенные недостижимые
  цели), `UnitRole` (`Combatant` / `Objective`), `MissionObject`, `ObjectsPolicy`, поля обороны в `ScenarioRules`. Simulation:
  `AiSystem` (`Waves, Packs, Ai, Blocks, Movement, Skills, Attack, Tiles, Statuses, Objectives`) отдаёт только обычные
  приказы — атаку цели или путь домой; `WaveSystem` впускает группы из гексов источника (`SourceCells`) и вешает ИИ;
  `GameSimulation.SpawnObjective` — объекты миссии без приказов и атаки; `ObjectiveSystem` — цели обороны (объекты, отряд,
  время, волны). Content: `AiData`, `MissionObjectData`, поля обороны `ScenarioData`, `RuntimeFactory.AiOf/SourceCells` и
  проверки. Editor: `ScenarioEditorModel.Defense.cs` — объекты, цели, шаблоны, источники и ИИ групп и врагов;
  `MatchFactory` вешает ИИ на врагов карты. Interaction: черновики ТД не строятся для объектов. Клиент: разделы «Цель
  обороны» и «Объекты миссии», «Откуда»/«Поведение»/«Дополнительно ▾» в `ScenarioEditor`, строка «Выстоять» и здоровье
  объектов в `GameMode`.
- **Импорт и экспорт (с v0.20.0, D-113–D-118).** `ContentExporter`: объект → JSON, пакет → ZIP с `manifest.json` и
  замыканием зависимостей. `ContentImporter.Inspect` проверяет файл и объекты без записи и строит `ImportPreview`
  (статус и решение каждого объекта); `Install` проверяет выбранный набор поверх библиотеки (`ContentLibrary.Overlay`) и
  пишет транзакционно с резервными копиями и откатом. Миграция старых файлов — при загрузке библиотеки с копией оригинала.
  Окна — `ModalDialog` с содержимым из `DeveloperMode` (модели `ExportPlan`, `ImportSession`).
- **Постоянное правило (ТЗ ч.4 §9.3).** Любая новая редактируемая механика в том же релизе получает: правила в ТЗ,
  версионированную схему, проверку и миграцию, единый обработчик в симуляции, поля и предпросмотр в режиме разработчика,
  сохранение/импорт/экспорт и тесты (игра, редактирование, загрузка, совместимость, откат). Новые категории — через реестр,
  с сохранением существующих объектов и их ID. Полный текст — `CLAUDE.md` и `docs/CONTENT_FORMAT.md`.

## 8. Визуальные слои

| Порядок | Слой | Реализация |
|---|---|---|
| 0 | Поле | `BoardView`: один `ArrayMesh` — шестигранные призмы без зазоров; препятствия — поднятые колонны |
| 1 | Проходимость | `HexFillLayer` (MultiMesh, полупрозрачная заливка, `render_priority = 1`): при протягивании все 120 клеток — зелёные (можно поставить ТД) / красные (препятствие, занято, изолировано), исходная — жёлтая; при перетаскивании карточки ростера — насыщенно-зелёные разрешённые свободные клетки и мягко-красные остальные; в отладочном режиме выставления — зелёные/красные по правилам появления |
| 2 | Маршрут/стрелки/ТД — **верхний слой** | `OrdersView`, все материалы **без теста глубины** (`NoDepthTest`), т. е. поверх юнитов, препятствий и любых объектов сцены (ТЗ ч.2 §3.1); порядок внутри слоя — `render_priority` 20…28: цепочки «юнит → ТД1 → ТД2…» **белыми** толстыми прямыми стрелками (ширина ½ стороны гекса в плоскости поля), стрелка протягивания, тонкие линии путей (тёмная — маршрут, зелёная — предпросмотр), белые маркеры ТД с тёмной каймой и номерами. HUD (CanvasLayer) — над ними |
| 3 | Юниты | `UnitsView` → `UnitView`: цилиндр (r 0,28), **белая мини-стрелка у ребра гекса** в направлении взгляда с тёмной обводкой (видна всегда, не только при выделении), тор выделения, красный «!» при остановке, жёлтый «?» у юнита без взгляда, подпись имени оперативника; позиция — точка кривой из симуляции (`UnitMotion.PositionAtProgress`) с экстраполяцией в пределах тика. Отладка движения (F3) — `MotionDebugView`. «Призрак» карточки — полупрозрачный цилиндр над клеткой под курсором (зелёный/красный) |
| 3+ | Курсорный гекс | `HexFillLayer` с белой заливкой α = 0.22, `render_priority = 3` |
| UI | HUD, ростер | `CanvasLayer` слева (HUD) и снизу (`RosterBar`, пять карточек); оба масштабируются по высоте окна |

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
по высоте под юниты) в свободную область окна справа от HUD и над панелью ростера: `Camera.Size = k·H`, где `k` — мировых единиц на
пиксель; затем камера сдвигается в своей плоскости так, чтобы поле было по центру свободной области.

## 10. Ввод: экран → карта

- `BoardPicker`: луч камеры пересекается сначала с плоскостью верха препятствий (они загораживают то,
  что за ними), затем с плоскостью земли `y = 0`; точка плоскости → `HexLayout.PlaneToHex`. Клетка возвращается
  и вне поля — границы проверяет автомат/модель. Дополнительно в `PointerSample.Ground` передаётся точка земли без учёта
  препятствий: по ней выбирается сектор взгляда исходной клетки, если её край закрыт соседней колонной (D-060).
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
Шаги v0.8: `roster:N>P1>P2…` — нажать настоящую карточку N панели ростера, протянуть через точки и отпустить на
последней (`bar` — обратно над карточкой; `roster:N` — клик по карточке; хвост `>hold` оставляет кнопку зажатой, затем
`release:P`). Проверки: `expect:roster:N:AvailableInRoster|PlacementPreview|PlacedPendingFacing|Active`,
`expect:lifecycle:ID:Active|PlacedPendingFacing`, `expect:mode:RosterDrag|SingleDraft|…`.
Шаги v0.9: `command` — нажать настоящую карточку «Команда». Проверки: `expect:group:none|M:K` (M участников, K КТД в
открытом командном черновике), `expect:captain:ID`, `expect:ingroup:ID:yes|no` (юнит выполняет командный приказ).
Проверка v0.10: `expect:facing-near:ID:Dir` — взгляд Dir или ±60° (спутники).
Шаги/проверки v0.11: `debug:on|off` (оверлей движения), `expect:arrived:ID:SECONDS` (игровое время от `mark` до
`MovementFinished` юнита, ±0,05 с), `expect:in-cell:ID` (нарисованное тело лежит в логическом гексе).
Шаги/проверки v0.12: `spawn:enemy:c,r` (неподвижная мишень), `waitdead:ID[:max]`, `expect:hp:ID:N`,
`expect:alive:ID:yes|no`, `expect:attack:ID:None|FollowingRoute|Approaching|Attacking|TargetUnreachable`.
Проверка v0.13: `expect:pending-skill:ID:yes|no` (навык запланирован и ещё не сработал).
Шаги/проверки v0.14: `menu-skill`, `menu-clear` (нажать настоящие кнопки открытого меню ТД), `expect:menu:none|open[:N]`,
`expect:wpskills:ID:N` (запланированные навыки на ТД юнита), `expect:draft-skills:N` (навыки в открытом черновике).
Проверка v0.14.1: `expect:movefill:on|off` (показана ли зелёно-красная подсветка движения).
С v0.15.0 запуск начинается со стартового меню; шаги приложения (`AppRoot`) обрабатываются раньше шагов боя:
`ui:<id>` — нажать зарегистрированный элемент интерфейса настоящими событиями мыши (`menu.play`, `menu.dev`, `menu.quit`,
`game.menu`, `dev.cat.<вид>`, `dev.create|open|duplicate|rename|delete|validate|undo|redo|back`, `dialog.ok|cancel`…),
`field:<id>=<значение>` (поле ввода, список, флажок, цвет; `dev.search`, `dialog.input`), `key:ctrl+z` (клавиши с
модификаторами), `row:<id>` или `row:name=<название>` (клик по строке библиотеки); проверки `expect:screen:menu|play|dev`,
`expect:dialog:none|<начало заголовка>`, `expect:category:<вид>`, `expect:rows:N`, `expect:selected:<id|название>|none`,
`expect:item:<вид>:<название>:yes|no`, `expect:undo:N`, `expect:message:<текст>`. Аргументы: `--hx-start=play|dev` (минуя
меню), `--hx-user-root=<папка>` (отдельная папка пользовательского контента).
С v0.16.0 при открытом редакторе прочие шаги идут ему: `click:c,r`, `hover:c,r`, `wheel:up|down`; проверки
`expect:editor:map|none`, `expect:tiles:N`, `expect:tile:c,r:<тип>|none`, `expect:hex:c,r|none` (выбранный гекс),
`expect:delete-mode:on|off`, `expect:delete-count:N`, `expect:dirty:yes|no`, `expect:testpoints:N`, `expect:level:c,r:N|type`,
`expect:map-size:WxH`, `expect:map-name:<название>`, `expect:map-errors:N`. Элементы редактора: `map.save|undo|redo|validate|test|view|back`,
`map.delete`, `map.delete.cancel`, `map.tab.hex|map`, `map.brush`, `map.tile.type|level|zone|testpoint|note|deselect`, `map.zone.name`,
`map.name`, `map.description`, `map.basename`, `map.columns`, `map.rows`, `map.resize`; на стартовом экране — `menu.map`, `menu.squad`.
С v0.17.0 в редакторах определений: `<префикс>.save|undo|redo|validate|test|back|name|description` и поля `<префикс>.<ключ>`
(юнит: `unit.maxHealth|moveSpeed|attackRate|attackPower|attackRange|side|color|scale`, `unit.skill.filter|pick|add`,
`unit.skill.<i>[.up|.down|.remove]`); проверки `expect:dirty|name|errors`, `expect:editor-message:<текст>`,
`expect:unit-field:<ключ>:<значение>`, `expect:skills:N|<имена через запятую>`. Шаг `ui:` прокручивает панель к элементу.
С v0.18.0 в редакторе навыков: шаг `cell:q,r` — ЛКМ по гексу, нарисованному со смещением `q,r` от базового юнита (при текущем
взгляде); элементы `skill.turn.left|right`, `skill.radius`, `skill.erase`, `skill.icon`, `skill.cell.remove|deselect|color|alpha|own`,
поля эффектов `skill.e<i>.<ключ>` (+ `.reset` — вернуть значение навыка, `skill.e<i>.remove`), `skill.addtype`, `skill.add`,
`skill.previewUnit`, `skill.previewAp`; проверки `expect:cells:N`, `expect:selected-cell:q,r|none`, `expect:skill-facing:Dir`,
`expect:cell-color:q,r:#RRGGBB|default|none`, `expect:preview:N:<текст>` (строка N предпросмотра содержит текст). В бою:
`menu-skill:N` — N-я кнопка навыка в меню ТД.
С v0.19.0: редактор гексов — поля `tile.category|passable|waypoints|level|color|icon`, эффекты `tile.e<i>.<ключ>`,
`tile.e<i>.remove`, `tile.addtype`, `tile.add`; проверки `expect:tile-field:<ключ>:<значение>`, `expect:effects:N`,
`expect:tile-preview:N:<текст>`; в редакторе карт `expect:tile:c,r:` принимает и название типа. С v0.27.0: поля A юнита и профиля — `unit.armor.<ключ>`, `profile.armor.<ключ>` (и `.reset`);
правила боя — `rules.k`, `rules.k.pick`, `rules.k.add`, `rules.k.<семейство>`, `rules.k.<семейство>.remove`; карта —
`map.ruleset`; песочница — `<префикс>sandbox.armorpen|armorpenpct|ruleset`; проверки `expect:armor:<тип|global>:<A>`,
`expect:profile-armor:<тип>:<A>`, `expect:ruleset-k:[<семейство>:]<K>`, `expect:ruleset-preview:<текст>`,
`expect:map-ruleset:<id|название>`. С v0.28.0: секция `unit.sec.features`; пассивы — `unit.passive.template`, `unit.passive.add`,
`unit.passive.<id>.card|name|stat|type|key|value|rule|below|group|remove`; песочница — `<префикс>sandbox.attacker`
(`__self__` — «этот юнит бьёт пустую цель»); проверки `expect:passives:<N>`, `expect:passive:<название>:<текст>`,
`expect:stat:<характеристика>:<итог>`, `expect:breakdown:<текст>`. С v0.29.0: группы каталога `dev.cat.mechanics`, `dev.cat.origin`
(первый экран — `mech.<вид>`); вид — `species.parent|icon`, эффекты `species.effect.template|add`, `species.effect.<ключ>.card|name|
stat|type|key|value|countspecies|samepack|self|radius|max|below|group|inherit|remove` (у тега — `trait.effect.…`); тег — `trait.category|
icon|group|policy|priority|stack|maxstacks|species.pick|species.toggle|other.pick|require|exclude`; характеристика — `stat.unit|base|
min|max|decimals`; юнит — `unit.species`, `unit.trait.pick|add`, `unit.trait.<i>.remove`, `unit.pack`, `unit.stat.<id>`; проверки
`expect:species-lineage:<линия>`, `expect:species-preview:<текст>`, `expect:trait-preview:<текст>`, `expect:stat-base:<число>`. С v0.30.0:
правила боя — `rules.surprise`, `rules.asymmetric`, `rules.surprise.<0–5>` (без «Разные левый и правый» — `1` и `2` задают пары
флангов), `rules.surprise.multiplier`, `rules.crit`, `rules.crit.base|multiplier|super`; юнит — `unit.critChance`; песочница —
`<префикс>sandbox.side` (0–5) и `<префикс>sandbox.outcome` (`roll|normal|surprise|crit|super|surprise_super`). С v0.31.0: правила
боя — `rules.hex.<0–5>` (сторона на рисунке), `rules.side.value`, `rules.side.test`, `rules.surprise.melee`, `rules.crit.test`,
`rules.crit.rolls`; песочница — `<префикс>sandbox.seed|batch|reset`; тег — `trait.assign.pick`, `trait.assign.add`, `trait.assign`;
проверки `expect:ruleset-side:<n>`, `expect:ruleset-rolls:<текст>`, `expect:trait-units:<N>`, `expect:features-summary:<текст>`,
`expect:hitword:<слово>:yes|no` (слово над ударом в этом матче). С v0.32.0: базовая атака — `battack.name|priority|targets|power|type|more|
crit|surprise|moving`, `battack.turn.left|right`, `cell:q,r` (гекс поля относительно юнита); карта — `map.autoattacks`; юнит —
`unit.autoAttackEnabled`, `unit.basicAttack`; проверки `expect:battack-cells:<q,r q,r …>`, `expect:battack-preview:<текст>`,
`expect:battack-facing:<направление>`, `expect:map-autoattacks:yes|no`, `expect:battle-preview:<текст>` (строки «В бою» юнита).
Шаг `row:` прокручивает список к строке и щёлкает в следующем кадре (D-169). С v0.33.0: юнит — `unit.block`; в игре
`expect:blocked:<ID>:yes|no` (юнит удерживается блоком). С v0.34.0: меню — `menu.scenario`; редактор сценария —
`scenario.mode.<режим>`, `scenario.map`, `scenario.map.open`, `scenario.brush`, `scenario.turn.left|right`, `scenario.enemy.remove|deselect`,
`scenario.enemies.fix`, `scenario.squad.remove.<N>`, `scenario.squad.pick`, `scenario.squad.add`, `scenario.ruleset`,
`scenario.startpaused|autoattacks|blocking|timelimit`, `scenario.test`, шаги `cell:c,r` (ЛКМ по гексу карты сценария) и
`rcell:c,r` (ПКМ — убрать врага); проверки `expect:scenario-enemies:<N>`, `expect:scenario-enemy:c,r:<ID юнита>:<взгляд>`,
`expect:scenario-squad:<N|ID,ID…>`, `expect:scenario-errors:<N>`, `expect:scenario-preview:<текст>`, `expect:scenario-selected:c,r|none`;
в игре `expect:phase:Preparing|Running|Victory|Defeat`, `expect:enemies-left:<N>`. С v0.35.0: редактор карт — `map.routes`,
`map.tile.base`, `map.base.name`, `map.route.pick.<id>`, `map.route.name|enabled|redraw|copy|delete|back|cancel`, `map.routes.check`,
шаги `routedraw:c,r>c,r>…` (ЛКМ по первому гексу, затем курсор по следующим — одно движение за кадр), `rclick:c,r`; проверки
`expect:route-mode:on|off`, `expect:routes:<N>`, `expect:route:<id>:<c,r c,r …>`, `expect:route-ends:<id>:<база врага>><база игрока>`,
`expect:route-draft:<N>`, `expect:route-error:<текст>|none`, `expect:route-selected:<id>|none`, `expect:route-check:<id>:ok|<N>`,
`expect:route-name:<id>:<название>`; в игре — `game.routes.show`, `game.route.<id>`, шаг `route:<id>`, проверки
`expect:routes-visible:yes|no`, `expect:route-index:<ID>:<индекс>|done|none`. С v0.36.0: редактор сценария —
`scenario.base.<N>.lives|critical`, `scenario.wave.add`, `scenario.wave.pick.<n>`, `scenario.wave.name|start|delay|enabled|copy|up|down|delete`,
`scenario.group.<n>.route|unit|count|delay|interval|offset|enabled|remove`, `scenario.group.add`, `scenario.defeat`; проверки
`expect:scenario-waves:<N>`, `expect:scenario-wave-total:<n>:<N>`, `expect:scenario-wave-enemies:<N>`, `expect:scenario-route-total:<id>:<N>`,
`expect:scenario-base-lives:<база>:<N>`; в бою `expect:wave:<начато>/<всего>`, `expect:upcoming:<N>`, `expect:base-lives:<база>:<N>`. С v0.37.0:
`scenario.template.survive|protect|waves`, `scenario.survive`, `scenario.objects.policy`, `scenario.squadloss`,
`scenario.brush.enemies|objects`, `scenario.object.name|health|critical|remove|deselect`, `scenario.enemy.ai|guard|detect`,
`scenario.group.<n>.source|ai|detect`, `scenario.<enemy|group.<n>>.ai.more|priority|reevaluate|switch`; проверки
`expect:scenario-objects:<N>`, `expect:scenario-object:<c,r>:<название>:<HP>:yes|no`, `expect:scenario-enemy-ai:<c,r>:<пресет>`,
`expect:scenario-group:<волна>:<группа>:<источник|route:id>:<пресет>`, `expect:scenario-ai-more:<c,r|в.г>:<приоритет>:<с>:yes|no`,
`expect:scenario-preview:<текст>`; в бою `expect:object-hp:<название>:<HP>|dead`, `expect:ai-target:<ID>:<ID цели>|none`.
В бою: `advance:N` — ровно N
тиков симуляции за один кадр (пауза сохраняется), `expect:hp:ID:значение~допуск`.
С v0.20.0: каталог — `dev.export`, `dev.import`; окно экспорта — `dialog.input` (название пакета), `export.pick.<название>`,
`export.json`, `export.hxp`, `export.folder`, `export.cancel`; импорт — `dialog.input` (путь или имя файла из `exports`),
`import.browse`, `import.preview`, `import.res.<N>` (`install|skip|replace|copy`), `import.install`, `import.cancel`;
проверка `expect:import:<текст>` (итог последнего предпросмотра).
С v0.21.0: поля сопротивлений `unit.res.<global|ID типа>`, проверка `expect:resistance:<ключ>:<итог>`; в бою
`spawn:enemy:c,r@Название` (юнит отладки из определения), `expect:info:ID:текст:yes|no` (что показывает карточка юнита).
С v0.22.0: редактор правил — поля `rule.<ключ>` (`rule.trigger`, `rule.damageTypeId`, `rule.useResistanceThreshold`…),
`rule.template`, `rule.template.apply`, реакции `rule.a<i>.<ключ>`, `rule.a<i>.remove`, `rule.addtype`, `rule.add`,
`rule.advanced`; проверки `expect:rule-summary:<текст>`, `expect:rule-actions:<effect,…|N>`, `expect:rule-field:<ключ>:<значение>`;
в редакторе юнита `unit.rule.filter`, `unit.rule.pick`, `unit.rule.add`, `unit.rule.<i>.remove`, `unit.shield.add`,
`unit.shield.<i>.name|capacity|damageTypeId|order|remove`, проверки `expect:rules:<N|названия>`, `expect:shield:<i>:<название>:<прочность>`;
`spawn:cell:c,r@Название` — юнит игрока из определения.
С v0.23.0: каталог — `dev.cat.mechanics` (первый экран «Механик», `expect:category:mechanics`), `mech.<вид>`; создание типа —
`dialog.base.physical|magical|true|family`; редактор типа — `dtype.color|icon|more|family|strategy|tags`, проверки
`expect:dtype:<family|strategy|tags>:<значение>`, `expect:dtype-preview:<текст>`; редактор набора — `profile.parent`,
`profile.res.<ключ>`, `profile.res.<ключ>.reset`, `expect:profile-total:<тип>:<итог>`, `expect:profile-preview:<текст>`; юнит —
`unit.profile.pick|add|save`, `unit.profile.<i>.remove`, `unit.res.<ключ>.reset`, `expect:profiles:<N|названия>`,
`expect:resistance-source:<ключ>:own|inherited|none`; песочница — `<префикс>sandbox` (раскрыть), `<префикс>sandbox.target|type|amount|penetration|
health|enemies|allies|run` (префикс `rule.`, `dtype.`, `unit.`), проверка `expect:sandbox:<текст>`.
С v0.24.0: секции юнита `unit.sec.main|attack|res|more` (`expect:section:<секция>`); таблица — `unit.res.search`, `unit.res.all`,
`unit.res.<тип>.expand`, `unit.res.<тип>.rules` (`expect:res-rows:<ключи без core.damage.>`, `expect:res-has:<строка>:yes|no`,
`expect:res-summary:<текст>`); карточки — `unit.rule.<i>.card|copy|remove`, `unit.shield.<i>.card`; меню — `unit.rule.addmenu`;
копирование — `unit.copy.from|run`; бой — `game.debugdefense`; `expect:control:<id>:yes|no` — элемент есть на экране.
Шаг к элементу закрытой секции сначала раскрывает её (до 6 кадров). С v0.24.1: `expect:on-screen:<id>:yes|no` — элемент
целиком в окне. С v0.25.0 шаги `ui:`/`field:`/`row:` после смены открытого редактора ждут 4 кадра.
С v0.26.0: в поле-ссылке `field:<id>=__create__` выбирает «＋ Новый тип урона…»; `dtype.element` — флажок «элемент»;
каталог — `dev.unarchive`, диалог удаления — `dialog.replace.pick`, `dialog.replace`, `dialog.archive`; `expect:resistance`
принимает и название типа.
Регрессионные сценарии релиза — `tools/verify_release.ps1` (`$RegressionScenarioV05` для v0.5–v0.6, `$RegressionScenarioV07`
для v0.7, `$RegressionScenarioV08` = V07 + ростер для v0.8, `$RegressionScenarioV09` = V08 + команда для v0.9, `$RegressionScenarioV10` = V09 (взгляд спутника ±60°) + команда из
трёх с двумя КТД для v0.10, `$RegressionScenarioV11` = V10 + замер времени трёх переходов для v0.11, `$RegressionScenarioV12` = V11 + атака мишени
для v0.12, `$RegressionScenarioV13` = V12 + «Линейный удар» для v0.13, `$RegressionScenarioV14` = V13 + навык в ТД через
меню для v0.14.0, `$RegressionScenarioV141` = V14 + подсветка при меню/прицеливании для v0.14.1, `$RegressionScenarioV15` =
«Играть» + V141 + выход в меню + каталог разработчика для v0.15.0, `$RegressionScenarioV16` = V15 + редактор карт, тест,
игра на сохранённой карте для v0.16.0, `$RegressionScenarioV17` = V16 + редактор юнитов, тест, отряд для v0.17.0,
`$RegressionScenarioV18` = V17 + редактор навыков, тест навыка, навык своего юнита из меню ТД для v0.18.0,
`$RegressionScenarioV19` = V18 + редактор гексов, тест гекса, карта с жгучей и целебной поверхностями для v0.19.x,
`$RegressionScenarioV20` = V19 + экспорт, импорт, конфликты для v0.20.0, `$RegressionScenarioV21` = V20 + сопротивления и типы
урона для v0.21.0, `$RegressionScenarioV22` = V21 + правила защиты, щиты и «огонь 300 %» в бою для v0.22.0,
`$RegressionScenarioV23` = V22 + «Механики» (тип, набор, песочница, наборы юнита) для v0.23.0,
`$RegressionScenarioV24` = V23 + секции юнита, таблица, карточки, правило поверх юнита, отладка теста для v0.24.0,
`$RegressionScenarioV241` = V24 + кнопки панелей в пределах окна для v0.24.1,
`$RegressionScenarioV25` = V241 + UX-испытание пяти юнитов, их бой, экспорт «Защиты» для v0.25.0,
`$RegressionScenarioV26` = V25 + тип из селектора, его доступность в правиле, гексе и юните, переименование, архив для v0.26.0,
`$RegressionScenarioV27` = V26 + правила боя, защита A юнита, песочница, бой в тестах юнита и карты для v0.27.0,
`$RegressionScenarioV28` = V27 + пассивы «Гидроманта», откуда взялось, песочница с атакующим, бой для v0.28.0,
`$RegressionScenarioV29` = V28 + дерево видов, теги, стая, бонус вожака в тестовом запуске для v0.29.0,
`$RegressionScenarioV30` = V29 + «Засада», «Убийца», песочница с заданным исходом, бой на совместимых и стандартных правилах для v0.30.0,
`$RegressionScenarioV31` = V30 + «Вода», тег «Гидромант», «Водный маг» (вид, три тега, пассив, R, A), назначение тега, шестиугольник,
100 событий, 1000 бросков, «Сверхкрит!» в игре, экспорт «Вода.hxp» для v0.31.0,
`$RegressionScenarioV32` = V31 + базовая атака «Копьё», «Копейщик», автоатака на «Арене копий» (3 удара/с, поворот) для v0.32.0,
`$RegressionScenarioV33` = V32 + «Страж» с блоком 1: один из двух проходящих удержан, второй дошёл, для v0.33.0,
`$RegressionScenarioV34` = V33 + сценарий «Засада» в редакторе (враг щелчком, поворот, отмена, отряд из одного), его тест от
«Подготовки» до «Победы» и «Учебный бой» из меню для v0.34.0,
`$RegressionScenarioV35` = V34 + «Две линии»: базы 1 и 2, две красные линии курсором (повторы, ошибка на препятствии, Backspace,
Esc, скачок мыши), «Петля», враги по обеим линиям в тесте карты для v0.35.0,
`$RegressionScenarioV36` = V35 + Tower Defense «Оборона двух линий»: волна из двух групп, копия выключена, жизни баз, утечки по
своим базам и «Победа» в тесте для v0.36.0,
`$RegressionScenarioV37` = V36 + «Двор» без базы игрока и оборона «Оборона двора» из шаблона «Защитить объект»: «Генератор»,
охранник, волна из зоны к объекту, «Дополнительно» ИИ; поражение с критическим объектом и победа по времени с некритическим
для v0.37.0+ (`$CleanImportScenarioV31` = V25 + импорт «Вода.hxp» и те же
30 урона в тесте юнита; `$CleanImportScenarioV25` —
чистая установка с двумя пакетами), `$CleanImportScenarioV20` — третий запуск с чистой
папкой: импорт пакета первого запуска и игра на нём;
`$PersistenceScenarioV15/V16/V17/V18/V19/V20/V21/V22/V23/V24/V25/V26/V27/V28/V29/V30/V31/V32/V33/V34/V35/V36/V37` — второй запуск с той же папкой пользователя:
сохранённое видно после перезапуска). Сценарий длиннее 32 000 символов (предел командной строки Windows) идёт несколькими запусками `smoke`, `smoke-2`, … с одной папкой пользователя, разрезанный по концам сценариев прежних версий (D-164). Точка `P` — `c,r` (центр гекса) или
`c,r@Direction:dist` (смещение от центра в плоскости карты, в размерах гекса), например `drag:9,8>9,8@NorthWest:0.6`.

## 12. Версия и сборка

- `Directory.Build.props` → `Version`; .NET SDK добавляет хеш коммита в `AssemblyInformationalVersion`
  (`0.13.0+<sha>`), HUD показывает версию и короткий коммит.
- `project.godot` → `config/version` (метаданные exe). Скрипт релиза проверяет совпадение версий и тега.
- Экспорт: `godot --headless --path src/GodotClient --export-release "Windows Desktop" <out>/HexTactics.exe`
  → `HexTactics.exe`, `HexTactics.console.exe` (с консолью/логом), `HexTactics.pck`,
  `data_HexTactics_windows_x86_64/` (самодостаточный .NET 8 рантайм и сборки игры).
