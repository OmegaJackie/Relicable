using System.Numerics;

namespace Relicable.Data;

// Nexus stage: once the Novus relic's Light reaches 2000, the Novus -> Nexus upgrade is performed at
// Jalzahn (Hyrstmill, North Shroud), reached from the Fallgourd Float aetheryte. Jalzahn is the Doman
// who runs every Zodiac Atma/Animus/Novus/Nexus enhancement; he stands at the Hyrstmill anvil beside
// Gerolt, so his navigation anchor is that same in-game-verified spot (NpcInteractor homes on his live
// object by data id regardless -- the anchor only needs to stream him in). The teleport target is
// resolved from Lumina (Fallgourd Float is North Shroud's only aetheryte), so no raw aetheryte id is
// hand-authored here.
public static class NexusData
{
    // Jalzahn, ENpcResident (verified against ENpcResident.csv: 1008948 "Jalzahn").
    public const uint JalzahnNpcId = 1008948;
    public const uint JalzahnTerritory = 154; // North Shroud (Hyrstmill)

    // The Hyrstmill anvil where Jalzahn stands beside Gerolt. Same in-game-verified coordinate the
    // base-relic Gerolt turn-ins use; NpcInteractor walks from here to Jalzahn's live position by his
    // data id, so the shared anvil spot only needs to bring him into the object table.
    public static readonly Vector3 JalzahnPosition = new(440.726f, -0.937455f, -62.1923f);

    // Teleportable aetheryte for the trip to Jalzahn (Fallgourd Float). 0 if unresolved, in which case
    // the executor falls back to plain navigation from wherever the player is.
    public static uint FallgourdAetheryte => Locations.AetheryteForTerritory(JalzahnTerritory);
}
