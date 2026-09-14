using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4WinWPF;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class DualSenseManualDisconnectTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ManualDisconnectRemovesOnlyItsRowWithoutWaitingForHidReadFailure(bool fromTray)
    {
        OnSta(() =>
        {
            var model = new ControllerListViewModel(new ProfileList());
            var device = Device();
            var other = Device();
            bool previousLinked = Global.linkedProfileCheck[2];
            bool otherLinked = Global.linkedProfileCheck[3];
            using var radioReached = new ManualResetEventSlim();
            int radioCalls = 0, removalCalls = 0, finalizations = 0;
            bool? requestedRemoval = null;
            bool commandAliveAtRadio = true, outputAliveAtRadio = true;
            Thread commandOwner = null, outputOwner = null, lifecycleOwner = null;
            device.PhysicalOutputWriteTestHook = () => { };
            device.PhysicalOutputFinalizeTestHook = () => Interlocked.Increment(ref finalizations);
            device.Removal += (_, _) => Interlocked.Increment(ref removalCalls);
            device.PhysicalBluetoothDisconnectTestHook = remove =>
            {
                requestedRemoval = remove;
                commandAliveAtRadio = commandOwner.IsAlive;
                outputAliveAtRadio = outputOwner.IsAlive;
                Interlocked.Increment(ref radioCalls);
                // Emulate base DisconnectBT's successful radio operation and
                // its optional Removal event. Never touch a real HID or radio.
                typeof(DS4Device).GetProperty(nameof(DS4Device.IsDisconnecting))!.SetValue(device, true);
                if (remove) RunRemoval(device);
                radioReached.Set();
                return true;
            };
            try
            {
                model.AddController(device, 2);
                model.AddController(other, 3);
                Global.linkedProfileCheck[2] = true;
                Global.linkedProfileCheck[3] = true;
                InvokeDevice(device, "StartPhysicalWorkers");
                commandOwner = Worker(device, "deviceCommandThread");
                outputOwner = Worker(device, "physicalOutputThread");
                lifecycleOwner = Worker(device, "physicalLifecycleThread");

                if (fromTray)
                    ClickTrayDisconnect(device);
                else
                {
                    model.ControllerDict[2].RequestDisconnect();
                    InvokeDevice(device, "DrainQueuedInputEvents");
                }

                Assert.IsTrue(radioReached.Wait(5000), "Manual Disconnect did not reach the radio boundary.");
                Assert.IsTrue(lifecycleOwner.Join(5000), "Disconnect lifecycle did not finish.");
                Assert.AreEqual(true, requestedRemoval,
                    "Manual Disconnect must remove the row even if the HID failure arrives after lifecycle retirement.");
                Assert.AreEqual(1, radioCalls);
                Assert.AreEqual(1, finalizations);
                Assert.AreEqual(1, removalCalls);
                Assert.IsFalse(commandAliveAtRadio, "Radio I/O preceded command-owner retirement.");
                Assert.IsFalse(outputAliveAtRadio, "Radio I/O preceded output-owner retirement.");
                Assert.AreEqual(1, model.ControllerCol.Count);
                Assert.IsFalse(model.ControllerDict.ContainsKey(2));
                Assert.IsFalse(Global.linkedProfileCheck[2]);
                Assert.AreSame(other, model.ControllerDict[3].Device);
                Assert.IsTrue(Global.linkedProfileCheck[3]);

                // A late notification for the disconnected generation must
                // not erase a newly connected controller reusing its slot.
                var replacement = Device();
                model.AddController(replacement, 2);
                Global.linkedProfileCheck[2] = true;
                RunRemoval(device);
                Assert.AreEqual(2, model.ControllerCol.Count);
                Assert.AreSame(replacement, model.ControllerDict[2].Device);
                Assert.IsTrue(Global.linkedProfileCheck[2]);
            }
            finally
            {
                try
                {
                    // Keep fixture cleanup bounded even if lifecycle joining
                    // regresses; never block the test runner in public Stop.
                    typeof(DualSenseDevice).GetMethod("RequestPhysicalLifecycleShutdown", PrivateInstance)!
                        .Invoke(device, new object[] { false, false });
                    Assert.IsTrue(lifecycleOwner == null || lifecycleOwner.Join(5000),
                        "Fixture lifecycle failed to retire during cleanup.");
                }
                finally
                {
                    Global.linkedProfileCheck[2] = previousLinked;
                    Global.linkedProfileCheck[3] = otherLinked;
                }
            }
        });
    }

    private static DualSenseDevice Device()
    {
        var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
        var device = new DualSenseDevice(hid, "Synthetic manual disconnect") { Synced = true };
        typeof(DS4Device).GetField("conType", PrivateInstance)!.SetValue(device, ConnectionType.BT);
        return device;
    }

    private static void ClickTrayDisconnect(DS4Device device)
    {
        // Execute the actual tray handler without constructing a ControlService,
        // subscribing global UI events, or starting physical discovery.
        var tray = (TrayIconViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconViewModel));
        using var listLock = new ReaderWriterLockSlim();
        typeof(TrayIconViewModel).GetField("_colLocker", PrivateInstance)!.SetValue(tray, listLock);
        typeof(TrayIconViewModel).GetField("controllerList", PrivateInstance)!.SetValue(tray,
            new List<ControllerHolder> { new(device, 2) });
        typeof(TrayIconViewModel).GetMethod("DisconnectMenuItem_Click", PrivateInstance)!.Invoke(tray,
            new object[] { new MenuItem { Tag = 0 }, new RoutedEventArgs() });
    }

    private static void RunRemoval(DS4Device device) =>
        typeof(DS4Device).GetMethod("RunRemoval", PrivateInstance)!.Invoke(device, null);

    private static void InvokeDevice(DualSenseDevice device, string name) =>
        typeof(DualSenseDevice).GetMethod(name, PrivateInstance)!.Invoke(device, null);

    private static Thread Worker(DualSenseDevice device, string name) =>
        (Thread)typeof(DualSenseDevice).GetField(name, PrivateInstance)!.GetValue(device)!;

    private static void OnSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(20000), "Manual-disconnect fixture did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
