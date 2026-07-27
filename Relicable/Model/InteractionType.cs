namespace Relicable.Model;

// How an InteractNpc step should treat the target NPC. TextAdvance handles the
// resulting dialogue once interaction is triggered.
public enum InteractionType
{
    Talk,
    AcceptLeve,
    CompleteLeve,
    Vendor,
    TradeRelic,
}
