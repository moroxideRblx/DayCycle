# Unreleased

- Updated for v81. Builds against the NuGet game libraries, no NetcodePatcher needed.
- Fixed the clock, spawn and threat level hooks for v81. Each hook is applied on its own now, so one broken hook doesn't take the rest down.
- Indoor vents don't stop spawning after 6 AM anymore.
- Spawn curves, including each creature's own, follow the looping day.
- The threat level matches vanilla's clock times again.
- New saves start with a full deadline, and the deadline doesn't refill on long stays.
- A leave-early vote makes the ship leave 1.8 hours later, like vanilla.

# 1.0.0
