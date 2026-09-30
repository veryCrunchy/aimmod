using System.Net;
using AimMod.Desktop.Visuals;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class UiInfrastructureTests
{
    [Test]
    public void DebouncerCoalescesAnEditBurstIntoOneRun()
    {
        var runs = new List<string>();
        var debouncer = new QueryDebouncer(runs.Add, 250);
        debouncer.Submit("a", 0);
        debouncer.Submit("ab", 100);
        debouncer.Submit("abc", 200);
        debouncer.Update(300);
        Assert.That(runs, Is.Empty, "The last edit restarts the delay.");
        debouncer.Update(450);
        debouncer.Update(900);
        Assert.That(runs, Is.EqualTo(new[] { "abc" }));
    }

    [Test]
    public void EnterAfterTypingQueriesOnce()
    {
        var runs = new List<string>();
        var debouncer = new QueryDebouncer(runs.Add);
        debouncer.Submit("stream", 0);
        debouncer.Commit("stream");
        debouncer.Update(10_000);
        Assert.Multiple(() =>
        {
            Assert.That(runs, Is.EqualTo(new[] { "stream" }));
            Assert.That(debouncer.IsPending, Is.False);
        });
    }

    [Test]
    public void DebouncerSkipsARunForTextAlreadyQueried()
    {
        var runs = new List<string>();
        var debouncer = new QueryDebouncer(runs.Add);
        debouncer.Submit("jump", 0);
        debouncer.Update(1_000);
        debouncer.Submit("jumps", 1_010);
        debouncer.Submit("jump", 1_020);
        debouncer.Update(2_000);
        debouncer.Commit("jump");
        Assert.That(runs, Is.EqualTo(new[] { "jump", "jump" }), "Only an explicit commit repeats the same query.");
    }

    [Test]
    public void DebouncerDelayIsClampedToAResponsiveRange()
    {
        var runs = new List<string>();
        var debouncer = new QueryDebouncer(runs.Add, 5_000);
        debouncer.Submit("x", 0);
        debouncer.Update(401);
        Assert.That(runs, Is.EqualTo(new[] { "x" }));
    }

    [Test]
    public void ListDiffDetectsAppendsAndStaleKeys()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ListDiff.IsAppend(new[] { 1, 2 }, new[] { 1, 2, 3 }), Is.True);
            Assert.That(ListDiff.IsAppend(new[] { 1, 2 }, new[] { 2, 1, 3 }), Is.False);
            Assert.That(ListDiff.IsAppend(Array.Empty<int>(), new[] { 1 }), Is.False);
            Assert.That(ListDiff.SameOrder(new[] { 1, 2 }, new[] { 1, 2 }), Is.True);
            Assert.That(ListDiff.SameOrder(Array.Empty<int>(), Array.Empty<int>()), Is.True);
            Assert.That(ListDiff.SameOrder(new[] { 1, 2 }, new[] { 1, 2, 3 }), Is.False);
            Assert.That(ListDiff.Stale(new[] { 1, 2, 3 }, new[] { 2 }), Is.EqualTo(new[] { 1, 3 }));
        });
    }

    [Test]
    public void KeyedFlowKeepsUnchangedRowsAndReordersInPlace()
    {
        var flow = new FillFlowContainer<Drawable> { Direction = FillDirection.Vertical };
        var keyed = new KeyedFlow<int, (int Id, string Label), Container>(flow, item => item.Id, item => item.Label,
            item => new Container { Name = item.Label });

        int created = keyed.Apply(new[] { (1, "a"), (2, "b"), (3, "c") });
        keyed.TryGet(1, out Container first);
        keyed.TryGet(3, out Container third);

        int recreated = keyed.Apply(new[] { (3, "c"), (1, "a"), (4, "d") });
        keyed.TryGet(1, out Container firstAgain);
        keyed.TryGet(3, out Container thirdAgain);

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.EqualTo(3));
            Assert.That(recreated, Is.EqualTo(1), "Only the new key needs a row.");
            Assert.That(firstAgain, Is.SameAs(first));
            Assert.That(thirdAgain, Is.SameAs(third));
            Assert.That(keyed.Keys, Is.EqualTo(new[] { 3, 1, 4 }));
            Assert.That(flow.Count, Is.EqualTo(3), "The removed key's row is gone.");
            Assert.That(flow.Children.OrderBy(flow.GetLayoutPosition).Select(child => child.Name), Is.EqualTo(new[] { "c", "a", "d" }));
        });

        keyed.Apply(new[] { (3, "changed") });
        keyed.TryGet(3, out Container replaced);
        Assert.That(replaced, Is.Not.SameAs(third), "A changed signature replaces the row.");
    }

    [Test]
    public void ChangeTrackerOnlyReportsNewValues()
    {
        var tracker = new AimModLayout.ChangeTracker<float>();
        Assert.Multiple(() =>
        {
            Assert.That(tracker.Update(10), Is.True);
            Assert.That(tracker.Update(10), Is.False);
            Assert.That(tracker.Update(11), Is.True);
        });
        tracker.Reset();
        Assert.That(tracker.Update(11), Is.True);
    }

    [TestCase(1_600, AimModSidebarMode.Full)]
    [TestCase(900, AimModSidebarMode.Full)]
    [TestCase(899, AimModSidebarMode.Rail)]
    [TestCase(640, AimModSidebarMode.Rail)]
    [TestCase(0, AimModSidebarMode.Full)]
    public void SidebarCollapsesToARailInSmallWindows(float width, AimModSidebarMode expected) =>
        Assert.That(AimModLayout.SelectSidebarMode(width), Is.EqualTo(expected));

    [Test]
    public void RailKeepsPageContentWiderThanTheFullSidebar()
    {
        MarginPadding rail = AimModVisualStyle.PagePaddingFor(AimModLayout.SidebarWidth(AimModSidebarMode.Rail));
        MarginPadding full = AimModVisualStyle.PagePaddingFor(AimModLayout.SidebarWidth(AimModSidebarMode.Full));
        Assert.Multiple(() =>
        {
            Assert.That(full, Is.EqualTo(AimModVisualStyle.PagePadding));
            Assert.That(rail.Left, Is.LessThan(full.Left));
            Assert.That(rail.Right, Is.EqualTo(full.Right));
        });
    }

    [TestCase(1_000, 280, 3, 3)]
    [TestCase(500, 280, 3, 1)]
    [TestCase(0, 280, 3, 1)]
    public void ColumnsCollapseOnNarrowWidths(float width, float minimum, int maximum, int expected) =>
        Assert.That(AimModLayout.ColumnsFor(width, minimum, maximum), Is.EqualTo(expected));

    [Test]
    public void FriendlyErrorsKeepTechnicalTextAsDetail()
    {
        (string message, string? detail) = AimModFriendlyError.Describe(new IOException("Sharing violation on path"), "Reading replays");
        (string rateMessage, _) = AimModFriendlyError.Describe(
            new HttpRequestException("429", null, HttpStatusCode.TooManyRequests), "Searching osu!");
        (string timeoutMessage, _) = AimModFriendlyError.Describe(new AggregateException(new TimeoutException()), "Loading");
        Assert.Multiple(() =>
        {
            Assert.That(message, Is.EqualTo("Reading replays failed while reading or writing files."));
            Assert.That(detail, Is.EqualTo("Sharing violation on path"));
            Assert.That(rateMessage, Does.Contain("rate limited"));
            Assert.That(timeoutMessage, Does.Contain("took too long"));
        });
    }

    [Test]
    public void ReadableFontSizesNeverDropBelowTheMinimum()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AimModVisualStyle.Readable(8), Is.EqualTo(AimModVisualStyle.MinReadableFontSize));
            Assert.That(AimModVisualStyle.Readable(14), Is.EqualTo(14));
            Assert.That(AimModVisualStyle.CaptionFont.Size, Is.GreaterThanOrEqualTo(11));
            Assert.That(AimModVisualStyle.LabelFont.Size, Is.GreaterThanOrEqualTo(11));
        });
    }

    [Test]
    public void InlineStatusCollapsesUntilShownAndOffersRetry()
    {
        var status = new AimModInlineStatus();
        int retries = 0;
        Assert.That(status.IsShowing, Is.False);

        status.ShowLoading("Searching...");
        Assert.Multiple(() =>
        {
            Assert.That(status.IsShowing, Is.True);
            Assert.That(status.IsError, Is.False);
        });

        status.ShowError(new TimeoutException("socket timeout"), "Searching osu!", () => retries++);
        Assert.Multiple(() =>
        {
            Assert.That(status.IsError, Is.True);
            Assert.That(status.MessageText, Does.StartWith("Searching osu! took too long"));
        });

        status.Dismiss();
        Assert.Multiple(() =>
        {
            Assert.That(status.IsShowing, Is.False);
            Assert.That(status.IsError, Is.False);
            Assert.That(retries, Is.Zero);
        });
    }

    [Test]
    public void LoadingOverlayCancelCallsBackOnce()
    {
        var overlay = new AimModLoadingOverlay();
        int cancelled = 0;
        overlay.ShowLoading("Downloading skin", "Checking the archive", onCancel: () => cancelled++);
        Assert.Multiple(() =>
        {
            Assert.That(overlay.IsLoading, Is.True);
            Assert.That(overlay.HandleNonPositionalInput, Is.True, "Escape must reach a cancellable overlay.");
        });
        overlay.HideLoading();
        Assert.Multiple(() =>
        {
            Assert.That(overlay.IsLoading, Is.False);
            Assert.That(overlay.HandleNonPositionalInput, Is.False);
            Assert.That(cancelled, Is.Zero);
        });
    }
}
