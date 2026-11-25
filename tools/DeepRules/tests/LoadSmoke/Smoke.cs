using System;
using System.Threading.Tasks;

namespace LoadSmoke
{
    public class Smoke
    {
        // DR004: async void outside an event handler
        public async void Fire() => await Task.Delay(1);

        // DR007: lock (this)
        public void Locked() { lock (this) { } }

        // DR014: swallowed exception
        public void Quiet() { try { Fire(); } catch (Exception) { } }
    }
}
