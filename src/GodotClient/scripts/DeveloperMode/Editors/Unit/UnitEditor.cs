using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using HexTactics.Client.DeveloperMode.SharedInspector;
using HexTactics.Client.Presentation;
using HexTactics.Client.UI;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Editor.Units;

namespace HexTactics.Client.DeveloperMode.Editors.Unit;

/// <summary>
/// The unit editor (spec part 4 §5): name/description, characteristics from the field schema (HP, movement, attack rate,
/// Attack Power, range, side), the skill set as references to saved skills (add from a filtered catalog, reorder, remove),
/// appearance (tint, size); a preview of the unit with the area of the chosen skill, the combat numbers, validation and a
/// test run with the unit in the roster.
/// </summary>
public partial class UnitEditor : DefinitionEditorShell
{
    private UnitEditorModel _model = null!;
    private BoardView _board = null!;
    private HexFillLayer _area = null!;
    private Node3D _body = null!;
    private MeshInstance3D _bodyMesh = null!;
    private StandardMaterial3D _bodyMaterial = null!;
    private HexGrid _grid = null!;
    private string _filter = string.Empty;
    private string? _previewSkill;
    private string? _pick;
    private string _ruleFilter = string.Empty;
    private string? _profilePick;
    private SandboxPanel? _sandbox;
    private string? _rulePick;

    private static readonly HexCoord Center = HexCoord.FromOffset(3, 3);

    public override string Kind => ContentTypeRegistry.Unit;

    protected override string IdPrefix => "unit.";

    protected override string KindTitle => "Юнит";

    protected override string? TestButtonText => "▶ Тест юнита";

    public UnitEditorModel UnitModel => _model;

    public void Configure(ContentLibrary library, ContentItem item, ContentItem? draft = null)
    {
        _model = new UnitEditorModel(library, item);
        if (draft is not null)
        {
            _model.LoadDraft(draft);
        }

        Attach(library, _model);
    }

