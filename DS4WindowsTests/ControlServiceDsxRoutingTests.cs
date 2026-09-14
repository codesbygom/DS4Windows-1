using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.DS4Control;
using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
public class ControlServiceDsxRoutingTests
{
    [TestMethod]
    public void ActualServiceCallbacksReachCompositeDualSenseWithoutLegacyRegistration()
    {
        using var fixture = new Fixture();
        var device = fixture.AddDevice(0);
        Assert.AreEqual(DS4DeviceWorkerLifecycleSupport.UnsupportedDualSenseCompositeWorkers,
            device.WorkerLifecycleSupport);
        Assert.IsNull(fixture.Field("inputRegistrationTable"));
        fixture.Begin();

        fixture.Update(0, state => state with { MicLed = 2, Color = new DS4Color(17, 83, 201) });

        var overlay = Mailbox(device).ReadLatest().DsxOverlay;
        Assert.AreSame(fixture.Session, overlay.Owner);
        Assert.AreEqual((byte)2, overlay.MicLed.Value);
        Assert.AreEqual((byte)201, overlay.Color.Value.blue);
        var status = (DSXStatusResponse)fixture.Invoke("ReadDsxStatus", fixture.Session);
        Assert.IsTrue(status.isControllerConnected);
        Assert.AreEqual(1, status.Devices.Count);
        Assert.AreEqual(0, status.Devices[0].Index);
        fixture.End();
        fixture.Update(0, state => state with { MicLed = 0 });
        Assert.AreEqual(overlay, Mailbox(device).ReadLatest().DsxOverlay,
            "A completed packet cannot continue publishing.");
        fixture.Begin();
        fixture.Update(0, null);
        Assert.AreEqual(0, Mailbox(device).ReadLatest().DsxOverlay.Fields);
        Assert.AreEqual(DualSenseDsxOverlay.MicField | DualSenseDsxOverlay.ColorField,
            Mailbox(device).ReadLatest().DsxReleaseFields);
    }

    [TestMethod]
    public void ReplacementMidPacketCannotReceiveItsPredecessorsInstruction()
    {
        using var fixture = new Fixture();
        var original = fixture.AddDevice(0);
        fixture.Begin();
        fixture.Update(0, state => state with { MicLed = 1 });
        var originalState = Mailbox(original).ReadLatest();
        var replacement = fixture.AddDevice(0);

        fixture.Update(0, state => state with { MicLed = 2 });

        Assert.AreEqual(originalState, Mailbox(original).ReadLatest());
        Assert.AreEqual(0, Mailbox(replacement).ReadLatest().DsxOverlay.Fields);
        fixture.Invoke("ReleaseDsxDevice", original);
        Assert.AreEqual(0, Mailbox(original).ReadLatest().DsxOverlay.Fields);
        fixture.End();
        fixture.Begin();
        fixture.Update(0, state => state with { MicLed = 2 });
        Assert.AreEqual((byte)2, Mailbox(replacement).ReadLatest().DsxOverlay.MicLed.Value);
        fixture.Invoke("ReleaseDsxDevice", original);
        Assert.AreEqual((byte)2, Mailbox(replacement).ReadLatest().DsxOverlay.MicLed.Value,
            "Late cleanup for the old physical identity cannot release the replacement.");
    }

    [TestMethod]
    public void UnexpectedStopRevokesExactlyThatSessionAndCannotReleaseItsSuccessor()
    {
        using var fixture = new Fixture();
        var device = fixture.AddDevice(0);
        fixture.Begin();
        fixture.Update(0, state => state with { MicLed = 1 });
        object previous = fixture.Session;

        fixture.Invoke("OnDsxUnexpectedStopped", previous, "synthetic listener failure");

        Assert.IsNull(fixture.Field("dsxSession"));
        Assert.IsNull(fixture.Field("_dsxUdpServer"));
        Assert.AreEqual("synthetic listener failure", fixture.Service.DSXUDPServerError);
        Assert.AreEqual(0, Mailbox(device).ReadLatest().DsxOverlay.Fields);
        fixture.ReplaceSession();
        fixture.Begin();
        fixture.Update(0, state => state with { MicLed = 2 });
        var successor = Mailbox(device).ReadLatest();
        fixture.Invoke("OnDsxUnexpectedStopped", previous, "late old failure");
        fixture.Invoke("BeginDsxPacket", previous);
        fixture.Invoke("UpdateDsxDevice", previous, 0,
            (Func<DualSenseDsxOverlay, DualSenseDsxOverlay>)(state => state with { MicLed = 0 }));
        fixture.Invoke("EndDsxPacket", previous);
        Assert.AreEqual(successor, Mailbox(device).ReadLatest());
        Assert.AreSame(fixture.Session, fixture.Field("dsxSession"));
        fixture.Update(0, state => state with { MicLed = 1 });
        Assert.AreEqual((byte)1, Mailbox(device).ReadLatest().DsxOverlay.MicLed.Value,
            "The predecessor's late completion cannot end the successor's admitted packet.");
    }

