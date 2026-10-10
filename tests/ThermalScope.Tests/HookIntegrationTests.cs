using System.Diagnostics;
using System.IO;
using ThermalScope.Services;
using Xunit;

namespace ThermalScope.Tests;

/// <summary>
/// Injects the real helpers into small test games. Needs THERMALSCOPE_TESTGAMES to point at a folder
/// holding testgame.exe (x64 D3D11), testgame32.exe (x86 D3D11) and testgame9_32.exe (x86 D3D9);
/// skipped silently otherwise. Close ThermalScope first: both use the same shared-memory name.
/// </summary>
public class HookIntegrationTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public HookIntegrationTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("testgame.exe")]
    [InlineData("testgame32.exe")]
    [InlineData("testgame9_32.exe")]
    [InlineData("testgame9v_32.exe")] // D3D9 with the game's own vsync on
    public void Locked_pacing_follows_the_display(string exe)
    {
        var folder = Environment.GetEnvironmentVariable("THERMALSCOPE_TESTGAMES");
        if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, exe)))
        {
            return;
        }

        using var game = Process.Start(Path.Combine(folder, exe))!;
        try
        {
            Thread.Sleep(2000);
            using var monitor = new GameFpsMonitor();
            Assert.True(monitor.TryAttach(game.Id, out var error), error);
            monitor.SetTargetFps(60);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!monitor.IsHookReady && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }

            Thread.Sleep(2500);
            var timer = Jitter(monitor, game.Id);
            var hz = monitor.RefreshHz;
            _output.WriteLine($"display {hz} Hz; timer 60: avg {monitor.ReadFps(game.Id)?.Average:0.0} fps, jitter {timer:0.000} ms");
            Assert.True(hz >= 24, "helper did not report the refresh rate");

            monitor.SetPacingLocked(true);
            monitor.SetTargetFps(hz);
            Thread.Sleep(3500);
            var locked = Jitter(monitor, game.Id);
            var avg = monitor.ReadFps(game.Id)!.Value.Average;
            _output.WriteLine($"locked {hz}: avg {avg:0.0} fps, jitter {locked:0.000} ms");
            Assert.InRange(avg, hz - 1.5, hz + 1.5);

            monitor.SetTargetFps(hz / 2);
            Thread.Sleep(3500);
            avg = monitor.ReadFps(game.Id)!.Value.Average;
            _output.WriteLine($"locked {hz / 2}: avg {avg:0.0} fps, jitter {Jitter(monitor, game.Id):0.000} ms");
            Assert.InRange(avg, hz / 2.0 - 1.5, hz / 2.0 + 1.5);
        }
        finally
        {
            game.Kill();
        }
    }

    private static double Jitter(GameFpsMonitor monitor, int pid)
    {
        var frames = monitor.ReadFrameTimes(pid, 120);
        var mean = frames.Average();
        return Math.Sqrt(frames.Sum(v => (v - mean) * (v - mean)) / frames.Length);
    }

    [Theory]
    [InlineData("testgame.exe")]
    [InlineData("testgame32.exe")]
    [InlineData("testgame9_32.exe")]
    public void Limiter_holds_60_fps(string exe)
    {
        var folder = Environment.GetEnvironmentVariable("THERMALSCOPE_TESTGAMES");
        if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, exe)))
        {
            return;
        }

        using var game = Process.Start(Path.Combine(folder, exe))!;
        try
        {
            Thread.Sleep(2000);
            using var monitor = new GameFpsMonitor();
            Assert.True(monitor.TryAttach(game.Id, out var error), error);
            monitor.SetTargetFps(60);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!monitor.IsHookReady && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }

            Assert.True(monitor.IsHookReady, "helper never saw a frame");
            Thread.Sleep(3000);
            var limited = monitor.ReadFps(game.Id);
            Assert.True(limited.HasValue);
            Assert.InRange(limited.Value.Average, 55, 65);

            monitor.SetTargetFps(0);
            Thread.Sleep(3000);
            var free = monitor.ReadFps(game.Id);
            Assert.True(free.HasValue);
            Assert.True(free.Value.Average > 80, $"unlimited FPS was {free.Value.Average}");
        }
        finally
        {
            game.Kill();
        }
    }
}
