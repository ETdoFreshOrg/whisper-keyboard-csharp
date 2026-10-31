using WhisperKeyboard.Core.TextInsertion;
using Xunit;

namespace WhisperKeyboard.Core.Tests;

public class LandingCheckTests
{
    private static FieldSnapshot F(string? text, string owner = "chrome") => new(owner, text);

    [Fact]
    public void Text_appended_to_field_has_landed()
    {
        Assert.Equal(LandingVerdict.Landed, LandingCheck.Evaluate(F("Hi "), F("Hi hello world."), "hello world."));
    }

    [Fact]
    public void Unchanged_field_has_not_landed()
    {
        Assert.Equal(LandingVerdict.NotLanded, LandingCheck.Evaluate(F("Hi"), F("Hi"), "hello world."));
    }

    [Fact]
    public void Empty_field_that_stays_empty_has_not_landed()
    {
        Assert.Equal(LandingVerdict.NotLanded, LandingCheck.Evaluate(F(""), F(""), "hello"));
    }

    [Fact]
    public void Browser_whitespace_changes_still_count_as_landed()
    {
        // contenteditable turns edge spaces into non-breaking spaces and CRLF into LF
        Assert.Equal(LandingVerdict.Landed,
            LandingCheck.Evaluate(F(""), F("Line one\nLine two "), "Line one\r\nLine two "));
    }

    [Fact]
    public void Second_copy_of_text_already_in_field_has_landed()
    {
        Assert.Equal(LandingVerdict.Landed, LandingCheck.Evaluate(F("Okay."), F("Okay. Okay."), "Okay."));
    }

    [Fact]
    public void Unreadable_field_is_unknown()
    {
        Assert.Equal(LandingVerdict.Unknown, LandingCheck.Evaluate(F(null), F(null), "hello"));
        Assert.Equal(LandingVerdict.Unknown, LandingCheck.Evaluate(F("x"), null, "hello"));
    }

    [Fact]
    public void Focus_moving_to_another_app_is_unknown()
    {
        Assert.Equal(LandingVerdict.Unknown, LandingCheck.Evaluate(F("x", "chrome"), F("x", "slack"), "hello"));
    }

    [Fact]
    public void Field_changed_some_other_way_is_unknown_not_a_miss()
    {
        // Partial typing or an autocorrect: don't call it a miss, or a retry would double it.
        Assert.Equal(LandingVerdict.Unknown, LandingCheck.Evaluate(F(""), F("hello wor"), "hello world"));
    }

    [Fact]
    public void Unknown_before_but_text_visible_after_has_landed()
    {
        Assert.Equal(LandingVerdict.Landed, LandingCheck.Evaluate(F(null), F("hello world"), "hello world"));
    }

    [Theory]
    [InlineData("  a \t b \r\n c  ", "a b c")]
    [InlineData("a​b", "ab")]
    [InlineData(" x ", "x")]
    public void Normalize_folds_browser_whitespace(string input, string expected)
    {
        Assert.Equal(expected, LandingCheck.Normalize(input));
    }
}

public class VerifiedTextInserterTests
{
    /// <summary>A pretend text box: the backend writes into it when a delivery "works".</summary>
    private sealed class FakeField : IFocusedFieldProbe, IInsertionBackend
    {
        public string Text = "";
        public bool Readable = true;
        public string Owner = "chrome";
        public Queue<bool> Deliveries = new(); // per keystroke send: does it arrive? (default yes)
        public bool ClipboardWorks = true;
        public List<string> Calls = new();

        public Task<FieldSnapshot?> CaptureAsync() =>
            Task.FromResult<FieldSnapshot?>(new FieldSnapshot(Owner, Readable ? Text : null));

        public Task WaitForModifiersReleasedAsync() { Calls.Add("wait"); return Task.CompletedTask; }

        public Task<bool> PasteAsync(string text)
        {
            if (!ClipboardWorks) { Calls.Add("paste-refused"); return Task.FromResult(false); }
            Calls.Add("paste");
            Deliver(text);
            return Task.FromResult(true);
        }

        public Task TypeAsync(string text) { Calls.Add("type"); Deliver(text); return Task.CompletedTask; }

        private void Deliver(string text)
        {
            if (Deliveries.Count == 0 || Deliveries.Dequeue()) Text += text;
        }
    }

    private static VerifiedTextInserter Make(FakeField f, bool paste = true, int retries = 2) =>
        new(f, f, delay: _ => Task.CompletedTask)
        {
            PasteMode = paste,
            RetryCount = retries,
            VerifyTimeout = TimeSpan.FromMilliseconds(300),
            PollInterval = TimeSpan.FromMilliseconds(100),
        };

