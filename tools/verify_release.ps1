<#
.SYNOPSIS
    Verifies an archived HexTactics release: checksum, build info, Git tag and ZIP contents.

.DESCRIPTION
    Read-only: never modifies the archive. With -Run the ZIP is extracted into a temporary
    folder and the game is started in smoke-test mode (it renders a few frames and exits).

.EXAMPLE
    pwsh tools/verify_release.ps1 -Version 0.1.0
    pwsh tools/verify_release.ps1 -Version 0.1.0 -Run
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$ArchiveRoot,
    [switch]$Run,
    # Command used to launch the Windows executable on non-Windows hosts (e.g. wine64).
    [string]$Launcher
)

. "$PSScriptRoot/common.ps1"

# End-to-end scenarios on the prototype map (see docs/ARCHITECTURE.md, "Автоматизация"). The input scheme changed
# in v0.7.0 (spec part 2: continuous drafts + mandatory final facing), so each archive is checked with the scenario of
# its own generation.
$script:RegressionScenarioV05 = @(
    'spawn:base', 'expect:units:1',
    'drag:0,4>3,2', 'expect:waypoints:1:1',
    'key:Space', 'expect:paused:true', 'mark', 'wait:20', 'expect:same-tick',
    'drag:3,2>6,1', 'expect:waypoints:1:2',
    'key:Space', 'expect:paused:false',
    'waitidle:1:3000', 'expect:cell:1:6,1', 'expect:waypoints:1:0', 'expect:status:1:Idle',
    'drag:6,1>6,1@SouthWest:0.6', 'expect:facing:1:SouthWest',
    'place', 'click:9,8', 'expect:units:2',
    'drag:9,8>8,3', 'expect:waypoints:2:0',
    'key:Escape'
) -join ';'

$script:RegressionScenarioV07 = @(
    'spawn:base', 'expect:units:1', 'key:Space', 'expect:paused:true',
    'drag:0,4>3,2', 'expect:draft:1', 'click:3,2@North:0.6', 'expect:draft:none', 'expect:waypoints:1:1',
    'mark', 'wait:20', 'expect:same-tick',
    'drag:3,2>6,1', 'expect:draft:2', 'click:6,1@SouthWest:0.6', 'expect:waypoints:1:2',
    'key:Space', 'expect:paused:false',
    'waitidle:1:3000', 'expect:cell:1:6,1', 'expect:waypoints:1:0', 'expect:status:1:Idle', 'expect:facing:1:SouthWest',
    'click:6,1', 'click:6,1@North:0.6', 'wait:3', 'expect:facing:1:North', 'expect:cell:1:6,1',
    'place', 'click:9,8', 'expect:units:2',
    'drag:9,8>8,3', 'expect:draft:none', 'expect:waypoints:2:0',
    'drag:9,8>9,6', 'expect:draft:1', 'rclick:0,0', 'expect:draft:none', 'expect:waypoints:2:0',
    'key:Escape'
) -join ';'

# v0.8.0+: the v0.7 scenario plus roster placement (spec part 2 §5): a card dropped on a wall returns, a card dropped
# on the base opens the arrow at once, a cancelled draft leaves the unit pending, its card reopens the draft.
$script:RegressionScenarioV08 = ($script:RegressionScenarioV07 -replace ';key:Escape$', '') + ';' + (@(
    'roster:2>3,5>5,5', 'expect:roster:2:AvailableInRoster', 'expect:units:2',
    'roster:1>3,5>1,4', 'expect:units:3', 'expect:lifecycle:3:PlacedPendingFacing', 'expect:roster:1:PlacedPendingFacing', 'expect:draft:0',
    'click:3,4', 'expect:draft:1', 'click:3,4@NorthEast:0.6', 'expect:draft:none', 'expect:lifecycle:3:Active', 'expect:roster:1:Active',
    'waitidle:3:3000', 'expect:cell:3:3,4', 'expect:facing:3:NorthEast',
    'roster:3>0,6', 'expect:lifecycle:4:PlacedPendingFacing', 'rclick:6,6', 'expect:draft:none', 'expect:lifecycle:4:PlacedPendingFacing',
    'roster:3', 'expect:draft:0', 'click:0,6@North:0.6', 'expect:facing:4:North', 'expect:roster:3:Active', 'expect:units:4',
    'key:Escape'
) -join ';')

# v0.9.0+: the v0.8 scenario plus the command mode (spec part 2 §6): the real "Команда" card, captain = first click,
# no duplicates, RMB cancels, КТД1 + captain's facing applies the command order to both members.
$script:RegressionScenarioV09 = ($script:RegressionScenarioV08 -replace ';key:Escape$', '') + ';' + (@(
    'command', 'expect:mode:GroupCollect', 'click:3,4', 'expect:captain:3', 'click:0,6', 'click:3,4', 'expect:group:2:0',
    'rclick:6,6', 'expect:group:none', 'expect:waypoints:3:0',
    'command', 'click:3,4', 'click:0,6', 'click:2,2', 'expect:group:2:1', 'click:2,2@South:0.6', 'expect:group:none',
    'expect:ingroup:3:yes', 'expect:ingroup:4:yes',
    'waitidle:3:3000', 'waitidle:4:3000', 'expect:cell:3:2,2', 'expect:facing:3:South', 'expect:facing:4:South', 'expect:ingroup:4:no',
    'key:Escape'
) -join ';')

# v0.10.0+: companions look the captain's way or ±60° (seeded), so the v0.9 companion check is relaxed; then a team of
# three with two КТД moves in synchronised stages and ends in formation with the stored facings.
$script:RegressionScenarioV10 = (($script:RegressionScenarioV09 -replace ';key:Escape$', '') -replace 'expect:facing:4:South', 'expect:facing-near:4:South') + ';' + (@(
    'command', 'click:6,1', 'expect:captain:1', 'click:2,2', 'click:9,8', 'expect:group:3:0',
    'click:4,4', 'click:8,5', 'expect:group:3:2', 'click:8,5@South:0.6', 'expect:group:none',
    'expect:ingroup:1:yes', 'expect:ingroup:2:yes', 'expect:ingroup:3:yes',
    'waitidle:1:4000', 'waitidle:2:4000', 'waitidle:3:4000',
    'expect:cell:1:8,5', 'expect:facing:1:South', 'expect:facing-near:2:South', 'expect:facing-near:3:South', 'expect:ingroup:2:no',
    'key:Escape'
) -join ';')

# v0.11.0+ (spec part 3 §2): the v0.10 scenario plus a timed move — three transitions at 1 hex/s take 3 game seconds,
# and the drawn body always lies in the unit's logical cell.
$script:RegressionScenarioV11 = ($script:RegressionScenarioV10 -replace ';key:Escape$', '') + ';' + (@(
    'spawn:cell:11,9', 'expect:units:5', 'pause:on', 'drag:11,9>11,6', 'click:11,6@North:0.6', 'expect:waypoints:5:1',
    'mark', 'pause:off', 'wait:15', 'expect:in-cell:5', 'waitidle:5:3000', 'expect:arrived:5:3.0', 'expect:cell:5:11,6',
    'expect:in-cell:5', 'expect:facing:5:North',
    'key:Escape'
) -join ';')

# v0.12.0+ (spec part 3 §3–4): a static enemy target; the white arrow dropped on it gives an attack order on pause —
# the unit faces the target at once, no damage while paused; after resuming it approaches and kills it (10 × 10 HP).
$script:RegressionScenarioV12 = ($script:RegressionScenarioV11 -replace ';key:Escape$', '') + ';' + (@(
    'spawn:enemy:11,4', 'expect:units:6', 'expect:hp:6:100', 'pause:on', 'drag:11,6>11,4', 'expect:draft:none',
    'expect:facing:5:North', 'expect:attack:5:None', 'wait:10', 'expect:hp:6:100', 'pause:off',
    'waitdead:6:4000', 'expect:units:5', 'expect:hp:5:100', 'expect:cell:5:11,5', 'expect:in-cell:5', 'expect:attack:5:None',
    'key:Escape'
) -join ';')

