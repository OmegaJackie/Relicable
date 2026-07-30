# Changelog

## 1.5.4.5 — Melee actually closes to melee, and the relic can finally come off

### Fixed

- **It stops out of melee range and stands there.** Every combat loop decided "am I close enough
  to fight?" by measuring 4 yalms from the target's *centre*. The game measures reach from the
  target's *hitbox edge*, so anything with a sizeable collision hull could never satisfy it: the
  character closes until the hull stops it, several yalms short of the centre, the check stays
  false, and it keeps re-issuing an approach it can never finish while standing on top of a mob it
  could have been hitting. The FATE loop was given the hitbox term a few builds ago; the relic-note
  grind and the leve runner never got it. All three now share one distance model.

- **The relic could not be taken off, so upgrades and hand-overs silently did nothing.** Taking a
  weapon off was implemented as "move it to a free armoury slot" — correct for an off hand, and
  impossible for a main hand, because FFXIV has no bare-handed state and the server just refuses
  the move. It failed without an error, so the relic stayed on, the trade window listed nothing to
  trade, and the step waited out its timer.

  The main hand now **swaps** instead: Relicable picks the best non-relic weapon that job owns and
  puts it on, which displaces the relic into the armoury — the same end state, by a route the game
  permits. If the job owns no other weapon at all, it says so and tells you to get one rather than
  failing quietly.

- **A Paladin's Holy Shield was never put back.** Every "re-equip what we took off" path sent the
  item to the main hand, and the game silently refuses a shield there. The destination hand is now
  taken from the item itself.

### Added

- **Ranged jobs hold at range instead of walking into melee.** The engage distance is now chosen
  from the job's role: melee and tanks close to the hitbox edge, physical ranged and casters settle
  at about 15 yalms. The near distance is a floor, never a retreat — a caster that is already close
  just fights from where it stands, so this never tugs against BossMod Reborn's own positioning.
  Two deliberate exceptions still close all the way in: a blocked line of sight (a cast the terrain
  eats is a silent no-op), and holding station on a protection leve's charge.

- **Gear sets follow the relic through its upgrades.** Each upgrade replaces the weapon with a new
  item id, which left the gear set you actually use naming a weapon that no longer exists — so the
  next `/gearset change` came up with an empty main hand, and no shield on a Paladin. The set you
  are wearing is now rewritten to the current relic after an upgrade. It only does that when the
  set belongs to the job you are on and every non-weapon slot already matches, so the write cannot
  change anything but the weapon. Turn it off with "Keep gear sets on the current relic" in the
  config.

## 1.5.4.4 — A Treasured Mother reports to Ealdwine, not Brangwine

### Fixed

- **A Treasured Mother was being reported to the wrong person, in the wrong zone.** Between dungeon
  batches that quest does not send you back to Brangwine — Brangwine hands you off to **Ealdwine at
  Swiftperch in Western La Noscea**, and only the *final* turn-in returns to Mor Dhona. The engine
  had one NPC per material quest and used it for everything, so every report flew to Revenant's Toll
  and stood in front of somebody with nothing to say.

  The cause was the source of the data, not a typo: the table was built from the quest's start and
  end NPCs, which genuinely are Brangwine for both. The intermediate steps were never in it. The
  turn-in target is now read from the quest's own objective marker for the sequence it is actually
  on, so it is the game's answer rather than a transcription — for all four material quests, and for
  reports and final turn-ins alike. The old table remains only as a fallback, with Ealdwine added to
  it.

  The trip also now teleports to the aetheryte **nearest** the NPC rather than just one in the zone.
  Western La Noscea has two, and the generic pick could have landed at Aleport and walked.

### Changed

- **The dungeon-step sequences for all four material quests are now confirmed against game data.**
  They were calibrated by hand in-game, one quest at a time, and Labor of Love's second pair had only
  been derived by analogy. Every number matches what the quests themselves declare.

## 1.5.4.3 — The Braves shopping list can pull from your retainers

### Added

