using System;

namespace DS4Windows.InputDevices;

// Value-owned presentation state; profile and native feedback remain separate
// and continue updating while a mod temporarily owns an output field.
internal readonly record struct DualSenseDsxOverlay(
    object Owner = null,
    DualSenseDevice.TriggerEffectData? Left = null,
    DualSenseDevice.TriggerEffectData? Right = null,
    DS4Color? Color = null,
    byte? MicLed = null,
    byte? PlayerLeds = null)
{
    internal const int LeftField = 1, RightField = 2, ColorField = 4, MicField = 8, PlayerField = 16;
    internal int Fields => (Left.HasValue ? LeftField : 0) | (Right.HasValue ? RightField : 0) |
        (Color.HasValue ? ColorField : 0) | (MicLed.HasValue ? MicField : 0) | (PlayerLeds.HasValue ? PlayerField : 0);

    internal static int Released(in DualSenseDsxOverlay previous, in DualSenseDsxOverlay next) => previous.Fields & ~next.Fields;

    internal static DualSenseDevice.TriggerEffectData ReadTrigger(byte[] data, int offset)
    {
        if (data == null || offset < 0 || offset + 11 > data.Length) throw new ArgumentException("A complete trigger effect is required.");
        var result = new DualSenseDevice.TriggerEffectData();
        result.ChangeRaw(data[offset], data[offset + 1], data[offset + 2], data[offset + 3],
            data[offset + 4], data[offset + 5], data[offset + 6], data[offset + 9]);
        result.triggerReserved7 = data[offset + 7];
        result.triggerReserved8 = data[offset + 8];
        result.triggerReserved10 = data[offset + 10];
        return result;
    }

    internal static void WriteTrigger(byte[] data, int offset, in DualSenseDevice.TriggerEffectData effect)
    {
        Array.Clear(data, offset, 11);
        data[offset] = effect.triggerMotorMode;
        data[offset + 1] = effect.triggerStartResistance;
        data[offset + 2] = effect.triggerEffectForce;
        data[offset + 3] = effect.triggerRangeForce;
        data[offset + 4] = effect.triggerNearReleaseStrength;
        data[offset + 5] = effect.triggerNearMiddleStrength;
        data[offset + 6] = effect.triggerPressedStrength;
        data[offset + 9] = effect.triggerActuationFrequency;
        data[offset + 7] = effect.triggerReserved7;
        data[offset + 8] = effect.triggerReserved8;
        data[offset + 10] = effect.triggerReserved10;
    }

    internal DualSenseDsxOverlay ObserveNative(byte[] report, int offset)
    {
        if (report == null || offset < 0 || offset + 47 > report.Length) throw new ArgumentException("A complete native state is required.");
        var next = this;
        if ((report[offset] & 0x04) != 0) next = next with { Right = ReadTrigger(report, offset + 10) };
        if ((report[offset] & 0x08) != 0) next = next with { Left = ReadTrigger(report, offset + 21) };
        if ((report[offset + 1] & 0x01) != 0) next = next with { MicLed = report[offset + 8] };
        if ((report[offset + 1] & 0x08) != 0) next = next with { Color = null, PlayerLeds = null };
        else
        {
            if ((report[offset + 1] & 0x04) != 0) next = next with { Color = new DS4Color(report[offset + 44], report[offset + 45], report[offset + 46]) };
            if ((report[offset + 1] & 0x10) != 0) next = next with { PlayerLeds = report[offset + 43] };
        }
        return next;
    }
}