# v0.13.0+ (spec part 3 §5): «Линейный удар» aimed with the real key R on pause — planned, no damage until resume;
# then both enemies in the two hexes take 20; RMB cancels a second aiming without effect.
$script:RegressionScenarioV13 = ($script:RegressionScenarioV12 -replace ';key:Escape$', '') + ';' + (@(
    'spawn:enemy:11,4', 'spawn:enemy:11,3', 'expect:units:7', 'select:5', 'pause:on', 'key:R', 'expect:mode:SkillAim',
    'move:11,3', 'click:11,3', 'expect:mode:UnitSelected', 'expect:pending-skill:5:yes', 'wait:10', 'expect:hp:7:100',
    'pause:off', 'wait:6', 'expect:pending-skill:5:no', 'expect:hp:7:80', 'expect:hp:8:80', 'expect:hp:5:100', 'expect:facing:5:North',
    'key:R', 'move:11,6', 'rclick:11,6', 'expect:mode:UnitSelected', 'expect:pending-skill:5:no', 'wait:6', 'expect:hp:7:80',
    'key:Escape'
) -join ';')

# v0.14.0+ (spec part 3 §6): a skill planned at a waypoint with the real menu button on pause. Short click on the
# ТД1 marker opens the menu (no new waypoint; Esc closes only the menu); the icon starts the red arrow from ТД1; the
# direction click plans it in the draft; the ✕ button removes it; planned again, the edge sector of ТД2 confirms the
# order with the final facing. After resuming the unit stops at ТД1, strikes once (enemy 7 −20, enemy 8 beyond the two
# hexes untouched), walks on to ТД2 and turns to the final facing; the skill never fires again.
$script:RegressionScenarioV14 = ($script:RegressionScenarioV13 -replace ';key:Escape$', '') + ';' + (@(
    'select:5', 'pause:on', 'drag:11,5>11,6', 'click:11,7', 'expect:draft:2',
    'click:11,6', 'expect:menu:open:1', 'expect:draft:2', 'key:Escape', 'expect:menu:none', 'expect:draft:2',
    'click:11,6', 'expect:menu:open:1', 'menu-skill', 'expect:mode:SkillAim', 'expect:menu:none',
    'move:11,4', 'click:11,4', 'expect:mode:SingleDraft', 'expect:draft-skills:1',
    'click:11,6', 'expect:menu:open:1', 'menu-clear', 'expect:draft-skills:0', 'expect:menu:none',
    'click:11,6', 'menu-skill', 'move:11,4', 'click:11,4', 'expect:draft-skills:1', 'expect:draft:2',
    'click:11,7@North:0.6', 'expect:draft:none', 'expect:wpskills:5:1', 'wait:10', 'expect:hp:7:80', 'expect:hp:8:80',
    'pause:off', 'waitidle:5:4000', 'expect:cell:5:11,7', 'expect:facing:5:North', 'expect:wpskills:5:0',
    'expect:hp:7:60', 'expect:hp:8:80', 'expect:hp:5:100', 'wait:30', 'expect:hp:7:60',
    'key:Escape'
) -join ';')

# v0.14.1+ (spec part 3 §6.4): while the waypoint menu is open or a skill is aimed from a waypoint the green/red movement
# fills are hidden (the grey area is never mixed with them); cancelling the aiming brings the draft and its fills back.
$script:RegressionScenarioV141 = ($script:RegressionScenarioV14 -replace ';key:Escape$', '') + ';' + (@(
    'select:5', 'pause:on', 'drag:11,7>10,7', 'expect:draft:1', 'expect:movefill:on',
    'click:10,7', 'expect:menu:open:1', 'expect:movefill:off', 'menu-skill', 'expect:mode:SkillAim', 'expect:movefill:off',
    'move:10,5', 'rclick:10,5', 'expect:mode:SingleDraft', 'expect:draft:1', 'expect:draft-skills:0', 'expect:movefill:on',
    'key:Escape', 'expect:draft:none', 'expect:movefill:off', 'expect:cell:5:11,7', 'pause:off',
    'key:Escape'
) -join ';')

# v0.15.0+ (spec part 4 §2, §8): the build starts on the menu — the whole v0.14.1 scenario runs behind the real
# «Играть» button (map, unit and skill now come from the content library); then back to the menu (Esc keeps the match),
# the developer catalog: tile category, duplicate a built-in type, rename it, Ctrl+Z / Ctrl+Y, delete with confirmation
# and undo, all categories listed. The user data folder is a fresh temporary one (--hx-user-root).
$script:RegressionScenarioV15 = 'ui:menu.play;expect:screen:play;' + ($script:RegressionScenarioV141 -replace ';key:Escape$', '') + ';' + (@(
    'ui:game.menu', 'expect:dialog:Выйти', 'key:Escape', 'expect:dialog:none', 'expect:screen:play',
    'ui:game.menu', 'ui:dialog.ok', 'expect:screen:menu',
    'ui:menu.dev', 'expect:screen:dev', 'ui:dev.cat.tile', 'expect:category:tile', 'expect:rows:7',
    'row:core.tile.ground', 'expect:selected:core.tile.ground', 'ui:dev.duplicate', 'expect:rows:8',
    'ui:dev.rename', 'expect:dialog:Переименовать', 'field:dialog.input=Кислотный гекс', 'ui:dialog.ok', 'expect:dialog:none',
    'expect:item:tile:Кислотный гекс:yes', 'key:ctrl+z', 'expect:item:tile:Кислотный гекс:no', 'key:ctrl+y', 'expect:item:tile:Кислотный гекс:yes',
    'ui:dev.delete', 'expect:dialog:Удалить', 'ui:dialog.ok', 'expect:item:tile:Кислотный гекс:no', 'expect:rows:7',
    'key:ctrl+z', 'expect:item:tile:Кислотный гекс:yes', 'expect:rows:8',
    'field:dev.search=кисл', 'expect:rows:1', 'field:dev.search=', 'expect:rows:8',
    'ui:dev.cat.unit', 'expect:rows:1', 'ui:dev.cat.skill', 'expect:rows:1', 'ui:dev.cat.map', 'expect:rows:1',
    'ui:dev.back', 'expect:screen:menu'
) -join ';')

# Second launch with the same user data folder: what was saved before the restart is still in the library, and the
# game still starts (spec part 4 §8.1, §12.3 p.24).
$script:PersistenceScenarioV15 = (@(
    'expect:screen:menu', 'ui:menu.dev', 'ui:dev.cat.tile', 'expect:rows:8', 'expect:item:tile:Кислотный гекс:yes',
    'ui:dev.back', 'ui:menu.play', 'expect:screen:play', 'spawn:base', 'expect:units:1'
) -join ';')

# v0.16.0+ (spec part 4 §4, §12.1 p.3–9): the map editor through its real buttons — an empty 12×10 workspace, clicks create
# exactly one hex each and select it, types from the library, the red two-stage deletion (marks first, deletes on the
# second press, Ctrl+Z restores both, Esc and an empty selection delete nothing, a linked test point needs confirmation),
# saving, a test run in the game mode and back to the unchanged editor, then playing the saved map from the menu
# (a waypoint on an empty place is refused).
$script:RegressionScenarioV16 = $script:RegressionScenarioV15 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.map', 'ui:dev.create', 'expect:editor:map', 'expect:tiles:0',
    'click:2,4', 'expect:tiles:1', 'expect:hex:2,4', 'field:map.tile.type=База игрока', 'expect:tile:2,4:core.tile.player_base',
    'click:3,4', 'click:4,4', 'click:3,4', 'expect:hex:3,4', 'expect:tiles:3',
    'click:4,4', 'field:map.tile.type=Высокий гекс', 'click:5,4', 'field:map.tile.type=База врага', 'expect:tiles:4',
    'ui:map.delete', 'expect:delete-mode:on', 'click:4,4', 'click:5,4', 'expect:delete-count:2', 'expect:tiles:4',
    'ui:map.delete', 'expect:delete-mode:off', 'expect:tiles:2', 'key:ctrl+z', 'expect:tiles:4',
    'expect:tile:4,4:core.tile.high', 'expect:tile:5,4:core.tile.enemy_base',
    'ui:map.delete', 'click:4,4', 'key:Escape', 'expect:delete-mode:off', 'expect:tiles:4',
    'ui:map.delete', 'ui:map.delete', 'expect:delete-mode:off', 'expect:tiles:4',
    'click:2,4', 'ui:map.tile.testpoint', 'click:5,4', 'ui:map.tile.testpoint', 'expect:testpoints:2',
    'ui:map.delete', 'click:5,4', 'ui:map.delete', 'expect:dialog:Удалить', 'ui:dialog.ok', 'expect:tiles:3', 'expect:testpoints:1',
    'key:ctrl+z', 'expect:tiles:4', 'expect:testpoints:2',
    'ui:map.tab.map', 'field:map.name=Перешеек', 'expect:map-name:Перешеек', 'ui:map.save', 'expect:dirty:no', 'expect:map-errors:0',
    'ui:map.test', 'expect:screen:play', 'spawn:base', 'expect:units:1', 'expect:cell:1:2,4',
    'drag:2,4>4,4', 'click:4,4@North:0.6', 'waitidle:1:900', 'expect:cell:1:4,4',
    'ui:game.menu', 'ui:dialog.ok', 'expect:screen:dev', 'expect:editor:map', 'expect:tiles:4', 'expect:dirty:no',
    'ui:map.back', 'expect:editor:none', 'expect:item:map:Перешеек:yes', 'ui:dev.back', 'expect:screen:menu',
    'field:menu.map=Перешеек (своя)', 'ui:menu.play', 'expect:screen:play', 'spawn:base', 'expect:cell:1:2,4',
    'press:2,4', 'move:3,2', 'release:3,2', 'expect:draft:none', 'ui:game.menu', 'ui:dialog.ok', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV16 = (@(
    'ui:menu.dev', 'ui:dev.cat.map', 'expect:item:map:Перешеек:yes', 'row:name=Перешеек', 'ui:dev.open', 'expect:editor:map',
    'expect:tiles:4', 'expect:tile:2,4:core.tile.player_base', 'expect:tile:4,4:core.tile.high', 'expect:tile:5,4:core.tile.enemy_base',
    'expect:testpoints:2', 'ui:map.back', 'expect:editor:none', 'ui:dev.back'
) -join ';') + ';' + $script:PersistenceScenarioV15

