using Microsoft.Win32;

namespace TBMini
{
    /// Auto adjust's GPU switching on charger changes, as a small state machine. Decide is pure (no I/O), so the whole
    /// table can be printed and checked.
    ///
    /// Why: Lenovo's EC caps the dGPU on USB-C power (about 15 W on 65 W or less, slower than the iGPU; about 30 W on
    /// 100 W). The user's rules:
    ///   - connecting a 65 W-or-less charger in Hybrid (or Auto): go to iGPU only;
    ///   - connecting a 100 W or the full-power (barrel) charger in iGPU only: go to Hybrid, whoever chose iGPU only;
    ///   - each of those is TBMini's override and is undone when its reason goes away (battery, or the opposite kind of
    ///     charger), back to what the user had.
    ///
    /// State (persisted): None = the GPU mode is the user's; ToIgpuFrom* = TBMini went to iGPU only for a small charger
    /// (and what the user had); ToHybrid = TBMini went to Hybrid for a strong charger (the user had iGPU only).
    ///
    ///                   | Small charger         | Strong charger        | Battery               | Unknown USB-C
    ///   None (Connect)  | Hybrid/Auto -> iGPU,  | iGPU -> Hybrid,       | -                     | -
    ///                   | state ToIgpuFrom*     | state ToHybrid        |                       |
    ///   ToIgpuFrom*     | keep                  | -> what the user had  | -> what the user had  | keep
    ///   ToHybrid        | -> iGPU, None         | keep                  | -> iGPU, None         | keep
    ///
    /// Plus: if the GPU is no longer where TBMini put it, the user (or Lenovo's software) changed it, so the state is
    /// None. With auto adjust off nothing happens and the state is None (turning it off clears it: TBMini owes nothing).
    /// Only connecting a charger starts an override; undoing one happens on any event (also resume / startup). Unknown
    /// GPU mode, dGPU direct (needs a restart) or an unknown charger class: no switch. If a switch fails the caller keeps
    /// the old state, so the next event tries again.
    static class GpuAuto
    {
        /// Connect = a charger was connected (plug-in, or a swap that kept AC on); Battery = AC was removed;
        /// Recheck = resume or startup (only undoes TBMini's overrides, never starts one).
        public enum Ev { Connect, Battery, Recheck }

        /// Small = USB-C 65 W or less; Strong = USB-C 100 W or the barrel adapter; Unknown = USB-C of unknown class.
        public enum Chg { Battery, Small, Strong, Unknown }

        public enum St { None = 0, ToIgpuFromHybrid = 0x10, ToIgpuFromAuto = 0x12, ToHybrid = 0x20 }

        public struct Decision
        {
            public int? Target;   // iGPU sub-mode to switch to (0 Hybrid, 1 iGPU only, 2 Auto), or null for no switch
            public St Next;       // state once the switch succeeded (or right away when there is no switch)
        }

        public static Decision Decide(Ev ev, Chg chg, GpuMode mode, St st, bool auto)
        {
            if (!auto) return new Decision { Next = St.None };                // auto adjust off: TBMini owes nothing
            bool known = mode == GpuMode.Hybrid || mode == GpuMode.Auto || mode == GpuMode.IgpuOnly || mode == GpuMode.Discrete;
            if (!known) return new Decision { Next = st };                    // cannot see the mode: change nothing

            // no longer where TBMini put it: the user changed it, TBMini owes nothing
            if ((st == St.ToIgpuFromHybrid || st == St.ToIgpuFromAuto) && mode != GpuMode.IgpuOnly) st = St.None;
            if (st == St.ToHybrid && mode != GpuMode.Hybrid) st = St.None;

            switch (st)
            {
                case St.ToIgpuFromHybrid:
                case St.ToIgpuFromAuto:
                    if (chg == Chg.Battery || chg == Chg.Strong)
                        return new Decision { Target = st == St.ToIgpuFromAuto ? 2 : 0, Next = St.None };
                    return new Decision { Next = st };

                case St.ToHybrid:
                    if (chg == Chg.Battery || chg == Chg.Small) return new Decision { Target = 1, Next = St.None };
                    return new Decision { Next = st };

                default:
                    if (ev != Ev.Connect) return new Decision { Next = St.None };
                    if (chg == Chg.Small && (mode == GpuMode.Hybrid || mode == GpuMode.Auto))
                        return new Decision { Target = 1, Next = mode == GpuMode.Auto ? St.ToIgpuFromAuto : St.ToIgpuFromHybrid };
                    if (chg == Chg.Strong && mode == GpuMode.IgpuOnly) return new Decision { Target = 0, Next = St.ToHybrid };
                    return new Decision { Next = St.None };
            }
        }

        const string Key = @"HKEY_CURRENT_USER\Software\TBMini";

        public static St Load() => Registry.GetValue(Key, "AutoGpu", 0) is int v && (v == 0x10 || v == 0x12 || v == 0x20) ? (St)v : St.None;

        public static void Save(St st)
        {
            try
            {
                if (st != St.None) Registry.SetValue(Key, "AutoGpu", (int)st, RegistryValueKind.DWord);
                else using (var k = Registry.CurrentUser.OpenSubKey(@"Software\TBMini", true)) k?.DeleteValue("AutoGpu", false);
            }
            catch { }
        }
    }
}
