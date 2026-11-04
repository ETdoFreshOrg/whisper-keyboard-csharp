using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace WhisperKeyboard.Tests;

/// <summary>
/// The waveform redraws 20 times a second while recording. It used to replace its bar
/// controls on every redraw, and on macOS each replaced control could be kept alive by
/// an accessibility peer: about 10 GB after two days. Redrawing must not add or remove
/// any controls.
/// </summary>
public class WaveformTests
{
    [AvaloniaFact]
    public void Recording_bar_redraws_without_creating_controls()
    {
        var indicator = new RecordingIndicator();
        Pump(TimeSpan.FromMilliseconds(200)); // let its deferred first show run
        indicator.ShowPushToTalk();
        Pump(TimeSpan.FromMilliseconds(200), indicator.UpdateVolume);

        AssertRedrawsKeepTheSameControls(indicator, indicator.UpdateVolume);
        indicator.HideCompletely();
    }

    [AvaloniaFact]
    public void Toggle_to_talk_bar_redraws_without_creating_controls()
    {
        var indicator = new ToggleToTalkIndicator();
        indicator.ShowRecording();
        Pump(TimeSpan.FromMilliseconds(200), indicator.UpdateVolume);

        AssertRedrawsKeepTheSameControls(indicator, indicator.UpdateVolume);
        indicator.ShowStopped();
    }

    [AvaloniaFact]
    public void Toggle_to_talk_waveform_stops_redrawing_once_hidden()
    {
        var indicator = new ToggleToTalkIndicator();
        indicator.ShowRecording();
        Pump(TimeSpan.FromMilliseconds(200), indicator.UpdateVolume);
        indicator.ShowStopped(); // not pinned: moves off-screen

        var waveform = indicator.GetVisualDescendants().OfType<WaveformView>().Single();
        int updates = waveform.UpdateCount;
        Pump(TimeSpan.FromMilliseconds(300), indicator.UpdateVolume);

        Assert.Equal(updates, waveform.UpdateCount);
    }

    private static void AssertRedrawsKeepTheSameControls(Avalonia.Visual window, Action<double> feedVolume)
    {
        var waveform = window.GetVisualDescendants().OfType<WaveformView>().Single();
        var before = window.GetVisualDescendants().ToHashSet();
        int updates = waveform.UpdateCount;

        Pump(TimeSpan.FromMilliseconds(600), feedVolume);

        Assert.True(waveform.UpdateCount - updates >= 5, $"waveform redrew only {waveform.UpdateCount - updates} times");
        Assert.True(before.SetEquals(window.GetVisualDescendants()),
            "redrawing the waveform added or removed controls");
    }

    /// <summary>
    /// Runs the UI thread's dispatcher loop, timers included, for <paramref name="duration"/>,
    /// feeding a volume level every 10 ms like the microphone does.
    /// </summary>
    private static void Pump(TimeSpan duration, Action<double>? feedVolume = null)
    {
        var random = new Random(7);
        var feeder = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Normal,
            (_, _) => feedVolume?.Invoke(random.NextDouble() * 600));
        feeder.Start();

        var frame = new DispatcherFrame();
        DispatcherTimer.RunOnce(() => frame.Continue = false, duration);
        Dispatcher.UIThread.PushFrame(frame);
        feeder.Stop();
    }
}