# v0.17.0+ (spec part 4 §5, §12.2 p.10–11): the unit editor through its real controls — invalid values (−5 HP, a word for
# the speed) are refused with an explanation and change nothing; HP 175, 1.5 hex/s, 2 attacks/s, attack power 12.5, a
# skill added from the catalog (undo/redo); saved; a test match with the unit as roster card 1: 175 HP and three hexes
# in exactly 2.0 game seconds; then «Играть» with the squad of this unit.
$script:RegressionScenarioV17 = $script:RegressionScenarioV16 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.unit', 'expect:rows:1', 'ui:dev.create', 'expect:editor:unit',
    'field:unit.maxHealth=-5', 'expect:editor-message:не меньше', 'expect:unit-field:maxHealth:100',
    'field:unit.moveSpeed=быстро', 'expect:editor-message:не число', 'expect:unit-field:moveSpeed:1',
    'field:unit.maxHealth=175', 'field:unit.moveSpeed=1,5', 'field:unit.attackRate=2', 'field:unit.attackPower=12.5',
    'field:unit.name=Тяжёлый', 'field:unit.description=Медленный, но живучий', 'expect:name:Тяжёлый',
    'ui:unit.skill.add', 'expect:skills:1', 'key:ctrl+z', 'expect:skills:0', 'key:ctrl+y', 'expect:skills:1',
    'ui:unit.save', 'expect:dirty:no', 'expect:errors:0',
    'ui:unit.test', 'expect:screen:play', 'pause:on', 'roster:1>0,5', 'click:3,5', 'click:3,5@North:0.6', 'mark', 'pause:off',
    'waitidle:1:900', 'expect:arrived:1:2.0', 'expect:hp:1:175', 'ui:game.menu', 'ui:dialog.ok', 'expect:editor:unit',
    'ui:unit.back', 'expect:editor:none', 'expect:rows:2', 'expect:item:unit:Тяжёлый:yes', 'ui:dev.back',
    'field:menu.map=Прототип 12×10', 'field:menu.squad=Тяжёлый (свой) × 5', 'ui:menu.play', 'expect:screen:play', 'roster:2>1,5', 'click:1,5@North:0.6', 'expect:lifecycle:1:Active', 'expect:hp:1:175',
    'ui:game.menu', 'ui:dialog.ok', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV17 = (@(
    'ui:menu.dev', 'ui:dev.cat.unit', 'expect:item:unit:Тяжёлый:yes', 'row:name=Тяжёлый', 'ui:dev.open', 'expect:editor:unit',
    'expect:unit-field:maxHealth:175', 'expect:unit-field:moveSpeed:1.5', 'expect:unit-field:attackRate:2',
    'expect:unit-field:attackPower:12.5', 'expect:skills:1', 'ui:unit.back', 'ui:dev.back'
) -join ';') + ';' + $script:PersistenceScenarioV16

# v0.18.0+ (spec part 4 §6, §12.2 p.12–15, 18–20): the skill editor through its real controls. The built-in «Линейный
# удар» is ordinary data (two cells, 20 damage each); the drawing turns with the facing of the central unit in all six
# directions (a click on the drawn hex selects the same stored cell). A new skill: base damage 3 × 150 % of Attack Power
# every 0.5 s, cell (0,-1) inherits it, cell (0,-2) overrides only the multiplier (75 %), cell (-1,0) has its own fixed
# 7 damage and a red editor colour; an invalid hit count is refused; undo/redo. A test match: enemies in the three cells
# lose 45, 22.5 and 7 HP (Attack Power 10). Then the skill is bound to the user unit «Тяжёлый» (first skill), and in
# «Играть» it is chosen from the waypoint menu with the red arrow: the unit reaches ТД1, strikes three times with its
# Attack Power 12.5 (56.25 and 28.125 damage) and walks on to ТД2.
$script:RegressionScenarioV18 = $script:RegressionScenarioV17 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.skill', 'expect:rows:1', 'row:name=Линейный удар', 'ui:dev.open', 'expect:editor:skill',
    'expect:cells:2', 'expect:preview:0:урон 20 врагам', 'expect:preview:1:урон 20 врагам',
    'ui:skill.turn.right', 'expect:skill-facing:NorthEast', 'cell:1,-1', 'expect:selected-cell:0,-1',
    'ui:skill.turn.right', 'expect:skill-facing:SouthEast', 'cell:1,0', 'expect:selected-cell:0,-1',
    'ui:skill.turn.right', 'expect:skill-facing:South', 'cell:0,2', 'expect:selected-cell:0,-2',
    'ui:skill.turn.right', 'expect:skill-facing:SouthWest', 'cell:-1,1', 'expect:selected-cell:0,-1',
    'ui:skill.turn.right', 'expect:skill-facing:NorthWest', 'cell:-2,0', 'expect:selected-cell:0,-2',
    'ui:skill.turn.right', 'expect:skill-facing:North', 'cell:0,-1', 'expect:selected-cell:0,-1', 'key:Escape',
    'expect:selected-cell:none', 'expect:dirty:no', 'ui:skill.back', 'expect:editor:none',
    'ui:dev.create', 'expect:editor:skill', 'expect:cells:0', 'field:skill.name=Тройной удар',
    'field:skill.addtype=damage', 'ui:skill.add', 'field:skill.e0.formula=attack', 'field:skill.e0.multiplier=150',
    'field:skill.e0.hits=3', 'field:skill.e0.interval=0.5', 'field:skill.e0.hits=0', 'expect:editor-message:не меньше',
    'cell:0,-1', 'cell:0,-2', 'expect:cells:2', 'expect:selected-cell:0,-2', 'field:skill.e0.multiplier=75',
    'expect:preview:0:урон 3 × 150 % силы атаки 10 = 15 = 45', 'expect:preview:1:урон 3 × 75 % силы атаки 10 = 7.5 = 22.5',
    'cell:-1,0', 'field:skill.cell.color=#D04040', 'field:skill.cell.own=true', 'field:skill.e0.formula=fixed',
    'field:skill.e0.amount=7', 'field:skill.e0.hits=1', 'expect:preview:2:урон 7 врагам', 'expect:cell-color:-1,0:#D04040',
    'key:ctrl+z', 'expect:preview:2:урон 3 × 7 = 21', 'key:ctrl+y', 'expect:preview:2:урон 7 врагам',
    'ui:skill.save', 'expect:dirty:no', 'expect:errors:0',
    'ui:skill.test', 'expect:screen:play', 'pause:on', 'spawn:cell:11,6', 'spawn:enemy:11,5', 'spawn:enemy:11,4', 'spawn:enemy:10,6',
    'expect:units:4', 'select:1', 'key:R', 'expect:mode:SkillAim', 'move:11,4', 'click:11,4', 'expect:pending-skill:1:yes',
    'wait:10', 'expect:hp:2:100', 'pause:off', 'wait:120', 'expect:pending-skill:1:no',
    'expect:hp:2:55', 'expect:hp:3:77.5', 'expect:hp:4:93', 'expect:hp:1:100',
    'ui:game.menu', 'ui:dialog.ok', 'expect:editor:skill', 'expect:dirty:no', 'ui:skill.back', 'expect:rows:2',
    'expect:item:skill:Тройной удар:yes',
    'ui:dev.cat.unit', 'row:name=Тяжёлый', 'ui:dev.open', 'expect:editor:unit', 'expect:skills:1', 'ui:unit.skill.add',
    'expect:skills:2', 'ui:unit.skill.1.up', 'expect:skills:Тройной удар,Линейный удар', 'ui:unit.save', 'expect:dirty:no',
    'ui:unit.back', 'ui:dev.back', 'expect:screen:menu',
    'field:menu.map=Прототип 12×10', 'field:menu.squad=Тяжёлый (свой) × 5', 'ui:menu.play', 'expect:screen:play', 'pause:on',
    'roster:1>0,5', 'click:0,5@North:0.6', 'expect:lifecycle:1:Active', 'spawn:enemy:2,5', 'spawn:enemy:2,4', 'expect:units:3',
    'select:1', 'drag:0,5>2,6', 'click:2,8', 'expect:draft:2', 'click:2,6', 'expect:menu:open:1', 'menu-skill:0',
    'expect:mode:SkillAim', 'move:2,4', 'click:2,4', 'expect:draft-skills:1', 'click:2,8@North:0.6', 'expect:draft:none',
    'expect:wpskills:1:1', 'wait:10', 'expect:hp:2:100', 'pause:off', 'waitidle:1:4000', 'expect:cell:1:2,8',
    'expect:wpskills:1:0', 'expect:hp:2:43.75', 'expect:hp:3:71.875', 'expect:hp:1:175',
    'ui:game.menu', 'ui:dialog.ok', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV18 = (@(
    'ui:menu.dev', 'ui:dev.cat.skill', 'expect:item:skill:Тройной удар:yes', 'row:name=Тройной удар', 'ui:dev.open',
    'expect:editor:skill', 'expect:cells:3', 'expect:cell-color:-1,0:#D04040', 'expect:cell-color:0,-1:default',
    'expect:preview:0:урон 3 × 150 % силы атаки 10 = 15 = 45', 'expect:preview:1:урон 3 × 75 % силы атаки 10 = 7.5 = 22.5',
    'expect:preview:2:урон 7 врагам', 'ui:skill.back', 'ui:dev.cat.unit', 'row:name=Тяжёлый', 'ui:dev.open',
    'expect:skills:Тройной удар,Линейный удар', 'ui:unit.back', 'ui:dev.back'
) -join ';') + ';' + ($script:PersistenceScenarioV17 -replace 'expect:skills:1;', 'expect:skills:2;')

