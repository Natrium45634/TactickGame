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
    private readonly Queue<string> _steps = new();
    private int _frame;
    private int _waitFrames;

    /// <summary>Raised for each script step; the handler returns the number of frames to wait afterwards.</summary>
    public Func<string, int>? StepRequested { get; set; }

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
                _steps.Enqueue(step);
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
            var step = _steps.Dequeue();
            GD.Print($"[automation] step: {step}");
            _waitFrames = StepRequested?.Invoke(step) ?? 1;
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

        if ((shot is not null && Get("stay") is null) || Get("quit") is not null)
        {
            GD.Print("[automation] done");
            GetTree().Quit();
        }

        _args.Clear();
    }
}