- **Every Braves material is now fetchable from your retainers — individually, by group, or all at
  once.** The Novus planner has been able to empty your retainers into your bags for a while; the
  Braves list, which is where the expensive materials actually pile up, could only tell you what to
  buy. Each row now has its own **Fetch** button, each section has **Fetch group**, and the top of
  the panel has **Fetch all from retainers**. Open a summoning bell and Relicable visits each
  retainer in turn, retrieves what is still needed, and backs out of the retainer UI cleanly when it
  is done. AutoRetainer is paused for the duration and restored afterwards, so the two never fight
  over the bell.

  Turning off *Pull items automatically* keeps the same buttons but moves nothing: open a retainer
  and the status line lists exactly what to drag out.

- **A Retainer column on every material.** The bell scan that already recorded your materia now
  records the Braves shopping list too, so each row shows how many sit on a retainer even while you
  are nowhere near a bell — and with no retainer open, a fetch tells you which retainer to visit.
  The sixteen dungeon drops are key items and can never be entrusted to a retainer, so they show no
  retainer count and no button.

### Fixed

- **Unloading the plugin mid-fetch no longer leaves AutoRetainer paused.** A fetch suppresses
  AutoRetainer while it runs and only its Stop restores it, so unloading part-way through left
  AutoRetainer switched off with nothing left to switch it back on.

## 1.5.4.2 — A FATE it cannot reach no longer hangs the run

### Fixed

- **An unreachable FATE target no longer loops forever.** The FATE approach had no progress check of
  any kind, so a goal it could never actually get to — a mob hovering over water, one stood on a
  ledge the navmesh does not cover, a ring it cannot path into — meant walking at it indefinitely
  with the rotation switched off and nothing to break the tie. The main grind has had this guard for
  a long time; the FATE loop never got one.

  It now notices when it has spent twenty seconds without getting meaningfully closer, and takes the
  cheapest way forward: skip that mob and take another one, or, if there is no other mob and it has
  never made it into the ring, move to a different objective and come back later. Nothing is thrown
  away — every skip expires on its own. Crucially, once it *has* fought in the ring it never walks
  away: it keeps retrying until the FATE ends on its own timer, so credit it already earned is never
  forfeited, and it defends itself while it waits.

- **It no longer hovers forever over a mob it cannot land next to.** Landing was driven with no time
  limit at all, so a target with no floor beneath it meant descending forever. It now gives up after
  six seconds, dismounts, and moves to another target.

- **Big FATE bosses are engaged from the right distance.** Range was measured centre to centre, which
  a large boss's collision hull makes impossible to satisfy — the character would close to the hull,
  be unable to get any nearer, and look stalled while stood on top of a boss it could have been
  hitting. It now accounts for the target's size, as the game itself does.

- **A zone whose map is still building is no longer mistaken for being stuck.** Nothing can move
  until that finishes, and a cold zone takes far longer than the stall timeout, so a perfectly good
  FATE could be abandoned purely for loading slowly.

## 1.5.4.1 — Fight back when something aggroes

### Fixed

- **It no longer stands there while something beats on it.** Rare, but real, and it had four
  separate causes.

  The scan that answers "what is attacking me?" was the only one of its kind in the plugin that
  never checked whether the thing it found was an *enemy*. Friendly allied NPCs count as
  combatants, and an ally healer targets **you** in order to heal you, so it could be picked as
  "the aggressor" and hard targeted on every single tick while the mob actually hitting you was
  never touched. The same scan also only looked for enemies targeting *you*, so the moment a mob
  switched to your chocobo it became invisible: the run decided nothing was attacking, turned the
  rotation off, and walked on. You cannot mount in combat, so it walked.

- **An add that pulls from range is now chased down.** Standing in melee of a relic mob when
  something ranged aggroes from a ledge or a neighbouring tier, the run would target and mark the
  archer but keep its footwork planted on the relic mob, so it closed on neither and attacked
  neither. The approach is now decided for whatever it is actually fighting.

