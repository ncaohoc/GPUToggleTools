// Shared string constants. Source files stay ASCII and backslash-free (see README),
// so quotes, newlines and backslashes come from here.

using System;
using System.Globalization;

namespace GpuToggle
{
    static class Txt
    {
        public static readonly string Q = ((char)34).ToString();
        public static readonly string NL = Environment.NewLine;
        public static readonly char BS = (char)92;
        public const string NA = "-";
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Registry path from its parts, e.g. RegPath("Software", "GPUToggle").
        public static string RegPath(params string[] parts) { return string.Join(BS.ToString(), parts); }
    }
}
