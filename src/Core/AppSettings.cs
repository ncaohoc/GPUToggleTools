// Where the app keeps its own settings (theme, OSD): HKCU/Software/GPUToggle.

namespace GpuToggle
{
    static class AppSettings
    {
        public static readonly string KeyPath = Txt.RegPath("Software", "GPUToggle");
    }
}
