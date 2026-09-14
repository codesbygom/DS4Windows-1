using System.Diagnostics;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public sealed class HeldInputPauseTests
{
    [TestMethod]
    public void XboxSeriesPreservesHeldControlsAcrossProducerPauses() =>
        VerifyPause(new XboxOneEgressScheduler(0),
            new XboxOneEgressState(XboxOneEgressState.AButton, 50, 200, 16000, -12000, 8000, -4000),
            XboxOneEgressState.Neutral);

    [TestMethod]
    public void Xbox360PreservesHeldControlsAcrossProducerPauses() =>
        VerifyPause(new Xbox360EgressScheduler(0),
            new Xbox360EgressState(0x1000, 50, 200, 16000, -12000, 8000, -4000),
            Xbox360EgressState.Neutral);

    [TestMethod]
    public void Switch2PreservesHeldControlsAcrossProducerPauses() =>
        VerifyPause(new Switch2EgressScheduler(0),
            new Switch2EgressState(1, 3200, 1200, 2800, 400, 1, 2, 3, 4, 5, 6),
            Switch2EgressState.Neutral);

    private static void VerifyPause<TState>(OrderedEgressScheduler<TState> scheduler,
        TState held, TState released) where TState : struct, IOrderedEgressState<TState>
    {
        var epoch = scheduler.CurrentProducerEpoch;
        Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
            scheduler.Publish(epoch, held, 1));
        Assert.IsTrue(scheduler.TryClaim(1, out var first));
        Assert.AreEqual(held, first.State);
        Assert.IsTrue(scheduler.TryAdmit(first, 1));
        Assert.IsTrue(scheduler.Complete(first, OrderedEgressCompletion.Commit));

        // Logical monotonic-clock jumps, not sleeps or hardware simulation.
        // A paused producer cannot manufacture a release after 100 ms or a day.
        long now = 1;
        foreach (double milliseconds in new[] { 50d, 100, 250, 1000, 86_400_000 })
        {
            now += (long)(Stopwatch.Frequency * milliseconds / 1000);
            Assert.IsTrue(scheduler.TryClaim(now, out var idle));
            Assert.AreEqual(OrderedEgressClaimKind.Idle, idle.Kind);
            Assert.AreEqual(held, idle.State, "Idle presentation released a held control.");
            Assert.IsTrue(scheduler.TryAdmit(idle, now));
            Assert.IsTrue(scheduler.Complete(idle, OrderedEgressCompletion.Commit));
        }

        // Retention is not a debounce delay: a real release remains immediate.
        Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
            scheduler.Publish(epoch, released, ++now));
        Assert.IsTrue(scheduler.TryClaim(now, out var release));
        Assert.AreEqual(OrderedEgressClaimKind.Ordered, release.Kind);
        Assert.AreEqual(released, release.State);
        Assert.IsTrue(scheduler.TryAdmit(release, now));
        Assert.IsTrue(scheduler.Complete(release, OrderedEgressCompletion.Commit));
        var snapshot = scheduler.Snapshot();
        Assert.AreEqual(0L, snapshot.OverflowFaults);
        Assert.AreEqual(0L, snapshot.OrderedAgeFaults);
        Assert.AreEqual(0L, snapshot.MandatoryNeutralCommits);
    }

    [TestMethod]
    public void PlayStationMappedQueueDoesNotInventAReleaseWhenProducerIsIdle()
    {
        var scheduler = new ViiperInputScheduler();
        scheduler.Reset(1);
        var held = ViiperStatePacketBuilder.BuildMappedState(new DS4State
        {
            LX = 240, LY = 32, RX = 190, RY = 80, L2 = 50, R2 = 200, Cross = true,
        }, -1);
        Assert.IsTrue(scheduler.Publish(held, 1).Accepted);
        Assert.IsTrue(scheduler.TryClaim(out var press));
        scheduler.CompleteSuccess(press, 2);
        for (int i = 0; i < 1000; i++)
            Assert.IsFalse(scheduler.TryClaim(out _), "An empty input queue invented another report.");
        Assert.AreEqual(held, scheduler.Snapshot().LastTransported);

        long afterPause = 2 + 86_400L * Stopwatch.Frequency;
        Assert.IsTrue(scheduler.Publish(ViiperMappedInputState.Neutral, afterPause).Accepted);
        Assert.IsTrue(scheduler.TryClaim(out var release));
        Assert.AreEqual(ViiperMappedInputState.Neutral, release.State);
        scheduler.CompleteSuccess(release, afterPause);
        Assert.AreEqual(0L, scheduler.Snapshot().OverflowCount);
    }
}
