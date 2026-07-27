namespace Relicable.Model;

// Ordered stages of the ARR Zodiac relic line. The controller selects the lowest
// incomplete stage for the currently equipped relic.
public enum RelicStage
{
    None = 0,
    Relic = 1,   // A Relic Reborn: the base 2-star weapon (Curtana, Bravura, ...)
    Atma = 2,    // 12 Atma from FATEs
    Animus = 3,  // Trials of the Braves, 9 books
    Novus = 4,   // Sphere Scroll plus materia
    Nexus = 5,   // Light farming
    Braves = 6,  // il125 Zodiac Braves: "Wherefore Art Thou, Zodiac" + 4 quests + a
                 // second set of Trials of the Braves books (mostly manual). The il125
                 // weapon this yields is where the Zeta Mahatma is then charged.
    Zeta = 7,    // Mahatma (12 awakened on the il125 weapon) -> final il135 weapon
    Complete = 8,
}
