namespace Heresy.Render.Configuration;

public enum OutputFilterType
{
    None = 0,
    LowPass = 1,
    HighPass = 2,
    /// <summary>Independent inline reusable Filter chain owned by output settings.</summary>
    Chain = 3,
}
