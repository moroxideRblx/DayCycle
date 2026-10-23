# Unreleased

- Updated for v81. Builds against the NuGet game libraries, no NetcodePatcher needed.
- Fixed the clock, spawn and threat level hooks for v81. Each hook is applied on its own now, so one broken hook doesn't take the rest down.
- Indoor vents don't stop spawning after 6 AM anymore.
- Spawn curves, including each creature's own, follow the looping day.
- The threat level matches vanilla's clock times again.
- New saves start with a full deadline, and the deadline doesn't refill on long stays.
- A leave-early vote makes the ship leave 1.8 hours later, like vanilla.
- Rebuilt the 24-hour sun bundle for all three sun types (normal, stormy, eclipse).
- Modded moons keep their own sun and fade into the morning from 3 to 6 AM.
- Settings moved into numbered sections, with optional LethalConfig support and a debug log option.
- Moon blacklist: moons on it get the plain vanilla day.
- Flood water follows a daily tide instead of rising forever.
- The clock goes through the game's own code, wraps at exactly 24 hours and plays the sting once per phase change. There's an optional new icon schedule too.
- Optional outside and daytime enemy cycle, with a Cycled / Vanilla choice per enemy.
- Optional indoor enemy cycle. Nutcrackers, Maneaters, the Ghost Girl and Cadaver Bloom always stay.

# 1.0.0
