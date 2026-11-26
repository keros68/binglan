using System.Globalization;
using BingLan.Core.Dock;
using BingLan.Core.Services;

namespace BingLan.Core.Models;

/// <summary>
/// Pure model, layout, label, and drag-merge rules for grouped (tabbed) file boxes.
/// Grouped boxes remain ordinary <see cref="FileBoxState"/> records sharing a <see cref="FileBoxState.GroupId"/>
/// so desktop organisation, launcher/dock targeting, and per-tab rules stay intact.
/// </summary>
public static class FileBoxGroupRules
{
    public const double TabStripWidthDip = 40d;
    public const double StandaloneMinWidthDip = 226d;
    public const double GroupedMinWidthDip = StandaloneMinWidthDip + TabStripWidthDip;
    public const int MergeInsetPixels = 16;
    public const double DefaultDetachOffsetDip = 28d;
    public const double DefaultSplitCascadeStepDip = 32d;

    public static readonly TimeSpan DragSwitchTabDelay = TimeSpan.FromMilliseconds(260);
    public static readonly TimeSpan HoverSwitchTabDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Formats a tab title into compact vertically stacked glyphs (up to 4 text elements)
    /// for the left vertical tab strip, preserving grapheme clusters cleanly.
    /// </summary>
    public static string FormatVerticalTabLabel(string? title)
    {
        var committed = WidgetTitleRules.CommitTitle(title, EditableWidgetTitleKind.FileBox);
        var elements = new List<string>(4);
        var enumerator = StringInfo.GetTextElementEnumerator(committed);
        while (enumerator.MoveNext() && elements.Count < 4)
        {
            var glyph = enumerator.GetTextElement();
            if (string.IsNullOrWhiteSpace(glyph))
            {
                continue;
            }
            elements.Add(glyph);
        }

        if (elements.Count == 0)
        {
            return "盒\n子";
        }

        return string.Join('\n', elements);
    }

    public static string FormatGroupDisplayTitle(IReadOnlyList<FileBoxState> members, Guid? activeBoxId = null)
    {
        if (members is null || members.Count == 0)
        {
            return WidgetTitleRules.DefaultFileBoxTitle;
        }
        if (members.Count == 1)
        {
            return WidgetTitleRules.CommitTitle(members[0].Title, EditableWidgetTitleKind.FileBox);
        }

        var active = members.FirstOrDefault(m => activeBoxId is { } id && m.Id == id)
            ?? members.FirstOrDefault(m => m.IsActiveTab)
            ?? members[0];
        var title = WidgetTitleRules.CommitTitle(active.Title, EditableWidgetTitleKind.FileBox);
        return $"{title}（组合 · {members.Count} 页）";
    }

    public static string FormatCollapsedSummary(int itemCount, int tabCount) =>
        tabCount > 1
            ? $"{itemCount} 项 · 共 {tabCount} 页"
            : $"{itemCount} 项";

    /// <summary>
    /// Returns true when dropping a dragged file box onto <paramref name="targetBounds"/> should
    /// merge them into a grouped tabbed box. Holding Alt suppresses drag-merge for free overlap.
    /// </summary>
    public static bool CanMergeOnDrop(PixelRect draggedBounds, PixelRect targetBounds, bool altHeld)
    {
        if (altHeld || !draggedBounds.HasArea || !targetBounds.HasArea)
        {
            return false;
        }

        var centerX = (draggedBounds.Left + draggedBounds.Right) / 2;
        var centerY = (draggedBounds.Top + draggedBounds.Bottom) / 2;
        var insetX = Math.Min(MergeInsetPixels, Math.Max(0, targetBounds.Width / 4));
        var insetY = Math.Min(MergeInsetPixels, Math.Max(0, targetBounds.Height / 4));

        return centerX >= targetBounds.Left + insetX
            && centerX <= targetBounds.Right - insetX
            && centerY >= targetBounds.Top + insetY
            && centerY <= targetBounds.Bottom - insetY;
    }

    /// <summary>
    /// Normalizes all file box groups: strips degenerate single-member groups back to standalone,
    /// assigns contiguous <see cref="FileBoxState.TabOrder"/>, ensures exactly one active tab per
    /// group, and syncs group-shared properties from the active tab.
    /// </summary>
    public static void NormalizeGroups(IList<FileBoxState> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);

        foreach (var box in boxes)
        {
            if (box.GroupId == Guid.Empty)
            {
                box.GroupId = null;
            }
            if (box.GroupId is null)
            {
                box.TabOrder = 0;
                box.IsActiveTab = false;
                box.HoverSwitchTabs = false;
            }
        }

        var grouped = boxes
            .Where(box => box.GroupId is not null)
            .GroupBy(box => box.GroupId!.Value)
            .ToList();

