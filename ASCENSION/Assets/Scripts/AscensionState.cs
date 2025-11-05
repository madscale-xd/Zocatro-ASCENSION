// AscensionState.cs
using System.Collections.Generic;

/// <summary>
/// Local client-side mirror of which actorNumbers are ascendees per zoneViewID.
/// Used to make quick local PvP permission checks without hitting the network for every damage event.
/// </summary>
public static class AscensionState
{
    // zoneViewID -> set of actorNumbers that are ascendees
    private static readonly Dictionary<int, HashSet<int>> zoneToAscendees = new Dictionary<int, HashSet<int>>();

    public static void SetZoneAscendees(int zoneViewID, int[] ascendeeActorNumbers)
    {
        if (!zoneToAscendees.ContainsKey(zoneViewID))
            zoneToAscendees[zoneViewID] = new HashSet<int>();

        var set = zoneToAscendees[zoneViewID];
        set.Clear();
        if (ascendeeActorNumbers != null)
        {
            foreach (var a in ascendeeActorNumbers) set.Add(a);
        }
    }

    public static void ClearZone(int zoneViewID)
    {
        if (zoneToAscendees.ContainsKey(zoneViewID))
            zoneToAscendees.Remove(zoneViewID);
    }

    public static bool IsActorAscendeeInZone(int zoneViewID, int actorNumber)
    {
        if (!zoneToAscendees.TryGetValue(zoneViewID, out var set)) return false;
        return set.Contains(actorNumber);
    }

    public static bool AreActorsInSameZone(int zoneViewID, int actorA, int actorB)
    {
        if (!zoneToAscendees.TryGetValue(zoneViewID, out var set)) return false;
        return set.Contains(actorA) && set.Contains(actorB);
    }

    public static void RemoveActorFromAllZones(int actorNumber)
    {
        var keys = new List<int>(zoneToAscendees.Keys);
        foreach (var k in keys)
        {
            zoneToAscendees[k].Remove(actorNumber);
            if (zoneToAscendees[k].Count == 0) zoneToAscendees.Remove(k);
        }
    }
}
