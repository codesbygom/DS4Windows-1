using System;
using System.Collections.Generic;

namespace DS4Windows.DS4Control;

/// <summary>
/// Binds a DSX packet to the exact devices present when that packet began.
/// The caller keeps its output gate held through admission, report-lease
/// acquisition, and publication; this class never grants an execution lease.
/// </summary>
internal sealed class DSXControllerAdmission<TDevice> where TDevice : class
{
    private readonly object syncRoot = new();
    private readonly object owner;
    private readonly TDevice[] captured;
    private bool active;
    private bool revoked;

    internal DSXControllerAdmission(object owner, int capacity)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.owner = owner;
        captured = new TDevice[capacity];
    }

    internal bool Begin(object packetOwner, IReadOnlyList<TDevice> current,
        Func<int, TDevice, bool> eligible)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(eligible);
        lock (syncRoot)
        {
            if (revoked || !ReferenceEquals(owner, packetOwner)) return false;
            active = false;
            Array.Clear(captured);
            for (int index = 0; index < Math.Min(current.Count, captured.Length); index++)
            {
                TDevice device = current[index];
                if (device != null && eligible(index, device)) captured[index] = device;
            }
            if (revoked)
            {
                Array.Clear(captured);
                return false;
            }
            active = true;
            return true;
        }
    }

    internal bool TryGet(object packetOwner, int index, IReadOnlyList<TDevice> current,
        out TDevice device)
    {
        ArgumentNullException.ThrowIfNull(current);
        lock (syncRoot)
        {
            device = null;
            if (revoked || !active || !ReferenceEquals(owner, packetOwner) ||
                index < 0 || index >= captured.Length || index >= current.Count ||
                captured[index] == null || !ReferenceEquals(captured[index], current[index])) return false;
            device = captured[index];
            return true;
        }
    }

    internal bool End(object packetOwner)
    {
        lock (syncRoot)
        {
            if (!ReferenceEquals(owner, packetOwner)) return false;
            active = false;
            Array.Clear(captured);
            return true;
        }
    }

    internal bool Revoke(object packetOwner)
    {
        lock (syncRoot)
        {
            if (revoked || !ReferenceEquals(owner, packetOwner)) return false;
            revoked = true;
            active = false;
            Array.Clear(captured);
            return true;
        }
    }
}
