using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HexTactics.Content.Kinds;

namespace HexTactics.Editor.Scenarios;

/// <summary>A step of the scenario wizard: its id, title, a short explanation and whether its part has no errors.</summary>
public sealed record WizardStep(string Id, string Title, string Hint, bool Done);

/// <summary>
/// The wizard path of the scenario editor (spec part 7 §9.3): «Тип → Карта → Силы → Маршруты/волны или ИИ → Цели → Проверка», the
/// steps a mode does not need hidden, every issue of the check tied to its step, and «Перейти и исправить» that selects the
/// broken object (an enemy, an object, a wave, an area brush) and names the step to open.
/// </summary>
public sealed partial class ScenarioEditorModel
{
    /// <summary>The scenario was never saved: the editor starts it on the first step of the wizard.</summary>
    public bool IsNew => Item.Revision == 0;

    public IReadOnlyList<WizardStep> WizardSteps()
    {
        var s = Scenario;
        var errors = Validate().Where(i => i.IsError).Select(i => StepOf(i.Path)).ToHashSet();
        var steps = new List<WizardStep>
        {
            new("type", "Тип", "Что будет происходить: обычный бой, Tower Defense, оборона или исследование. Остальные шаги подстроятся под тип.", !errors.Contains("type")),
            new("map", "Карта", s.IsExploration
                ? "Поле боя из библиотеки и где отряд входит и выходит: кисти «Вход» и «Выход», щелчок по гексу добавляет или убирает его."
                : s.IsTowerDefense ? "Поле боя с базами и маршрутами врагов: их рисуют в редакторе карт («✎ Открыть карту»)."
                : "Поле боя из библиотеки; гексы выставления и препятствия правятся в редакторе карт.", !errors.Contains("map")),
            new("forces", "Силы", s.IsDefense
                ? "Кто где стоит: объекты миссии и враги с начала (щелчок по полю), отряд игрока — карточки нижней панели."
                : "Кто где стоит: враги на карте (щелчок по полю, Q / E — поворот) и отряд игрока — карточки нижней панели.", !errors.Contains("forces")),
        };
        if (s.IsTowerDefense || s.IsDefense)
        {
            steps.Add(new("waves", s.IsDefense ? "Волны и ИИ" : "Волны", s.IsDefense
                ? "Кто и когда приходит: группы из баз врага и зон карты с простым поведением; суммы считаются сами."
                : "Кто и когда идёт по маршрутам; жизни каждой базы игрока. Суммы считаются сами.", !errors.Contains("waves")));
        }

        steps.Add(new("goals", "Цели", "Когда победа и когда поражение — карточками и флажками, без формул. Итог словами — в нижней строке.", !errors.Contains("goals")));
        steps.Add(new("check", "Проверка", "Что исправить — с переходом к месту; затем «Сохранить» и «▶ Тест сценария».", errors.Count == 0));
        return steps;
    }

    /// <summary>The wizard step an issue path belongs to.</summary>
    public static string StepOf(string path)
    {
        var key = Regex.Match(Field(path), @"^[A-Za-z]+").Value;
        return key switch
        {
            "mode" => "type",
            "map" or "entry" or "exit" => "map",
            "enemies" or "squad" or "objects" => "forces",
            "waves" or "bases" => "waves",
            "survive" or "objectsPolicy" or "exitRule" or "keyUnit" or "required" or "extraGoal" or "defeat" or "timeLimit" or "ruleset" => "goals",
            _ => "check",
        };
    }

    /// <summary>The issue path without the «data.» of the data checks: «data.waves[0]» → «waves[0]».</summary>
    private static string Field(string path) => path.StartsWith("data.", System.StringComparison.Ordinal) ? path["data.".Length..] : path;

    /// <summary>«Перейти и исправить»: selects what the issue names (enemy, object, wave, area brush) and returns its step.</summary>
    public string GoTo(string path)
    {
        var match = Regex.Match(Field(path), @"^(?<key>[A-Za-z]+)(?:[.\[](?<index>\d+)\]?)?");
        var key = match.Groups["key"].Value;
        var index = match.Groups["index"].Success ? int.Parse(match.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
        switch (key)
        {
            case "enemies" when index >= 0 && index < Scenario.Enemies.Count:
                SetBrush("enemies");
                Selected = index;
                break;
            case "objects" when index >= 0 && index < Scenario.Objects.Count:
                SetBrush("objects");
                SelectedObject = index;
                break;
            case "waves" when index >= 0 && index < Scenario.Waves.Count:
                SelectedWave = Scenario.Waves[index].Id;
                break;
            case "entry" or "exit" when Scenario.IsExploration:
                SetBrush(key);
                break;
        }

        RaiseChanged();
        return StepOf(path);
    }
}
