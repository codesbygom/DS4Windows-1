using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using NAudio.Wave;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class ProcessLoopbackSharedCaptureRetirementTests
{
    [TestMethod]
    public void AcquireWhilePreviousLeaseRetiresKeepsTheReplacementSubscribedAndRegistered()
    {
        using var fixture = new SessionFixture();
        int oldDeliveries = 0, replacementDeliveries = 0;
        IDisposable oldLease = fixture.Acquire((_, _) => oldDeliveries++);
        object subscriber = fixture.Subscribers[0];
        Exception retirementFailure = null;
        var retire = new Thread(() =>
        {
            try { oldLease.Dispose(); }
            catch (Exception ex) { retirementFailure = ex; }
        }) { IsBackground = true };

        IDisposable replacement;
        lock (fixture.RegistryGate)
        {
            retire.Start();
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                (int)SessionFixture.Field(subscriber, "Disposed").GetValue(subscriber)! == 1 &&
                (retire.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, 3000),
                "The previous lease did not reach the deliberately held registry gate.");
            // Registry Acquire is reentrant on this thread. The previous owner is
            // parked at the same gate, so this is the exact last-unsubscribe gap.
            replacement = fixture.Acquire((_, _) => replacementDeliveries++);
        }
        Assert.IsTrue(retire.Join(3000), "Lease retirement did not finish after the registry gate was released.");
        Assert.IsNull(retirementFailure,
            "Retiring the old lease attempted to destroy the still-subscribed shared capture.");
        Assert.AreSame(fixture.Session, fixture.RegisteredSession);
        Assert.AreEqual(0, fixture.ReadInt("disposed"));
        fixture.Deliver(new byte[16]);
        Assert.AreEqual(0, oldDeliveries);
        Assert.AreEqual(1, replacementDeliveries);
        fixture.SuppressNativeDisposal();
        replacement.Dispose();
    }

    [TestMethod]
    public void RemovingOneOfTwoConsumersDoesNotStopOrRemoveTheirSharedCapture()
    {
        using var fixture = new SessionFixture();
        int firstDeliveries = 0, secondDeliveries = 0;
        IDisposable first = fixture.Acquire((_, _) => firstDeliveries++);
        IDisposable second = fixture.Acquire((_, _) => secondDeliveries++);
        first.Dispose();
        first.Dispose();
        Assert.AreSame(fixture.Session, fixture.RegisteredSession);
        Assert.AreEqual(1, fixture.Subscribers.Count);
        fixture.Deliver(new byte[16]);
        Assert.AreEqual(0, firstDeliveries);
        Assert.AreEqual(1, secondDeliveries);
        fixture.SuppressNativeDisposal();
        second.Dispose();
        Assert.IsNull(fixture.RegisteredSession);
    }

    [TestMethod]
    public void LastConsumerMakesTheSessionUnavailableBeforeNativeCleanupAndCannotRemoveASuccessor()
    {
        using var fixture = new SessionFixture();
        IDisposable lease = fixture.Acquire((_, _) => { });
        fixture.SuppressNativeDisposal();
        lease.Dispose();
        Assert.IsNull(fixture.RegisteredSession);
        Assert.AreEqual(1, fixture.ReadInt("retiring"),
            "Last-subscriber removal must close admission before releasing the registry gate.");

        using var successor = new SessionFixture();
        lock (fixture.RegistryGate)
            fixture.Registry[fixture.Key] = successor.Session;
        fixture.RemoveRegistration();
        Assert.AreSame(successor.Session, fixture.RegisteredSession,
            "Late retirement of an old object must not remove a replacement under the same key.");
        lock (fixture.RegistryGate)
            fixture.Registry.Remove(fixture.Key);
    }

    private sealed class SessionFixture : IDisposable
    {
        private static readonly Type OwnerType = typeof(ProcessLoopbackWaveCapture);
        private static readonly Type RegistryType = OwnerType.GetNestedType("ProcessCaptureRegistry", BindingFlags.NonPublic)!;
        private static readonly Type SessionType = OwnerType.GetNestedType("ProcessCaptureSession", BindingFlags.NonPublic)!;
        private static readonly Type SubscriberType = OwnerType.GetNestedType("ProcessCaptureSubscriber", BindingFlags.NonPublic)!;
        private static int nextProcessId = 1800000000;
        private readonly int processId = Interlocked.Increment(ref nextProcessId);
        private readonly WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        private readonly string route = "managed-retirement-" + Guid.NewGuid().ToString("N");
        private readonly EventWaitHandle captureEvent = new(false, EventResetMode.AutoReset);
        private readonly ManualResetEvent stopped = new(false);

        public SessionFixture()
        {
            RegistryGate = RegistryType.GetField("syncRoot", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Registry = (IDictionary)RegistryType.GetField("sessions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Key = (string)RegistryType.GetMethod("BuildKey", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { processId, format, route })!;
            // No Windows endpoint or capture client is created. Only registry,
            // subscription, and callback code runs on this managed session.
            Session = RuntimeHelpers.GetUninitializedObject(SessionType);
            Set("registryKey", Key);
            Set("subscriberLock", new object());
            Set("subscribers", Activator.CreateInstance(typeof(List<>).MakeGenericType(SubscriberType))!);
            Set("captureEvent", captureEvent);
            Set("stopped", stopped);
            Set("captureThread", new Thread(() => { }));
            lock (RegistryGate)
                Registry[Key] = Session;
        }

        public object Session { get; }
        public string Key { get; }
        public object RegistryGate { get; }
        public IDictionary Registry { get; }
        public IList Subscribers => (IList)Field(Session, "subscribers").GetValue(Session)!;
        public object RegisteredSession { get { lock (RegistryGate) return Registry[Key]; } }
        public int ReadInt(string name) => (int)Field(Session, name).GetValue(Session)!;
        public IDisposable Acquire(Action<byte[], int> callback) =>
            (IDisposable)RegistryType.GetMethod("Acquire", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new object[] { processId, format, route, callback, null! })!;
        public void Deliver(byte[] bytes) => SessionType.GetMethod("NotifyDataAvailable", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(Session, new object[] { bytes, bytes.Length });
        public void SuppressNativeDisposal() => Set("disposed", 1);
        public void RemoveRegistration() => RegistryType.GetMethod("Remove", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { (object)Key, Session });
        private void Set(string name, object value) => Field(Session, name).SetValue(Session, value);
        public static FieldInfo Field(object instance, string name) => instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        public void Dispose()
        {
            SuppressNativeDisposal();
            RemoveRegistration();
            captureEvent.Dispose();
            stopped.Dispose();
        }
    }
}
