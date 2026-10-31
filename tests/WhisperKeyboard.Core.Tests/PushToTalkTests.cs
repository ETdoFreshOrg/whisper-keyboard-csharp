using System.Text.Json;
using Xunit;
using Change = WhisperKeyboard.Core.PushToTalkChordTracker.Change;

namespace WhisperKeyboard.Core.Tests;

public class PushToTalkTests
{
    [Fact]
    public void Submit_chord_alone_submits()
    {
        var t = new PushToTalkChordTracker();

        Assert.Equal(Change.Started, t.Update(submitHeld: true, typeOnlyHeld: false));
        Assert.Equal(PushToTalkMode.Submit, t.Mode);
        Assert.Equal(Change.Stopped, t.Update(false, false));
        Assert.Equal(PushToTalkMode.Submit, t.Mode);
    }

    [Fact]
    public void Adding_the_extra_key_mid_press_makes_it_type_only()
    {
        // Command+Shift goes down first (submit), then Option joins (type-only).
        var t = new PushToTalkChordTracker();

        Assert.Equal(Change.Started, t.Update(true, false));
        Assert.Equal(Change.ModeChanged, t.Update(true, true));
        Assert.Equal(PushToTalkMode.TypeOnly, t.Mode);
    }

    [Fact]
    public void Type_only_sticks_when_the_extra_key_is_released_first()
    {
        var t = new PushToTalkChordTracker();
        t.Update(true, false);
        t.Update(true, true);

        // Option released while Command+Shift are still down: keep recording, still type-only.
        Assert.Equal(Change.None, t.Update(true, false));
        Assert.True(t.IsActive);
        Assert.Equal(Change.Stopped, t.Update(false, false));
        Assert.Equal(PushToTalkMode.TypeOnly, t.Mode);
    }

    [Fact]
    public void Next_press_starts_fresh_in_submit_mode()
    {
        var t = new PushToTalkChordTracker();
        t.Update(true, true);
        t.Update(false, false);

        Assert.Equal(Change.Started, t.Update(true, false));
        Assert.Equal(PushToTalkMode.Submit, t.Mode);
    }

    [Fact]
    public void Disjoint_type_only_chord_starts_type_only()
    {
        var t = new PushToTalkChordTracker();

        Assert.Equal(Change.Started, t.Update(false, true));
        Assert.Equal(PushToTalkMode.TypeOnly, t.Mode);
        Assert.Equal(Change.None, t.Update(false, true));
    }

    [Fact]
    public void Spec_parsing_accepts_aliases_and_normalizes_order()
    {
        Assert.Equal("RightShift+RightCommand+RightOption",
            PushToTalkKeySpec.Normalize("RightAlt + rcmd+RightShift"));
        Assert.Equal(PushToTalkKeySpec.Normalize("RightControl+RightShift"),
            PushToTalkKeySpec.Normalize("rshift+rctrl"));
    }

    [Fact]
    public void Empty_or_unknown_spec_means_off()
    {
        Assert.Empty(PushToTalkKeySpec.Parse(""));
        Assert.Empty(PushToTalkKeySpec.Parse(null));
        Assert.Empty(PushToTalkKeySpec.Parse("Banana"));
    }

    [Fact]
    public void Describe_uses_platform_key_names()
    {
        const string spec = "RightCommand+RightShift+RightOption";
        Assert.Equal("Right Shift + Right Command + Right Option", PushToTalkKeySpec.Describe(spec, windows: false));
        Assert.Equal("Right Shift + Right Win + Right Alt", PushToTalkKeySpec.Describe(spec, windows: true));
    }

    [Fact]
    public void Legacy_device_index_is_read_but_not_written_once_cleared()
    {
        var config = JsonSerializer.Deserialize<Config>("{\"DeviceIndex\": 2}")!;
        Assert.Equal(2, config.DeviceIndex);
        Assert.Equal("", config.DeviceName);

        config.DeviceIndex = null;
        config.DeviceName = "Name’s AirPods";
        var json = JsonSerializer.Serialize(config);
        Assert.DoesNotContain("DeviceIndex", json);
        Assert.Equal("Name’s AirPods", JsonSerializer.Deserialize<Config>(json)!.DeviceName);
    }
}

public class ExitWordTests
{
    private static TextProcessor Processor() => new(new Config());

    [Fact]
    public void Exit_word_is_stripped_and_requests_enter_by_default()
    {
        var (text, shouldEnter) = Processor().ProcessText("send the report over");

        Assert.Equal("Send the report.", text);
        Assert.True(shouldEnter);
    }

    [Fact]
    public void Exit_word_stays_in_the_text_when_detection_is_off()
    {
        // Push-to-talk: the key decides about Enter, so "over" is kept as spoken.
        var (text, shouldEnter) = Processor().ProcessText("send the report over", detectExitWords: false);

        Assert.Equal("Send the report over.", text);
        Assert.False(shouldEnter);
    }
}
