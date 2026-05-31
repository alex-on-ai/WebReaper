using WebReaper.Core.Loaders.Abstract;

namespace WebReaper.Core.Loaders.Concrete;

/// <summary>
/// The no-op <see cref="IClimbObserver"/> (ADR-0085): the default when no observer
/// is wired, so the climb has zero observation overhead and unchanged behaviour.
/// The same null-object idiom as <c>NullPageCache</c> and <c>NullActionResolver</c>.
/// </summary>
public sealed class NullClimbObserver : IClimbObserver
{
    /// <summary>The shared instance.</summary>
    public static readonly NullClimbObserver Instance = new();

    private NullClimbObserver() { }

    /// <inheritdoc />
    public void OnStep(ClimbStep step) { }
}
