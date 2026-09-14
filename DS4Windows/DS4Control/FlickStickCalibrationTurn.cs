using System;
using System.Diagnostics;

namespace DS4Windows
{
    /// <summary>
    /// Report-driven, one-shot calibration. RWC is counts per degree, exactly
    /// as in ProcessFlickStick. No workers, sleeps, hold repeat or queued turns.
    /// Only the mapper owns this state; cold cancellation uses a generation.
    /// </summary>
    internal struct FlickStickCalibrationTurn
    {
        internal const double DurationSeconds = 1.0;
        internal const double MaximumReportGapSeconds = 0.25;
        internal const double MaximumCalibration = 200.0; // Profile editor ceiling.
        private byte pressed, previousPressed;
        private bool initialized, active;
        private object owner, handler;
        private long revision, started, lastTimestamp;
        private int resetGeneration, target, emitted;

        internal bool Active => active;
        internal void BeginFrame() => pressed = 0;

        internal void Press(bool rightStick) => pressed |= (byte)(rightStick ? 2 : 1);

        internal static bool TryGetTurnCounts(double calibration, out int counts)
        {
            counts = 0;
            if (!double.IsFinite(calibration) || calibration <= 0 ||
                calibration > MaximumCalibration) return false;
            counts = (int)Math.Round(360.0 * calibration, MidpointRounding.AwayFromZero);
            return counts > 0;
        }

        internal int Advance(long timestamp, long profileRevision, int cancellationGeneration,
            object inputOwner, object outputHandler, bool inputAvailable,
            double leftCalibration, double rightCalibration)
        {
            if (!inputAvailable || inputOwner == null || outputHandler == null ||
                profileRevision < 0 || timestamp < 0)
            {
                initialized = active = false;
                previousPressed = pressed;
                return 0;
            }

            // A new connection/profile/output owner must see a released button
            // before a held input can start another turn. Never replay old work.
            if (!initialized || !ReferenceEquals(owner, inputOwner) ||
                !ReferenceEquals(handler, outputHandler) || revision != profileRevision ||
                resetGeneration != cancellationGeneration || timestamp < lastTimestamp ||
                (timestamp - lastTimestamp) / (double)Stopwatch.Frequency > MaximumReportGapSeconds)
            {
                initialized = true;
                active = false;
                owner = inputOwner;
                handler = outputHandler;
                revision = profileRevision;
                resetGeneration = cancellationGeneration;
                previousPressed = pressed;
                lastTimestamp = timestamp;
                return 0;
            }

            lastTimestamp = timestamp;
            byte rising = (byte)(pressed & ~previousPressed);
            previousPressed = pressed;
            if (!active && rising != 0)
            {
                // Simultaneous calibration buttons request one turn, not 720°.
                double calibration = (rising & 2) != 0 ? rightCalibration : leftCalibration;
                if (!TryGetTurnCounts(calibration, out target)) return 0;
                emitted = 0;
                started = timestamp;
                active = true;
            }
            if (!active) return 0;

            double progress = Math.Min(1.0,
                (timestamp - started) / (double)Stopwatch.Frequency / DurationSeconds);
            // Cumulative integer targets conserve counts at every report rate
            // and bypass the ordinary pointer's fractional-remainder filtering.
            int desired = (int)Math.Round(target * progress, MidpointRounding.AwayFromZero);
            int delta = desired - emitted;
            emitted = desired;
            if (progress >= 1.0) active = false;
            return delta;
        }
    }
}