- **An unreachable attacker no longer pins the run forever.** The guard that gives up on a mob it
  cannot path to, and moves on to one it can, was having its clock reset on every combat tick, so
  in the one case that mattered it could never fire.

- **Leve enemies no longer get stuck at "targeted but never swung at".** The leve fight used a
  single 4 yalm threshold, so a mob drifting across it switched the rotation off and back on each
  time. Under BossMod Reborn that tears the preset down and rebuilds it, so no attack ever
  completed. It now uses the same engage/disengage band as the main grind.

- **Waiting, walking and staging now defend themselves.** Waiting for a FATE to spawn, walking to
  a treasure map dig site, standing between FATE waves, holding at a leve anchor: none of these
  read combat state at all, and none of their target scans can see an ordinary overworld enemy
  (a FATE scan matches only that FATE's mobs, a leve scan only that leve's). So anything that
  wandered over and aggroed was ignored indefinitely. All of them now fight back first, and their
  timeouts pause while they do, so a fight cannot make a step fail for the wrong reason.

## 1.5.4.0 — Stop flying away from a FATE that is already up

### Fixed

- **It no longer teleports away from a book FATE that is live in the zone you are standing in.**
  The run order is by kind and book — enemies, then leves, then dungeons, then FATEs — and
  nothing in it looked at where you actually were. So a FATE could be up in your own zone,
  ready to clear, while the engine flew off to an enemy entry somewhere else and left it to
  expire. A FATE that is up in your current zone is now taken first, ahead of everything,
  because it is the one piece of work that costs no travel at all and will not be there later.
  The existing "same zone as enemy work" pairing still applies after that, and both still
  require enough time left on the FATE to actually reach and clear it.

### Added

- **Aetheryte Tickets.** The run teleports constantly — twelve atma zones, book entries
  scattered across the whole of A Realm Reborn — and every hop was paid in gil. It can now
  spend a ticket instead, but only when the destination is actually expensive: the threshold is
  yours to set (default 300 gil), compared against the game's own price for that destination, so
  favoured and free destinations are priced correctly and cheap hops stay on gil. Runs out of
  tickets mid-session and it quietly goes back to paying gil. On by default; the switch and a
  live ticket count are in `/relic config` → Teleporting.

- **Choose which kinds of book work to do.** Book entries were worked in a fixed order with no
  say in the matter, so there was no way to grind a book without spending leve allowances or
  queueing dungeons. The main window now has a **Book work** section: leave it on **Auto** for
  exactly the behaviour you have today, or switch to **Manual** and tick only the kinds you want
  — Enemies, Leves, Dungeons, FATEs. Untick everything and it tells you rather than silently
  stopping.

- **Run a specific entry next.** The same section lists what is left in the book you are
  holding, with a **Run next** button on each. It jumps the queue once, including for a kind you
  have unticked, and if the engine is stopped the pick waits for you to press Start rather than
  being thrown away.

## 1.5.3.2 — Say it out loud when a game patch moves something

Patch 7.55 hardening. Two places in the plugin read the game at addresses and array
positions that a game patch can renumber with no warning and no build error. Neither used to
announce itself; both do now.

### Changed

- **A broken retainer lookup is reported at load, not hours later.** Pulling an item out of a
  retainer uses the game's own function, found by pattern-matching the game code. A patch
  moves that pattern. The search used to happen the first time you were actually standing at a
  summoning bell — deep inside a Novus material restock — so a patch showed up as "it just
  stopped taking things out of retainers" mid-run. The search now happens when the plugin
  loads and says plainly whether it worked, what stops working if it did not, and how to fix
  it. Nothing else changes: withdrawal still falls back to buying.
- **The levemete's leve list checks its own layout before touching it.** The board is read at
  fixed positions for the entry count, each leve's name, and the current selection. If a patch
  shortens or renumbers that list, the plugin now stops and logs the live layout instead of
  reading whichever value has moved into place — which also closes an out-of-bounds read of
  the entry count. `/relic leveboard`, with a leve list open, dumps the layout on demand.