# v0.19.0+ (spec part 4 §7, §12.3 p.21–23): the tile type editor through its real controls — «Жгучая поверхность»
# (special, orange, icon, periodic damage 5 HP/s while on the hex; −5 HP/s and a 5-letter icon refused; undo/redo),
# its test run (a unit on it loses exactly 10 HP in 2 game seconds — 60 ticks, none while paused), «Целебная
# поверхность» (+5 HP/s); a new map in the map editor with both user types placed by reference; its test run: a unit walks
# onto the burning hex (−10 HP over 2 s of logical presence), onto the healing one (+), off it (healing stops) and back
# (never above the maximum).
$script:RegressionScenarioV19 = $script:RegressionScenarioV18 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.tile', 'expect:rows:8', 'ui:dev.create', 'expect:editor:tile', 'field:tile.name=Жгучая поверхность',
    'field:tile.category=special', 'field:tile.color=#E0805A', 'field:tile.icon=♨', 'field:tile.addtype=periodic', 'ui:tile.add',
    'expect:effects:1', 'expect:tile-preview:1:урон 5 HP за игровую секунду нахождения (за 2 с — 10 HP)',
    'field:tile.e0.perSecond=-5', 'expect:editor-message:не меньше', 'field:tile.icon=ОГОНЬ', 'expect:editor-message:не длиннее',
    'expect:tile-field:icon:♨', 'key:ctrl+z', 'expect:effects:0', 'key:ctrl+y', 'expect:effects:1',
    'ui:tile.save', 'expect:dirty:no', 'expect:errors:0',
    'ui:tile.test', 'expect:screen:play', 'pause:on', 'spawn:cell:2,5', 'expect:hp:1:100', 'advance:60', 'expect:hp:1:90',
    'wait:20', 'expect:hp:1:90', 'spawn:cell:4,5', 'advance:30', 'expect:hp:2:100', 'expect:hp:1:85',
    'ui:game.menu', 'ui:dialog.ok', 'expect:editor:tile', 'ui:tile.back', 'expect:rows:9',
    'ui:dev.create', 'expect:editor:tile', 'field:tile.name=Целебная поверхность', 'field:tile.category=special',
    'field:tile.color=#8FD19E', 'field:tile.icon=+', 'field:tile.addtype=periodic', 'ui:tile.add', 'field:tile.e0.kind=heal',
    'expect:tile-preview:1:лечение 5 HP за игровую секунду', 'ui:tile.save', 'expect:errors:0', 'ui:tile.back', 'expect:rows:10',
    'ui:dev.cat.map', 'ui:dev.create', 'expect:editor:map', 'click:2,4', 'field:map.tile.type=База игрока',
    'click:3,4', 'field:map.tile.type=Жгучая поверхность (свой)', 'expect:tile:3,4:Жгучая поверхность',
    'click:4,4', 'field:map.tile.type=Целебная поверхность (свой)', 'expect:tile:4,4:Целебная поверхность', 'click:5,4',
    'expect:tiles:4', 'ui:map.tab.map', 'field:map.name=Поверхности', 'ui:map.save', 'expect:dirty:no', 'expect:map-errors:0',
    'ui:map.test', 'expect:screen:play', 'pause:on', 'spawn:base', 'expect:cell:1:2,4', 'expect:hp:1:100',
    'drag:2,4>3,4', 'click:3,4@North:0.6', 'expect:draft:none', 'advance:75', 'expect:cell:1:3,4', 'expect:hp:1:90~0.2',
    'drag:3,4>4,4', 'click:4,4@North:0.6', 'advance:45', 'expect:cell:1:4,4', 'expect:hp:1:92.5~0.4',
    'drag:4,4>5,4', 'click:5,4@North:0.6', 'advance:60', 'expect:cell:1:5,4', 'expect:hp:1:95~0.6', 'advance:60', 'expect:hp:1:95~0.6',
    'drag:5,4>4,4', 'click:4,4@North:0.6', 'advance:300', 'expect:hp:1:100',
    'ui:game.menu', 'ui:dialog.ok', 'expect:editor:map', 'ui:map.back', 'expect:item:map:Поверхности:yes', 'ui:dev.back', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV19 = (@(
    'ui:menu.dev', 'ui:dev.cat.tile', 'expect:item:tile:Жгучая поверхность:yes', 'row:name=Жгучая поверхность', 'ui:dev.open',
    'expect:editor:tile', 'expect:effects:1', 'expect:tile-field:icon:♨', 'expect:tile-field:category:special',
    'expect:tile-preview:1:урон 5 HP за игровую секунду', 'ui:tile.back', 'ui:dev.cat.map', 'row:name=Поверхности', 'ui:dev.open',
    'expect:editor:map', 'expect:tile:3,4:Жгучая поверхность', 'expect:tile:4,4:Целебная поверхность', 'ui:map.back', 'ui:dev.back'
) -join ';') + ';' + ($script:PersistenceScenarioV18 -replace 'ui:dev.cat.tile;expect:rows:8', 'ui:dev.cat.tile;expect:rows:10')

