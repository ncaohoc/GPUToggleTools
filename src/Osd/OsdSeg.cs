// One piece of an OSD line: colored text, or a small graph as wide as its text.

using System.Drawing;

namespace GpuToggle
{
    class OsdSeg
    {
        public string Text;
        public Color Color;
        public float[] Graph;    // when set, a small line graph is drawn over the width of Text instead of the text
        public float[] Graph2;   // optional second series on the same scale
        public Color Color2;
        public float Floor;      // the scale never goes below this, so idle noise stays flat
        public float Fixed;      // when > 0 the scale is exactly 0..Fixed (percentages)
        public OsdSeg(string text, Color color) { Text = text; Color = color; }
        public OsdSeg(float[] graph, int chars, Color color) { Graph = graph; Text = new string(' ', chars); Color = color; }
    }
}