### Fixed

- **Tagged releases no longer depend on someone else's latest commit.** The release build
  pulled ECommons from its default branch, so an upstream change could break a Relicable
  version that had already shipped — most likely right after a game patch, when ECommons is
  being updated hourly. It is pinned to an exact commit now.
- **Releases are built against the Dalamud that players are actually running.** For the first
  hours or days after a game patch, the stable Dalamud branch does not support the new client
  at all — the working build is on staging. The release build always took stable, so anything
  shipped in that window was compiled against a Dalamud nobody was running: it builds cleanly
  and then misbehaves in game. It now asks Dalamud which branch supports the live client,
  prefers stable whenever stable works, and refuses to build if the API level has moved out
  from under the plugin.

## 1.5.3.1 — The run parked after the beastman hunt

### Fixed

- **The relic now comes off before the post-hunt report to Gerolt.** The beastman hunt
  necessarily ends with the unfinished relic in your hands — its kills only credit while it
  is equipped — and the report that follows is a hand-over, not just a conversation. A
  hand-over never lists an equipped item, so the turn-in had nothing to offer and the run sat
  there. The weapon is taken off for that turn-in now, put straight back if Gerolt does not
  take it, and the Hydra re-equips it either way.

## 1.5.3.0 — The Zenith step runs itself

### Added

- **Zenith is automated.** Finishing the base relic used to stop the run with "go trade it
  at the Furnace yourself". Now Start does it: if you are short on **Thavnairian Mist** it
  goes to Auriana at Revenant's Toll and buys the shortfall, and if you already have it, it
  skips Mor Dhona entirely and flies straight to the **Furnace beside Gerolt** in Hyrstmill.
  The traded weapon is equipped afterwards, so the run continues into the Atma stage without
  stopping.

  Details that matter:

  - **One trade per weapon.** Every solo main hand costs 3 mists, but Paladin is two
    separate entries (Curtana + 2, Holy Shield + 1) and both have to happen.
  - **Your retainers are checked before buying.** Poetics are farmed, so if a retainer is
    holding mist the run says which one instead of spending on a second set.
  - **Nothing is ever clicked blind.** The Furnace's window is driven by positive
    identification only: a list entry by the weapon's own name, a shop row by the item id
    it hands back. If nothing matches, the step stops with the window's real wording logged
    rather than picking something arbitrary.

### Fixed

