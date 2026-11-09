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

# v0.25.0+ (spec part 5 §7.5, §11.4 p.34, §11.5 p.36–43; plan v0.22): the UX trial of five different units made in the unit
# editor alone (counts in docs/UX_TRIALS.md) — a tank, a piercing skirmisher weak to magic, a mage with the «Маг в латах» set and
# an area attack, a fire guard with the «Сложная реакция» template made on top of the unit, a ghost with true-damage immunity
# and a shield; they fight in a test match (the skirmisher and the mage fall, the guard and the tank lose nothing); the package
# «Защита» (two units, their set and rules) is exported for the clean installation run.
$script:RegressionScenarioV25 = $script:RegressionScenarioV241 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.unit', 'ui:dev.create', 'field:unit.name=UX-1 Танк', 'field:unit.maxHealth=160', 'ui:unit.sec.res',
    'field:unit.res.core.damage.physical=50', 'ui:unit.save', 'expect:errors:0', 'ui:unit.back',
    'ui:dev.create', 'field:unit.name=UX-2 Застрельщик', 'field:unit.attackDamageType=core.damage.piercing', 'field:unit.attackRange=3',
    'field:unit.res.core.damage.magical=-50', 'field:unit.side=enemy', 'ui:unit.save', 'expect:errors:0', 'ui:unit.back',
    'ui:dev.create', 'field:unit.name=UX-3 Маг', 'field:unit.attackDamageType=core.damage.magical', 'field:unit.attackAreaRadius=1',
    'field:unit.profile.pick=Маг в латах (свой)', 'ui:unit.profile.add', 'field:unit.side=enemy', 'ui:unit.save', 'expect:errors:0',
    'expect:resistance:core.damage.magical:60', 'ui:unit.back',
    'ui:dev.create', 'field:unit.name=UX-4 Огненный страж', 'field:unit.res.core.damage.fire_example=300',
    'field:unit.rule.pick=шаблон: Сложная реакция', 'ui:unit.rule.add', 'expect:editor:defense_rule', 'field:rule.damageTypeId=core.damage.fire_example',
    'expect:rule-summary:При попадании урона «Огонь (пример)» и сопротивлении ≥ 300 %', 'ui:rule.save', 'expect:errors:0', 'ui:rule.back',
    'expect:editor:unit', 'expect:rules:1', 'ui:unit.save', 'expect:errors:0', 'ui:unit.back',
    'ui:dev.create', 'field:unit.name=UX-5 Призрак', 'ui:unit.res.core.damage.true.rules', 'ui:unit.rule.add', 'expect:editor:defense_rule',
    'ui:rule.save', 'ui:rule.back', 'expect:rules:1', 'ui:unit.shield.add', 'field:unit.shield.0.capacity=30', 'field:unit.side=enemy', 'ui:unit.save',
    'expect:errors:0', 'ui:unit.test', 'expect:screen:play', 'pause:on', 'spawn:cell:3,5@UX-1 Танк', 'spawn:cell:2,5@UX-4 Огненный страж',
    'spawn:enemy:3,2@UX-2 Застрельщик', 'spawn:enemy:2,2@UX-3 Маг', 'spawn:enemy:4,2@UX-5 Призрак', 'expect:units:5', 'select:1', 'drag:3,5>3,2',
    'select:2', 'drag:2,5>2,2', 'advance:300', 'expect:alive:3:no', 'expect:alive:4:no', 'expect:hp:5:100', 'expect:hp:1:160', 'expect:hp:2:100',
    'ui:game.menu', 'ui:dialog.ok', 'expect:editor:unit', 'ui:unit.back', 'row:name=Латник', 'ui:dev.export', 'field:export.pick.Танк v24=true',
    'field:dialog.input=Защита', 'ui:export.hxp', 'expect:message:Экспорт: пакет, объектов 5', 'ui:dev.back', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV25 = (@(
    'ui:menu.dev', 'ui:dev.cat.unit', 'row:name=UX-4 Огненный страж', 'ui:dev.open', 'expect:rules:1', 'expect:resistance:core.damage.fire_example:300',
    'ui:unit.back', 'row:name=UX-5 Призрак', 'ui:dev.open', 'expect:rules:1', 'expect:shield:0:Щит:30', 'ui:unit.back', 'ui:dev.back'
) -join ';') + ';' + ($script:PersistenceScenarioV24 -replace 'ui:dev.cat.defense_rule;expect:rows:4', 'ui:dev.cat.defense_rule;expect:rows:6')

# v0.25.0+ third run: the clean installation also imports «Защита» — sets and rules arrive with the units, the sandbox hits the
# imported unit with the same numbers (spec part 5 §11.5 p.41).
$script:CleanImportScenarioV25 = $script:CleanImportScenarioV20 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.unit', 'ui:dev.import', 'field:dialog.input={PACKAGE2}', 'ui:import.preview', 'expect:import:5 объект(ов): новых 5',
    'ui:import.install', 'expect:message:Импорт: установлено 5', 'row:name=Латник', 'ui:dev.open', 'expect:profiles:Маг в латах',
    'expect:resistance:core.damage.slashing:50', 'ui:unit.sandbox', 'field:unit.sandbox.type=Режущий', 'field:unit.sandbox.amount=40',
    'ui:unit.sandbox.run', 'expect:sandbox:HP цели: 100 → 80', 'ui:unit.back', 'row:name=Танк v24', 'ui:dev.open', 'expect:rules:2',
    'expect:shield:0:Щит:50', 'ui:unit.back', 'ui:dev.cat.defense_rule', 'expect:rows:3', 'ui:dev.back', 'expect:screen:menu'
) -join ';')

