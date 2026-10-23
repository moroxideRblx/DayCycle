# 24 HOUR DAY CYCLES

Makes days 24 hours long and indefinitely looping, with changes to the sun animator to make it behave correctly. 
Has many features either planned or already implemented to make it work correctly in terms of enemy spawns, HUD elements, and custom moons.

Updated for Lethal Company v81.

## Features

- Days are 24 hours long and keep looping. Stay as long as you want: night, dawn, then a new day.
- The ship leaves when you pull the lever, when everyone dies, or 1.8 hours after a leave-early vote.
- Every vanilla moon gets a proper 24-hour sun with sunsets, dark nights and sunrises, in normal, stormy and eclipse versions.
- Modded moons keep their own sun and fade into the morning between 3 and 6 AM.
- Spawns follow the day: busier towards midnight, calmer towards dawn, every day you stay.
- The HUD clock goes around the full 24 hours and still runs the game's own clock code.
- Flooded moons get a daily tide: the water rises from 8 AM, peaks at midnight and is back down by 6 AM.
- Each moon visit costs one deadline day however long you stay.
- Moon blacklist: moons on it get the plain vanilla 18-hour day.
- Plus a bunch of v81 fixes for spawns, the clock and the time-of-day stingers.

Everything below is off by default, turn on what you like in the config.

- Outside enemy cycle: night enemies leave between 4 and 7 AM and come back at night. Old Birds power down instead. Nothing leaves while you're looking at it or while it's chasing someone.
- Daytime enemy cycle: manticoils and locusts still leave at nightfall, but now they come back every morning.
- You can set every enemy, vanilla or modded, to follow the cycle or act like vanilla.

## Config

Everything is in `BepInEx/config/DayCycle.cfg`. Change it in your mod manager, or in game with LethalConfig. Settings only show up while the feature they belong to is on.

## Credits

- nooshstuff made DayCycle: the 24-hour day, the sun animations and the whole idea. [github.com/nooshstuff/DayCycle](https://github.com/nooshstuff/DayCycle)
- v81 update, fixes and the optional extras by Moroxide.