- **The beastman hunt could run with an empty main hand.** The auto-equip took the first
  relic weapon it found in the armoury, of any job. A relic can only be equipped by its own
  job and the game refuses the swap **silently**, so on a character with a second relic
  parked in the armoury the equip did nothing, the hunt ran unarmed, and the kills never
  credited. It now looks for the current job's relic first, and searches the off-hand
  armoury slot too (the Paladin's Holy Shield lives there).

- **The wrong job's Treasure Coffer.** Nine of the ten "A Relic Reborn" broken-weapon
  coffers share a stronghold with another job's — Zahar'ak has Paladin and Monk, U'Ghamaro
  has Warrior, Black Mage and White Mage, Natalan has Dragoon and Bard, Sapsa has Ninja and
  Scholar — and every one of them is named "Treasure Coffer". The finder took the nearest
  match, which for Warrior/Black Mage and Ninja/Scholar is a coin flip (they are authored at
  identical coordinates). Reported on Monk: it walked to the Paladin coffer and hammered an
  untargetable object until the step timed out. Only the coffer belonging to the quest step
  you are on is targetable, so that is now what the finder prefers.

- **Items in your inventory did not count if they were HQ.** The counter asked the game for
  NQ copies only. Most things the plugin counts have no HQ form so it never showed, but the
  "A Relic Reborn" class weapon is routinely bought or crafted HQ — and read as "0 / 1"
  while sitting in the bag. Both qualities are counted now.

- **The run climbed to the second storey to reach Rowena.** Her approach anchor is a map
  coordinate, which has no height, and the floor probe casts downward from above — so inside
  Rowena's House of Splendors it resolved to the upper floor. The run went up, waited for her
  to load, then walked back down. It now stops once it is at the anchor horizontally and lets
  her stream in, then goes to where she actually is.

## 1.5.2.5 — Finishing a relic sent the run into another job's line

### Fixed

- **Finishing the base relic dropped the engine's sense of progress, and it wandered into
  another job.** Reported on Bard: the Artemis Bow arrives, and the run immediately shows
  a **Monk** objective and goes to buy a *second* quenching oil.

  Gerolt hands the finished relic over **unequipped**, and which stage you are on is read
  off the weapon in your hands. So for the window between receiving it and putting it on,
  the engine saw no relic at all — which reads as *no relic progress at all*, and
  selection falls through to whatever sorts first: another job's base relic.

  Three fixes, because one alone would have left the same hole open elsewhere:

  - The line now **equips the relic** as its last step. Every stage transition has this
    shape — each upgrade hands the new weapon back unequipped too — so this is worth doing
    at the source, and it is what you want anyway, since the Zenith trade needs the weapon
    findable.
  - The progress floor comes from **the highest relic held anywhere** — hands, armoury, or
    bags — not just an equipped one. It previously looked only for a *Zenith* sitting
    unequipped, so a bare finished base relic, which is exactly what the line hands you at
    the end, was invisible to it.
  - A base-relic objective gated to a quest sequence is now only a candidate once **its own
    job's quest** has reached that sequence. That gate existed but was only applied while
    the equipped job was mid-relic; outside that, Monk's oil purchase (gated to sequence
    19) was eligible with Monk's quest sitting at 0, purely because it was incomplete.

- **Purchases check your retainers first.** Poetics are farmed, so buying a second
  quenching oil while one sits in a retainer's bag is wasted farming. Retainer contents
  can't be read unless a retainer is open, so this uses the cache the plugin builds during
  its own retainer visits — and only in the direction that is safe when slightly stale: it
  can say "you already have one, don't buy", never "you don't have one". It stops and
  names the retainer rather than withdrawing, since that needs a summoning bell trip.

### Notes

"Next step: Zenith — 3 Thavnairian Mist" after finishing the relic is correct, not part of
the bug. Zenith is the next stage, and that trade at the Furnace is still manual.

## 1.5.2.4 — The purchase confirmation was never answered

### Fixed

- **Selecting the oil raised the Yes/No confirmation and nothing clicked it.** Buying
  anything in this game always raises that prompt after the item is picked — and it opens
  *on top of* the shop window, which stays open underneath. The step checked the shop
  first, so it kept re-firing "Exchange" at a window that was blocked waiting on a prompt
  nobody was answering, until the stuck-menu watchdog gave up.

  The confirmation is now answered before anything else touches a shop window, and
  answering it is all that happens on that tick — re-running the item pick while the
  prompt is up fires a selection at the blocked window.

  The same ordering has been applied to the Trials of the Braves book purchase, which had
  the same shape (it confirmed *and* re-picked on the same tick). The treasure-map restock
  already did this correctly.

## 1.5.2.3 — Buying the quenching oil opened the wrong Auriana exchange

### Fixed

- **The oil purchase went into Auriana's first Poetics option — the gear one — and sat
  there.** She does not offer *one* Poetics exchange; she offers several, and every one of
  them is named "Allagan Tomestones of Poetics (...)". The step matched on the word
  "poetics", so it always took whichever was listed first: the Disciple of War arms grid,
  which of course does not stock the oil. The symptom was a repeating *"the Poetics
  exchange is open but the oil (item 6267) is not listed"*.

  The relic materials are under **Special Arms**, so that is what it looks for now. But
  rather than swap one guessed word for another, the step works off her actual menu: it
  ranks her live entries, tries the most likely first, and — this is the part that makes
  it robust — if a grid opens **without** the oil in it, closes that grid and tries the
  next entry. Her map exchange and the leave/cancel lines are skipped; everything else
  gets a turn. So the right category is reached even if the wording is not what we expect,
  and the only way to fail is genuinely running out of options, which now says so plainly
  and lists what it tried instead of stalling on a watchdog.

