using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace WhisperKeyboard;

/// <summary>
/// The volume bars in the recording indicators. Draws them in <see cref="Render"/>
/// rather than as one child control per bar.
/// </summary>
/// <remarks>
/// The indicators used to clear a Canvas and add a new Rectangle for every bar on every
/// 50 ms tick. On macOS, once an accessibility client has read the window (window
/// managers, automation and dictation tools do), Avalonia keeps an automation peer for
/// each of those controls, and the peer keeps the removed Rectangle alive. That came to
/// about 87 KB per tick, or roughly 10 GB after two days of push-to-talk. One control that
/// draws its own bars doesn't add or remove anything per frame, so there's nothing to retain.
/// </remarks>
public sealed class WaveformView : Control
{
    private static readonly IPen CenterLinePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(40, 40, 40)), 1);

    // One brush per distinct bar colour. Colours come from bytes, so this stays small.
    private readonly Dictionary<Color, IImmutableSolidColorBrush> _brushes = new();

    private double[] _levels = Array.Empty<double>();
    private int _oldestIndex;
    private bool _isActive;

    /// <summary>Bar colour for a volume normalised to 0-1, while active (recording) or not.</summary>
    public Func<double, bool, Color> BarColor { get; set; } = (_, _) => Colors.Gray;

    /// <summary>Space kept free above and below the tallest bar, in total.</summary>
    public double VerticalInset { get; set; } = 4;

    /// <summary>Draw a thin horizontal line through the middle.</summary>
    public bool ShowCenterLine { get; set; }

    /// <summary>How many times <see cref="Update"/> has run (lets tests see the redraw timer tick).</summary>
    internal int UpdateCount { get; private set; }

    /// <summary>
    /// Shows <paramref name="levels"/> as a ring buffer whose oldest entry is at
    /// <paramref name="oldestIndex"/>. The array is read at render time, not copied.
    /// </summary>
    public void Update(double[] levels, int oldestIndex, bool isActive)
    {
        _levels = levels;
        _oldestIndex = oldestIndex;
        _isActive = isActive;
        UpdateCount++;
        InvalidateVisual();
    }

    // Purely decorative: keep it out of the accessibility tree.
    protected override AutomationPeer OnCreateAutomationPeer() => new NoneAutomationPeer(this);

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        int count = _levels.Length;
        if (width <= 0 || height <= 0 || count == 0) return;

        var barWidth = width / count;
        var centerY = height / 2;

        for (int i = 0; i < count; i++)
        {
            double vol = _levels[(_oldestIndex + i) % count];
            double normalizedVolume = Math.Min(1.0, vol / 400.0);
            double barHeight = Math.Max(2, normalizedVolume * (height - VerticalInset));

            context.FillRectangle(
                BrushFor(BarColor(normalizedVolume, _isActive)),
                new Rect(i * barWidth, centerY - barHeight / 2, Math.Max(1, barWidth - 1), barHeight));
        }

        if (ShowCenterLine)
        {
            context.DrawLine(CenterLinePen, new Point(0, centerY), new Point(width, centerY));
        }
    }

    private IImmutableSolidColorBrush BrushFor(Color color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = new ImmutableSolidColorBrush(color);
            _brushes[color] = brush;
        }
        return brush;
    }
}
