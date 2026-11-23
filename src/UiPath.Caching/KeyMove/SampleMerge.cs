namespace UiPath.Caching.KeyMove;

internal static class SampleMerge
{
    public static ControlSample? Highest(ControlSample? src, ControlSample? dst)
    {
        if (src is null || dst is null)
        {
            return src ?? dst;
        }

        if (src.Ep > dst.Ep)
        {
            return src;
        }

        if (dst.Ep > src.Ep)
        {
            return dst;
        }

        var agree = src.Id == dst.Id && src.State == dst.State && src.Direction == dst.Direction && !dst.Conflicting;
        return dst with
        {
            Durable = src.Durable || dst.Durable,
            Conflicting = !agree,
            NPop = src.NPop,
            SrcHome = true,
            SrcDurable = src.Durable,
        };
    }
}
