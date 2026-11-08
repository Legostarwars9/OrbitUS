using System;
using System.Threading.Tasks;

namespace Orbit_Us
{
    // Only bulk state uses this budget. Movement, damage, teleports and heartbeats
    // bypass it, so a large world cannot queue them behind its appearance data.
    internal sealed class GameplaySendBudget
    {
        private readonly object gate=new object();
        private double next;
        public Task Wait(int bytes)
        {
            double delay;
            lock(gate)
            {
                double now=NetworkTime.Now;
                next=Math.Max(next,now-.012)+(bytes+89)/200000.0;
                delay=next-now;
            }
            return delay>.001?Task.Delay((int)Math.Ceiling(delay*1000)):Task.CompletedTask;
        }
    }
}