## 1.5.2.2 — The beastman hunt stops walking past the enemies it needs

### Changed

- **The 24-beastman hunt is one step now, not three.** The journal asks for eight each of
  three types, and those three spawn groups are mixed together across a single
  stronghold — so killing them one type at a time meant walking past two thirds of the
  enemies that still needed killing, then walking the same ground again, and again.
  Reported as the hunt taking significantly longer than it should.

  It now takes whichever wanted type is **nearest**, so the stronghold is cleared in
  roughly one pass instead of three.

  The quest caps each type at eight and silently ignores kills past that, so a type is
  retired once it stops counting — otherwise the last few kills would go to whatever
  happened to be standing closest. Two independent signals retire a type: eight local
  kills of it, and (surviving a re-plan, which resets local counts) two kills that
  produced no rise in the quest's own counter, each judged only after a 5s grace so a
  credit landing a frame late cannot be mistaken for a cap. If every type ends up retired
  while the hunt is unfinished, the retirements are thrown away and all three are
  re-offered — a wrong guess costs a few kills, never a stall.

  Completion is unchanged: the sum of the three quest counters reaching 24.

### Fixed

- **The unfinished relic is equipped the moment Gerolt hands it over** (sequence 9),
  rather than being left in a bag for the hunt objective to notice. The hunt is a long
  trip to a stronghold; arriving to find the weapon was never equipped cost the trip.

  Auto-equip also stopped giving up after one look. The weapon takes a server round-trip
  to land in your bags after a turn-in, so the single check at step start usually missed
  it and failed with "none found" for a weapon that was about to appear. It now retries
  for up to 10 seconds.

- **"Give the unfinished \<weapon\> to Gerolt" (sequence 14) takes the weapon off first.**
  The hand-over UI lists your inventory and armoury but not what is in your hands, so that
  step could never be satisfied while the relic was equipped. If the turn-in does not
  happen — aborted, failed, re-planned — the weapon goes straight back on, so a stalled
  step cannot leave you bare-handed.

- **Unequipping for a turn-in no longer makes the engine forget your progress.** Which
  stage you are on is read off the equipped weapon, so for the length of any trip that
  requires the weapon off — the two Jalzahn trades and the sequence-14 hand-over — that
  read was "no relic at all", which re-opened stages you finished long ago for selection.
  Those steps now record the tier before unequipping, and planning falls back to it while
  the hands are empty. Deliberately narrow: a live read always wins, the stand-in is
  dropped the instant a relic is equipped again, and it expires on its own.

## 1.5.2.1 — "A Relic Reborn" was missing both Rowena steps

### Fixed

- **The base-relic quest table skipped Rowena entirely, so everything from the class
  weapon through Amdapor Keep was gated two sequences too late.** Reported live: parked
  on *"Speak with Rowena"* at sequence 6 with the run trying to queue the Chimera.

  "A Relic Reborn" sends you to Rowena at Revenant's Toll twice — she is the one who
  asks for the Amdapor Glyph, and she is the one who hands you the tome copy Gerolt
  wants. Neither step was authored, and the table had been closed up over the gap:

  | Journal step | Was | Now |
  | --- | --- | --- |
  | Deliver the melded class weapon to Gerolt | 5 | **3** |
  | The Chimera → Alumina Salts | 6 | **4** |
  | Deliver the Alumina Salts to Gerolt | 7 | **5** |
  | Speak with Rowena, Revenant's Toll | *missing* | **6** |
  | Amdapor Keep → Amdapor Glyph | 8 | **7** |
  | Deliver the Amdapor Glyph to Rowena | *manual* | **8** |
  | Deliver the tome copy to Gerolt | 9 | 9 |

  Both Rowena visits are now driven the same way every Gerolt turn-in is (teleport,
  approach, interact, TextAdvance carries the dialogue), so the line no longer needs a
  hand from the player between the Chimera and the beastman hunt.

  The tail of the quest — the hunt at 10, the Hydra at 12, the hand-over at 14, the three
  primals at 15–17, the delivery at 18 — was already correct and is unchanged. That is
  why this went unnoticed: only the head was shifted, and it re-converged at sequence 9.

