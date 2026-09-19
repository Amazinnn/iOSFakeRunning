using System.Windows;
using System.Windows.Media;

namespace iOSFakeRun;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
public partial class App
{
    public App()
    {
        // Software rendering, always. The machine this build runs on spent a day with its WPF
        // hardware pipeline wedged: every WPF app — the upstream original included — drew a blank
        // client area while UIA still saw a live element tree, and forcing HKCU
        // DisableHWAcceleration=1 made the original render again instantly. This app's visuals are
        // a few dozen 256px tiles and a handful of polylines invalidated once or twice a second, so
        // the CPU rasteriser covers them comfortably and a bad GPU driver state cannot blank the
        // window again.
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
    }
}
