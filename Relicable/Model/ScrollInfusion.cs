namespace Relicable.Model;

// One Sphere Scroll's authoritative infusion counter, as the game reports it in the open
// RelicSphereScroll window (AtkValue 10 = current, 11 = max). Persisted per scroll spec name so the
// answer to "is this scroll finished" survives the window closing -- the per-stat block cannot do
// that job (it is only trusted when it reconciles with this counter, and it is cleared when a scroll
// completes so the next scroll starts fresh).
//
// Max is stored alongside Current rather than assumed to be 75: Paladin's two scrolls are 53
// (Curtana) and 22 (Holy Shield), and the max is what identifies WHICH scroll a live read belongs to.
public sealed class ScrollInfusion
{
    public int Current { get; set; }
    public int Max { get; set; }

    public bool IsFull => Max > 0 && Current >= Max;
}
