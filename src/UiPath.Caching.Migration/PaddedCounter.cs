using System.Runtime.InteropServices;

namespace UiPath.Caching.Migration;

[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct PaddedCounter
{
    [FieldOffset(64)]
    public int Value;
}