    protected override void BuildCanvas()
    {
        TilePalette.Current = new TilePalette();
        _grid = new HexGrid(7, 7);
        foreach (var c in _grid.Coords)
        {
            if (c.DistanceTo(Center) > 2)
            {
                _grid.SetTile(c, TileType.Void, null, 0);
            }
        }

        _board = new BoardView { Name = "PreviewBoard" };
        AddChild(_board);
        _board.Build(_grid, Layout);
        _area = new HexFillLayer { Name = "SkillArea", RenderPriority = 2, ExtraLift = 0.002f };
        AddChild(_area);
        _area.Build(_grid, Layout);

        _body = new Node3D { Name = "UnitPreview", Position = Layout.HexToPlane(Center).ToWorld(0f) };
        AddChild(_body);
        _bodyMaterial = new StandardMaterial3D { Roughness = 0.75f };
        _bodyMesh = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = UnitView.BodyRadius, BottomRadius = UnitView.BodyRadius, Height = UnitView.BodyHeight, RadialSegments = 32 },
            MaterialOverride = _bodyMaterial,
        };
        _body.AddChild(_bodyMesh);

        // Facing pointer towards North (the skill area preview is drawn for this facing).
        var north = Layout.EdgeMidpoint(Center, HexDirection.North) - Layout.HexToPlane(Center);
        var tip = north * 0.95;
        var side = new PlaneVector(-north.Z, north.X) * 0.25;
        var arrow = new ImmediateMesh();
        arrow.SurfaceBegin(Mesh.PrimitiveType.Triangles, new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = VisualConfig.UnitFacingPointer, CullMode = BaseMaterial3D.CullModeEnum.Disabled });
        arrow.SurfaceAddVertex(tip.ToWorld(0.01f));
        arrow.SurfaceAddVertex(((north * 0.6) + side).ToWorld(0.01f));
        arrow.SurfaceAddVertex(((north * 0.6) - side).ToWorld(0.01f));
        arrow.SurfaceEnd();
        _body.AddChild(new MeshInstance3D { Mesh = arrow });
    }

    protected override void UpdateCanvas()
    {
        var unit = _model.Unit;
        _bodyMaterial.AlbedoColor = unit.Side == "enemy" ? VisualConfig.UnitEnemy : Color.FromHtml(unit.Color);
        var s = (float)unit.Scale;
        _bodyMesh.Scale = new Vector3(s, s, s);
        _bodyMesh.Position = new Vector3(0, UnitView.BodyHeight * 0.5f * s, 0);

        _area.ClearAll();
        var skillId = _previewSkill is not null && unit.Skills.Contains(_previewSkill) ? _previewSkill : unit.Skills.FirstOrDefault();
        if (skillId is not null && Library.Find(ContentTypeRegistry.Skill, skillId) is { } skill)
        {
            foreach (var cell in RuntimeFactory.BuildSkill(skill, Library.Registry.Effects).Area(Center, HexDirection.North, _grid))
            {
                _area.SetFill(cell, VisualConfig.SkillAreaAim);
            }
        }
    }

    protected override Aabb CanvasBounds()
    {
        var (min, max) = _grid.PlaneBounds(Layout);
        return new Aabb(new Vector3((float)min.X, -VisualConfig.TileThickness, (float)min.Z), new Vector3((float)(max.X - min.X), 1.2f, (float)(max.Z - min.Z)));
    }

    // ------------------------------------------------------------------ inspector (spec part 5 §7.1: one open section)

    private const string SecMain = "main";
    private const string SecAttack = "attack";
    private const string SecRes = "res";
    private const string SecMore = "more";
    private const string SecFeatures = "features";

    private static readonly string[] MainKeys = { "maxHealth", "moveSpeed", "attackRate", "side" };
    private static readonly string[] AttackKeys = { "attackPower", "attackRange", "attackDamageType", "attackAreaRadius", "attackAreaSplit", "critChance", "attackArmorPenetration", "attackArmorPenetrationPercent" };

    private string _section = SecMain;
    private string _resSearch = string.Empty;
    private bool _resAll;
    private readonly HashSet<string> _expanded = new(System.StringComparer.Ordinal);
    private bool _addRuleOpen;
    private string? _openCard;
    private string? _copyFrom;


    protected override void BuildProperties(Inspector inspector)
    {
        var sections = _model.Sections();
        if (SectionHeader(inspector, SecMain, "Основное", sections.Main))
        {
            BuildMain(inspector);
        }

        if (SectionHeader(inspector, SecAttack, "Атака и навыки", sections.Attack))
        {
            BuildAttack(inspector);
        }

        if (SectionHeader(inspector, SecRes, "Сопротивления и защита", sections.Resistances))
        {
            BuildResistances(inspector);
        }

        if (SectionHeader(inspector, SecFeatures, "Особенности", sections.Features))
        {
            BuildFeatures(inspector);
        }

        if (SectionHeader(inspector, SecMore, "Дополнительно", sections.More))
        {
            BuildMore(inspector);
        }
    }

    /// <summary>
    /// «Особенности» (spec part 6 §4.1, §5, §9.2): the species (one branch of the tree), the shared traits as chips, the pack,
    /// own values of registry characteristics and the personal passives as cards.
    /// </summary>
    private void BuildFeatures(Inspector inspector)
    {
        var unit = _model.Unit;
        var species = new List<ChoiceOption> { new(string.Empty, "— без вида —") };
        species.AddRange(Library.Items(ContentTypeRegistry.Species).Select(sp => new ChoiceOption(sp.Id, string.Join(" → ", HexTactics.Content.Runtime.PassiveEffects.SpeciesChain(Library, sp.Id).Select(i => i.Name)))));
        inspector.Choice(IdPrefix + "species", "Вид", species, unit.Species, v => Refuse(_model.SetSpecies(v)), "Юнит считается и всеми предками вида; эффекты вида действуют один раз.");

        inspector.Text("Теги", size: 13);
        foreach (var chip in _model.TraitChips())
        {
            var row = inspector.Row();
            var label = UiKit.Label((chip.Acts ? "● " : "○ ") + chip.Name + (chip.Note.Length > 0 ? $" — {chip.Note}" : string.Empty), 13);
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(label);
            var index = chip.Index;
            row.AddChild(UiKit.Button("✕", () => _model.RemoveTrait(index), ButtonKind.Normal, $"{IdPrefix}trait.{index}.remove", "Убрать тег у юнита (сам тег остаётся в библиотеке)"));
        }

        var traits = Library.Items(ContentTypeRegistry.Trait).Where(t => !t.Archived).Select(t => new ChoiceOption(t.Id, t.Name)).ToList();
        if (traits.Count > 0)
        {
            _traitPick = traits.Any(t => t.Value == _traitPick) ? _traitPick : traits[0].Value;
            inspector.Choice(IdPrefix + "trait.pick", "Добавить тег", traits, _traitPick!, v => _traitPick = v);
            inspector.Button(IdPrefix + "trait.add", "＋ Добавить тег", () => Refuse(_model.AddTrait(_traitPick!)));
        }
        else
        {
            inspector.Hint("Тегов в библиотеке нет — «Происхождение и теги» → «Теги».");
        }

        inspector.LineText(IdPrefix + "pack", "Стая (packId)", unit.PackId, v => Refuse(_model.SetPackId(v)), "Юниты одной стаи считаются вместе бонусами «за каждого союзника».");
        foreach (var stat in Library.Items(ContentTypeRegistry.UnitStat))
        {
            var id = stat.Id;
            var final = HexTactics.Content.Runtime.PassiveEffects.Stat(unit, Library, id);
            inspector.LineText($"{IdPrefix}stat.{id}", $"{stat.Name} (своя база; итог {Inspector.Num(final.Final)})", unit.CustomStats.TryGetValue(id, out var own) ? Inspector.Num(own) : string.Empty,
                v => { if (_model.SetCustomStat(id, v) is { } issue) { Message("✗ " + issue.Message, HexTactics.Content.Model.IssueSeverity.Error); Stamp++; } },
                $"Пусто — база характеристики ({Inspector.Num(((HexTactics.Content.Kinds.UnitStatData)stat.Data).BaseValue)}).");
        }

        inspector.Text("Пассивы", size: 13);
        inspector.Hint("Пассив — постоянная особенность этого юнита: действует всё время, пока юнит жив (или при заданном условии), без кнопки и без слота навыка. Другие юниты не меняются.");
        BuildEffectList(inspector, IdPrefix + "passive.", _model.PassiveCards(), unit.Passives, _model.AddPassive, _model.RemovePassive, _model.SetPassive, inheritance: false, "＋ Добавить пассив");
    }

    private string? _traitPick;

    private void Refuse(string? refusal)
    {
        if (refusal is not null)
        {
            Message("✗ " + refusal, HexTactics.Content.Model.IssueSeverity.Error);
            Stamp++;
        }
    }

    /// <summary>A section header: the open one shows its content, the others one line with their summary.</summary>
    private bool SectionHeader(Inspector inspector, string id, string title, string summary)
    {
        var open = _section == id;
        var button = inspector.Button($"{IdPrefix}sec.{id}", (open ? "▾ " : "▸ ") + title + (open ? string.Empty : "  ·  " + summary), () =>
        {
            _section = id;
            Stamp++;
        }, open ? ButtonKind.Primary : ButtonKind.Flat, summary);
        button.Alignment = HorizontalAlignment.Left;
        button.ClipText = true;
        return open;
    }

    private void Fields(Inspector inspector, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (UnitData.Fields.FirstOrDefault(f => f.Key == key) is { } field)
            {
                inspector.Field(IdPrefix, field, _model.Unit, ApplyField);
            }
        }
    }

    private void BuildMain(Inspector inspector)
    {
        Fields(inspector, MainKeys);
        Fields(inspector, UnitData.Fields.Where(f => f.Section == HexTactics.Content.Schema.FieldSections.Appearance).Select(f => f.Key));
        inspector.Hint("Скорость атак — сколько обычных ударов в секунду; сила удара — в «Атака и навыки».");
    }

    private void BuildAttack(Inspector inspector)
    {
        var unit = _model.Unit;
        Fields(inspector, AttackKeys);
        inspector.Hint("Сила атаки (Attack Power) — урон одного удара и база навыков «% от атаки».");

        inspector.Text("Навыки", size: 13);
        inspector.Hint("Ссылки на сохранённые навыки. Первый — кнопка R и меню ТД в бою; в одной ТД — одно применение.");
        for (var i = 0; i < unit.Skills.Count; i++)
        {
            var id = unit.Skills[i];
            var item = Library.Find(ContentTypeRegistry.Skill, id);
            var row = inspector.Row();
            var name = UiKit.Button(item is null ? $"✗ {id}" : $"{((SkillData)item.Data).Icon} {item.Name}", () => { _previewSkill = id; Stamp++; }, ButtonKind.Flat, $"{IdPrefix}skill.{i}", "Показать зону навыка на поле");
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            name.Alignment = HorizontalAlignment.Left;
            if (item is null)
            {
                name.AddThemeColorOverride("font_color", UiKit.Error);
            }

            row.AddChild(name);
            var index = i;
            row.AddChild(UiKit.Button("↑", () => _model.MoveSkill(id, -1), ButtonKind.Normal, $"{IdPrefix}skill.{index}.up", "Выше"));
            row.AddChild(UiKit.Button("↓", () => _model.MoveSkill(id, 1), ButtonKind.Normal, $"{IdPrefix}skill.{index}.down", "Ниже"));
            row.AddChild(UiKit.Button("✕", () => _model.RemoveSkill(id), ButtonKind.Normal, $"{IdPrefix}skill.{index}.remove", "Убрать навык у юнита (сам навык остаётся в библиотеке)"));
        }

        if (unit.Skills.Count == 0)
        {
            inspector.Hint("Навыков нет.");
        }

        inspector.LineText(IdPrefix + "skill.filter", "Добавить навык: поиск", _filter, v => { _filter = v; Stamp++; });
        var choices = _model.AvailableSkills(_filter);
        if (choices.Count == 0)
        {
            inspector.Hint(_filter.Length > 0 ? "Ничего не найдено." : "Все навыки библиотеки уже у юнита.");
        }
        else
        {
            _pick = choices.Any(c => c.Id == _pick) ? _pick! : choices[0].Id;
            inspector.Choice(IdPrefix + "skill.pick", "Навык из библиотеки", choices.Select(c => new ChoiceOption(c.Id, $"{c.Icon} {c.Name}{(c.IsBuiltIn ? string.Empty : " (свой)")} — {c.Summary}")).ToList(), _pick, v => { _pick = v; _previewSkill = null; Stamp++; });
            inspector.Button(IdPrefix + "skill.add", "＋ Добавить навык", () =>
            {
                if (_pick is not null && _model.AddSkill(_pick) is { } refusal)
                {
                    Message("✗ " + refusal, HexTactics.Content.Model.IssueSeverity.Error);
                }
                else
                {
                    _previewSkill = _pick;
                }
            });
        }
    }

    /// <summary>
    /// «Сопротивления» (spec part 5 §7.2, §7.5): sets, the short table (groups; subtypes under an open family; custom types by
    /// search or «Показать все»), the special defenses as cards and the summary.
    /// </summary>
    private void BuildResistances(Inspector inspector)
    {
        var unit = _model.Unit;
        inspector.Hint("Только для разработчика: в игре числа не показываются. Итог = все типы + семейство + подтип. R — проценты −500…+500 (сверх 100 % урон просто 0, без лечения); A — очки защиты: после R урон × K / (K + A), K из правил боя карты.");
        BuildProfiles(inspector);

        var view = _model.ResistanceView(_resSearch, _resAll, _expanded);
        var tools = inspector.Row();
        var search = AutomationIds.Register(new LineEdit { Text = _resSearch, PlaceholderText = "Поиск: тип, тег, семейство", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClearButtonEnabled = true }, IdPrefix + "res.search");
        search.SetMeta("hx_commit_on_submit", true);
        search.TextSubmitted += v => { _resSearch = v.Trim(); Stamp++; };
        search.FocusExited += () =>
        {
            if (search.Text.Trim() != _resSearch)
            {
                _resSearch = search.Text.Trim();
                Stamp++;
            }
        };
        search.TextChanged += v =>
        {
            if (v.Length == 0 && _resSearch.Length > 0)
            {
                _resSearch = string.Empty;
                Stamp++;
            }
        };
        tools.AddChild(search);
        tools.AddChild(UiKit.Button(_resAll ? "Только основные" : "Показать все", () => { _resAll = !_resAll; Stamp++; }, ButtonKind.Flat, IdPrefix + "res.all",
            _resAll ? "Скрыть нулевые строки и закрытые подтипы" : "Все типы урона, включая нулевые и свои"));

        foreach (var viewRow in view.Rows)
        {
            var row = viewRow.Row;
            var key = row.Key;
            var label = (row.Depth > 0 ? "    " : string.Empty) + row.Label;
            if (row.Bypass)
            {
                if (row.Depth == 0)
                {
                    inspector.ReadOnly(label, "обходит численные R");
                    inspector.Button($"{IdPrefix}res.{key}.rules", "Особые правила (иммунитет) ▸", () => OpenAddRule(DefenseTemplateFor(key), key), ButtonKind.Flat,
                        "Чистый урон нельзя ослабить числом — только явным правилом защиты").Alignment = HorizontalAlignment.Left;
                }

                continue;
            }

            // One row: R (percent) and A (defense points) of the same layer (spec part 6 §3.1, §9.4).
            inspector.PairText($"{IdPrefix}res.{key}", "R, %", Inspector.Num(row.Value), v =>
            {
                if (_model.SetResistance(key, v) is { } issue)
                {
                    Message("✗ " + issue.Message, HexTactics.Content.Model.IssueSeverity.Error);
                    Stamp++;
                }
            }, $"{IdPrefix}armor.{key}", "A", Inspector.Num(row.Armor), v =>
            {
                if (_model.SetArmor(key, v) is { } issue)
                {
                    Message("✗ " + issue.Message, HexTactics.Content.Model.IssueSeverity.Error);
                    Stamp++;
                }
            }, label, Explain(row), "Защита A в очках: урон после R × K / (K + A); при A = K — вдвое меньше. Не процент.");
            if (viewRow.HasChildren && !_resAll && _resSearch.Length == 0)
            {
                inspector.Button($"{IdPrefix}res.{key}.expand", viewRow.Expanded ? "▾ скрыть подтипы" : "▸ подтипы", () =>
                {
                    if (!_expanded.Remove(key))
                    {
                        _expanded.Add(key);
                    }

                    Stamp++;
                }, ButtonKind.Flat).Alignment = HorizontalAlignment.Left;
            }

            if (row.Own && unit.ResistanceProfiles.Count > 0)
            {
                inspector.Button($"{IdPrefix}res.{key}.reset", "↺ Вернуть значение набора", () => _model.ResetResistance(key), ButtonKind.Flat, "Убрать своё значение юнита — действует значение набора (или 0)").Alignment = HorizontalAlignment.Left;
            }

            if (row.ArmorOwn && unit.ResistanceProfiles.Count > 0)
            {
                inspector.Button($"{IdPrefix}armor.{key}.reset", "↺ Вернуть защиту A набора", () => _model.ResetArmor(key), ButtonKind.Flat, "Убрать свою защиту A юнита — действует значение набора (или 0)").Alignment = HorizontalAlignment.Left;
            }
        }

        if (view.Hidden > 0)
        {
            inspector.Hint($"Скрыто строк: {view.Hidden} (нулевые подтипы и свои типы) — поиск или «Показать все».");
        }

        BuildDefenses(inspector);
        inspector.Text(_model.ResistanceSummary(), size: 12);
    }

    private static string DefenseTemplateFor(string key) => key == HexTactics.Core.Combat.DamageTypes.True ? "immunity" : "complex";

    private void OpenAddRule(string templateId, string? damageType)
    {
        _addRuleOpen = true;
        _rulePick = UnitEditorModel.TemplatePrefix + templateId;
        _ruleDamageType = damageType;
        Stamp++;
    }

    private string? _ruleDamageType;

    private void BuildMore(Inspector inspector)
    {
        var unit = _model.Unit;
        inspector.ReadOnly("ID", _model.Item.Id);
        inspector.ReadOnly("Редакция / схема", $"{_model.Item.Revision} / {_model.Item.Kind.SchemaVersion}");
        inspector.ReadOnly("Наборы сопротивлений", unit.ResistanceProfiles.Count == 0 ? "нет" : string.Join(" → ", unit.ResistanceProfiles.Select(p => Library.Find(ContentTypeRegistry.ResistanceProfile, p)?.Name ?? p)));
        var users = Library.UsedBy(_model.Item.Ref);
        inspector.ReadOnly("Используется", users.Count == 0 ? "нигде" : string.Join(", ", users.Select(u => u.Name)));

        inspector.Text("Скопировать сопротивления", size: 13);
        var others = Library.Items(ContentTypeRegistry.Unit).Where(u => u.Id != _model.Item.Id && !u.HasErrors).ToList();
        if (others.Count == 0)
        {
            inspector.Hint("Других юнитов нет.");
            return;
        }

        _copyFrom = others.Any(o => o.Id == _copyFrom) ? _copyFrom : others[0].Id;
        inspector.Choice(IdPrefix + "copy.from", "Из юнита", others.Select(o => new ChoiceOption(o.Id, o.Name)).ToList(), _copyFrom!, v => _copyFrom = v,
            "Наборы и свои значения сопротивлений; правила и щиты не меняются.");
        inspector.Button(IdPrefix + "copy.run", "Скопировать (Ctrl+Z — отменить)", () =>
        {
            if (_copyFrom is not null && _model.CopyResistancesFrom(_copyFrom) is { } refusal)
            {
                Message("✗ " + refusal, HexTactics.Content.Model.IssueSeverity.Error);
            }
            else
            {
                Message($"Сопротивления скопированы из «{Library.Find(ContentTypeRegistry.Unit, _copyFrom!)?.Name}».", null);
            }
        });
    }

    /// <summary>
    /// Resistance sets of the unit (spec part 5 §6.4): applied by reference, in order (a later set wins per key); the rows below
    /// say what is inherited and what is the unit's own; «Сохранить как набор» stores the current values for other units.
    /// </summary>
    private void BuildProfiles(Inspector inspector)
    {
        var unit = _model.Unit;
        for (var i = 0; i < unit.ResistanceProfiles.Count; i++)
        {
            var id = unit.ResistanceProfiles[i];
            var item = Library.Find(ContentTypeRegistry.ResistanceProfile, id);
            var row = inspector.Row();
            var title = new Label { Text = $"Набор: {item?.Name ?? "✗ " + id} — {UnitEditorModel.ProfileSummary(Library, id)}", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            title.AddThemeFontSizeOverride("font_size", 12);
            row.AddChild(title);
            row.AddChild(UiKit.Button("✕", () => _model.RemoveProfile(id), ButtonKind.Normal, $"{IdPrefix}profile.{i}.remove", "Убрать набор у юнита (сам набор остаётся в библиотеке)"));
        }

        var choices = _model.AvailableProfiles();
        var tools = inspector.Row();
        if (choices.Count > 0)
        {
            _profilePick = choices.Any(c => c.Id == _profilePick) ? _profilePick! : choices[0].Id;
            inspector.Choice(IdPrefix + "profile.pick", "Набор (пресет)", choices.Select(c => new ChoiceOption(c.Id, c.Name + (c.IsBuiltIn ? string.Empty : " (свой)"))).ToList(), _profilePick,
                v => { _profilePick = v; Stamp++; }, choices.FirstOrDefault(c => c.Id == _profilePick)?.Summary);
            tools.AddChild(UiKit.Button("＋ Применить набор", () =>
            {
                if (_profilePick is not null && _model.AddProfile(_profilePick) is { } refusal)
                {
                    Message("✗ " + refusal, HexTactics.Content.Model.IssueSeverity.Error);
                }
            }, ButtonKind.Normal, IdPrefix + "profile.add", "Значения набора — основа; свои значения юнита их перекрывают"));
            tools.MoveToFront();
        }

        tools.AddChild(UiKit.Button("Сохранить как набор", () =>
        {
            var result = _model.SaveAsProfile($"{_model.Item.Name}: защита");
            Message(result.Ok ? $"Сохранён набор «{result.Item!.Name}» — его можно применить к другим юнитам." : "✗ " + result.Message, result.Ok ? null : HexTactics.Content.Model.IssueSeverity.Error);
        }, ButtonKind.Flat, IdPrefix + "profile.save", "Текущие сопротивления юнита — новым набором для других юнитов (юнит не меняется)"));
    }

    /// <summary>
    /// «Особые защиты и реакции» (spec part 5 §7.2–§7.3): short cards of the rules and shields that open in place, «Добавить
    /// правило» with templates and saved rules; a template or «Изменить копию» opens the rule editor on top and comes back here.
    /// </summary>
    private void BuildDefenses(Inspector inspector)
    {
        var unit = _model.Unit;
        inspector.Text("Особые защиты и реакции", size: 13);
        var cards = _model.DefenseCards();
        var ruleIndex = 0;
        var shieldIndex = 0;
        var orderedShields = unit.Shields.Select((s, i) => (s, i)).OrderBy(x => x.s.Order).ThenBy(x => x.s.Name, System.StringComparer.Ordinal).Select(x => x.i).ToList();
        foreach (var card in cards)
        {
            var isShield = card.Id.StartsWith("shield:", System.StringComparison.Ordinal);
            var cardId = isShield ? $"{IdPrefix}shield.{orderedShields[shieldIndex]}" : $"{IdPrefix}rule.{ruleIndex}";
            var open = _openCard == cardId;
            var button = inspector.Button(cardId + ".card", (open ? "▾ " : "▸ ") + card.Title + "  ·  " + card.Short, () => { _openCard = open ? null : cardId; Stamp++; }, ButtonKind.Flat, card.Summary);
            button.Alignment = HorizontalAlignment.Left;
            button.ClipText = true;
            if (card.Problem)
            {
                button.AddThemeColorOverride("font_color", UiKit.Error);
            }

            if (open && !isShield)
            {
                var ruleId = card.Id;
                inspector.Text(card.Summary, dim: true, size: 12);
                var row = inspector.Row();
                row.AddChild(UiKit.Button("Изменить копию", () => EditCopy(ruleId), ButtonKind.Normal, $"{cardId}.copy", "Копия правила только для этого юнита; общее правило не меняется"));
                row.AddChild(UiKit.Button("✕ Убрать", () => { _openCard = null; _model.RemoveRule(ruleId); }, ButtonKind.Normal, $"{cardId}.remove", "Убрать правило у юнита (само правило остаётся в библиотеке)"));
            }
            else if (open)
            {
                BuildShield(inspector, orderedShields[shieldIndex]);
            }

            if (isShield)
            {
                shieldIndex++;
            }
            else
            {
                ruleIndex++;
            }
        }

        if (cards.Count == 0)
        {
            inspector.Hint("Особых защит нет — обычному юниту они не нужны.");
        }

        var actions = inspector.Row();
        actions.AddChild(UiKit.Button(_addRuleOpen ? "▾ Добавить правило" : "＋ Добавить правило", () => { _addRuleOpen = !_addRuleOpen; Stamp++; }, ButtonKind.Normal, IdPrefix + "rule.addmenu",
            "Шаблон (новое правило для этого юнита) или сохранённое правило библиотеки"));
        actions.AddChild(UiKit.Button("＋ Щит", () =>
        {
            if (_model.AddShield() is { } refusal)
            {
                Message("✗ " + refusal, HexTactics.Content.Model.IssueSeverity.Error);
            }
            else
            {
                _openCard = $"{IdPrefix}shield.{_model.Unit.Shields.Count - 1}";
            }
        }, ButtonKind.Normal, IdPrefix + "shield.add"));
        if (_addRuleOpen)
        {
            BuildAddRule(inspector);
        }
    }

    private void BuildAddRule(Inspector inspector)
    {
        inspector.LineText(IdPrefix + "rule.filter", "Поиск: шаблоны и правила", _ruleFilter, v => { _ruleFilter = v; Stamp++; });
        var choices = _model.AddRuleChoices(_ruleFilter);
        if (choices.Count == 0)
        {
            inspector.Hint("Ничего не найдено.");
            return;
        }

        _rulePick = choices.Any(c => c.Value == _rulePick) ? _rulePick! : choices[0].Value;
        inspector.Choice(IdPrefix + "rule.pick", "Шаблон или правило", choices.Select(c => new ChoiceOption(c.Value, c.Label)).ToList(), _rulePick,
            v => { _rulePick = v; Stamp++; }, choices.FirstOrDefault(c => c.Value == _rulePick).Hint);
        inspector.Button(IdPrefix + "rule.add", "Добавить", () =>
        {
            if (_rulePick is null)
            {
                return;
            }

            if (_rulePick.StartsWith(UnitEditorModel.TemplatePrefix, System.StringComparison.Ordinal))
            {
                var item = _model.NewRuleFromTemplate(_rulePick[UnitEditorModel.TemplatePrefix.Length..]);
                if (item is null)
                {
                    return;
                }

                if (_ruleDamageType is { } type && item.Data is DefenseRuleData data && data.Trigger != HexTactics.Content.Registry.DefenseActionRegistry.OnStatusApplied)
                {
                    data.DamageType = type;
                }

                _ruleDamageType = null;
                RequestNested(item, saved =>
                {
                    if (saved is not null && _model.AddRule(saved.Id) is null)
                    {
                        Message($"Правило «{saved.Name}» сохранено и добавлено юниту.", null);
                    }
                    else
                    {
                        Message("Правило не сохранено — юнит не изменился.", HexTactics.Content.Model.IssueSeverity.Warning);
                    }

                    Stamp++;
                });
                return;
            }

            if (_model.AddRule(_rulePick) is { } refusal)
            {
                Message("✗ " + refusal, HexTactics.Content.Model.IssueSeverity.Error);
            }
            else
            {
                _addRuleOpen = false;
            }
        }, ButtonKind.Primary, "Шаблон откроет редактор правила; после сохранения правило добавится юниту");
    }

    /// <summary>«Изменить копию» (spec part 5 §7.3): the copy opens in the rule editor; when saved the unit refers to it.</summary>
    private void EditCopy(string ruleId)
    {
        if (_model.CopyOfRule(ruleId) is not { } copy)
        {
            return;
        }

        RequestNested(copy, saved =>
        {
            if (saved is not null)
            {
                _model.ReplaceRule(ruleId, saved.Id);
                Message($"Юнит использует копию «{saved.Name}»; общее правило не изменилось.", null);
            }
            else
            {
                Message("Копия не сохранена — юнит не изменился.", HexTactics.Content.Model.IssueSeverity.Warning);
            }

            Stamp++;
        });
    }

    private void BuildShield(Inspector inspector, int index)
    {
        var shield = _model.Unit.Shields[index];
        var types = new List<ChoiceOption> { new(ShieldData.AnyType, "любой урон") };
        types.AddRange(ReferenceOptions(ContentTypeRegistry.DamageType, HexTactics.Content.Schema.ReferenceFilter.Any, shield.DamageType, allowCreate: false));
        inspector.LineText($"{IdPrefix}shield.{index}.name", "Название", shield.Name, v => ApplyShield(index, "name", v));
        inspector.LineText($"{IdPrefix}shield.{index}.capacity", "Прочность, HP", Inspector.Num(shield.Capacity), v => ApplyShield(index, "capacity", v), "Больше 0, не больше 100 000. В бою начинается полным.");
        inspector.Choice($"{IdPrefix}shield.{index}.damageTypeId", "Принимает", types, shield.DamageType, v => ApplyShield(index, "damageTypeId", v), "Семейство принимает и свои подтипы; чистый урон щит тоже принимает.");
        inspector.LineText($"{IdPrefix}shield.{index}.order", "Порядок", shield.Order.ToString(CultureInfo.InvariantCulture), v => ApplyShield(index, "order", v), "Меньше — тратится раньше.");
        inspector.Button($"{IdPrefix}shield.{index}.remove", "✕ Убрать щит", () => { _openCard = null; _model.RemoveShield(index); }, ButtonKind.Flat);
    }

    private void ApplyShield(int index, string key, object? value)
    {
        if (_model.SetShield(index, key, value) is { } issue)
        {
            Message("✗ " + issue.Message, HexTactics.Content.Model.IssueSeverity.Error);
            Stamp++;
        }
    }

    /// <summary>Opens the section (and the family, card or menu) that holds an automation control (spec part 5 §7.1).</summary>
    public override bool Reveal(string automationId)
    {
        if (!automationId.StartsWith(IdPrefix, System.StringComparison.Ordinal))
        {
            return false;
        }

        var id = automationId[IdPrefix.Length..];
        var key = id.Split('.')[0];
        if (MainKeys.Contains(key) || key is "color" or "scale")
        {
            _section = SecMain;
        }
        else if (AttackKeys.Contains(key) || key == "skill")
        {
            _section = SecAttack;
        }
        else if (key == "copy")
        {
            _section = SecMore;
        }
        else if (key is "passive" or "species" or "trait" or "pack" or "stat")
        {
            _section = SecFeatures;
            var parts = id.Split('.');
            if (key == "passive" && parts.Length > 2 && parts[1] is not ("template" or "add"))
            {
                OpenEffect(IdPrefix + "passive.", parts[1]);
            }
        }
        else if (key is "res" or "armor" or "profile" or "rule" or "shield")
        {
            _section = SecRes;
            var parts = id.Split('.');
            if (key == "rule" && parts.Length > 1 && (parts[1] is "filter" or "pick" or "add"))
            {
                _addRuleOpen = true;
            }
            else if ((key is "rule" or "shield") && parts.Length > 2 && int.TryParse(parts[1], out _))
            {
                _openCard = $"{IdPrefix}{key}.{parts[1]}";
            }
            else if (key is "res" or "armor" && parts.Length > 1 && parts[1] is not ("search" or "all"))
            {
                RevealResistance(string.Join('.', parts.Skip(1)).Replace(".reset", string.Empty, System.StringComparison.Ordinal).Replace(".expand", string.Empty, System.StringComparison.Ordinal).Replace(".rules", string.Empty, System.StringComparison.Ordinal));
            }
        }
        else
        {
            return false;
        }

        Stamp++;
        return true;
    }

    private void RevealResistance(string typeKey)
    {
        if (_model.ResistanceView(_resSearch, _resAll, _expanded).Rows.Any(r => r.Row.Key == typeKey))
        {
            return;
        }

        if (Library.Find(ContentTypeRegistry.DamageType, typeKey)?.Data is DamageTypeData { Family.Length: > 0 } d)
        {
            _expanded.Add(d.Family);
        }

        if (!_model.ResistanceView(_resSearch, _resAll, _expanded).Rows.Any(r => r.Row.Key == typeKey))
        {
            _resSearch = string.Empty;
            _resAll = true;
        }
    }

    /// <summary>The row's value in words; for a layered row also the total with «все типы» and the family.</summary>
    private string? Explain(ResistanceRow row)
    {
        var parts = new List<string>();
        var total = row.Key == UnitData.GlobalResistance ? row.Value : _model.EffectiveResistance(row.Key);
        if (row.Value != 0 || total != 0 || row.Source is not null)
        {
            var own = row.Value == 0 && !row.Own
                ? (row.Depth > 0 && total != 0 ? "своего нет — унаследовано" : "значения нет")
                : UnitEditorModel.Explain(row.Value);
            if (row.Source is { } source)
            {
                own = $"{source}: {own}";
            }

            parts.Add(total == row.Value ? own : $"{own} · итог: {UnitEditorModel.Explain(total)}");
        }

        // Defense A of the row (spec part 6 §3.2): own or from a set, and the layered total for a type.
        var armorTotal = row.Key == UnitData.GlobalResistance ? row.Armor : _model.EffectiveArmor(row.Key);
        if (row.Armor != 0 || armorTotal != 0 || row.ArmorSource is not null)
        {
            var a = row.Armor == 0 && !row.ArmorOwn ? (armorTotal != 0 ? "A своей нет — унаследовано" : "A нет") : UnitEditorModel.ExplainArmor(row.Armor);
            if (row.ArmorSource is { } source)
            {
                a = $"{source}: {a}";
            }

            parts.Add(armorTotal == row.Armor ? a : $"{a} · итог: {UnitEditorModel.ExplainArmor(armorTotal)}");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    protected override void BuildPreview(Inspector preview)
    {
        preview.Section("В бою");
        foreach (var line in _model.CombatPreview())
        {
            preview.Text(line);
        }

        preview.Hint("Зона на поле — навык, выбранный в списке (для взгляда на север).");
        var breakdown = _model.Breakdown();
        if (breakdown.Count > 0)
        {
            // Spec part 6 §4.3, §9.2: «Откуда взялось?» — base, final and every source of what the passives change.
            preview.Section("Откуда взялось");
            foreach (var line in breakdown)
            {
                preview.Text(line, size: 12);
            }
        }

        // «Проверить на ударе» against this unit as edited (unsaved values, sets, rules and shields); or this unit attacks
        // an empty target (its unsaved outgoing passives).
        _sandbox ??= new SandboxPanel(IdPrefix, new HexTactics.Editor.Mechanics.SandboxSetup(), fixedTarget: true);
        _sandbox.Build(preview, Library, setup => setup.AttackerUnitId == SandboxPanel.SelfAttacker
            ? HexTactics.Editor.Mechanics.HitSandbox.Run(Library, setup with { AttackerUnitId = null, TargetUnitId = null }, attackerDraft: _model.Item)
            : HexTactics.Editor.Mechanics.HitSandbox.Run(Library, setup, unitDraft: _model.Item), () => Stamp++);
    }

    protected override SandboxPanel? Sandbox => _sandbox;

    protected override bool Expect(string[] a, out string actual, out bool ok)
    {
        var unit = _model.Unit;
        switch (a[0])
        {
            case "unit-field":
                {
                    var field = UnitData.Fields.FirstOrDefault(f => f.Key == a[1]);
                    var value = field?.Get?.Invoke(unit);
                    actual = value switch
                    {
                        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                        null => "none",
                        _ => value.ToString() ?? string.Empty,
                    };
                    ok = actual == a[2];
                    return true;
                }

            case "resistance":
                {
                    // expect:resistance:<type id, type name or global>:<effective value> — global + family + subtype of this unit
                    var key = a[1] == UnitData.GlobalResistance || Library.Find(ContentTypeRegistry.DamageType, a[1]) is not null
                        ? a[1]
                        : Library.Items(ContentTypeRegistry.DamageType).FirstOrDefault(t => t.Name == a[1])?.Id ?? a[1];
                    actual = (key == UnitData.GlobalResistance ? _model.ResistanceRows()[0].Value : _model.EffectiveResistance(key)).ToString("0.##", CultureInfo.InvariantCulture);
                    ok = actual == a[2];
                    return true;
                }

            case "passives":
                // expect:passives:<N>
                actual = unit.Passives.Count.ToString(CultureInfo.InvariantCulture);
                ok = actual == a[1];
                return true;
            case "passive":
                {
                    // expect:passive:<name>:<text in its summary>
                    var card = _model.PassiveCards().FirstOrDefault(c => c.Name == a[1]);
                    actual = card is null ? "none" : card.Summary;
                    ok = card is not null && actual.Contains(string.Join(":", a.Skip(2)), System.StringComparison.Ordinal);
                    return true;
                }

            case "stat":
                // expect:stat:<attackPower|maxHealth|moveSpeed|attackRate>:<final value with passives>
                actual = HexTactics.Content.Runtime.PassiveEffects.Stat(unit, Library, a[1]).Final.ToString("0.##", CultureInfo.InvariantCulture);
                ok = actual == a[2];
                return true;
            case "breakdown":
                actual = string.Join(" | ", _model.Breakdown());
                ok = actual.Contains(string.Join(":", a.Skip(1)), System.StringComparison.Ordinal);
                return true;
            case "armor":
                {
                    // expect:armor:<type id, type name or global>:<A_raw> — global + family + subtype of this unit (spec part 6 §3.2)
                    var key = a[1] == UnitData.GlobalResistance || Library.Find(ContentTypeRegistry.DamageType, a[1]) is not null
                        ? a[1]
                        : Library.Items(ContentTypeRegistry.DamageType).FirstOrDefault(t => t.Name == a[1])?.Id ?? a[1];
                    actual = (key == UnitData.GlobalResistance ? _model.ResistanceRows()[0].Armor : _model.EffectiveArmor(key)).ToString("0.##", CultureInfo.InvariantCulture);
                    ok = actual == a[2];
                    return true;
                }

            case "skills":
                actual = string.Join(",", unit.Skills.Select(s => Library.Find(ContentTypeRegistry.Skill, s)?.Name ?? s));
                ok = a[1] == unit.Skills.Count.ToString(CultureInfo.InvariantCulture) || actual == a[1];
                return true;
            case "profiles":
                // expect:profiles:<N or names joined by ,>
                actual = string.Join(",", unit.ResistanceProfiles.Select(p => Library.Find(ContentTypeRegistry.ResistanceProfile, p)?.Name ?? p));
                ok = a[1] == unit.ResistanceProfiles.Count.ToString(CultureInfo.InvariantCulture) || actual == a[1];
                return true;
            case "resistance-source":
                {
                    // expect:resistance-source:<key>:own|inherited|none
                    var row = _model.ResistanceRows().FirstOrDefault(r => r.Key == a[1]);
                    actual = row is null || row.Source is null ? "none" : row.Own ? "own" : "inherited";
                    ok = actual == a[2];
                    return true;
                }

            case "section":
                // expect:section:main|attack|res|more — the open section of the inspector
                actual = _section;
                ok = actual == a[1];
                return true;
            case "res-rows":
                // expect:res-rows:<keys joined by ,> — the visible rows of the resistance table
                actual = string.Join(",", _model.ResistanceView(_resSearch, _resAll, _expanded).Rows.Select(r => r.Row.Key == UnitData.GlobalResistance ? "global" : r.Row.Key.Replace("core.damage.", string.Empty, System.StringComparison.Ordinal)));
                ok = actual == a[1];
                return true;
            case "res-has":
                // expect:res-has:<row label>:yes|no — a row with this label is visible in the table
                actual = _model.ResistanceView(_resSearch, _resAll, _expanded).Rows.Any(r => r.Row.Label == a[1]) ? "yes" : "no";
                ok = actual == a[2];
                return true;
            case "features-summary":
                // expect:features-summary:<text> — the summary of «Особенности» (species, tags, active effects)
                actual = _model.FeaturesSummary();
                ok = actual.Contains(string.Join(":", a.Skip(1)), System.StringComparison.Ordinal);
                return true;
            case "res-summary":
                actual = _model.ResistanceSummary();
                ok = actual.Contains(string.Join(":", a.Skip(1)), System.StringComparison.Ordinal);
                return true;
            case "rules":
                // expect:rules:N or expect:rules:<names joined by ,>
                actual = string.Join(",", _model.RuleSummaries().Select(r => r.Name));
                ok = a[1] == unit.DefenseRules.Count.ToString(CultureInfo.InvariantCulture) || actual == a[1];
                return true;
            case "shield":
                {
                    // expect:shield:<index>:<name>:<capacity>
                    var n = int.Parse(a[1], CultureInfo.InvariantCulture);
                    actual = n < unit.Shields.Count ? $"{unit.Shields[n].Name}:{unit.Shields[n].Capacity.ToString("0.##", CultureInfo.InvariantCulture)}" : "none";
                    ok = actual == string.Join(":", a.Skip(2));
                    return true;
                }
            default:
                actual = "unknown";
                ok = false;
                return false;
        }
    }
}