- The class-weapon step is **one** journal entry (sequence 3), not three. It was authored
  as obtain 3 / meld 4 / deliver 5, which is where the shift originated — buying the
  weapon and melding the two materia are preparation the quest never tracks. The
  `/relic` panel for it now opens as soon as the line is underway instead of waiting for
  sequence 3, so there is time to line the weapon up while the timeworn one is fetched.

- **The final turn-in could never fire.** The oil step was gated to exactly sequence 255,
  and the last journal entry is 19. It is a lower bound now, so it runs under either
  convention. The Bard quest-path file also carried an auto-generated `Sequence: 255`
  block that would have walked you to Gerolt without the oil and then waited forever;
  it is gone, and all ten jobs finish through the same objective.

## 1.5.2.0 — BossMod Reborn avoidance no longer steals your target

### Fixed

- **The BossMod Reborn avoidance preset defaulted to `"VBM Multibox"`, which hijacked
  targeting.** That preset contains `MiscAI.AutoTarget [Retarget=Always]`, which writes
  `Hints.ForcedTarget` every frame — BossMod copies that straight into
  `TargetSystem->Target`, so it overwrote the hard target belonging to whichever plugin
  was actually running the rotation. It also contains `MiscAI.FollowSlot`, which walks
  the character into melee against vnavmesh.

  The avoidance path is active whenever the combat backend is *not* BossMod Reborn — so
  this affected the Rotation Solver Reborn backend and, as of 1.5.1.0, Wrath Combo.
  Relicable's own config window already warned that this preset fights navigation, and
  then shipped it as the default.

  Relicable now installs and uses its own **"Relicable Avoidance"** preset, containing
  exactly one module: `MiscAI.NormalMovement`. That module is pure movement — it never
  assigns `Hints.ForcedTarget` and never touches `TargetSystem`. Omitting `AutoTarget`
  entirely is stronger than setting its `Retarget` track to `Never`, because a module
  absent from a preset is never instantiated at all.

  Existing configurations are migrated once: a saved `"VBM Multibox"` becomes blank
  (= use the built-in preset). A deliberate later choice is preserved.

- The config window's AI-preset warning now applies to the **avoidance** field too, which
  is the field it was always describing. It is name-based now, so it can be asked about
  either field without the avoidance field flagging itself.

### Notes

Re-verified against the installed BossMod Reborn 7.5.1.35 that avoidance does **not**
require BossMod's AI loop: `ExecuteHints()` runs unconditionally, and the preset's
modules run gated only on a preset being active. So keeping `/bmrai` off — which
Relicable does everywhere, because BossMod's `AIBehaviour` reassigns the active preset
every frame — costs nothing here.

Two honest limits of preset-based avoidance: it acts only while you are standing still,
and it stands aside for vnavmesh while that is moving you (BossMod checks the shared
`vnav.PathIsRunning` flag). So it dodges between navigation legs, not during travel.

## 1.5.1.0 — Wrath Combo support

### Added

- **Wrath Combo is now a supported combat backend**, alongside BossMod Reborn and
  Rotation Solver Reborn. Select it in `/relic config` → Combat backend.

  Wrath is *lease-based*, which makes it different from the other two: Relicable
  registers for control, and while that lease is held Wrath names Relicable as the owner
  of the settings it drives and locks them. So the lease is taken only when combat
  actually engages — not when you select the backend or travel to an objective — and it
  is handed back both when Relicable unloads and when you switch to a different backend.
  Turning auto-rotation off is not enough to release it; without an explicit release you
  would be left unable to edit your own Wrath settings.

  Wrath has no "manual mode" switch of its own, so the neutral relic-note grind pins
  `DPSRotationMode` to Manual and clears both in-combat gates (`InCombatOnly` and
  `OnlyAttackInCombat`) — otherwise Wrath waits for combat that never starts and the
  character stands over the mob doing nothing. In FATEs it sets `FATEPriority`, and
  `BypassFATE` so the rotation can open on a FATE mob out of combat.

  Note that Wrath hard-skips auto-rotation while mounted or occupied, and no setting
  relaxes that — the dismount-before-engage ordering in the executors is load-bearing
  for this backend.

