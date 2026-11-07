// TeleportStateLedger.cs
// Tiny pure helper for "disable now, restore EXACTLY what was there before": it remembers the ORIGINAL enabled value of each object the first time that object is captured and hands the same value back on restore.
// It never turns anything on by itself - a component that was already disabled before the restriction is restored as disabled. Used by UnexploredTeleportAvailability; no Unity dependency, so it is unit-testable.
// All comments in English per project convention.

using System.Collections.Generic;

public sealed class TeleportStateLedger
{
    readonly Dictionary<int, bool> original = new Dictionary<int, bool>();

    public int Count => original.Count;

    /// <summary>Remember the value of 'id' once. Returns true when this call stored it; a second capture while the entry exists never overwrites the original.</summary>
    public bool Capture(int id, bool currentEnabled)
    {
        if (original.ContainsKey(id)) return false;
        original[id] = currentEnabled;
        return true;
    }

    /// <summary>The ids that were captured, in a stable (insertion-independent) snapshot, with the value each had BEFORE the restriction.</summary>
    public List<KeyValuePair<int, bool>> Snapshot() => new List<KeyValuePair<int, bool>>(original);

    public bool TryGetOriginal(int id, out bool value) => original.TryGetValue(id, out value);

    /// <summary>Forget everything (after the originals have been written back).</summary>
    public void Clear() { original.Clear(); }
}
