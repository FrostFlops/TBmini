# PawnIO module

`IntelMSR.bin` is the official signed module from
[PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) release
[0.2.11](https://github.com/namazso/PawnIO.Modules/releases/tag/0.2.11), unmodified.
TBMini embeds it and loads it into the PawnIO driver, if that is installed, to read the CPU package temperature
(MSRs 0x1A2 and 0x1B1) and the platform energy counter (MSR 0x64D, used for whole-system power on boards that wire it up).
The module only allows reading a fixed list of MSRs, and TBMini only reads.

The module is licensed under the GNU Lesser General Public License v2.1 or later ([COPYING](COPYING)).
Its source is included here (`IntelMSR.p`); the build files are in the upstream repository at the tag above.

TBMini does not ship or install the PawnIO driver itself. Get it from [pawnio.eu](https://pawnio.eu) or with `winget install namazso.PawnIO`.