# v0.26.0+ (spec part 6 §2; acceptance §12.1 P6-01…P6-05, P6-07): «Огонь» is created right from the damage type selector of the
# skill «Тройной удар» (on top of the skill editor, which keeps its unsaved state) and is at once offered in the rule, tile and
# unit editors (the unit with «Маг в латах» inherits magical 60 % for it); renamed to «Пламя» the links hold; deleting it lists
# its users and offers replacement or archive — archived it still works for the skill and is brought back.
$script:RegressionScenarioV26 = $script:RegressionScenarioV25 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.skill', 'row:name=Тройной удар', 'ui:dev.open', 'expect:editor:skill', 'field:skill.e0.damageType=__create__',
    'expect:dialog:Новый тип урона', 'ui:dialog.base.magical', 'expect:editor:damage_type', 'field:dtype.name=Огонь', 'field:dtype.element=true',
    'expect:dtype:family:core.damage.magical', 'ui:dtype.save', 'expect:errors:0', 'ui:dtype.back', 'expect:editor:skill', 'expect:preview:0:огонь',
    'expect:dirty:yes', 'ui:skill.save', 'expect:dirty:no', 'ui:skill.back',
    'ui:dev.cat.unit', 'row:name=Латник', 'ui:dev.open', 'expect:editor:unit', 'field:unit.res.search=Огонь', 'expect:res-has:Огонь:yes',
    'expect:resistance:Огонь:60', 'ui:unit.back',
    'ui:dev.cat.defense_rule', 'ui:dev.create', 'field:rule.damageTypeId=Огонь · элемент (свой)', 'expect:rule-summary:урона «Огонь»', 'ui:rule.back',
    'ui:dialog.discard', 'ui:dev.cat.tile', 'row:name=Жгучая поверхность', 'ui:dev.open', 'field:tile.e0.damageType=Огонь · элемент (свой)',
    'expect:tile-preview:1:тип урона — огонь', 'ui:tile.back', 'ui:dialog.discard',
    'ui:dev.cat.damage_type', 'row:name=Огонь', 'ui:dev.rename', 'field:dialog.input=Пламя', 'ui:dialog.ok', 'ui:dev.cat.skill', 'row:name=Тройной удар',
    'ui:dev.open', 'expect:preview:0:пламя', 'ui:skill.back', 'ui:dev.cat.damage_type', 'row:name=Пламя', 'ui:dev.delete', 'expect:dialog:Удалить «Пламя»',
    'ui:dialog.archive', 'expect:message:в архиве', 'ui:dev.cat.skill', 'row:name=Тройной удар', 'ui:dev.open', 'expect:preview:0:пламя', 'ui:skill.back',
    'ui:dev.cat.damage_type', 'row:name=Пламя', 'ui:dev.unarchive', 'expect:message:снова предлагается', 'ui:dev.back', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV26 = (@(
    'ui:menu.dev', 'ui:dev.cat.damage_type', 'row:name=Пламя', 'ui:dev.open', 'expect:editor:damage_type', 'expect:dtype:family:core.damage.magical',
    'expect:dtype-preview:Используют: 1', 'ui:dtype.back', 'ui:dev.back'
) -join ';') + ';' + ($script:PersistenceScenarioV25 -replace 'expect:preview:0:режущий', 'expect:preview:0:пламя')

# v0.27.0+ (spec part 6 §3, §9.5; acceptance §12.2): «Правила боя» — a new set «Правила арены» (K 50, magical K 200, a wrong K
# refused); the enemy «Броненосец» with defense A 100 for every type (A −5 refused), the sandbox shows the A stage with armor
# penetration and the K of a chosen set; in the unit's test run (compatible rules, K 100) «Поджигатель» hits it for 20 instead of
# 40; the map «Арена брони» plays with «Правила арены» (magical K 200: 26.667); the game card never shows A.
$script:RegressionScenarioV27 = $script:RegressionScenarioV26 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.mechanics', 'ui:mech.combat_ruleset', 'expect:category:combat_ruleset', 'expect:rows:2',
    'ui:dev.create', 'expect:editor:combat_ruleset', 'field:rules.name=Правила арены', 'field:rules.k=0', 'expect:ruleset-k:100',
    'field:rules.k=50', 'expect:ruleset-k:50', 'expect:ruleset-preview:A 100 → ×0.333', 'field:rules.k.pick=core.damage.magical', 'ui:rules.k.add',
    'field:rules.k.core.damage.magical=200', 'expect:ruleset-k:core.damage.magical:200', 'ui:rules.save', 'expect:dirty:no', 'expect:errors:0',
    'ui:rules.back', 'expect:rows:3',
    'ui:dev.cat.unit', 'ui:dev.create', 'expect:editor:unit', 'field:unit.name=Броненосец', 'field:unit.side=enemy', 'field:unit.armor.global=100',
    'field:unit.armor.core.damage.physical=-5', 'expect:editor-message:от 0 до 1 000 000', 'expect:armor:Колющий:100', 'expect:armor:global:100',
    'expect:resistance:Колющий:0', 'ui:unit.sandbox', 'field:unit.sandbox.type=Режущий', 'field:unit.sandbox.amount=60', 'field:unit.sandbox.armorpen=50',
    'ui:unit.sandbox.run', 'expect:sandbox:после пробития A = 50, K = 100', 'expect:sandbox:HP цели: 100 → 60',
    'field:unit.sandbox.ruleset=Правила арены', 'ui:unit.sandbox.run', 'expect:sandbox:K = 50', 'expect:sandbox:HP цели: 100 → 70',
    'ui:unit.save', 'expect:dirty:no', 'expect:errors:0',
    'ui:unit.test', 'expect:screen:play', 'pause:on', 'spawn:cell:3,5@Поджигатель', 'spawn:enemy:3,4@Броненосец', 'expect:units:2', 'select:1',
    'drag:3,5>3,4', 'advance:58', 'expect:hp:2:100', 'advance:6', 'expect:hp:2:80', 'select:2', 'expect:info:2:брон:no', 'expect:info:2:%:no',
    'ui:game.menu', 'ui:dialog.ok', 'expect:editor:unit', 'ui:unit.back',
    'ui:dev.cat.map', 'row:name=Прототип 12×10', 'ui:dev.duplicate', 'row:name=Прототип 12×10 (копия)', 'ui:dev.rename', 'field:dialog.input=Арена брони',
    'ui:dialog.ok', 'row:name=Арена брони', 'ui:dev.open', 'expect:editor:map', 'expect:map-ruleset:core.ruleset.legacy',
    'field:map.ruleset=Правила арены (свой)', 'expect:map-ruleset:Правила арены', 'ui:map.save', 'expect:dirty:no',
    'ui:map.test', 'expect:screen:play', 'pause:on', 'spawn:cell:3,5@Поджигатель', 'spawn:enemy:3,4@Броненосец', 'select:1', 'drag:3,5>3,4',
    'advance:58', 'expect:hp:2:100', 'advance:6', 'expect:hp:2:73.333~0.01', 'ui:game.menu', 'ui:dialog.ok', 'expect:editor:map', 'ui:map.back',
    'ui:dev.back', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV27 = (@(
    'ui:menu.dev', 'ui:dev.cat.combat_ruleset', 'expect:rows:3', 'row:name=Правила арены', 'ui:dev.open', 'expect:editor:combat_ruleset',
    'expect:ruleset-k:50', 'expect:ruleset-k:core.damage.magical:200', 'ui:rules.back', 'ui:dev.cat.unit', 'row:name=Броненосец', 'ui:dev.open',
    'expect:editor:unit', 'expect:armor:global:100', 'expect:armor:Колющий:100', 'ui:unit.back', 'ui:dev.cat.map', 'row:name=Арена брони', 'ui:dev.open',
    'expect:editor:map', 'expect:map-ruleset:Правила арены', 'ui:map.back', 'ui:dev.back'
) -join ';') + ';' + $script:PersistenceScenarioV26

