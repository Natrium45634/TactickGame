using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace HexTactics.Client.Debug;

/// <summary>
/// Command-line automation used for screenshots and smoke tests. Active only when user
/// arguments are passed after <c>--</c>, e.g.:
/// <code>HexTactics.exe -- --hx-screenshot=shot.png --hx-frames=30</code>
/// Supported: <c>--hx-screenshot=path</c>, <c>--hx-frames=N</c>, <c>--hx-quit-after=N</c>,
/// <c>--hx-script=a;b;c</c> (steps run by the owner, see <see cref="StepRequested"/>).
/// </summary>
public partial class AutomationRunner : Node
{
    private readonly Dictionary<string, string> _args = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _steps = new();
    private readonly List<string> _failures = new();
    private int _checks;
    private int _frame;
    private int _waitFrames;

    /// <summary>
    /// Raised for each script step; the handler returns the number of frames to wait afterwards,
    /// or <see cref="Repeat"/> to run the same step again on the next frame (polling steps).
    /// </summary>
    public Func<string, int>? StepRequested { get; set; }

    public const int Repeat = -1;

    /// <summary>Records the result of an <c>expect:</c> step; any failure makes the process exit with code 1.</summary>
    public void ReportCheck(bool ok, string description)
    {
        _checks++;
        if (ok)
        {
            GD.Print($"[automation] check ok: {description}");
        }
        else
        {
            _failures.Add(description);
            GD.PrintErr($"[automation] CHECK FAILED: {description}");
        }
    }

    public bool IsActive => _args.Count > 0;

    public string? Get(string key) => _args.TryGetValue(key, out var v) ? v : null;

    public override void _EnterTree()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (!arg.StartsWith("--hx-", StringComparison.Ordinal))
            {
                continue;
            }

            var eq = arg.IndexOf('=');
            var key = eq > 0 ? arg[5..eq] : arg[5..];
            var value = eq > 0 ? arg[(eq + 1)..] : "1";
            _args[key] = value;
        }

        if (Get("script") is { } script)
        {
            foreach (var step in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                _steps.AddLast(step);
            }
        }

        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Process(double delta)
    {
        if (!IsActive)
        {
            return;
        }

        _frame++;
        if (_frame < 10)
        {
            return; // let the window settle and the camera fit
        }

        if (_waitFrames > 0)
        {
            _waitFrames--;
            return;
        }

        if (_steps.Count > 0)
        {
            var step = _steps.First!.Value;
            _steps.RemoveFirst();
            var result = StepRequested?.Invoke(step) ?? 1;
            if (result == Repeat)
            {
                _steps.AddFirst(step);
                _waitFrames = 0;
            }
            else
            {
                GD.Print($"[automation] step: {step}");
                _waitFrames = result;
            }

            return;
        }

        var frames = int.TryParse(Get("frames"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 20;
        if (_frame < frames)
        {
            return;
        }

        var shot = Get("screenshot");
        if (shot is not null)
        {
            var image = GetViewport().GetTexture().GetImage();
            var err = image.SavePng(shot);
            GD.Print($"[automation] screenshot {image.GetWidth()}x{image.GetHeight()} -> {shot}: {err}");
        }

        if (_checks > 0)
        {
            GD.Print(_failures.Count == 0
                ? $"[automation] checks passed: {_checks}"
                : $"[automation] FAILED {_failures.Count} of {_checks} checks");
        }

        if ((shot is not null && Get("stay") is null) || Get("quit") is not null)
        {
            GD.Print("[automation] done");
            GetTree().Quit(_failures.Count == 0 ? 0 : 1);
        }

        _args.Clear();
    }
}