# v0.20.0+ (spec part 4 §8.3, §12.3 p.24–28): export and import through the real dialogs — the map «Поверхности» with the
# unit «Тяжёлый» as a package (its tile types and skill are added automatically: 5 objects), the skill alone as JSON;
# importing the same package shows 5 unchanged objects and installs nothing without a choice; after the skill is changed
# locally the package conflicts on it: «копией с новым ID» for the skill and the unit (the copy of the unit refers to the
# copy of the skill), then «заменить» with a backup; a missing file is rejected with a report.
$script:RegressionScenarioV20 = $script:RegressionScenarioV19 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.map', 'row:name=Поверхности', 'ui:dev.export', 'expect:dialog:Экспорт: карта «Поверхности»',
    'field:export.pick.Тяжёлый=true', 'field:dialog.input=Поверхности', 'ui:export.hxp', 'expect:message:Экспорт: пакет, объектов 5',
    'ui:dev.cat.skill', 'row:name=Тройной удар', 'ui:dev.export', 'ui:export.json', 'expect:message:Экспорт: навык «Тройной удар»',
    'ui:dev.import', 'expect:dialog:Импорт', 'field:dialog.input=Поверхности.hxp', 'ui:import.preview', 'expect:dialog:Импорт: предпросмотр',
    'expect:import:новых 0, без изменений 5, конфликтов 0', 'ui:import.install', 'expect:message:Импорт не выполнен: ничего не выбрано',
    'row:name=Тройной удар', 'ui:dev.open', 'expect:editor:skill', 'field:skill.icon=⚔', 'ui:skill.save', 'expect:dirty:no', 'ui:skill.back',
    'ui:dev.import', 'field:dialog.input=Поверхности.hxp', 'ui:import.preview', 'expect:import:без изменений 4, конфликтов 1',
    'field:import.res.3=copy', 'field:import.res.4=copy', 'ui:import.install', 'expect:message:Импорт: установлено 2',
    'expect:rows:3', 'expect:item:skill:Тройной удар (импорт):yes', 'ui:dev.cat.unit', 'expect:item:unit:Тяжёлый (импорт):yes',
    'ui:dev.import', 'field:dialog.input=Поверхности.hxp', 'ui:import.preview', 'expect:import:конфликтов 1',
    'field:import.res.3=replace', 'ui:import.install', 'expect:message:Импорт: установлено 1',
    'ui:dev.import', 'field:dialog.input=нет-такого-файла.hxp', 'ui:import.preview', 'expect:import:файл отклонён', 'ui:import.cancel',
    'expect:message:Импорт отменён', 'ui:dev.back', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV20 = (@(
    'ui:menu.dev', 'ui:dev.cat.skill', 'expect:rows:3', 'expect:item:skill:Тройной удар (импорт):yes',
    'ui:dev.cat.unit', 'expect:item:unit:Тяжёлый (импорт):yes', 'ui:dev.back'
) -join ';') + ';' + $script:PersistenceScenarioV19

# v0.20.0+ third run (spec part 4 §12.3 p.26): a fresh user folder imports the package exported by the first run; the
# links resolve and the imported map plays with the imported unit (175 HP) on the imported burning surface.
$script:CleanImportScenarioV20 = (@(
    'ui:menu.dev', 'ui:dev.cat.map', 'expect:rows:1', 'ui:dev.import', 'field:dialog.input={PACKAGE}', 'ui:import.preview',
    'expect:import:5 объект(ов): новых 5', 'ui:import.install', 'expect:message:Импорт: установлено 5', 'expect:rows:2',
    'expect:item:map:Поверхности:yes', 'ui:dev.cat.tile', 'expect:item:tile:Жгучая поверхность:yes', 'ui:dev.back',
    'field:menu.map=Поверхности (своя)', 'field:menu.squad=Тяжёлый (свой) × 5', 'ui:menu.play', 'expect:screen:play', 'pause:on',
    'roster:1>2,4', 'click:2,4@North:0.6', 'expect:lifecycle:1:Active', 'expect:hp:1:175', 'drag:2,4>3,4', 'click:3,4@North:0.6',
    'advance:90', 'expect:cell:1:3,4', 'expect:hp:1:161.67~0.4', 'ui:game.menu', 'ui:dialog.ok', 'expect:screen:menu'
) -join ';')

# v0.21.0+ (spec part 5 §3, §7.3, §8; acceptance §11.2 p.8–13, §11.5 p.36): a unit «Бронированный» made in the unit editor
# only — physical +50 %, piercing −100 % (effective −50 %), a refused 600 %, a blunt normal attack; its test match: the
# default operative's slashing «Линейный удар» (20) takes 10 HP from it, and its game card shows no resistance; the skill
# editor offers the damage type of a skill effect (preview «режущий»), the tile rules name the type of the surface.
$script:RegressionScenarioV21 = $script:RegressionScenarioV20 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.unit', 'ui:dev.create', 'expect:editor:unit', 'field:unit.name=Бронированный',
    'field:unit.res.core.damage.physical=50', 'expect:resistance:core.damage.slashing:50', 'field:unit.res.core.damage.piercing=-100',
    'expect:resistance:core.damage.piercing:-50', 'field:unit.res.core.damage.blunt=600', 'expect:editor-message:от −500 до +500',
    'expect:resistance:core.damage.blunt:50', 'field:unit.attackDamageType=core.damage.blunt', 'expect:unit-field:attackDamageType:core.damage.blunt',
    'ui:unit.save', 'expect:dirty:no', 'expect:errors:0',
    'ui:unit.test', 'expect:screen:play', 'pause:on', 'spawn:cell:3,5', 'spawn:enemy:3,4@Бронированный', 'expect:units:2', 'expect:hp:2:100',
    'select:1', 'key:R', 'expect:mode:SkillAim', 'move:3,3', 'click:3,3', 'advance:3', 'expect:hp:2:90',
    'select:2', 'expect:info:2:сопротив:no', 'expect:info:2:%:no', 'ui:game.menu', 'ui:dialog.ok', 'expect:editor:unit', 'ui:unit.back',
    'ui:dev.cat.skill', 'row:name=Тройной удар', 'ui:dev.open', 'expect:editor:skill', 'expect:preview:0:физический',
    'field:skill.e0.damageType=core.damage.slashing', 'expect:preview:0:режущий', 'ui:skill.save', 'expect:dirty:no', 'ui:skill.back',
    'ui:dev.cat.tile', 'row:name=Жгучая поверхность', 'ui:dev.open', 'expect:tile-preview:1:тип урона — физический', 'ui:tile.back',
    'ui:dev.back', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV21 = (@(
    'ui:menu.dev', 'ui:dev.cat.unit', 'row:name=Бронированный', 'ui:dev.open', 'expect:editor:unit',
    'expect:resistance:core.damage.slashing:50', 'expect:resistance:core.damage.piercing:-50', 'expect:unit-field:attackDamageType:core.damage.blunt',
    'ui:unit.back', 'ui:dev.cat.skill', 'row:name=Тройной удар', 'ui:dev.open', 'expect:preview:0:режущий', 'ui:skill.back', 'ui:dev.back'
) -join ';') + ';' + $script:PersistenceScenarioV20