# v0.28.0+ (spec part 6 §4, §8 stages 2 and 8; acceptance §12.3): «Особенности» — personal passives from templates: the unit
# «Гидромант» (magical attack 20) gets «Водная мощь» +50 % outgoing magical damage (+5000 % refused) and «Матёрый» +10 % attack
# power; «откуда взялось» shows 20 → 22; the sandbox with this unit as the attacker shows 20 → 30 before D0; in its test run it
# hits «Броненосец» (A 100) for 22 × 1.5 × 0.5 = 16.5.
$script:RegressionScenarioV28 = $script:RegressionScenarioV27 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.unit', 'ui:dev.create', 'expect:editor:unit', 'field:unit.name=Гидромант', 'field:unit.attackDamageType=core.damage.magical',
    'field:unit.attackPower=20', 'field:unit.attackRate=0.5', 'ui:unit.sec.features', 'expect:section:features', 'expect:passives:0',
    'field:unit.passive.template=outgoing_damage_percent', 'ui:unit.passive.add', 'expect:passives:1', 'field:unit.passive.p1.name=Водная мощь',
    'field:unit.passive.p1.type=core.damage.magical', 'field:unit.passive.p1.value=50', 'field:unit.passive.p1.value=5000', 'expect:editor-message:до 1000',
    'expect:passive:Водная мощь:+50 % исходящего урона: магический', 'field:unit.passive.template=stat_add_percent', 'ui:unit.passive.add',
    'field:unit.passive.p2.name=Матёрый', 'field:unit.passive.p2.value=10', 'expect:passives:2', 'expect:stat:attackPower:22',
    'expect:breakdown:Сила атаки: 20 → 22 (+10 % · пассив «Матёрый»)', 'ui:unit.sandbox', 'field:unit.sandbox.attacker=__self__',
    'field:unit.sandbox.type=core.damage.magical', 'field:unit.sandbox.amount=20', 'ui:unit.sandbox.run', 'expect:sandbox:Пассивы атакующего: сила 20 → 30',
    'ui:unit.save', 'expect:dirty:no', 'expect:errors:0',
    'ui:unit.test', 'expect:screen:play', 'pause:on', 'spawn:cell:3,5@Гидромант', 'spawn:enemy:3,4@Броненосец', 'select:1', 'drag:3,5>3,4',
    'advance:58', 'expect:hp:2:100', 'advance:6', 'expect:hp:2:83.5', 'select:1', 'expect:info:1:Водная:no', 'expect:info:1:%:no',
    'ui:game.menu', 'ui:dialog.ok', 'expect:editor:unit', 'ui:unit.back', 'ui:dev.back', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV28 = (@(
    'ui:menu.dev', 'ui:dev.cat.unit', 'row:name=Гидромант', 'ui:dev.open', 'expect:editor:unit', 'expect:passives:2',
    'expect:passive:Водная мощь:+50 % исходящего урона: магический', 'expect:stat:attackPower:22', 'ui:unit.back', 'ui:dev.back'
) -join ';') + ';' + $script:PersistenceScenarioV27

# v0.29.0+ (spec part 6 §5; acceptance §12.3 P6-23…P6-29, P6-32): «Происхождение и теги» — the tree Зверь → Волк → Лунный волк
# (+5 % Agility), the traits «Вожак» (+3 % attack per other living wolf of the pack) and «Матёрый» (+5 % Agility); units of the pack
# «north» and a stranger of «south»; Agility 100 → 110 with its sources; in a test run the leader's attack is 100 alone and 112
# with four wolves of its pack — the stranger does not count.
$script:RegressionScenarioV29 = $script:RegressionScenarioV28 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.origin', 'expect:category:origin', 'ui:mech.species', 'expect:category:species', 'expect:rows:0',
    'ui:dev.create', 'expect:editor:species', 'field:species.name=Зверь', 'ui:species.save', 'expect:errors:0', 'ui:species.back',
    'ui:dev.create', 'field:species.name=Волк', 'field:species.parent=Зверь', 'ui:species.save', 'ui:species.back',
    'ui:dev.create', 'field:species.name=Лунный волк', 'field:species.parent=Волк', 'field:species.effect.template=stat_add_percent', 'ui:species.effect.add',
    'field:species.effect.p1.stat=core.stat.agility', 'field:species.effect.p1.value=5', 'expect:species-lineage:Лунный волк → Волк → Зверь',
    'expect:species-preview:ловкость +5 %', 'ui:species.save', 'expect:errors:0', 'ui:species.back', 'expect:rows:3',
    'ui:dev.cat.trait', 'ui:dev.create', 'expect:editor:trait', 'field:trait.name=Вожак', 'field:trait.effect.template=count_matching_units',
    'ui:trait.effect.add', 'field:trait.effect.p1.countspecies=Волк и потомки', 'expect:trait-preview:за каждого живого союзника вида «Волк» и потомков, той же стаи',
    'ui:trait.save', 'expect:errors:0', 'ui:trait.back',
    'ui:dev.create', 'field:trait.name=Матёрый', 'field:trait.effect.template=stat_add_percent', 'ui:trait.effect.add', 'field:trait.effect.p1.stat=core.stat.agility',
    'field:trait.effect.p1.value=5', 'ui:trait.save', 'expect:errors:0', 'ui:trait.back',
    'ui:dev.cat.unit', 'ui:dev.create', 'expect:editor:unit', 'field:unit.name=Вожак стаи', 'field:unit.attackPower=100', 'field:unit.species=Волк → Зверь',
    'field:unit.trait.pick=Вожак', 'ui:unit.trait.add', 'field:unit.pack=north', 'expect:breakdown:за каждого подходящего союзника (стая «north»)', 'ui:unit.save',
    'expect:errors:0', 'ui:unit.back',
    'ui:dev.create', 'field:unit.name=Волк стаи', 'field:unit.species=Лунный волк → Волк → Зверь', 'field:unit.trait.pick=Матёрый', 'ui:unit.trait.add',
    'field:unit.pack=north', 'expect:stat:core.stat.agility:110', 'expect:breakdown:Ловкость: 100 → 110', 'ui:unit.save', 'expect:errors:0', 'ui:unit.back',
    'ui:dev.create', 'field:unit.name=Чужой волк', 'field:unit.species=Волк → Зверь', 'field:unit.pack=south', 'ui:unit.save', 'expect:errors:0',
    'ui:unit.test', 'expect:screen:play', 'pause:on', 'spawn:cell:3,5@Вожак стаи', 'select:1', 'expect:info:1:атака 100:yes',
    'spawn:cell:2,5@Волк стаи', 'spawn:cell:4,5@Волк стаи', 'spawn:cell:3,6@Волк стаи', 'spawn:cell:2,6@Волк стаи', 'spawn:cell:4,6@Чужой волк',
    'expect:units:6', 'select:1', 'expect:info:1:атака 112:yes', 'ui:game.menu', 'ui:dialog.ok', 'expect:editor:unit', 'ui:unit.back', 'ui:dev.back', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV29 = (@(
    'ui:menu.dev', 'ui:dev.cat.species', 'expect:rows:3', 'row:name=Лунный волк', 'ui:dev.open', 'expect:species-lineage:Лунный волк → Волк → Зверь',
    'ui:species.back', 'ui:dev.cat.trait', 'expect:rows:2', 'ui:dev.cat.unit', 'row:name=Волк стаи', 'ui:dev.open', 'expect:stat:core.stat.agility:110',
    'ui:unit.back', 'ui:dev.back'
) -join ';') + ';' + $script:PersistenceScenarioV28

