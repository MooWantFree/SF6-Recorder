namespace Sf6Prototype;

public static class ViewportMath
{
    public static double CenterOffset(double itemMiddle, double viewportHeight, double scrollableHeight) =>
        Math.Clamp(itemMiddle - viewportHeight / 2, 0, Math.Max(0, scrollableHeight));

    public static double HistoryOffset(double itemTop, double rowHeight, double viewportHeight, double scrollableHeight, bool towardNewer=false)
    {
        var topSpace=towardNewer ? Math.Max(0,viewportHeight-2*rowHeight) : Math.Clamp(viewportHeight-rowHeight,0,rowHeight);
        return Math.Clamp(itemTop-topSpace,0,Math.Max(0,scrollableHeight));
    }
}