- **`Let Relicable configure Wrath's Auto-Rotation`** (on by default). Turn it off to
  have Relicable only switch auto-rotation on and off and leave the rest of your Wrath
  configuration untouched. The relic grind will then stall on neutral enemies unless you
  have already cleared Wrath's in-combat options yourself, and the config window says so.

- **`FATE targeting`** for Wrath: how it picks targets inside a FATE. Setting it to
  Manual hands targeting back to Relicable.

### Notes

The IPC surface was verified against Wrath's own source and the shipped
`WrathCombo.API.dll` rather than its published example — that example's copy of the
configuration-option enum is truncated and omits half the options used here.

## 1.5.0.0 — first public Early Alpha

The first build prepared for public release. Everything below the surface is the
same engine that has been in private development through 1.4.x; this release is
about making it installable and supportable by people other than the author.

### Added

- **Early Alpha access gate.** Relicable now requires a signed access code to run.
  Codes are ECDSA P-256 signatures issued individually, carrying the name they were
  issued to and an expiry date. The plugin ships only the public key, so codes cannot
  be forged. The issued-to name is displayed in the main window while the plugin is in
  use. See `tools/RelicableKeygen/README.md`.
- **`tools/RelicableKeygen`** — the offline generator for issuing, verifying and
  revoking access codes. It shares `AlphaCode.cs` with the plugin by source reference,
  so the minting and verifying sides cannot drift apart.
- **GitHub release pipeline** — a tagged push builds the plugin on Windows, packages it,
  and attaches it to a prerelease.
- **`repo.json`** — a Dalamud third-party repository manifest, so the plugin can be
  installed from a URL instead of built from source.
- **Issue templates** that ask for the version, stage, combat backend and debug log.

### Changed

- **Documentation rewritten for public use.** The README now describes the plugin as it
  actually is rather than as a scaffold, and states the User Agreement risk up front.
  `BUILDING.md` was corrected — it documented .NET 9 and `net9.0-windows` while the
  project has required the .NET 10 SDK and Dalamud API 15 for some time.
- **Diagnostic subcommands are gated.** `adcfg`, `adset`, `bravesseq`, `questwork`,
  `mahatma` and `prereq` still work, but only with *Enable debug log* turned on in
  `/relic config`, and they are no longer advertised in the command help. `adset` in
  particular writes into another plugin's live configuration and should not be reachable
  by typing a word after `/relic`.
- **Book dungeon territory remapping is now debug-level logging.** It previously wrote
  several lines of raw `TerritoryType` internals to the Dalamud log on every plugin load.
- The development changelog — roughly 2,900 lines living as an XML comment inside
  `Relicable.csproj` — was moved out. Release notes live here now.

### Removed

- **The Splatoon integration.** `SplatoonLocatorIpc` and the `RelicableLocator` script
  are gone, along with the `/sf` shortcut on the objective name.

  It was never reachable in practice: it required hand-loading a custom script into
  Splatoon, a step no documentation described, and both call sites already fell through
  to the authored coordinate path in every real install. It had also been progressively
  narrowed after it caused FATE staging to strand runs in the wrong ring. Both executors
  now use the authored coordinate directly — the path that was already running for
  everyone. Clicking an objective name now drops a map flag and travels there, which
  works with no extra plugin installed.

### Fixed

- Developer machine paths (`C:\Users\...`) removed from the build documentation, the
  project file, and the rotation template.
- `RepoUrl` in the plugin manifest was empty, so the in-game installer showed no project
  link.
