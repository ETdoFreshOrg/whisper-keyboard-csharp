using System.Drawing;

namespace WhisperKeyboard.Core.TextInsertion;

/// <summary>
/// Apps that draw their whole UI on a canvas (a Rust/WebAssembly app, a game engine)
/// still need a real text field to receive keystrokes and pastes, so they lay one over
/// the canvas, take whatever arrives in it and empty it straight away. The text then
/// only exists in the drawing, so the field reads empty before and after an insert
/// whether or not the text arrived. It can't prove a miss, and retrying there would
/// insert the text again on every attempt.
/// </summary>
public static class CanvasTextField
{
    /// <summary>True when one of <paramref name="images"/> (a canvas shows up to
    /// accessibility as an image) covers the whole field and is bigger than it.</summary>
    public static bool IsOverCanvas(RectangleF field, IEnumerable<RectangleF> images) =>
        images.Any(image => image.Contains(field) &&
                            image.Width * image.Height > field.Width * field.Height);
}