    [TestMethod]
    public void RemovalClaimWinsWhileCallbackWaitsAtTheDevicePublicationGate()
    {
        using var fixture = new Fixture();
        var device = fixture.AddDevice(0);
        fixture.Begin();
        using var entering = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        Exception failure = null;
        var callback = new Thread(() =>
        {
            entering.Set();
            try { fixture.Update(0, state => state with { MicLed = 2 }); }
            catch (Exception error) { failure = error; }
            finally { completed.Set(); }
        }) { IsBackground = true };
        lock (device.removeLocker)
        {
            callback.Start();
            Assert.IsTrue(entering.Wait(1000));
            Assert.IsFalse(completed.Wait(100),
                "The production callback must acquire the device gate before publishing to an untabled DualSense.");
            device.IsRemoving = true;
        }
        Assert.IsTrue(callback.Join(1000));
        Assert.IsNull(failure);
        Assert.AreEqual(0, Mailbox(device).ReadLatest().DsxOverlay.Fields,
            "A removal claim completed before publication must reject the admitted instruction.");
    }

    [TestMethod]
    public void StoppedServiceRejectsAnAlreadyCapturedPacket()
    {
        using var fixture = new Fixture();
        var device = fixture.AddDevice(0);
        fixture.Begin();
        fixture.Service.running = false;

        fixture.Update(0, state => state with { MicLed = 2 });

        Assert.AreEqual(0, Mailbox(device).ReadLatest().DsxOverlay.Fields);
    }

    private static DualSensePhysicalOutputStateMailbox Mailbox(DualSenseDevice device) =>
        (DualSensePhysicalOutputStateMailbox)typeof(DualSenseDevice).GetField(
            "physicalOutputStateMailbox", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(device);

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<DualSenseDevice> devices = new();
        private readonly List<DSXUdpServer> servers = new();
        internal ControlService Service { get; } =
            (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
        internal object Session { get; private set; }

        internal Fixture()
        {
            Service.DS4Controllers = new DS4Device[8];
            Service.running = true;
            Set("dsxOutputGate", new object());
            ReplaceSession();
        }

        internal void ReplaceSession()
        {
            Session = Activator.CreateInstance(typeof(ControlService).GetNestedType(
                "DsxSession", BindingFlags.NonPublic), nonPublic: true);
            var server = (DSXUdpServer)Session.GetType().GetField("Server", PrivateInstance).GetValue(Session);
            servers.Add(server);
            Set("dsxSession", Session);
            Set("_dsxUdpServer", server);
            Set("dsxLastError", "");
        }

        internal DualSenseDevice AddDevice(int slot)
        {
            var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
            var device = new DualSenseDevice(hid, "DSX composite routing regression");
            Service.DS4Controllers[slot] = device;
            devices.Add(device);
            return device;
        }

        internal void Begin() => Invoke("BeginDsxPacket", Session);
        internal void End() => Invoke("EndDsxPacket", Session);
        internal void Update(int slot, Func<DualSenseDsxOverlay, DualSenseDsxOverlay> update) =>
            Invoke("UpdateDsxDevice", Session, slot, update);
        internal object Field(string name) => typeof(ControlService).GetField(name, PrivateInstance).GetValue(Service);
        internal void Set(string name, object value) => typeof(ControlService).GetField(name, PrivateInstance).SetValue(Service, value);
        internal object Invoke(string name, params object[] args) =>
            typeof(ControlService).GetMethod(name, PrivateInstance).Invoke(Service, args);

        public void Dispose()
        {
            Invoke("DetachDsxSession");
            foreach (var server in servers) server.Dispose(); // No listener was ever started.
            foreach (var device in devices) device.ReadWaitEv.Dispose();
        }
    }
}
