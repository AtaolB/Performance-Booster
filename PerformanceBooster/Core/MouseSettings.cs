namespace PerformanceBooster.Core;

public readonly record struct MouseState(bool Acceleration, int Speed);

// Mouse settings for aiming: "Enhance pointer precision" (acceleration) off and pointer speed 10, the Windows default
// (6/11 on the slider), where one count of the mouse moves the pointer one pixel. Per user; needs no administrator rights.
public static class MouseSettings
{
    public const int RecommendedSpeed = 10;

    public static MouseState? Read()
    {
        var mouse = new int[3];
        uint speed = 0;
        if (!Native.SystemParametersInfo(Native.SPI_GETMOUSE, 0, mouse, 0) || !Native.SystemParametersInfo(Native.SPI_GETMOUSESPEED, 0, ref speed, 0)) return null;
        return new MouseState(mouse[2] != 0, (int)speed);
    }

    public static bool IsRecommended(MouseState state) => !state.Acceleration && state.Speed == RecommendedSpeed;

    // Saved for the user and applied at once (also in the registry, so it stays after a restart).
    public static bool ApplyRecommended() =>
        Native.SystemParametersInfo(Native.SPI_SETMOUSE, 0, [0, 0, 0], Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE)
        && Native.SystemParametersInfo(Native.SPI_SETMOUSESPEED, 0, (IntPtr)RecommendedSpeed, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE);
}