# v0.30.0+ (spec part 6 §6, §7, §9.5; acceptance §12.4): «Правила боя» → «Засада» with surprise (flanks 40 % mirrored) and crits; the
# unit «Убийца» with a critical chance of 200 % (supercrit always); the sandbox shows a forced rear-flank surprise supercrit 20 → 67.5;
# in its test run on the compatible rules of the prototype it hits for a plain 10; on «Арена засады» with the standard rules it
# hits «Каменный страж» (immune to surprise) for 10 × 2.25 = 22.5; the game card shows no chances.
$script:RegressionScenarioV30 = $script:RegressionScenarioV29 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.combat_ruleset', 'ui:dev.create', 'expect:editor:combat_ruleset', 'field:rules.name=Засада', 'field:rules.surprise=true',
    'field:rules.surprise.1=40', 'expect:ruleset-preview:передний правый фланг 40 %', 'expect:ruleset-preview:передний левый фланг 40 %', 'field:rules.surprise.3=150',
    'expect:editor-message:от 0 до 100', 'field:rules.crit=true', 'field:rules.crit.super=3', 'expect:ruleset-preview:сверхкрит ×3', 'ui:rules.save', 'expect:errors:0',
    'ui:rules.back', 'ui:dev.cat.unit', 'ui:dev.create', 'expect:editor:unit', 'field:unit.name=Каменный страж', 'field:unit.side=enemy', 'ui:unit.sec.features',
    'field:unit.passive.template=surprise_immunity', 'ui:unit.passive.add', 'expect:passives:1', 'ui:unit.save', 'expect:errors:0', 'ui:unit.back',
    'ui:dev.create', 'field:unit.name=Убийца', 'field:unit.critChance=200', 'field:unit.attackRate=0.5', 'ui:unit.sandbox', 'field:unit.sandbox.attacker=__self__',
    'field:unit.sandbox.type=core.damage.blunt', 'field:unit.sandbox.amount=20', 'field:unit.sandbox.side=2', 'field:unit.sandbox.outcome=surprise_super',
    'ui:unit.sandbox.run', 'expect:sandbox:Внезапность: задний правый фланг → 75 %', 'expect:sandbox:сверхкрит ×2.25', 'expect:sandbox:сила удара D0 = 67.5',
    'ui:unit.save', 'expect:errors:0',
    'ui:unit.test', 'expect:screen:play', 'pause:on', 'spawn:cell:3,5@Убийца', 'spawn:enemy:3,4@Каменный страж', 'select:1', 'drag:3,5>3,4',
    'advance:58', 'expect:hp:2:100', 'advance:6', 'expect:hp:2:90', 'ui:game.menu', 'ui:dialog.ok', 'expect:editor:unit', 'ui:unit.back',
    'ui:dev.cat.map', 'row:name=Прототип 12×10', 'ui:dev.duplicate', 'row:name=Прототип 12×10 (копия)', 'ui:dev.rename', 'field:dialog.input=Арена засады',
    'ui:dialog.ok', 'row:name=Арена засады', 'ui:dev.open', 'expect:editor:map', 'field:map.ruleset=Стандартные правила боя', 'expect:map-ruleset:core.ruleset.standard',
    'ui:map.save', 'expect:dirty:no', 'ui:map.test', 'expect:screen:play', 'pause:on', 'spawn:cell:3,5@Убийца', 'spawn:enemy:3,4@Каменный страж', 'select:1',
    'drag:3,5>3,4', 'advance:58', 'expect:hp:2:100', 'advance:6', 'expect:hp:2:77.5', 'select:1', 'expect:info:1:крит:no', 'expect:info:1:%:no',
    'ui:game.menu', 'ui:dialog.ok', 'expect:editor:map', 'ui:map.back', 'ui:dev.back', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV30 = (@(
    'ui:menu.dev', 'ui:dev.cat.combat_ruleset', 'row:name=Засада', 'ui:dev.open', 'expect:ruleset-preview:передний левый фланг 40 %',
    'expect:ruleset-preview:сверхкрит ×3', 'ui:rules.back', 'ui:dev.cat.unit', 'row:name=Убийца', 'ui:dev.open', 'expect:unit-field:critChance:200', 'ui:unit.back',
    'ui:dev.cat.map', 'row:name=Арена засады', 'ui:dev.open', 'expect:map-ruleset:core.ruleset.standard', 'ui:map.back', 'ui:dev.back'
) -join ';') + ';' + $script:PersistenceScenarioV29.Replace('ui:dev.cat.combat_ruleset;expect:rows:3;', 'ui:dev.cat.combat_ruleset;expect:rows:4;') # «Засада» is the fourth ruleset