    [Fact]
    public void Plan_pastes_twice_then_types()
    {
        Assert.Equal(new[] { InsertMethod.Paste, InsertMethod.Paste, InsertMethod.Type }, VerifiedTextInserter.Plan(true, 2));
        Assert.Equal(new[] { InsertMethod.Type, InsertMethod.Type }, VerifiedTextInserter.Plan(false, 1));
        Assert.Equal(new[] { InsertMethod.Paste }, VerifiedTextInserter.Plan(true, 0));
    }

    [Fact]
    public async Task Paste_that_lands_is_sent_once()
    {
        var f = new FakeField { Text = "Hi " };
        var outcome = await Make(f).InsertAsync("there.");
        Assert.Equal(LandingVerdict.Landed, outcome.Verdict);
        Assert.Equal("Hi there.", f.Text);
        Assert.Equal(new[] { "wait", "paste" }, f.Calls);
    }

    [Fact]
    public async Task Missed_paste_is_retried_and_lands_once()
    {
        var f = new FakeField();
        f.Deliveries.Enqueue(false); // first Ctrl+V goes nowhere
        var outcome = await Make(f).InsertAsync("hello");
        Assert.Equal(LandingVerdict.Landed, outcome.Verdict);
        Assert.Equal("hello", f.Text);
        Assert.Equal(new[] { InsertMethod.Paste, InsertMethod.Paste }, outcome.Attempts);
    }

    [Fact]
    public async Task Two_missed_pastes_fall_back_to_typing()
    {
        var f = new FakeField();
        f.Deliveries.Enqueue(false);
        f.Deliveries.Enqueue(false);
        var outcome = await Make(f).InsertAsync("hello");
        Assert.Equal(LandingVerdict.Landed, outcome.Verdict);
        Assert.Equal("hello", f.Text);
        Assert.Equal(new[] { "wait", "paste", "wait", "paste", "wait", "type" }, f.Calls);
    }

    [Fact]
    public async Task Every_attempt_missing_reports_failure()
    {
        var f = new FakeField();
        for (int i = 0; i < 3; i++) f.Deliveries.Enqueue(false);
        var outcome = await Make(f).InsertAsync("hello");
        Assert.True(outcome.Failed);
        Assert.Equal(3, outcome.Attempts.Count);
        Assert.Equal("", f.Text);
    }

    [Fact]
    public async Task Clipboard_refusing_the_text_types_it_instead_of_pasting_stale_content()
    {
        var f = new FakeField { ClipboardWorks = false };
        var outcome = await Make(f).InsertAsync("hello");
        Assert.Equal(LandingVerdict.Landed, outcome.Verdict);
        Assert.Equal(new[] { "wait", "paste-refused", "type" }, f.Calls);
        Assert.Equal(new[] { InsertMethod.Type }, outcome.Attempts);
    }

    [Fact]
    public async Task Unreadable_field_is_never_retried()
    {
        var f = new FakeField { Readable = false };
        f.Deliveries.Enqueue(false);
        var outcome = await Make(f).InsertAsync("hello");
        Assert.Equal(LandingVerdict.Unknown, outcome.Verdict);
        Assert.Single(outcome.Attempts);
        Assert.False(outcome.Failed);
    }

    [Fact]
    public async Task Verification_off_sends_once_without_reading()
    {
        var f = new FakeField();
        f.Deliveries.Enqueue(false);
        var inserter = Make(f);
        inserter.Verify = false;
        var outcome = await inserter.InsertAsync("hello");
        Assert.Single(outcome.Attempts);
        Assert.Equal(LandingVerdict.Unknown, outcome.Verdict);
    }

    [Fact]
    public async Task Typing_mode_retries_typing()
    {
        var f = new FakeField();
        f.Deliveries.Enqueue(false);
        var outcome = await Make(f, paste: false).InsertAsync("hello");
        Assert.Equal(LandingVerdict.Landed, outcome.Verdict);
        Assert.Equal(new[] { InsertMethod.Type, InsertMethod.Type }, outcome.Attempts);
    }

    [Fact]
    public async Task Probe_exception_counts_as_unreadable()
    {
        var inserter = new VerifiedTextInserter(new FakeField(), new ThrowingProbe(), delay: _ => Task.CompletedTask);
        var outcome = await inserter.InsertAsync("hello");
        Assert.Equal(LandingVerdict.Unknown, outcome.Verdict);
        Assert.Single(outcome.Attempts);
    }

    private sealed class ThrowingProbe : IFocusedFieldProbe
    {
        public Task<FieldSnapshot?> CaptureAsync() => throw new InvalidOperationException("no UIA");
    }
}
