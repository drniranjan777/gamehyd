using System.Collections.Generic;
using UnityEngine;

namespace RWBR.Client.Online
{
    /// <summary>
    /// How far through a reload each player is. Snapshots only say "reloading", so progress is timed from the moment
    /// the flag is first seen, over the weapon's reload length. Snapshots for other players are all interpolated with
    /// the same delay, so the start is late by at most one snapshot interval. A player already reloading when first
    /// seen starts at zero.
    /// </summary>
    public sealed class ReloadTracker
    {
        private readonly Dictionary<ushort, float> _started = new Dictionary<ushort, float>();

        /// <summary>Progress 0..1 while reloading, -1 when not.</summary>
        public float Progress(ushort id, bool reloading, float reloadSeconds, float time)
        {
            if (!reloading)
            {
                _started.Remove(id);
                return -1f;
            }
            if (!_started.TryGetValue(id, out float start))
            {
                start = time;
                _started[id] = start;
            }
            return Mathf.Clamp01((time - start) / Mathf.Max(reloadSeconds, 0.01f));
        }

        public void Forget(ushort id) => _started.Remove(id);
        public void Clear() => _started.Clear();
    }
}