# v0.31.0+ (spec part 6 §6.3–6.4, §9.2, §9.5, §11, §12.5; P6-47…P6-55): the user element «Вода» (magical), the tag «Гидромант»
# (+50 % outgoing «Вода»), the unit «Водный маг» with species, three tags, a passive, R and A — set without JSON; in its test run it
# hits for 20 × 1.5 = 30; the tag is assigned to «Убийца» from its own editor; «Засада»: the rear right side picked on the hexagon
# (60 %, mirrored), tested from that side, 100 events and 1000 crit rolls; on «Арена засады» «Убийца» shows «Сверхкрит!» but no
# chance; «Водный маг» goes to «Вода.hxp» for the clean installation.
$script:RegressionScenarioV31 = $script:RegressionScenarioV30 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.damage_type', 'ui:dev.create', 'expect:dialog:Создать тип урона', 'ui:dialog.base.magical', 'expect:editor:damage_type',
    'field:dtype.name=Вода', 'ui:dtype.save', 'expect:errors:0', 'ui:dtype.back',
    'ui:dev.cat.trait', 'ui:dev.create', 'expect:editor:trait', 'field:trait.name=Гидромант', 'field:trait.effect.template=outgoing_damage_percent',
    'ui:trait.effect.add', 'field:trait.effect.p1.type=Вода (свой)', 'field:trait.effect.p1.value=50', 'expect:trait-preview:+50 % исходящего урона: вода',
    'ui:trait.save', 'expect:errors:0', 'expect:trait-units:0', 'ui:trait.back',
    'ui:dev.cat.unit', 'ui:dev.create', 'expect:editor:unit', 'field:unit.name=Водный маг', 'field:unit.attackDamageType=Вода (свой)',
    'field:unit.attackPower=20', 'field:unit.attackRate=0.5', 'ui:unit.sec.res', 'field:unit.res.core.damage.magical=20', 'field:unit.armor.global=10',
    'ui:unit.sec.features', 'field:unit.species=Волк → Зверь', 'field:unit.trait.pick=Гидромант', 'ui:unit.trait.add', 'field:unit.trait.pick=Матёрый',
    'ui:unit.trait.add', 'field:unit.trait.pick=Вожак', 'ui:unit.trait.add', 'field:unit.passive.template=incoming_damage_percent', 'ui:unit.passive.add',
    'expect:passives:1', 'expect:features-summary:вид «Волк» · тегов: 3 · активных эффектов: 4', 'expect:stat:core.stat.agility:105',
    'ui:unit.save', 'expect:errors:0',
    'ui:unit.test', 'expect:screen:play', 'pause:on', 'spawn:cell:3,5@Водный маг', 'spawn:enemy:3,4@Каменный страж', 'select:1', 'drag:3,5>3,4',
    'advance:58', 'expect:hp:2:100', 'advance:6', 'expect:hp:2:70', 'ui:game.menu', 'ui:dialog.ok', 'expect:editor:unit', 'ui:unit.back',
    'ui:dev.cat.trait', 'row:name=Гидромант', 'ui:dev.open', 'expect:trait-units:1', 'field:trait.assign.pick=Убийца', 'ui:trait.assign.add',
    'ui:trait.assign', 'expect:trait-units:2', 'expect:editor-message:Тег назначен: Убийца', 'ui:trait.back',
    'ui:dev.cat.combat_ruleset', 'row:name=Засада', 'ui:dev.open', 'ui:rules.hex.2', 'expect:ruleset-side:2', 'field:rules.side.value=60',
    'expect:ruleset-preview:задний правый фланг 60 %', 'expect:ruleset-preview:задний левый фланг 60 %', 'ui:rules.side.test',
    'expect:sandbox:задний правый фланг → 60 %', 'ui:rules.sandbox.batch', 'expect:sandbox:100 событий (seed 20261106): внезапных 60',
    'field:rules.crit.test=125', 'ui:rules.crit.rolls', 'expect:ruleset-rolls:(по формуле 0 / 750 / 250)', 'ui:rules.save', 'expect:errors:0', 'ui:rules.back',
    'ui:dev.cat.map', 'row:name=Арена засады', 'ui:dev.open', 'ui:map.test', 'expect:screen:play', 'pause:on', 'spawn:cell:3,5@Убийца',
    'spawn:enemy:3,4@Каменный страж', 'select:1', 'drag:3,5>3,4', 'advance:64', 'expect:hp:2:77.5', 'expect:hitword:Сверхкрит:yes',
    'expect:hitword:Внезапн:no', 'expect:info:1:%:no', 'ui:game.menu', 'ui:dialog.ok', 'expect:editor:map', 'ui:map.back',
    'ui:dev.cat.unit', 'row:name=Водный маг', 'ui:dev.export', 'field:dialog.input=Вода', 'ui:export.hxp', 'expect:message:Экспорт: пакет, объектов',
    'ui:dev.back', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV31 = (@(
    'ui:menu.dev', 'ui:dev.cat.unit', 'row:name=Водный маг', 'ui:dev.open', 'expect:features-summary:вид «Волк» · тегов: 3 · активных эффектов: 4',
    'expect:resistance:core.damage.magical:20', 'expect:armor:global:10', 'ui:unit.back', 'ui:dev.cat.trait', 'row:name=Гидромант', 'ui:dev.open',
    'expect:trait-units:2', 'ui:trait.back', 'ui:dev.cat.combat_ruleset', 'row:name=Засада', 'ui:dev.open', 'expect:ruleset-preview:задний правый фланг 60 %',
    'ui:rules.back', 'ui:dev.back'
) -join ';') + ';' + $script:PersistenceScenarioV30.Replace('ui:dev.cat.trait;expect:rows:2;', 'ui:dev.cat.trait;expect:rows:3;').Replace(
    'ui:dev.cat.unit;row:name=Убийца;ui:dev.open;expect:unit-field:critChance:200;ui:unit.back;',
    'ui:dev.cat.unit;field:dev.search=Убийца;row:name=Убийца;ui:dev.open;expect:unit-field:critChance:200;ui:unit.back;field:dev.search=;')
# «Гидромант» is the third tag; with 21 units «Убийца» is below the fold of a 1600 × 900 window, and the row step of v0.31.0 clicks
# before its own scroll takes effect (D-169), so the search narrows the list first.

# v0.31.0+ clean installation: «Вода.hxp» brings the element, the tags, the species and the unit with the same ids; «Водный маг» hits
# for the same 30 in a test run (P6-55).
$script:CleanImportScenarioV31 = $script:CleanImportScenarioV25 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.unit', 'ui:dev.import', 'field:dialog.input={PACKAGE3}', 'ui:import.preview', 'expect:import:новых',
    'ui:import.install', 'expect:message:Импорт: установлено', 'row:name=Водный маг', 'ui:dev.open', 'expect:features-summary:вид «Волк» · тегов: 3',
    'ui:unit.test', 'expect:screen:play', 'pause:on', 'spawn:cell:3,5@Водный маг', 'spawn:enemy:3,4', 'select:1', 'drag:3,5>3,4', 'advance:64',
    'expect:hp:2:70', 'ui:game.menu', 'ui:dialog.ok', 'expect:editor:unit', 'ui:unit.back', 'ui:dev.back', 'expect:screen:menu'
) -join ';')

