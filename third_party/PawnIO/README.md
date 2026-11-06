# PawnIO module

`IntelMSR.bin` is the official signed module from
[PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) release
[0.2.11](https://github.com/namazso/PawnIO.Modules/releases/tag/0.2.11), unmodified
(SHA-256 `d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f`). Copyright (C) 2025 namazso.

TBMini embeds it and loads it into the PawnIO driver only when that driver is installed, TBMini runs as administrator
on an Intel CPU, and `PawnIOLib.dll` carries a valid signature from namazso. The driver is only opened while the panel is open or briefly by `TBMini --sensors`.
It is used to read the CPU package temperature (MSRs 0x1A2 and 0x1B1), the RAPL power unit (MSR 0x606, once, to convert
the energy counter to joules) and the platform energy counter (MSR 0x64D, used for whole-system power on boards that wire it up).
The module allows reading a fixed list of MSRs and writing a few (package power limit, VR current limit, OC/VR mailbox, prefetcher control).
TBMini only calls its read function.

The module is licensed under the GNU Lesser General Public License v2.1 or later ([COPYING](COPYING)).
Its source is included here (`IntelMSR.p`); the build files are in the upstream repository at the tag above.
Every TBMini build embeds `IntelMSR.bin`; the TBMini tag of each release has its license and source in this folder.

TBMini does not ship or install the PawnIO driver itself. Get it from [pawnio.eu](https://pawnio.eu) or with `winget install namazso.PawnIO`.