# v0.22.0+ (spec part 5 §4–§6.4, §9.3; acceptance §11.3 p.18–19, §11.5): the built-in example rule reads as its summary; a
# new rule «Шипы» from the «Ответный удар» template (50 %, 900 % refused); «Огненный страж» (fire R +300 %, the example
# rule, a magical shield of 15) and «Поджигатель» (fire normal attack 40, 0.5/s) made in the unit editor. In the match the
# operative's slashing strike takes the guard to 80; each fire hit then heals it by 10 (80 → 90 → 100, no health loss)
# and sends 30 to every enemy of the guard on the six adjacent hexes (attacker and operative 100 → 70 → 40), not to the
# unit two hexes away; the game card shows no resistance, rule or shield.
$script:RegressionScenarioV22 = $script:RegressionScenarioV21 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.defense_rule', 'expect:category:defense_rule', 'expect:rows:1',
    'row:name=Огненная регенерация и круговое отражение (пример)', 'ui:dev.open', 'expect:editor:defense_rule',
    'expect:rule-summary:вылечить себя на 25 % силы удара', 'expect:rule-summary:75 % силы удара отразить каждому врагу на шести соседних гексах', 'ui:rule.back',
    'ui:dev.create', 'expect:editor:defense_rule', 'field:rule.name=Шипы', 'field:rule.template=retaliate', 'ui:rule.template.apply',
    'expect:rule-actions:retaliate', 'expect:rule-field:trigger:on_health_loss', 'field:rule.a0.fraction=50',
    'expect:rule-summary:ответить на 50 % потерянного HP атакующему', 'field:rule.a0.fraction=900', 'expect:editor-message:не больше 500 %',
    'ui:rule.save', 'expect:dirty:no', 'expect:errors:0', 'ui:rule.back', 'expect:rows:2',
    'ui:dev.cat.unit', 'ui:dev.create', 'expect:editor:unit', 'field:unit.name=Огненный страж', 'field:unit.res.core.damage.fire_example=300',
    'expect:resistance:core.damage.fire_example:300', 'field:unit.rule.pick=core.rule.fire_convert_25_reflect_75', 'ui:unit.rule.add',
    'expect:rules:1', 'ui:unit.shield.add', 'field:unit.shield.0.capacity=15', 'field:unit.shield.0.capacity=-5', 'expect:editor-message:прочность щита',
    'field:unit.shield.0.damageTypeId=core.damage.magical', 'expect:shield:0:Щит:15', 'field:unit.side=enemy', 'ui:unit.save', 'expect:dirty:no',
    'expect:errors:0', 'ui:unit.back', 'ui:dev.create', 'field:unit.name=Поджигатель', 'field:unit.attackDamageType=core.damage.fire_example',
    'field:unit.attackPower=40', 'field:unit.attackRate=0.5', 'ui:unit.save', 'expect:errors:0',
    'ui:unit.test', 'expect:screen:play', 'pause:on', 'spawn:cell:3,5', 'spawn:enemy:3,4@Огненный страж', 'spawn:cell:2,5@Поджигатель',
    'spawn:cell:3,2', 'expect:units:4', 'select:1', 'key:R', 'move:3,3', 'click:3,3', 'advance:3', 'expect:hp:2:80',
    'select:3', 'drag:2,5>3,4', 'advance:58', 'expect:hp:2:80', 'advance:6', 'expect:hp:2:90', 'expect:hp:3:70', 'expect:hp:1:70',
    'expect:hp:4:100', 'select:2', 'expect:info:2:сопротив:no', 'expect:info:2:%:no', 'expect:info:2:Огненная:no', 'expect:info:2:щит:no',
    'advance:60', 'expect:hp:2:100', 'expect:hp:3:40', 'expect:hp:1:40', 'expect:hp:4:100',
    'ui:game.menu', 'ui:dialog.ok', 'expect:editor:unit', 'ui:unit.back', 'ui:dev.back', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV22 = (@(
    'ui:menu.dev', 'ui:dev.cat.defense_rule', 'expect:rows:2', 'row:name=Шипы', 'ui:dev.open', 'expect:editor:defense_rule',
    'expect:rule-actions:retaliate', 'expect:rule-summary:ответить на 50 % потерянного HP', 'ui:rule.back',
    'ui:dev.cat.unit', 'row:name=Огненный страж', 'ui:dev.open', 'expect:editor:unit', 'expect:rules:Огненная регенерация и круговое отражение (пример)',
    'expect:shield:0:Щит:15', 'expect:resistance:core.damage.fire_example:300', 'ui:unit.back', 'ui:dev.back'
) -join ';') + ';' + $script:PersistenceScenarioV21

# v0.23.0+ (spec part 5 §6.1–§6.5; acceptance §11.5 p.35–40): «Механики» — one catalog entry with three large actions; a new
# damage type «Плазма» on the basis of magical (explicit family and strategy, tags, sandbox hit on «Огненный страж»); a
# resistance set «Маг в латах» based on «Бронированный» (+60 % magical, 700 % refused); the example rule's «Проверить на
# ударе» (heal 10, «без правила» 60 → 60; penetration 50 → «R после пробития = 250 %, порог 300 % не достигнут»); a unit
# «Латник» with the set — inherited / own value / reset, sandbox plasma 40 → 16, and in its test match the slashing strike
# takes 10, the game card shows nothing of the set.
$script:RegressionScenarioV23 = $script:RegressionScenarioV22 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.mechanics', 'expect:category:mechanics', 'expect:rows:0', 'ui:mech.damage_type', 'expect:category:damage_type',
    'expect:rows:9', 'ui:dev.create', 'expect:dialog:Создать тип урона', 'ui:dialog.base.magical', 'expect:editor:damage_type',
    'field:dtype.name=Плазма', 'ui:dtype.more', 'field:dtype.tags=plasma, energy', 'expect:dtype:family:core.damage.magical',
    'expect:dtype:strategy:resistance_affected', 'expect:dtype:tags:plasma,energy', 'expect:dtype-preview:Подтип семейства «Магический»',
    'ui:dtype.sandbox', 'field:dtype.sandbox.target=Огненный страж', 'field:dtype.sandbox.amount=40', 'ui:dtype.sandbox.run',
    'expect:sandbox:сила удара D0 = 40 (плазма)', 'ui:dtype.save', 'expect:dirty:no', 'expect:errors:0', 'ui:dtype.back', 'expect:rows:10',
    'ui:dev.cat.resistance_profile', 'expect:rows:2', 'ui:dev.create', 'expect:editor:resistance_profile', 'field:profile.name=Маг в латах',
    'field:profile.parent=Бронированный', 'field:profile.res.core.damage.magical=60', 'expect:profile-total:core.damage.slashing:50',
    'expect:profile-total:core.damage.magical:60', 'field:profile.res.core.damage.blunt=700', 'expect:editor-message:от −500 до +500',
    'expect:profile-preview:Сильные стороны', 'ui:profile.save', 'expect:dirty:no', 'expect:errors:0', 'ui:profile.back', 'expect:rows:3',
    'ui:dev.cat.defense_rule', 'row:name=Огненная регенерация и круговое отражение (пример)', 'ui:dev.open', 'expect:editor:defense_rule',
    'ui:rule.sandbox', 'field:rule.sandbox.target=Огненный страж', 'field:rule.sandbox.type=Огонь (пример)', 'field:rule.sandbox.amount=40',
    'field:rule.sandbox.health=60', 'field:rule.sandbox.enemies=3', 'ui:rule.sandbox.run', 'expect:sandbox:лечение +10',
    'expect:sandbox:(цель): сработало', 'expect:sandbox:Без правила: HP цели 60 → 60', 'field:rule.sandbox.penetration=50', 'ui:rule.sandbox.run',
    'expect:sandbox:R после пробития = 250 %, порог 300 % не достигнут', 'expect:dirty:no', 'ui:rule.back',
    'ui:dev.cat.unit', 'ui:dev.create', 'expect:editor:unit', 'field:unit.name=Латник', 'field:unit.profile.pick=Маг в латах (свой)',
    'ui:unit.profile.add', 'expect:profiles:Маг в латах', 'expect:resistance:core.damage.slashing:50',
    'expect:resistance-source:core.damage.physical:inherited', 'field:unit.res.core.damage.physical=0', 'expect:resistance:core.damage.slashing:0',
    'expect:resistance-source:core.damage.physical:own', 'ui:unit.res.core.damage.physical.reset', 'expect:resistance:core.damage.slashing:50',
    'ui:unit.sandbox', 'field:unit.sandbox.type=Плазма', 'field:unit.sandbox.amount=40', 'ui:unit.sandbox.run',
    'expect:sandbox:R после пробития = 60 %', 'expect:sandbox:HP цели: 100 → 84', 'ui:unit.save', 'expect:dirty:no', 'expect:errors:0',
    'ui:unit.test', 'expect:screen:play', 'pause:on', 'spawn:cell:3,5', 'spawn:enemy:3,4@Латник', 'expect:units:2', 'select:1', 'key:R',
    'move:3,3', 'click:3,3', 'advance:3', 'expect:hp:2:90', 'select:2', 'expect:info:2:Маг в латах:no', 'expect:info:2:%:no',
    'ui:game.menu', 'ui:dialog.ok', 'expect:editor:unit', 'ui:unit.back', 'ui:dev.back', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV23 = (@(
    'ui:menu.dev', 'ui:dev.cat.damage_type', 'row:name=Плазма', 'ui:dev.open', 'expect:editor:damage_type', 'expect:dtype:tags:plasma,energy',
    'expect:dtype:family:core.damage.magical', 'ui:dtype.back', 'ui:dev.cat.resistance_profile', 'row:name=Маг в латах', 'ui:dev.open',
    'expect:profile-total:core.damage.magical:60', 'expect:profile-total:core.damage.slashing:50', 'ui:profile.back',
    'ui:dev.cat.unit', 'row:name=Латник', 'ui:dev.open', 'expect:profiles:Маг в латах', 'expect:resistance:core.damage.slashing:50',
    'ui:unit.back', 'ui:dev.back'
) -join ';') + ';' + $script:PersistenceScenarioV22