# v0.32.0+ (spec part 7 §8; acceptance §12.5 M7-43…M7-50): «Механики» → «Базовые атаки» → «Копьё» drawn on the hex board (front and
# second front hex, turning the board does not move the stored area); the unit «Копейщик» (3 attacks/s) with it — «Базовая атака:
# 3 удар/с · 2 гекс. …» without a test; in its test run on the prototype (no automatic attacks, D-170) nothing happens by itself; on
# «Арена копий» with «Автоатака юнитов» it hits the enemy two hexes ahead 3 times a second, not the one behind, never turns or moves;
# turned to the south it hits the one behind instead.
$script:RegressionScenarioV32 = $script:RegressionScenarioV31 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.basic_attack', 'expect:category:basic_attack', 'expect:rows:1', 'ui:dev.create', 'expect:editor:basic_attack',
    'field:battack.name=Копьё', 'expect:battack-cells:0,-1', 'cell:0,-2', 'expect:battack-cells:0,-1 0,-2', 'ui:battack.turn.right',
    'expect:battack-facing:NorthEast', 'cell:0,0', 'expect:battack-cells:0,-1 0,-2', 'expect:battack-preview:2 гекс. (перед, второй перед)',
    'ui:battack.save', 'expect:errors:0', 'ui:battack.back', 'expect:rows:2',
    'ui:dev.cat.unit', 'ui:dev.create', 'expect:editor:unit', 'field:unit.name=Копейщик', 'field:unit.attackRate=3', 'field:unit.basicAttack=Копьё (свой)',
    'expect:battle-preview:Базовая атака: 3 удар/с · 2 гекс. (перед, второй перед) · 1 враг · 10 урона до защиты', 'ui:unit.save', 'expect:errors:0',
    'ui:unit.test', 'expect:screen:play', 'pause:on', 'spawn:cell:3,5@Копейщик', 'select:1', 'face:North', 'spawn:enemy:3,4', 'advance:61',
    'expect:hp:2:100', 'ui:game.menu', 'ui:dialog.ok', 'expect:editor:unit', 'ui:unit.back',
    'ui:dev.cat.map', 'row:name=Прототип 12×10', 'ui:dev.duplicate', 'row:name=Прототип 12×10 (копия)', 'ui:dev.rename', 'field:dialog.input=Арена копий',
    'ui:dialog.ok', 'row:name=Арена копий', 'ui:dev.open', 'expect:editor:map', 'expect:map-autoattacks:no', 'field:map.autoattacks=true',
    'expect:map-autoattacks:yes', 'ui:map.save', 'expect:dirty:no', 'ui:map.test', 'expect:screen:play', 'pause:on', 'spawn:cell:3,5@Копейщик', 'select:1',
    'face:North', 'spawn:enemy:3,3', 'spawn:enemy:3,6', 'advance:31', 'expect:hp:2:70', 'expect:hp:3:100', 'expect:facing:1:North', 'expect:cell:1:3,5',
    'select:1', 'face:South', 'advance:31', 'expect:hp:2:70', 'expect:hp:3:70', 'expect:info:1:удар/с:no',
    'ui:game.menu', 'ui:dialog.ok', 'expect:editor:map', 'ui:map.back', 'ui:dev.back', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV32 = (@(
    'ui:menu.dev', 'ui:dev.cat.basic_attack', 'row:name=Копьё', 'ui:dev.open', 'expect:battack-cells:0,-1 0,-2', 'ui:battack.back',
    'ui:dev.cat.unit', 'field:dev.search=Копейщик', 'row:name=Копейщик', 'ui:dev.open', 'expect:battle-preview:2 гекс. (перед, второй перед)', 'ui:unit.back',
    'field:dev.search=', 'ui:dev.cat.map', 'row:name=Арена копий', 'ui:dev.open', 'expect:map-autoattacks:yes', 'ui:map.back', 'ui:dev.back'
) -join ';') + ';' + $script:PersistenceScenarioV31

# v0.33.0+ (spec part 7 §7; acceptance §12.5 M7-36…M7-42, M7-57): the enemy unit «Страж» with «Блок» 1 — «Блок: 1 …» in «В бою»; in its
# test run two operatives walk past it: the first is held on its own hex next to the guard, the second (no free slot) walks on to
# its goal; never two units in one hex.
$script:RegressionScenarioV33 = $script:RegressionScenarioV32 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.unit', 'ui:dev.create', 'expect:editor:unit', 'field:unit.name=Страж', 'field:unit.side=enemy', 'field:unit.block=1',
    'expect:battle-preview:Блок: 1 — пока стоит, удерживает до 1', 'ui:unit.save', 'expect:errors:0',
    'ui:unit.test', 'expect:screen:play', 'pause:on', 'spawn:cell:3,5', 'spawn:enemy:3,4@Страж', 'spawn:cell:2,5', 'select:1', 'drag:3,5>3,1',
    'click:3,1@North:0.6', 'select:3', 'drag:2,5>2,1', 'click:2,1@North:0.6', 'advance:200', 'expect:cell:1:3,5', 'expect:blocked:1:yes',
    'expect:cell:2:3,4', 'expect:cell:3:2,1', 'expect:blocked:3:no', 'select:1', 'expect:info:1:блок:no',
    'ui:game.menu', 'ui:dialog.ok', 'expect:editor:unit', 'ui:unit.back', 'ui:dev.back', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV33 = (@(
    'ui:menu.dev', 'ui:dev.cat.unit', 'row:name=Страж', 'ui:dev.open', 'expect:battle-preview:Блок: 1 — пока стоит', 'ui:unit.back', 'ui:dev.back'
) -join ';') + ';' + $script:PersistenceScenarioV32