        foreach (var group in grouped)
        {
            var members = group.ToList();
            if (members.Count < 2)
            {
                var lone = members[0];
                lone.GroupId = null;
                lone.TabOrder = 0;
                lone.IsActiveTab = false;
                lone.HoverSwitchTabs = false;
                continue;
            }

            var ordered = members
                .Select((box, idx) => (Box: box, Index: idx))
                .OrderBy(entry => entry.Box.TabOrder)
                .ThenBy(entry => entry.Index)
                .Select(entry => entry.Box)
                .ToList();

            var active = ordered.FirstOrDefault(box => box.IsActiveTab) ?? ordered[0];
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].TabOrder = i;
                ordered[i].IsActiveTab = ReferenceEquals(ordered[i], active);
            }

            SyncGroupSharedProperties(active, ordered);
        }
    }

    /// <summary>
    /// Copies group-shared window/appearance/behavior fields from <paramref name="source"/> to
    /// every other member in the same group while leaving per-tab fields (Title, Items, ViewMode,
    /// DesktopCategory, CollectExtensions, CollectFolders) untouched.
    /// </summary>
    public static void SyncGroupSharedProperties(FileBoxState source, IEnumerable<FileBoxState> members)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(members);

        foreach (var member in members)
        {
            if (ReferenceEquals(member, source))
            {
                continue;
            }

            member.IsLocked = source.IsLocked;
            member.IsHidden = source.IsHidden;
            member.IsCollapsed = source.IsCollapsed;
            member.ExpandedHeight = source.ExpandedHeight;
            member.AutoCollapse = source.AutoCollapse;
            member.HoverExpand = source.HoverExpand;
            member.AutoCollapseDelaySeconds = source.AutoCollapseDelaySeconds;
            member.HideWhenIdle = source.HideWhenIdle;
            member.IdleHideDelaySeconds = source.IdleHideDelaySeconds;
            member.EdgeDockSide = source.EdgeDockSide;
            member.EdgeDockOffsetDip = source.EdgeDockOffsetDip;
            member.EdgeDockMonitorDeviceName = source.EdgeDockMonitorDeviceName;
            member.HoverSwitchTabs = source.HoverSwitchTabs;
            member.CornerRadius = source.CornerRadius;
            member.ScreenKey = MultiScreenRules.NormalizeScreenKey(source.ScreenKey);
            CopyAppearance(source.Appearance, member.Appearance);
            CopyPlacement(source.Placement, member.Placement);
        }
    }

    /// <summary>
    /// Merges <paramref name="sourceMembers"/> into <paramref name="targetMembers"/> as new tabs,
    /// adopting the target's shared window properties and returning the shared group ID.
    /// </summary>
    public static Guid Merge(
        IReadOnlyList<FileBoxState> sourceMembers,
        IReadOnlyList<FileBoxState> targetMembers,
        bool activateFirstMergedSource = true)
    {
        ArgumentNullException.ThrowIfNull(sourceMembers);
        ArgumentNullException.ThrowIfNull(targetMembers);
        if (sourceMembers.Count == 0 || targetMembers.Count == 0)
        {
            throw new ArgumentException("Both source and target must contain at least one file box.");
        }

        var targetLead = targetMembers.FirstOrDefault(m => m.IsActiveTab) ?? targetMembers[0];
        var groupId = targetLead.GroupId is { } existing && existing != Guid.Empty
            ? existing
            : Guid.NewGuid();

        var combined = new List<FileBoxState>(targetMembers.Count + sourceMembers.Count);
        var seen = new HashSet<Guid>();
        foreach (var member in targetMembers.OrderBy(m => m.TabOrder))
        {
            if (seen.Add(member.Id))
            {
                combined.Add(member);
            }
        }
        FileBoxState? firstAdded = null;
        foreach (var member in sourceMembers.OrderBy(m => m.TabOrder))
        {
            if (seen.Add(member.Id))
            {
                firstAdded ??= member;
                combined.Add(member);
            }
        }

        var active = activateFirstMergedSource && firstAdded is not null
            ? firstAdded
            : targetLead;

        SyncGroupSharedProperties(targetLead, combined);
        for (var i = 0; i < combined.Count; i++)
        {
            combined[i].GroupId = groupId;
            combined[i].TabOrder = i;
            combined[i].IsActiveTab = ReferenceEquals(combined[i], active);
        }

        return groupId;
    }

    /// <summary>
    /// Detaches a single <paramref name="tab"/> from <paramref name="groupMembers"/> back into a
    /// standalone box placed slightly offset from the group. Returns the remaining group members.
    /// If only one member remains, it automatically dissolves back into a standalone box.
    /// </summary>
    public static List<FileBoxState> Detach(
        FileBoxState tab,
        IReadOnlyList<FileBoxState> groupMembers,
        double offsetDip = DefaultDetachOffsetDip)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(groupMembers);

        var ordered = groupMembers
            .OrderBy(m => m.TabOrder)
            .ToList();
        var detachedIndex = ordered.FindIndex(m => m.Id == tab.Id);
        if (detachedIndex < 0)
        {
            return ordered;
        }

        var host = ordered.FirstOrDefault(m => m.IsActiveTab) ?? ordered[0];
        var remaining = ordered.Where(m => m.Id != tab.Id).ToList();

        // Snapshot the group's shared state BEFORE mutating the detached tab, because when the
        // detached tab is the active tab, `host` and `tab` are the same object reference.
        var savedPlacement = new WindowPlacement();
        CopyPlacement(host.Placement, savedPlacement);
        if (remaining.Count == 1)
        {
            var lone = remaining[0];
            SyncGroupSharedProperties(host, remaining);
            CopyPlacement(savedPlacement, lone.Placement);
            lone.GroupId = null;
            lone.TabOrder = 0;
            lone.IsActiveTab = false;
            lone.HoverSwitchTabs = false;
        }
        else if (remaining.Count > 1)
        {
            SyncGroupSharedProperties(host, remaining);
            var nextActive = remaining.FirstOrDefault(m => m.IsActiveTab)
                ?? remaining[Math.Min(detachedIndex, remaining.Count - 1)];
            for (var i = 0; i < remaining.Count; i++)
            {
                remaining[i].TabOrder = i;
                remaining[i].IsActiveTab = ReferenceEquals(remaining[i], nextActive);
            }
        }

        // Give the detached box a clean standalone placement offset from the group host.
        CopyPlacement(savedPlacement, tab.Placement);
        tab.ScreenKey = MultiScreenRules.NormalizeScreenKey(host.ScreenKey);
        tab.Placement.Left += offsetDip;
        tab.Placement.Top += offsetDip;
        tab.Placement.DisplayLayouts = [];
        if (tab.IsCollapsed)
        {
            tab.IsCollapsed = false;
            tab.Placement.Height = Math.Max(166d, tab.ExpandedHeight > 0 ? tab.ExpandedHeight : 306d);
        }
        if (tab.Placement.MonitorLocal is { } detachedLocal)
        {
            UpdateMonitorLocalAfterStandaloneOffset(
                detachedLocal,
                offsetDip,
                offsetDip,
                tab.Placement.Width,
                tab.Placement.Height);
        }
        tab.EdgeDockSide = null;
        tab.EdgeDockOffsetDip = null;
        tab.EdgeDockMonitorDeviceName = null;
        tab.GroupId = null;
        tab.TabOrder = 0;
        tab.IsActiveTab = false;
        tab.HoverSwitchTabs = false;

        return remaining;
    }

    /// <summary>
    /// Splits all members of a grouped box back into independent standalone boxes, cascading
    /// subsequent boxes so they do not completely overlap one another.
    /// </summary>
    public static List<FileBoxState> SplitAll(
        IReadOnlyList<FileBoxState> groupMembers,
        double cascadeStepDip = DefaultSplitCascadeStepDip)
    {
        ArgumentNullException.ThrowIfNull(groupMembers);
        var ordered = groupMembers.OrderBy(m => m.TabOrder).ToList();
        if (ordered.Count == 0)
        {
            return ordered;
        }

        var host = ordered.FirstOrDefault(m => m.IsActiveTab) ?? ordered[0];
        var basePlacement = new WindowPlacement();
        CopyPlacement(host.Placement, basePlacement);

        for (var i = 0; i < ordered.Count; i++)
        {
            var box = ordered[i];
            CopyPlacement(basePlacement, box.Placement);
            box.ScreenKey = MultiScreenRules.NormalizeScreenKey(host.ScreenKey);
            if (i > 0)
            {
                box.Placement.Left += i * cascadeStepDip;
                box.Placement.Top += i * cascadeStepDip;
                if (box.Placement.MonitorLocal is { } splitLocal)
                {
                    UpdateMonitorLocalAfterStandaloneOffset(
                        splitLocal,
                        i * cascadeStepDip,
                        i * cascadeStepDip,
                        box.Placement.Width,
                        box.Placement.Height);
                }
                box.Placement.DisplayLayouts = [];
                box.EdgeDockSide = null;
                box.EdgeDockOffsetDip = null;
                box.EdgeDockMonitorDeviceName = null;
            }
            box.GroupId = null;
            box.TabOrder = 0;
            box.IsActiveTab = false;
            box.HoverSwitchTabs = false;
        }

        return ordered;
    }

    /// <summary>
    /// Moves a tab up (negative offset) or down (positive offset) inside its group.
    /// </summary>
    public static bool MoveTab(IList<FileBoxState> groupMembers, Guid boxId, int offset)
    {
        ArgumentNullException.ThrowIfNull(groupMembers);
        if (offset == 0 || groupMembers.Count < 2)
        {
            return false;
        }

        var ordered = groupMembers.OrderBy(m => m.TabOrder).ToList();
        var index = ordered.FindIndex(m => m.Id == boxId);
        if (index < 0)
        {
            return false;
        }

        var target = Math.Clamp(index + offset, 0, ordered.Count - 1);
        if (target == index)
        {
            return false;
        }

        var moved = ordered[index];
        ordered.RemoveAt(index);
        ordered.Insert(target, moved);
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].TabOrder = i;
        }
        return true;
    }

    /// <summary>
    /// Groups all file boxes into host buckets preserving appearance order: standalone boxes form
    /// 1-element lists, grouped boxes form multi-element lists sorted by <see cref="FileBoxState.TabOrder"/>.
    /// </summary>
    public static List<List<FileBoxState>> PartitionIntoHosts(IReadOnlyList<FileBoxState> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        var result = new List<List<FileBoxState>>();
        var emittedGroups = new HashSet<Guid>();
        foreach (var box in boxes)
        {
            if (box.GroupId is not { } gid || gid == Guid.Empty)
            {
                result.Add([box]);
                continue;
            }

            if (!emittedGroups.Add(gid))
            {
                continue;
            }

            var members = boxes
                .Where(candidate => candidate.GroupId == gid)
                .OrderBy(candidate => candidate.TabOrder)
                .ToList();
            if (members.Count > 0)
            {
                result.Add(members);
            }
        }

        return result;
    }

    private static void CopyAppearance(WidgetAppearanceState source, WidgetAppearanceState target)
    {
        target.BackgroundColor = source.BackgroundColor;
        target.BackgroundOpacity = source.BackgroundOpacity;
        target.HeaderColor = source.HeaderColor;
        target.HeaderOpacity = source.HeaderOpacity;
        target.TextColor = source.TextColor;
        target.TitleFontFamily = source.TitleFontFamily;
        target.TitleFontBold = source.TitleFontBold;
        target.TitleFontItalic = source.TitleFontItalic;
    }

    private static void CopyPlacement(WindowPlacement source, WindowPlacement target)
    {
        target.Left = source.Left;
        target.Top = source.Top;
        target.Width = source.Width;
        target.Height = source.Height;
        target.DisplayLayouts = source.DisplayLayouts
            .Select(layout => new DisplayLayoutPlacement
            {
                Layout = layout.Layout,
                Left = layout.Left,
                Top = layout.Top,
                Right = layout.Right,
                Bottom = layout.Bottom
            })
            .ToList();
        target.MonitorLocal = source.MonitorLocal is null
            ? null
            : new MonitorLocalPlacement
            {
                LocalLeftDip = source.MonitorLocal.LocalLeftDip,
                LocalTopDip = source.MonitorLocal.LocalTopDip,
                WidthDip = source.MonitorLocal.WidthDip,
                HeightDip = source.MonitorLocal.HeightDip,
                AnchorX = source.MonitorLocal.AnchorX,
                AnchorY = source.MonitorLocal.AnchorY,
                SourceWorkWidthDip = source.MonitorLocal.SourceWorkWidthDip,
                SourceWorkHeightDip = source.MonitorLocal.SourceWorkHeightDip
            };
    }

    private static void UpdateMonitorLocalAfterStandaloneOffset(
        MonitorLocalPlacement local,
        double offsetX,
        double offsetY,
        double finalWidthDip,
        double finalHeightDip)
    {
        local.LocalLeftDip = Math.Max(0d, local.LocalLeftDip + offsetX);
        local.LocalTopDip = Math.Max(0d, local.LocalTopDip + offsetY);
        if (finalWidthDip > 0d)
        {
            local.WidthDip = finalWidthDip;
        }
        if (finalHeightDip > 0d)
        {
            local.HeightDip = finalHeightDip;
        }
        if (local.SourceWorkWidthDip > 0d)
        {
            var freeX = Math.Max(0d, local.SourceWorkWidthDip - local.WidthDip);
            local.LocalLeftDip = Math.Min(local.LocalLeftDip, freeX);
            local.AnchorX = freeX > 0d ? Math.Clamp(local.LocalLeftDip / freeX, 0d, 1d) : 0d;
        }
        if (local.SourceWorkHeightDip > 0d)
        {
            var freeY = Math.Max(0d, local.SourceWorkHeightDip - local.HeightDip);
            local.LocalTopDip = Math.Min(local.LocalTopDip, freeY);
            local.AnchorY = freeY > 0d ? Math.Clamp(local.LocalTopDip / freeY, 0d, 1d) : 0d;
        }
    }
}
