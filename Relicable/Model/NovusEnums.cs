namespace Relicable.Model;

// How the controller picks which stage to work. Auto keeps the original behaviour
// (the lowest incomplete stage for the equipped relic); Manual pins work to a
// user-inserted stage so a farmable stage that was already "passed" can be revisited
// (for example farming more Atma or Alexandrite after the engine thinks Atma/Novus
// is complete).
public enum StageSelectionMode
{
    Auto,
    Manual,
}

// Market scope for Universalis price lookups. DataCenter is the default: cross-world
// cheapest across the player's data centre.
public enum UniversalisScope
{
    World,
    DataCenter,
    Region,
}

// Which Novus weapon is being melded. The per-stat caps, grade tiers, and meld
// success curve differ for healer weapons and for Paladin (which splits into two
// scrolls). Standard covers every Disciple of War weapon and the two non-healer
// caster weapons (Stardust Rod, The Veil of Wiyu).
public enum NovusWeaponProfile
{
    Standard,   // 75 points; per-stat cap 44 (Piety 31); tiers 11/11/11/11
    Healer,     // 75 points; Piety cap 44, Direct Hit cap 35 (base +9); others 44
    Paladin,    // Curtana 53 + Holy Shield 22; caps 31/13 (Piety 22/9)
}