# v0.24.0+ (spec part 5 §7.1–§7.5; acceptance §11.5): the unit inspector — one open section of four; the tank of §7.3 in a few
# steps (physical +50 in «Сопротивления»); the short table (groups, subtypes under ▸, search narrows, «Показать все»); true
# damage → «Особые правила» → an immunity template edited on top of the unit and attached on return; a saved rule added and
# «Изменить копию» (the built-in rule keeps 300 %); a shield card; copying protection from another unit and Ctrl+Z; the
# marked defense debug exists only in a test run and is off by default; normal play has no such control.
$script:RegressionScenarioV24 = $script:RegressionScenarioV23 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.unit', 'ui:dev.create', 'expect:editor:unit', 'expect:section:main', 'field:unit.name=Танк v24', 'ui:unit.sec.res',
    'expect:section:res', 'expect:res-rows:global,physical,magical,true', 'field:unit.res.core.damage.physical=50',
    'expect:res-summary:Сильные стороны: физический +50 %', 'ui:unit.save', 'expect:dirty:no', 'expect:errors:0',
    'ui:unit.res.core.damage.physical.expand', 'expect:res-rows:global,physical,blunt,piercing,slashing,magical,true',
    'field:unit.res.search=Плазма', 'expect:res-has:Плазма:yes', 'expect:res-has:Физический:no', 'field:unit.res.search=',
    'expect:res-has:Плазма:no', 'ui:unit.res.all', 'expect:res-has:Плазма:yes', 'ui:unit.res.all', 'expect:res-has:Плазма:no',
    'ui:unit.res.core.damage.true.rules', 'ui:unit.rule.add', 'expect:editor:defense_rule', 'expect:rule-field:damageTypeId:core.damage.true',
    'expect:rule-actions:immunity', 'field:rule.name=Иммунитет к чистому', 'ui:rule.save', 'expect:errors:0', 'ui:rule.back', 'expect:editor:unit',
    'expect:section:res', 'expect:rules:Иммунитет к чистому', 'expect:dirty:yes',
    'field:unit.rule.pick=Огненная регенерация и круговое отражение (пример)', 'ui:unit.rule.add', 'expect:rules:2', 'ui:unit.rule.0.card',
    'ui:unit.rule.0.copy', 'expect:editor:defense_rule', 'expect:name:Огненная регенерация и круговое отражение (пример) (Танк v24)',
    'field:rule.minimumEffectiveResistancePercent=200', 'ui:rule.save', 'expect:errors:0', 'ui:rule.back', 'expect:editor:unit',
    'expect:res-summary:реакции: 2', 'ui:unit.shield.add', 'field:unit.shield.0.capacity=50', 'expect:shield:0:Щит:50', 'expect:res-summary:щиты: 1',
    'ui:unit.sec.attack', 'expect:section:attack', 'field:unit.attackPower=12', 'expect:unit-field:attackPower:12', 'ui:unit.sec.more',
    'field:unit.copy.from=Латник', 'ui:unit.copy.run', 'expect:profiles:Маг в латах', 'key:ctrl+z', 'expect:profiles:0',
    'expect:resistance:core.damage.slashing:50', 'ui:unit.save', 'expect:dirty:no', 'expect:errors:0',
    'ui:unit.test', 'expect:screen:play', 'pause:on', 'spawn:enemy:3,4@Танк v24', 'select:1', 'expect:info:1:ОТЛАДКА:no', 'expect:info:1:сопротив:no',
    'ui:game.debugdefense', 'expect:info:1:ОТЛАДКА ЗАЩИТЫ:yes', 'expect:info:1:физический +50 %:yes', 'expect:info:1:Щит 50/50:yes',
    'ui:game.debugdefense', 'expect:info:1:ОТЛАДКА:no', 'ui:game.menu', 'ui:dialog.ok', 'expect:editor:unit', 'ui:unit.back',
    'ui:dev.cat.defense_rule', 'row:name=Огненная регенерация и круговое отражение (пример)', 'ui:dev.open', 'expect:rule-summary:сопротивлении ≥ 300 %',
    'ui:rule.back', 'wait:10', 'ui:dev.back', 'wait:10', 'ui:menu.play', 'expect:screen:play', 'expect:control:game.debugdefense:no', 'expect:control:game.menu:yes',
    'ui:game.menu', 'ui:dialog.ok', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV24 = (@(
    'ui:menu.dev', 'ui:dev.cat.unit', 'row:name=Танк v24', 'ui:dev.open', 'expect:editor:unit', 'expect:section:main', 'expect:rules:2',
    'expect:shield:0:Щит:50', 'expect:resistance:core.damage.slashing:50', 'expect:unit-field:attackPower:12', 'ui:unit.back', 'ui:dev.back'
) -join ';') + ';' + ($script:PersistenceScenarioV23 -replace 'ui:dev.cat.defense_rule;expect:rows:2', 'ui:dev.cat.defense_rule;expect:rows:4')

# v0.24.1 (fix found by the v0.24.0 check at 1600×900): long category descriptions and object names never push the top-bar
# buttons out of the window — every «Механики» category and the editor of the built-in rule with its long name keep «← Меню»,
# «← Каталог», «Сохранить» inside the window.
$script:RegressionScenarioV241 = $script:RegressionScenarioV24 + ';' + (@(
    'ui:menu.dev', 'expect:on-screen:dev.back:yes', 'ui:dev.cat.defense_rule', 'expect:on-screen:dev.back:yes', 'expect:on-screen:dev.redo:yes',
    'ui:dev.cat.resistance_profile', 'expect:on-screen:dev.back:yes', 'ui:dev.cat.damage_type', 'expect:on-screen:dev.back:yes',
    'ui:dev.cat.mechanics', 'expect:on-screen:dev.back:yes', 'ui:dev.cat.defense_rule',
    'row:name=Огненная регенерация и круговое отражение (пример)', 'ui:dev.open', 'expect:on-screen:rule.back:yes', 'expect:on-screen:rule.save:yes',
    'ui:rule.back', 'wait:10', 'ui:dev.back', 'wait:10', 'expect:screen:menu'
) -join ';')

if (-not $Version) { $Version = Get-PropsVersion }
Assert-Version $Version
if (-not $ArchiveRoot) { $ArchiveRoot = $script:DefaultArchiveRoot }
$archive = Get-ArchiveInfo -Version $Version -ArchiveRoot $ArchiveRoot
$tag = "v$Version"
$errors = New-Object System.Collections.Generic.List[string]

Write-Step "Verifying $tag in $($archive.Directory)"
foreach ($f in @($archive.Zip, $archive.Sha256, $archive.BuildInfo)) {
    if (-not (Test-Path $f)) { Fail "Missing file: $f" }
}

