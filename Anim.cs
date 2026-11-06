using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TBMini
{
    /// Damped spring, integrated per frame. Slightly underdamped (zeta below 1) it overshoots and settles back,
    /// which is what makes the controls feel soft ("jelly") rather than tweened.
    struct Spring
    {
        public float X, V, Target;
        public float K, Zeta, Eps;     // stiffness (1/s²), damping ratio, settle threshold (in X's units)

        public Spring(float k, float zeta, float eps) { X = V = Target = 0; K = k; Zeta = zeta; Eps = eps; }

        public bool Moving => V != 0 || X != Target;

        public void Snap(float x) { X = Target = x; V = 0; }

        /// Advances dt seconds (in small sub-steps, so stiff springs stay stable); false once it has settled.
        public bool Step(float dt)
        {
            if (!Moving) return false;
            int n = Math.Max(1, (int)Math.Ceiling(dt / 0.004f));
            float h = dt / n, c = 2 * Zeta * (float)Math.Sqrt(K);
            for (int i = 0; i < n; i++) { V += (-K * (X - Target) - c * V) * h; X += V * h; }
            if (Math.Abs(X - Target) < Eps && Math.Abs(V) < Eps * 8) { X = Target; V = 0; return false; }
            return true;
        }
    }

    /// Something that animates: Tick advances it by dt seconds and returns false once it is at rest.
    interface IAnimated { bool Tick(float dt); }

    /// One UI-thread frame timer shared by every animated control. It runs only while something is moving,
    /// so an idle panel costs nothing.
    static class Animator
    {
        static Timer _timer;
        static readonly List<IAnimated> Items = new List<IAnimated>();
        static readonly Stopwatch Clock = new Stopwatch();
        static double _last;

        [DllImport("user32.dll")] static extern bool SystemParametersInfo(int action, int param, out bool value, int winIni);
        const int SPI_GETCLIENTAREAANIMATION = 0x1042;

        /// Windows' "Animation effects" setting (Accessibility > Visual effects). When it is off, controls snap.
        public static bool Enabled
        {
            get { try { return !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out bool on, 0) || on; } catch { return true; } }
        }

        /// Starts (or keeps) animating a: call on the UI thread.
        public static void Run(IAnimated a)
        {
            if (!Items.Contains(a)) Items.Add(a);
            if (_timer == null) { _timer = new Timer { Interval = 15 }; _timer.Tick += (s, e) => Frame(); }
            if (!_timer.Enabled) { Clock.Restart(); _last = 0; _timer.Start(); }
        }

        static void Frame()
        {
            double now = Clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.05, Math.Max(0.001, now - _last));   // a stalled frame doesn't make springs jump
            _last = now;
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (i >= Items.Count) continue;
                bool keep;
                try { keep = Items[i].Tick(dt); } catch { keep = false; }
                if (!keep && i < Items.Count) Items.RemoveAt(i);
            }
            if (Items.Count == 0) _timer.Stop();
        }
    }
}