# v0.34.0+ (spec part 7 §2–§3, §9; acceptance §12.1 M7-01…M7-04, M7-51, M7-59): in «Сценарии» a new battle «Засада» — one enemy
# placed by a click on the map and turned to the north, a click on an obstacle refused, a second enemy removed, undo and redo, a
# squad of one; its test run starts on «Подготовка»: the card is deployed and gets its route and facing while nothing moves, then
# «Начать бой», the operative attacks the enemy and the battle ends with «Победа» on a pause. From the start menu the built-in
# «Учебный бой» starts with three enemies and three cards; the menu goes back to free play for the next scenarios. The card of the
# operative held by «Страж» now says «удержан блоком врага» (the state, never the capacity — no «Блок» line). The free-play choice
# stays after «Разработчик» and back.
$script:RegressionScenarioV34 = ($script:RegressionScenarioV33 -replace 'expect:info:1:блок:no', 'expect:info:1:удержан блоком врага:yes;expect:info:1:Блок:no') + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.scenario', 'ui:dev.create', 'expect:editor:scenario', 'field:scenario.name=Засада', 'expect:scenario-errors:1',
    'cell:6,2', 'expect:scenario-enemies:1', 'expect:scenario-selected:6,2', 'ui:scenario.turn.right', 'ui:scenario.turn.right', 'ui:scenario.turn.right',
    'expect:scenario-enemy:6,2:core.unit.operative:North', 'cell:5,2', 'expect:scenario-enemies:1', 'cell:8,3', 'expect:scenario-enemies:2',
    'ui:scenario.enemy.remove', 'expect:scenario-enemies:1', 'ui:scenario.undo', 'expect:scenario-enemies:2', 'ui:scenario.redo', 'expect:scenario-enemies:1',
    'ui:scenario.squad.remove.2', 'ui:scenario.squad.remove.1', 'expect:scenario-squad:1', 'expect:scenario-preview:Победа: все враги (1) побеждены.',
    'expect:scenario-errors:0', 'ui:scenario.save', 'expect:errors:0',
    'ui:scenario.test', 'expect:screen:play', 'expect:phase:Preparing', 'expect:paused:true', 'expect:enemies-left:1', 'expect:units:1',
    'roster:1>1,5', 'expect:lifecycle:2:PlacedPendingFacing', 'click:3,5', 'click:3,5@NorthEast:0.6', 'expect:lifecycle:2:Active', 'wait:10',
    'expect:cell:2:1,5', 'expect:phase:Preparing', 'pause:off', 'waitidle:2:3000', 'expect:cell:2:3,5', 'expect:phase:Running',
    'drag:3,5>6,2', 'waitdead:1:8000', 'expect:phase:Victory', 'expect:paused:true', 'expect:enemies-left:0',
    'ui:game.menu', 'ui:dialog.ok', 'expect:editor:scenario', 'ui:scenario.back', 'ui:dev.back', 'expect:screen:menu',
    'field:menu.scenario=Учебный бой', 'ui:menu.play', 'expect:screen:play', 'expect:phase:Preparing', 'expect:enemies-left:3', 'expect:units:3',
    'expect:roster:3:AvailableInRoster', 'roster:1>1,5', 'click:1,5@North:0.6', 'expect:lifecycle:4:Active', 'pause:off', 'wait:10', 'expect:phase:Running',
    'ui:game.menu', 'ui:dialog.ok', 'expect:screen:menu', 'field:menu.scenario=свободная игра на карте',
    'ui:menu.dev', 'ui:dev.back', 'ui:menu.play', 'expect:screen:play', 'expect:units:0', 'ui:game.menu', 'ui:dialog.ok', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV34 = (@(
    'ui:menu.dev', 'ui:dev.cat.scenario', 'row:name=Засада', 'ui:dev.open', 'expect:editor:scenario', 'expect:scenario-enemies:1',
    'expect:scenario-enemy:6,2:core.unit.operative:North', 'expect:scenario-squad:1', 'expect:scenario-errors:0', 'ui:scenario.back', 'ui:dev.back',
    'field:menu.scenario=Засада (свой)', 'ui:menu.play', 'expect:screen:play', 'expect:phase:Preparing', 'expect:enemies-left:1',
    'ui:game.menu', 'ui:dialog.ok', 'expect:screen:menu', 'field:menu.scenario=свободная игра на карте'
) -join ';') + ';' + $script:PersistenceScenarioV33

# v0.35.0+ (spec part 7 §4.1–4.4; acceptance §12.2 M7-06…M7-13, M7-15, M7-16): the copy «Две линии» of the prototype gets an enemy base
# 1 at (6,0), an enemy base 2 at (11,5) and a player base 2 at (5,9); «Маршруты»: a line from base 1 to the player base drawn by
# moving the cursor over neighbouring hexes, a second one from base 2 to base 2 with a back-and-forth (two repeated visits); a draft
# into an obstacle stops with the hex named, Backspace and Esc change only the draft, a far cursor jump adds only neighbours, RMB
# cancels; the route is renamed «Петля» and saved. In the map test the lines are hidden until «Показать линии маршрутов»; an enemy on
# each route walks it exactly to its player base.
$script:RegressionScenarioV35 = $script:RegressionScenarioV34 + ';' + (@(
    'ui:menu.dev', 'ui:dev.cat.map', 'row:name=Прототип 12×10', 'ui:dev.duplicate', 'row:name=Прототип 12×10 (копия)', 'ui:dev.rename',
    'field:dialog.input=Две линии', 'ui:dialog.ok', 'row:name=Две линии', 'ui:dev.open', 'expect:editor:map',
    'click:6,0', 'field:map.tile.type=База врага', 'expect:tile:6,0:core.tile.enemy_base', 'click:11,5', 'field:map.tile.type=База врага',
    'field:map.tile.base=2', 'click:5,9', 'field:map.tile.type=База игрока', 'field:map.tile.base=2',
    'ui:map.routes', 'expect:route-mode:on', 'routedraw:6,0>6,1>5,1>4,1>4,2>4,3>4,4>3,4>2,4>1,4', 'expect:routes:1',
    'expect:route:route1:6,0 6,1 5,1 4,1 4,2 4,3 4,4 3,4 2,4 1,4', 'expect:route-ends:route1:1>1', 'expect:route-check:route1:ok', 'expect:route-selected:route1',
    'routedraw:11,5>10,6>10,7>10,8>9,8>8,8>7,8>6,8>7,8>6,8>6,9>5,9', 'expect:routes:2', 'expect:route-ends:route2:2>2',
    'expect:route:route2:11,5 10,6 10,7 10,8 9,8 8,8 7,8 6,8 7,8 6,8 6,9 5,9', 'expect:route-check:route2:ok',
    'routedraw:6,0>6,1>6,2>5,2', 'expect:route-error:гекс (5,2) непроходим', 'expect:route-draft:3', 'key:Backspace', 'expect:route-draft:2',
    'key:Escape', 'expect:route-draft:0', 'expect:routes:2', 'routedraw:6,0>6,1', 'hover:6,6', 'expect:route-draft:7', 'rclick:6,6', 'expect:route-draft:0',
    'ui:map.route.pick.route2', 'field:map.route.name=Петля', 'expect:route-name:route2:Петля', 'ui:map.save', 'expect:dirty:no', 'expect:map-errors:0',
    'ui:map.test', 'expect:screen:play', 'expect:routes-visible:no', 'ui:game.routes.show', 'expect:routes-visible:yes', 'route:route2', 'expect:units:1',
    'route:route1', 'expect:units:2', 'advance:420', 'expect:route-index:1:done', 'expect:cell:1:5,9', 'expect:route-index:2:done', 'expect:cell:2:1,4',
    'ui:game.menu', 'ui:dialog.ok', 'expect:editor:map', 'ui:map.back', 'ui:dev.back', 'expect:screen:menu'
) -join ';')

