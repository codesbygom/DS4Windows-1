using System;

namespace DS4Windows.DS4Control
{
    /// <summary>
    /// Adds two bounded HID movements without discarding counts. Their sum fits
    /// in at most two reports; each axis keeps its own exact remainder.
    /// </summary>
    internal readonly struct CalibrationMouseReports
    {
        internal const int MaximumComponent = 32767;
        internal readonly short FirstX, FirstY, SecondX, SecondY;
        internal bool HasSecond => SecondX != 0 || SecondY != 0;

        internal CalibrationMouseReports(int pendingX, int pendingY, int x, int y)
        {
            ValidateMovement(pendingX, pendingY);
            ValidateMovement(x, y);
            int combinedX = pendingX + x;
            int combinedY = pendingY + y;
            FirstX = (short)Math.Clamp(combinedX, -MaximumComponent, MaximumComponent);
            FirstY = (short)Math.Clamp(combinedY, -MaximumComponent, MaximumComponent);
            SecondX = (short)(combinedX - FirstX);
            SecondY = (short)(combinedY - FirstY);
        }

        internal static void ValidateMovement(int x, int y)
        {
            if (x < -MaximumComponent || x > MaximumComponent)
                throw new ArgumentOutOfRangeException(nameof(x), x,
                    "Calibration movement must fit a signed mouse report.");
            if (y < -MaximumComponent || y > MaximumComponent)
                throw new ArgumentOutOfRangeException(nameof(y), y,
                    "Calibration movement must fit a signed mouse report.");
        }
    }
}
