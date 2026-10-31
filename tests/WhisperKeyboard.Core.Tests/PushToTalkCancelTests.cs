using Xunit;
using Change = WhisperKeyboard.Core.PushToTalkChordTracker.Change;
using Decision = WhisperKeyboard.Core.CancelKeyFilter.Decision;

namespace WhisperKeyboard.Core.Tests;

public class PushToTalkCancelTests
{
    [Fact]
    public void Cancel_mid_press_reports_nothing_when_the_keys_come_up()
    {
        var t = new PushToTalkChordTracker();
        Assert.Equal(Change.Started, t.Update(true, false));

        Assert.True(t.Cancel());
        Assert.True(t.IsCancelled);
        // Adding the type-only key or releasing must not start typing anything.
        Assert.Equal(Change.None, t.Update(true, true));
        Assert.Equal(Change.None, t.Update(true, false));
        Assert.Equal(Change.None, t.Update(false, false));
        Assert.False(t.IsActive);
        Assert.False(t.IsCancelled);
    }

    [Fact]
    public void Next_press_after_a_cancel_starts_fresh()
    {
        var t = new PushToTalkChordTracker();
        t.Update(false, true);
        t.Cancel();
        t.Update(false, false);

        Assert.Equal(Change.Started, t.Update(true, false));
        Assert.Equal(PushToTalkMode.Submit, t.Mode);
        Assert.Equal(Change.Stopped, t.Update(false, false));
    }

    [Fact]
    public void Keys_must_all_come_up_before_recording_again()
    {
        // Cancel, then the type-only chord breaks but the submit chord stays held:
        // still the same cancelled press, nothing starts.
        var t = new PushToTalkChordTracker();
        t.Update(true, true);
        t.Cancel();
        Assert.Equal(Change.None, t.Update(true, false));
        Assert.True(t.IsActive);
        Assert.True(t.IsCancelled);
    }

    [Fact]
    public void Cancel_with_no_press_does_nothing()
    {
        var t = new PushToTalkChordTracker();
        Assert.False(t.Cancel());
        Assert.Equal(Change.Started, t.Update(true, false));
        Assert.True(t.Cancel());
        Assert.False(t.Cancel()); // a second Escape is a no-op
    }

    [Fact]
    public void Escape_passes_through_when_nothing_is_recording()
    {
        var f = new CancelKeyFilter();
        Assert.Equal(Decision.PassThrough, f.KeyDown(cancelArmed: false, nowMs: 0));
        Assert.Equal(Decision.PassThrough, f.KeyUp());
    }

    [Fact]
    public void Escape_while_recording_cancels_and_its_repeats_and_key_up_are_swallowed()
    {
        var f = new CancelKeyFilter();
        Assert.Equal(Decision.Cancel, f.KeyDown(cancelArmed: true, nowMs: 1000));
        // Key repeats arrive after the app has disarmed: still the same press.
        Assert.Equal(Decision.Swallow, f.KeyDown(cancelArmed: false, nowMs: 1500));
        Assert.Equal(Decision.Swallow, f.KeyDown(cancelArmed: false, nowMs: 1530));
        Assert.Equal(Decision.Swallow, f.KeyUp());

        // The next Escape, with nothing recording, reaches the app as usual.
        Assert.Equal(Decision.PassThrough, f.KeyDown(cancelArmed: false, nowMs: 3000));
        Assert.Equal(Decision.PassThrough, f.KeyUp());
    }

    [Fact]
    public void A_lost_key_up_does_not_leave_Escape_swallowed()
    {
        var f = new CancelKeyFilter();
        Assert.Equal(Decision.Cancel, f.KeyDown(true, nowMs: 0));
        // No key-up ever came. Much later, a fresh Escape must pass through.
        Assert.Equal(Decision.PassThrough, f.KeyDown(false, nowMs: CancelKeyFilter.RepeatWindowMs + 500));
        Assert.Equal(Decision.PassThrough, f.KeyUp());
    }

    [Fact]
    public void Defaults_are_right_shift_option_and_the_same_plus_command()
    {
        var config = new Config();
        Assert.Equal("RightShift+RightOption", PushToTalkKeySpec.Normalize(config.PushToTalkKeys));
        Assert.Equal("RightShift+RightCommand+RightOption", PushToTalkKeySpec.Normalize(config.PushToTalkNoEnterKeys));
        Assert.Equal("Right Shift + Right Option", PushToTalkKeySpec.Describe(config.PushToTalkKeys, windows: false));
        Assert.Equal("Right Shift + Right Alt", PushToTalkKeySpec.Describe(config.PushToTalkKeys, windows: true));
        Assert.Equal("Right Shift + Right Win + Right Alt", PushToTalkKeySpec.Describe(config.PushToTalkNoEnterKeys, windows: true));
    }

    [Fact]
    public void No_global_hotkey_is_on_by_default()
    {
        // In particular no hands-free toggle: Cmd/Ctrl+Shift+R is the browser's hard
        // reload, and a reload would start typing whatever the mic hears.
        var config = new Config();
        Assert.Equal("", config.ToggleRecordingHotkey);
        Assert.Equal("", config.PauseResumeHotkey);
        Assert.Equal("", config.OpenSettingsHotkey);
        Assert.Equal("", config.LongRecordHotkey);
    }

    [Fact]
    public void A_saved_config_without_hotkeys_loads_with_none()
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<Config>("{\"PushToTalkKeys\":\"RightControl\"}")!;
        Assert.Equal("", config.ToggleRecordingHotkey);
        Assert.Equal("RightControl", config.PushToTalkKeys); // saved values are kept
    }

    [Fact]
    public void Default_type_only_chord_contains_the_submit_chord()
    {
        // So pressing Shift+Option starts recording right away and adding Command makes it type-only.
        var config = new Config();
        var submit = PushToTalkKeySpec.Parse(config.PushToTalkKeys);
        var typeOnly = PushToTalkKeySpec.Parse(config.PushToTalkNoEnterKeys);
        Assert.Subset(new HashSet<string>(typeOnly), new HashSet<string>(submit));
        Assert.Equal(submit.Length + 1, typeOnly.Length);
    }
}
