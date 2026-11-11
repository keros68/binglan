namespace BingLan.Core.Dock;

/// <summary>
/// Explorer can keep an older rectangle for an AppBar — seen after moving from a laptop
/// screen to an external monitor with a different scale — while the bar's own strip is
/// unchanged, so a bar that skips unchanged strips would never correct it. The bar
/// compares the monitor's work-area edge with the inner edge of its strip and submits
/// again on a mismatch. Each distinct work-area edge is answered once: another bar
/// stacked beside this one keeps the gap and is not answered again.
/// </summary>
public sealed class AppBarWorkAreaCheck
{
    private int? _answeredEdge;

    public bool NeedsResubmit(int workAreaEdge, int stripInnerEdge)
    {
        if (workAreaEdge == stripInnerEdge)
        {
            _answeredEdge = null;
            return false;
        }

        if (_answeredEdge == workAreaEdge)
        {
            return false;
        }

        _answeredEdge = workAreaEdge;
        return true;
    }
}