$script:PersistenceScenarioV35 = (@(
    'ui:menu.dev', 'ui:dev.cat.map', 'row:name=Две линии', 'ui:dev.open', 'expect:editor:map', 'ui:map.routes', 'expect:routes:2',
    'expect:route:route2:11,5 10,6 10,7 10,8 9,8 8,8 7,8 6,8 7,8 6,8 6,9 5,9', 'expect:route-name:route2:Петля', 'expect:route-check:route1:ok',
    'expect:route-check:route2:ok', 'ui:map.back', 'ui:dev.back'
) -join ';') + ';' + $script:PersistenceScenarioV34

# Splits a regression scenario longer than $Limit characters at the ends of earlier versions' scenarios (v0.30.0+: the
# scenario passed the 32 767-character command line of Windows). Shorter scenarios stay one launch, as before.
function Split-Scenario([string]$Text, [int]$Limit = 32000) {
    $bounds = @(Get-Variable -Scope Script -Name 'RegressionScenarioV*' -ValueOnly |
        Where-Object { $_ -is [string] -and $Text.StartsWith($_ + ';') } | ForEach-Object { $_.Length } | Sort-Object -Unique)
    $chunks = @()
    $start = 0
    while ($Text.Length - $start -gt $Limit) {
        $cut = $bounds | Where-Object { $_ -gt $start -and ($_ - $start) -le $Limit } | Select-Object -Last 1
        if (-not $cut) { throw "A part of the regression scenario is longer than $Limit characters and has no version boundary to split at." }
        $chunks += $Text.Substring($start, $cut - $start)
        $start = $cut + 1
    }
    $chunks += $Text.Substring($start)
    return $chunks
}

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
            $scenario = if ([version]$Version -ge [version]'0.35.0') { $script:RegressionScenarioV35 }
                        elseif ([version]$Version -ge [version]'0.34.0') { $script:RegressionScenarioV34 }
                        elseif ([version]$Version -ge [version]'0.33.0') { $script:RegressionScenarioV33 }
                        elseif ([version]$Version -ge [version]'0.32.0') { $script:RegressionScenarioV32 }
                        elseif ([version]$Version -ge [version]'0.31.0') { $script:RegressionScenarioV31 }
                        elseif ([version]$Version -ge [version]'0.30.0') { $script:RegressionScenarioV30 }
                        elseif ([version]$Version -ge [version]'0.29.0') { $script:RegressionScenarioV29 }
                        elseif ([version]$Version -ge [version]'0.28.0') { $script:RegressionScenarioV28 }
                        elseif ([version]$Version -ge [version]'0.27.0') { $script:RegressionScenarioV27 }
                        elseif ([version]$Version -ge [version]'0.26.0') { $script:RegressionScenarioV26 }
                        elseif ([version]$Version -ge [version]'0.25.0') { $script:RegressionScenarioV25 }
                        elseif ([version]$Version -ge [version]'0.24.1') { $script:RegressionScenarioV241 }
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
            if ([version]$Version -ge [version]'0.35.0') { $persistence = $script:PersistenceScenarioV35 }
            elseif ([version]$Version -ge [version]'0.34.0') { $persistence = $script:PersistenceScenarioV34 }
            elseif ([version]$Version -ge [version]'0.33.0') { $persistence = $script:PersistenceScenarioV33 }
            elseif ([version]$Version -ge [version]'0.32.0') { $persistence = $script:PersistenceScenarioV32 }
            elseif ([version]$Version -ge [version]'0.31.0') { $persistence = $script:PersistenceScenarioV31 }
            elseif ([version]$Version -ge [version]'0.30.0') { $persistence = $script:PersistenceScenarioV30 }
            elseif ([version]$Version -ge [version]'0.29.0') { $persistence = $script:PersistenceScenarioV29 }
            elseif ([version]$Version -ge [version]'0.28.0') { $persistence = $script:PersistenceScenarioV28 }
            elseif ([version]$Version -ge [version]'0.27.0') { $persistence = $script:PersistenceScenarioV27 }
            elseif ([version]$Version -ge [version]'0.26.0') { $persistence = $script:PersistenceScenarioV26 }
            elseif ([version]$Version -ge [version]'0.25.0') { $persistence = $script:PersistenceScenarioV25 }
            elseif ([version]$Version -ge [version]'0.24.0') { $persistence = $script:PersistenceScenarioV24 }
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

        # Windows limits a whole command line to 32 767 characters. A longer regression scenario runs as consecutive launches
        # on the same user folder, cut where an earlier version's scenario ends (each of them ends on the start menu).
        $runs = @()
        $parts = @(Split-Scenario $scenario)
        for ($i = 0; $i -lt $parts.Count; $i++) {
            $runs += @{ Name = $(if ($i -eq 0) { 'smoke' } else { "smoke-$($i + 1)" }); Script = $parts[$i]; Root = $userArg }
        }
        if ($persistence) { $runs += @{ Name = 'restart'; Script = $persistence; Root = $userArg } }
        if ($regression -and [version]$Version -ge [version]'0.20.0') {
            # A clean installation imports the package the first run exported (spec part 4 §12.3 p.26).
            $cleanRoot = Join-Path $tmp 'clean-user'
            New-Item -ItemType Directory -Force -Path $cleanRoot | Out-Null
            $package = Join-Path (Join-Path $userRoot 'exports') 'Поверхности.hxp'
            # The package path goes inside the scenario text: Windows accepts forward slashes, so it stays free of escapes.
            $packageArg = if ($Launcher) { 'Z:' + $package } else { $package }
            $cleanArg = if ($Launcher) { 'Z:' + ($cleanRoot -replace '/', '\') } else { $cleanRoot }
            $clean = $script:CleanImportScenarioV20
            if ([version]$Version -ge [version]'0.25.0') {
                $package2 = Join-Path (Join-Path $userRoot 'exports') 'Защита.hxp'
                $clean = $script:CleanImportScenarioV25.Replace('{PACKAGE2}', $(if ($Launcher) { 'Z:' + $package2 } else { $package2 }))
                if ([version]$Version -ge [version]'0.31.0') {
                    $package3 = Join-Path (Join-Path $userRoot 'exports') 'Вода.hxp'
                    $clean = $script:CleanImportScenarioV31.Replace('{PACKAGE2}', $(if ($Launcher) { 'Z:' + $package2 } else { $package2 })).Replace('{PACKAGE3}', $(if ($Launcher) { 'Z:' + $package3 } else { $package3 }))
                }
            }
            $runs += @{ Name = 'clean-import'; Script = $clean.Replace('{PACKAGE}', $packageArg); Root = $cleanArg }
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