# Checksum
$expected = ((Get-Content $archive.Sha256 -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
$actual = Get-Sha256 $archive.Zip
if ($expected -ne $actual) { $errors.Add("SHA-256 mismatch: file $actual, recorded $expected") }
else { Write-Host "SHA-256 OK  $actual" }

# Build info vs Git
$info = Read-BuildInfo $archive.BuildInfo
if ($info['version'] -ne $Version) { $errors.Add("BUILD_INFO version '$($info['version'])' != $Version") }
if ($info['sha256'] -and $info['sha256'] -ne $actual) { $errors.Add('BUILD_INFO sha256 differs from the ZIP') }
$tagCommit = Get-GitOutput @('rev-list', '-n', '1', "refs/tags/$tag")
if (-not $tagCommit) {
    & git -C $script:RepoRoot cat-file -e "$($info['commit'])^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { $errors.Add("Neither tag $tag nor commit $($info['commit']) exist in this clone (git fetch --tags)") }
    else { Write-Warning "Tag $tag is missing; the build commit $($info['commit']) exists. Recreate tags: pwsh tools/create_release_tags.ps1" }
}
elseif ($tagCommit -ne $info['commit']) { $errors.Add("Tag $tag -> $tagCommit, but archive was built from $($info['commit'])") }
else { Write-Host "Commit OK   $tagCommit ($tag)" }

# ZIP contents
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($archive.Zip)
try {
    $names = $zip.Entries | ForEach-Object { $_.FullName -replace '\\', '/' }
    $root = $archive.BaseName
    foreach ($required in @("$root/HexTactics.exe", "$root/HexTactics.pck", "$root/BUILD_INFO.txt", "$root/data_HexTactics_windows_x86_64/HexTactics.dll", "$root/data_HexTactics_windows_x86_64/HexTactics.Core.dll")) {
        if ($names -notcontains $required) { $errors.Add("ZIP is missing $required") }
    }
    Write-Host "ZIP entries $($names.Count)"
}
finally { $zip.Dispose() }

# Optional smoke run
if ($Run -and $errors.Count -eq 0) {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("hextactics_verify_" + [guid]::NewGuid().ToString('N'))
    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($archive.Zip, $tmp)
        # The console wrapper forwards the game log to stdout (the main exe is a GUI application).
        $exe = Join-Path $tmp "$($archive.BaseName)/HexTactics.console.exe"
        if (-not (Test-Path $exe)) { $exe = Join-Path $tmp "$($archive.BaseName)/HexTactics.exe" }
        $log = Join-Path $tmp 'smoke.log'
        # From v0.5.0 the build understands expect:/waitidle: steps: run the end-to-end regression scenario
        # (spawn, drag orders, pause freezes time, chain built during pause, movement, rotate gesture,
        # unreachable target). Older builds get a plain smoke run.
        $regression = [version]$Version -ge [version]'0.5.0'
        $scenario = $null
        $persistence = $null
        if ($regression) {
            $scenario = if ([version]$Version -ge [version]'0.24.1') { $script:RegressionScenarioV241 }
                        elseif ([version]$Version -ge [version]'0.24.0') { $script:RegressionScenarioV24 }
                        elseif ([version]$Version -ge [version]'0.23.0') { $script:RegressionScenarioV23 }
                        elseif ([version]$Version -ge [version]'0.22.0') { $script:RegressionScenarioV22 }
                        elseif ([version]$Version -ge [version]'0.21.0') { $script:RegressionScenarioV21 }
                        elseif ([version]$Version -ge [version]'0.20.0') { $script:RegressionScenarioV20 }
                        elseif ([version]$Version -ge [version]'0.19.0') { $script:RegressionScenarioV19 }
                        elseif ([version]$Version -ge [version]'0.18.0') { $script:RegressionScenarioV18 }
                        elseif ([version]$Version -ge [version]'0.17.0') { $script:RegressionScenarioV17 }
                        elseif ([version]$Version -ge [version]'0.16.0') { $script:RegressionScenarioV16 }
                        elseif ([version]$Version -ge [version]'0.15.0') { $script:RegressionScenarioV15 }
                        elseif ([version]$Version -ge [version]'0.14.1') { $script:RegressionScenarioV141 }
                        elseif ([version]$Version -ge [version]'0.14.0') { $script:RegressionScenarioV14 }
                        elseif ([version]$Version -ge [version]'0.13.0') { $script:RegressionScenarioV13 }
                        elseif ([version]$Version -ge [version]'0.12.0') { $script:RegressionScenarioV12 }
                        elseif ([version]$Version -ge [version]'0.11.0') { $script:RegressionScenarioV11 }
                        elseif ([version]$Version -ge [version]'0.10.0') { $script:RegressionScenarioV10 }
                        elseif ([version]$Version -ge [version]'0.9.0') { $script:RegressionScenarioV09 }
                        elseif ([version]$Version -ge [version]'0.8.0') { $script:RegressionScenarioV08 }
                        elseif ([version]$Version -ge [version]'0.7.0') { $script:RegressionScenarioV07 }
                        else { $script:RegressionScenarioV05 }
            if ([version]$Version -ge [version]'0.24.0') { $persistence = $script:PersistenceScenarioV24 }
            elseif ([version]$Version -ge [version]'0.23.0') { $persistence = $script:PersistenceScenarioV23 }
            elseif ([version]$Version -ge [version]'0.22.0') { $persistence = $script:PersistenceScenarioV22 }
            elseif ([version]$Version -ge [version]'0.21.0') { $persistence = $script:PersistenceScenarioV21 }
            elseif ([version]$Version -ge [version]'0.20.0') { $persistence = $script:PersistenceScenarioV20 }
            elseif ([version]$Version -ge [version]'0.19.0') { $persistence = $script:PersistenceScenarioV19 }
            elseif ([version]$Version -ge [version]'0.18.0') { $persistence = $script:PersistenceScenarioV18 }
            elseif ([version]$Version -ge [version]'0.17.0') { $persistence = $script:PersistenceScenarioV17 }
            elseif ([version]$Version -ge [version]'0.16.0') { $persistence = $script:PersistenceScenarioV16 }
            elseif ([version]$Version -ge [version]'0.15.0') { $persistence = $script:PersistenceScenarioV15 }
        }

        # From v0.15.0 user content goes to a fresh temporary folder, so the run never touches real user data.
        $userRoot = Join-Path $tmp 'user'
        New-Item -ItemType Directory -Force -Path $userRoot | Out-Null
        $userArg = if ($Launcher) { 'Z:' + ($userRoot -replace '/', '\') } else { $userRoot }

        # Wine decodes the command line with the Unix locale: without UTF-8 the Cyrillic steps of the scenario
        # (names, dialog titles) arrive garbled. Native Windows passes UTF-16 and needs nothing.
        if ($Launcher) { $env:LC_ALL = 'C.UTF-8'; $env:LANG = 'C.UTF-8' }

        $runs = @(@{ Name = 'smoke'; Script = $scenario; Root = $userArg })
        if ($persistence) { $runs += @{ Name = 'restart'; Script = $persistence; Root = $userArg } }
        if ($regression -and [version]$Version -ge [version]'0.20.0') {
            # A clean installation imports the package the first run exported (spec part 4 §12.3 p.26).
            $cleanRoot = Join-Path $tmp 'clean-user'
            New-Item -ItemType Directory -Force -Path $cleanRoot | Out-Null
            $package = Join-Path (Join-Path $userRoot 'exports') 'Поверхности.hxp'
            # The package path goes inside the scenario text: Windows accepts forward slashes, so it stays free of escapes.
            $packageArg = if ($Launcher) { 'Z:' + $package } else { $package }
            $cleanArg = if ($Launcher) { 'Z:' + ($cleanRoot -replace '/', '\') } else { $cleanRoot }
            $runs += @{ Name = 'clean-import'; Script = $script:CleanImportScenarioV20.Replace('{PACKAGE}', $packageArg); Root = $cleanArg }
        }
        foreach ($pass in $runs) {
            $log = Join-Path $tmp "$($pass.Name).log"
            $smokeArgs = @('--windowed', '--resolution', '1600x900', '--', '--hx-frames=10', '--hx-quit')
            if ([version]$Version -ge [version]'0.15.0') { $smokeArgs += "--hx-user-root=$($pass.Root)" }
            if ($pass.Script) { $smokeArgs += "--hx-script=$($pass.Script)" }
            Write-Step ("{0} ({1}): {2}" -f ($(if ($regression) { 'Regression scenario' } else { 'Smoke run' }), $pass.Name, $exe))
            if ($Launcher) { & $Launcher $exe @smokeArgs *> $log }
            else { & $exe @smokeArgs *> $log }
            $exitCode = $LASTEXITCODE
            $text = Get-Content -Raw $log
            if ($text -notmatch [regex]::Escape("HexTactics v$Version")) { $errors.Add("Run '$($pass.Name)' did not report HexTactics v$Version. Log: $log") }
            elseif ($text -notmatch '\[automation\] done') { $errors.Add("Run '$($pass.Name)' did not finish. Log: $log") }
            elseif ($regression -and ($exitCode -ne 0 -or $text -notmatch '\[automation\] checks passed: \d+' -or $text -match 'CHECK FAILED')) {
                $errors.Add("Regression scenario '$($pass.Name)' failed (exit $exitCode). Log: $log")
            }
            else {
                if ($regression) { ($text -split "`n" | Where-Object { $_ -match '\[automation\] checks passed' }) | ForEach-Object { Write-Host "$($pass.Name): $($_.Trim())" } }
                Write-Host "Run '$($pass.Name)' OK"
            }
        }
    }
    finally {
        if ($errors.Count -eq 0) { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    exit 1
}
Write-Host "Release $tag verified." -ForegroundColor Green
