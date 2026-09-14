# Calibrate flick stick with a 360° turn

Bind a button to **360° turn — right stick calibration** (or the left-stick
equivalent) in the button remapper's **Flick-stick calibration** tab. Regular
and shifted bindings use the same actions, on every supported physical
controller and virtual controller type. The game must accept mouse look.

1. Set an initial **Real World Calibration** value under **Axis Config → the
   selected stick → Flick Stick**, then save the profile.
2. In the game, face an easy-to-recognize landmark. Keep other aiming inputs
   still and tap the bound button. It performs one test turn over about a second.
3. If the turn falls short of the landmark, increase Real World Calibration.
   If it overshoots, decrease it. Save and repeat until it returns to the start.

DS4Windows uses mouse counts per degree: **dots per 360 = 360 × RWC**.
For example, RWC 5.3 requests 1,908 counts. The action reads the selected stick's
existing RWC; it does not maintain a separate calibration setting. A turn takes
the value at the instant it starts and rounds its total to the nearest count.

Use the same in-game mouse sensitivity you intend to play with. Raw mouse input
and disabled acceleration make comparisons more repeatable where the game
supports them. Changing sensitivity, zoom/aim modes or acceleration can change
the result. This is a manual calibration aid, not automatic camera tracking or
a universal command for games that accept only virtual-stick input.

The button does not repeat while held. Taps during a turn are ignored, not
queued; release and tap again after it finishes. A profile change, disconnected
controller, output-handler replacement or report gap longer than 250 ms cancels
unfinished motion. A held button cannot restart it after that boundary.
Pausing the mapper, including recording a macro, cancels the turn too.

## Implementation notes

- New actions append output IDs 45 and 46; existing profile IDs do not change.
- The normal mapper selects the action and consumes the original game button.
  Its per-controller one-shot state emits cumulative integer targets at the
  controller report cadence, preserving the rounded total across report rates.
- There is no worker, sleep, macro queue, haptics change or virtual-pad change.
  Ordinary mouse quantization cannot discard calibration fractions.
- RWC must be finite, positive and at most 200 (the existing editor ceiling).
  Calibration flushes atomically through the mouse backend. Combined mouse
  deltas respect the buffered backend's range without overwriting an earlier
  move. Tests use recording backends, never system mouse injection.

## References

This implements [DS4Windows issue #104](https://github.com/hbashton/DS4Windows/issues/104).
The workflow matches Steam's documented [Turn Camera 360 calibration action](https://steamcommunity.com/games/593110/announcements/detail/4182231197207095613).
[JoyShockMapper's calibration documentation](https://github.com/JibbSmart/JoyShockMapper/blob/master/README.md#5-real-world-calibration)
and its [progress-based flick implementation](https://github.com/Electronicks/JoyShockMapper/blob/master/JoyShockMapper/src/JoyShock.cpp#L815)
informed the review. DS4Windows keeps its own established RWC units; settings
from other remappers are not assumed to be numerically interchangeable.
