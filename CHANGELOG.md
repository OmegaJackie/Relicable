# Changelog

## 1.5.9.7 — The market board trip stops in front of the board, not inside it

### Fixed

- **"Market board (Limsa)" on the class-weapon step walked you into a wall.** The very first thing
  the base relic asks you to line up is the class weapon and its two Grade III materia, and the
  step's travel button flagged the wrong spot: it used the market board object's own position out
  of the zone's layer data, which sits *inside* the counter. vnav pathed at the geometry and you
  ended up pressed against it with no board in reach. The flag is now the standing spot in front
  of the board, recorded off the navmesh in-game — same zone, same board, roughly four yalms out.

## 1.5.9.6 — One trip for every Braves quest, not one trip each

### Changed

- **The accept trip now takes every quest that is available, in one sweep.** Three of the four
  material quests are given within a few yalms of each other in Revenant's Toll — Papana, Guiding
  Star and Brangwine — and the fourth (Adkin) is in Central Thanalan. One objective per quest meant
  a full re-plan and a fresh approach between neighbours standing side by side, and because the
  order was the fixed accept order, the run went Mor Dhona → Central Thanalan → **back to Mor
  Dhona** for the third of them.

  A single objective now carries one stop per takeable quest, ordered so the trip is a sweep:

  - "Wherefore Art Thou, Zodiac" first whenever it is not in hand — a hard rule, not a preference,
    since none of the four are *offered* until it is taken, so proximity can never outrank it;
  - then the zone you are standing in, which costs no teleport at all;
  - then the remaining zones one at a time, so each is visited once;
  - nearest stop first inside each zone, chained from where the previous one left you.

  The objective's progress now reads "n / m quests accepted" rather than a single done/not-done.

- **Skip Step follows the same grain.** On an accept it skips that one *quest* and the trip carries
  on to the next giver, instead of throwing away the stops the sweep had left. On a report it is
  unchanged (a report is its own objective).

- A failed accept is now blamed on **the quest that stop was for**, not the objective's headline
  quest — with several stops per objective, the old bookkeeping would have blacklisted the wrong
  quest and retried the failing one forever.

## 1.5.9.5 — The weapon in your hands outranks the weapons in your armoury

### Fixed

- **A finished relic in the armoury shut down the stage for the relic you were building.** With an
  equipped **Nexus** weapon, one finished relic held and 'Relics to build' at 1, every Braves path
  declined work and the run stopped — asking you to change a setting before it would carry on. That
  is not automation.

  The held-count test answers *"should I start another relic?"* — which is exactly what 'Relics to
  build' is for. It cannot answer *"is the one I am building finished?"*, and the two were the same
  check. The equipped weapon answers the second question, which is the authority the stage's entry
  gate already used and the reason it was written that way. So: **a relic below the il125 weapon in
  your hands means the Braves stage is not finished, whatever is parked in the armoury.** Nothing
  equipped still falls through to the count — there is no weapon to speak for.

  'Relics to build' keeps its meaning for the case it was written for: finish the weapon you are
  carrying, then stop offering to start a new line.

- **A turn-in that cannot succeed is tried once now, not three times.** A report the engine drove
  that did not advance the quest is remembered for the run (quest **and** step, so the quest's next
  step gets its own attempt), exactly as a failed quest accept already was. Before, the same
  round trip ran three times and spent the whole failure budget, halting a run that still had
  dungeons to farm and quests to accept. A fresh Start re-tries — by then you may have bought the
  missing items.

- **The stop now names what is missing.** A report that was driven and refused is reported as
  `A Ponze of Flesh -> Papana (Revenant's Toll, Mor Dhona) [tried: Papana could not take it; still
  short: Bombard Core, Sacred Spring Water, Bronze Lake Crystal, Perfect Firewood, Furnace Ring]`,
  read from the live plan. The old text said "turn in what you have" for a turn-in that had just
  been proven impossible, and left you to work out why in another window.

## 1.5.9.4 — A finished Braves stage stops reporting its leftovers

### Fixed

- **A finished Braves stage still marched to Papana forever.** Reported live, with the readout from
  the new Skip Step tooltip: 'A Ponze of Flesh' — quest 65893, completed **yes**, sitting **accepted
  again at step 1**, reward **not** banked — while a finished relic weapon was already held and
  'Relics to build' was 1. Every run picked "report to Papana to advance the quest", the dialogue
  ended with nothing advanced, and three strikes later the run halted.

  The stage's "already done, never take these quests again" guard was asked by the entry gate, by
  the accept path and by the retainer-fetch path — but **not by the report path**, which is the one
  route that only needs a quest to be *accepted*. Nothing else could catch it either: the reward
  item is the per-quest "already delivered" witness, and 'His Dark Materia' consumes all four when
  the weapon is made, so a leftover copy of a quest from a finished weapon looks exactly like one
  waiting to be handed in. The report path now asks the same question as its siblings, and names any
  leftover it is ignoring.

- **The stop message for a finished stage described a stage still in progress.** "No dungeon item is
  being requested right now… gather the vendor/crafted items and turn in what you have" sent you
  hunting for 100,000-gil materials you did not need. When the stage is done by the held-count rule
  it now says so — how many finished weapons you hold against 'Relics to build', that this is why
  nothing Braves will run, and that raising that count (or ticking 'Repeat completed stages') is
  what re-opens it — plus a line for each leftover quest still sitting in the journal, with what it
  is and why it cannot be turned in.

## 1.5.9.3 — Skip Step, for a Braves quest step you have already handled

### Added

- **"Skip Step" on the main window**, the way Questionable offers one — but only while the running
  step is a Braves quest step: *accepting* one of the material quests, or *reporting* one to its NPC.
  Both decide they are finished by reading the quest out of game memory (in hand / its sequence
  changed), and both drive an NPC who may simply have nothing to say. When that read disagrees with
  what you can plainly see in your own journal, the run has no way out from the inside: it
  re-selects the objective, travels back to the same NPC, and the conversation ends with nothing
  advanced — the reported Papana loop, which then burns three strikes and halts the run.

  Pressing Skip stops that trip, takes the objective out of selection, and re-plans onto the rest of
  the stage. It survives Stop/Start on purpose — a skip is your answer, not the engine's inference,
  so clearing it on the next Start would send the run straight back to the NPC. Reload the plugin to
  un-skip.

  A skip lands at the grain that matches the step: an *accept* is skipped for the whole session,
  while a *report* is skipped only for the quest step it was pressed on, so waving off a turn-in you
  cannot satisfy today does not silence the next one once the quest moves on. The stop guidance
  names whatever you skipped, so a stage that goes quiet afterwards says why.

  No other step gets one: every other step either completes on a game-state flag that cannot lie (an
  item held, a duty cleared, a gauge filled) or fails into the 3-strike backoff, and a general "skip
  anything" button would mostly be a way to desync the engine from the game.

- **The button's tooltip shows the numbers the engine actually read** for that quest — its quest id,
  live sequence, completed flag and whether its reward item is banked — so a "but I already did
  this" disagreement can be diagnosed instead of argued with. The same line goes to the debug log on
  every skip.

## 1.5.9.2 — A finished Braves quest stays finished

### Fixed

- **Completing a Braves material quest re-accepted it on the spot, then looped on a report it could
  not advance.** Reported live: 'A Ponze of Flesh' turned in, immediately picked up again, and the
  run parked on "report to Papana" while the fresh quest — sitting at its first step with the
  vendor/crafted items already consumed — had nothing to hand over.

  The four material quests are repeatable, so the moment one completes its sequence returns to 0
  and it reads exactly like "never accepted" — and the existing guard (do you already hold a
  finished Braves weapon?) only helps once the *whole stage* is done, which is four quests and a
  finisher too late. Mid-stage, nothing testified that a quest was already delivered.

  Something does, though: each quest's **reward item**. A Ponze of Flesh pays a Book of Skylight,
  Labor of Love a Zodium, Method in His Malice a Zodiac Scroll and A Treasured Mother a Flawless
  Alexandrite (verified against the game's quest sheet), and 'His Dark Materia' consumes all four
  only at the very end of the stage. Holding one is proof its quest is delivered for the weapon in
  progress — and it survives reloads, relogs and job changes.

  All four quests now honour that witness everywhere the engine decides Braves work: a delivered
  quest is never re-accepted, never chosen for an NPC report, and none of its dungeons are run —
  even if a stray accepted copy is still sitting in the journal from before this fix (that copy is
  ignored, named once in the log, and simply serves the next weapon). When all four rewards are
  banked the run now says the real remaining step: complete 'His Dark Materia' at Gerolt
  (Hyrstmill, North Shroud) — or Jalzahn's 'Zodiac Weapon Recreation' on a repeat weapon.

- **The Braves shopping list re-demanded materials a finished quest had already eaten.** A
  delivered quest's rows now count zero (and the stage-wide Bombard Core / Sacred Spring Water
  rows shrink by one per delivered quest), so the totals, the window and the retainer auto-fetch
  stop asking you to re-buy 100,000-gil items for quests that are done.

- **The four reward items are protected from auto-discard**, alongside the other relic-line items:
  losing one would both un-prove the quest and cost the whole set of materials it was traded for.

## 1.5.9.1 — Avoidance survived exactly one pull

### Fixed

- **AoE avoidance stopped working after the first fight of every step, which in a FATE means it
  was never on.** Relicable hands BossMod Reborn its avoidance preset with `Presets.SetActive`,
  and re-sends it only when the *name changes* — sending the same name every tick would thrash it.
  But BossMod Reborn takes that slot back on its own: its `ClearPresetOnCombatEnd` and
  `ClearPresetOnDeath` settings null the active preset whenever combat drops or you die. Relicable
  never saw the clear, so the name it wanted was still the name it last sent, and it never sent it
  again.

  In a FATE combat ends between every wave, so avoidance lasted one pull and was gone for the rest
  of the step. The same latch drives the *rotation* preset under the BossMod Reborn backend, so a
  mid-fight death quietly ended the rotation too.

  Relicable now re-checks the slot every two seconds while engaged and puts its preset back when
  it finds it **empty**. A different preset is left alone — switching by hand mid-run still wins.
  A preset name BossMod Reborn does not have is latched after the first refusal instead of being
  retried (and re-warned) every two seconds.

- **The "Use BossMod Reborn AoE avoidance" checkbox did nothing at all under the BossMod Reborn
  backend**, which is the default. Only one preset can be active at a time, so a separate avoidance
  preset would have evicted the rotation — and the built-in combat preset had no movement module in
  it. The result was a ticked box that could not dodge anything.

  The movement module is now merged into the combat preset itself when the box is ticked, so one
  preset does both. Unticking it removes the module again from the next step onwards.

### Changed

- **The avoidance and combat preset boxes are now dropdowns** listing the presets you actually have
  in BossMod Reborn, instead of a name you had to type exactly. The first entry is always
  Relicable's own built-in preset. BossMod Reborn hides its stock presets when you have asked it to,
  and the list honours that. A configured preset that has since been deleted stays visible and
  selected, marked "(not found)", so opening the list cannot silently discard it. If BossMod Reborn
  is not installed the plain text box is shown as before.

- **Honest wording about what avoidance can do in a FATE.** BossMod Reborn has no scripted
  timelines for ARR overworld content, so out there it can only sidestep what it infers from an
  enemy's live cast. The setting now says so rather than implying full coverage.

## 1.5.9.0 — Braves work needs a Braves-ready weapon, not just a Braves quest

### Fixed

- **A second relic skipped Atma, Animus, Novus and Nexus and ran Braves dungeons instead.** The
  Braves block asked two questions, and neither of them was *has this weapon got that far?*

  Pool membership only means **incomplete**, and a Braves dungeon objective is incomplete whenever
  its drop is absent from your key items — true for everyone below the stage. The end-item test was
  supposed to catch that, but it counts finished weapons across **everything you own**, which is the
  wrong question when you are building a *second* relic: with one finished Zodiac Braves or Zeta
  weapon and "Relics to build" at 2, held(1) &lt; target(2), so it answered "not satisfied" at every
  stage of the new weapon.

  Both being true at Atma, the block took the pool over — and because it *replaces* the pool rather
  than adding to it, every stage between Atma and Braves was skipped outright. Reported live: a
  Summoner on Atma being sent repeatedly through the Tam-Tara Deepcroft, whose drop belongs to
  'A Treasured Mother' — a quest four stages ahead of the weapon.

  Braves work now also requires the **equipped** weapon to have reached Nexus. Equipped
  deliberately, and not the held count the end-item test uses: the Nexus → Braves upgrade operates
  on the weapon in your hands, while a held count sees the finished relic parked in your armoury and
  concludes the wrong thing. Manual mode is unaffected — pinning a stage is an explicit instruction
  and is honoured as before.

## 1.5.8.9 — Somebody has to press "Hand Over"

### Fixed

- **The quest delivery window was never clicked.** 1.5.8.8 caught the failed turn-ins and logged the
  addon chain that was up when they failed; on the sequence-14 hand-over it read exactly
  `menus open -> Request;`. That is the item-delivery window — TextAdvance carries the dialogue
  around it but does not press its button, so the window simply sat there until the conversation
  ended, and the quest never advanced.

  Turn-in steps now press **Hand Over** themselves, and confirm the prompt it raises. The button is
  driven through ECommons' verified `AddonMaster.Request`, and the click is a no-op unless the game
  has *enabled* it — which it does only once the requested items are actually in the window's slots.
  So a turn-in that cannot be satisfied (the item is not in your bags, or is still equipped) now
  clicks nothing and fails honestly, rather than handing over the wrong thing.

  Two guards keep this narrow: only a step that declares itself a turn-in may press a delivery
  button at all, and the follow-up "yes" is only ever given after *we* clicked Hand Over — never to
  a prompt that merely happened to be open, which is the same rule the coffer opener uses.

- **The final turn-in (sequence 19) is now driven and verified like the rest.** It delivers the
  quenching oil, so it had the same hole; it was the one hand-over not marked as such.

## 1.5.8.8 — A conversation ending is not a turn-in happening

### Fixed

- **Turn-ins reported success without the quest ever crediting.** `InteractNpcExecutor`'s only
  completion evidence was *the conversation ended* — and a conversation ends whether or not the
  hand-over actually took. So a turn-in that failed (the item not in the bags, the delivery window
  never driven, TextAdvance not carrying it) reported Complete anyway.

  What made that fatal rather than merely wrong: the controller then stamped the objective into its
  in-session ran-markers, so the **only step that could advance the quest was excluded from
  selection for the rest of the run** — and the next pass found nothing eligible and stopped,
  telling you to go report to Gerolt by hand. The engine had talked itself out of the one thing it
  needed to do.

  A turn-in step now names the sequence it exists to clear, and is judged on whether the quest
  actually moved past it (after a grace window for the server round-trip). If it didn't, the step
  fails and the run retries instead of marking it done. This is the rule `UpgradeRelicExecutor`
  already applied to relic upgrades — it refuses to call a conversation successful unless the weapon
  really changed — now extended to quest turn-ins. Applies to every Gerolt and Rowena hand-over
  (sequences 2, 3, 5, 6, 8, 9, 11, 13, 14, 18); the sequence-0 accept keeps its existing watchdog.

### Added

- **The delivery window is captured in the log while it is open.** A turn-in that doesn't take is
  usually one whose hand-over addon nobody drove, and that window is long gone by the time the
  failure is detectable — so the open-addon chain is now recorded as it appears, and named in the
  failure message.

## 1.5.8.7 — The beastman hunt insists on the *unfinished* relic

### Fixed

- **The hunt could arm the wrong relic and cull 24 beastmen that credited nothing.** The equip step
  asked "is *a* relic equipped?" — but 'A Relic Reborn' credits its kills only to the
  **"Unfinished &lt;weapon&gt;"** Gerolt forges at sequence 9. A finished relic, a Zenith or an Atma
  of the *same job* is equippable and reads as a relic just as happily, so a character with an
  older weapon of that job in hand passed the step instantly and hunted with the wrong one. There
  is no signal when this happens: 24 kills that credit nothing look exactly like 24 that do.

  The equip now knows the difference, and *prefers* the unfinished form when locating one. No step
  had to be told which weapon it wants — holding an unfinished form is itself proof the base-relic
  quest is mid-flight, since it exists only between sequence 9 and the hand-back at 14. The check
  relaxes on its own after that, which keeps the primal trials and every Atma+ upgrade unchanged.

- **A wrong-tier weapon in hand meant the swap was never attempted.** The retry loop bailed out on
  "some relic is equipped", so it timed out without trying. It now retries against the same test it
  is judged by.

- **The relic search inherited the Arcanist hole fixed in 1.5.8.6.** It resolved the job from the
  raw ClassJob id, where Arcanist means "no job", which in this code path means **"accept any relic
  weapon"** — so a Summoner reading as Arcanist handed the equip whatever relic sorted first,
  including another job's. The game refuses a wrong-job equip *silently*, so the hunt then ran with
  the old weapon and the kills quietly failed to credit. It now resolves the job the same way the
  controller does.

- **The weapon is re-checked immediately before the first swing.** The existing check sat before a
  teleport and a cross-zone ride, blind to anything that took the weapon off in between. Free when
  nothing is wrong — the step completes on its first tick without moving an item.

## 1.5.8.6 — An Arcanist is a Summoner when their Summoner relic quest is open

### Fixed

- **Summoner stalled at the Chimera with "no selectable objective ... 0 Relic objective(s) for
  this job".** The active relic job was read from the ClassJob id alone, and **Arcanist (26) is
  deliberately unmapped** — it can become either Summoner or Scholar, so it resolved to `None`.
  Nothing degrades gracefully from there: every generated objective belongs to a specific job and
  the selection filter is an equality test, so the pool emptied outright and the run stopped while
  reporting the job as "Unknown".

  The ambiguity is only ambiguous in isolation. When one of the two candidates has its
  **"A Relic Reborn (&lt;weapon&gt;)" quest live**, that quest names the job outright — and the
  controller was already reading those rows to find the sequence it printed. It now consults them.
  Deliberately *not* a general "whatever relic quest is open" fallback: an unmapped id that isn't
  a documented ambiguity is a genuine non-relic job, and resolving that into someone else's line
  would route the run into work whose weapon the player cannot equip.

- **The failure message pointed at the wrong thing.** An unresolved job fell through to the generic
  dump, which advised checking whether the trial duties had failed to resolve by name — unrelated
  to the actual cause. An undetermined job now stops with its own message, naming the raw ClassJob
  id and title the game reported.

## 1.5.8.5 — The broken-weapon coffer is a game object, not a wiki coordinate

### Fixed

- **The Summoner broken-weapon step flew to the wrong part of the Sylphlands and opened nothing.**
  Part 1 navigated to a *transcribed map coordinate* and then hoped the coffer was nearby. The
  finder can only see objects within 100 yalms of you, and Summoner's authored anchor (25.0, 19.0)
  sits **181 yalms** from the real coffer — so the run arrived at empty ground, the coffer never
  streamed into the search, and the step burned its full 120-second timeout without opening
  anything.

  Summoner was the worst but not the only one: **Warrior (147y), Dragoon (165y), Black Mage (168y)
  and Ninja (180y)** were all outside the same radius, and Paladin and Scholar were inside it only
  by margin. Bard worked because its coffer had been hand-authored from the game sheets — which is
  precisely the fix now applied to the other nine.

  Every one of the ten coffers is now addressed by its own sheet row: the `EObj` whose quest link
  is that job's relic quest, its `EObjName`, and the exact world position (height included) from
  its `Level` row. The Bard row regenerates that hand-authored file's coffer to three decimals,
  which is the check on the other nine.

- **Ninja's part-1 object could never be found by name.** The step hard-coded the target name
  "Treasure Coffer" for all ten jobs, and authored no DataId to fall back on. Yoshimitsu's object
  is a **Banded Chest**, so the search had nothing to match. The object name is per-job data now.

- **The wrong coffer in a shared stronghold.** Nine of the ten coffers share a beastman camp with
  another job's, all under the same name, and the step passed no DataId — so which one it walked to
  was decided by distance between two objects that can sit a few yalms apart. Each step now carries
  its own object id, which outranks the name in the finder.

- **Sapsa Spawning Grounds teleported to whichever Western La Noscea aetheryte sorted first.** With
  the coffer's exact position now known, part 1 picks the *nearest* aetheryte to it rather than
  "an" aetheryte in the zone. No effect on the single-aetheryte zones.

## 1.5.8.4 — A finished stage is one whose end item you hold

### Fixed

- **The run re-accepted Braves material quests it had just finished.** Nothing anywhere could tell
  "done" from "never started". The four material quests are *repeatable*, so completing one returns
  its sequence to 0 — identical to never having taken it — and the materials are *consumed* at
  turn-in, so the key-item test reads the same before the stage and after it. Every signal the
  plugin consulted reset at exactly the moment the work finished, so it walked back to Papana and
  took 'A Ponze of Flesh' again.

  A stage is now judged finished by the one witness that survives: **you hold its end item.** That
  answer outlives a plugin reload, a relog and a job change, which an in-memory latch would not.

- **Braves work was selected two stages early.** The gate that decided whether to run the Braves
  block tested "is there a Braves objective in the pool?", documented as a no-op for every other
  stage. It was not. Pool membership only means *incomplete*, and a Braves dungeon objective is
  incomplete whenever its drop is absent from your key items — true for everyone who has not reached
  the stage. So an Animus- or Novus-tier weapon ran the Braves block on every pass: on a repeat
  relic it accepted the quests two stages ahead of the weapon, and on a first relic it burned four
  cross-zone accept trips per `/relic start` on quests the giver cannot offer. It now asks whether
  the stage's output already exists.

### Added

- **"Relics to build"** (default 1) and **"Repeat completed stages"**. At 1, a stage whose end item
  you already hold is finished and its quests are never offered again. Raise the count to build
  another weapon and the stage re-opens until you hold that many. The toggle ignores the count
  entirely — for deliberately re-running a stage, or when a finished weapon sits somewhere the count
  cannot see it (a retainer, the glamour dresser).

## 1.5.8.3 — The Braves report waits for the hand-over instead of the quest sequence

### Fixed

- **A material quest reported complete without delivering anything.** The report step treated a
  change in the quest's sequence number as proof the turn-in had happened. It is not: talking to the
  NPC can advance the quest by itself, and the Request (hand-over) window then opens on top of the
  *new* sequence. The step saw the sequence move, declared success and tore the interaction down
  with the items still in the bag — so the run continued as though the batch had been delivered.
  Doing it by hand worked, because a player finishes the window the executor walked away from.

  The hand-over windows are now driven and checked *before* the sequence is consulted, and while
  either the Request or the JournalResult summary is open the step is unfinished no matter what the
  sequence says. This also closes the same race just after the hand-over, where the quest advances
  while the JournalResult summary is still waiting to be confirmed.

## 1.5.8.2 — Auto-discard only ever touches enemy drops

### Fixed

- **Auto-discard destroyed crafting materials it was never meant to touch.** The feature is called
  "auto-discard mob drops", but it did not check whether an item came from a mob. It selected by
  description instead: common (white), stackable, not gear, not usable, vendor price at or under
  100. That also describes about 5,400 other items — among them the entire Trials of the Braves
  crafting pipeline. Runs lost Aged Eye of Fire and Aged Pestle Pieces (desynthed from 3,000 gil
  vendor items), Pumice, Belah'dian Silver, Electrum Sand, Silver Ingot, Sunstone and the Eel Pie
  ingredients — bought and staged for the eight HQ crafts, then deleted.

  The real fault was the shape of the rule, not a missing entry in it. Every guard was phrased as a
  reason to *keep* an item, and a list of reasons to keep is incomplete by construction: whatever
  nobody anticipated gets deleted. Adding the Braves materials to the protected set would have
  fixed those twelve items and left the next unanticipated ones exposed.

  So the question is now asked the other way round, and answered before anything is deleted: **does
  this item drop from an enemy?** Only items on a generated allowlist of known enemy drops are
  eligible. Anything crafted, gathered, fished, bought, desynthed, quest-related or simply not in
  the table is left alone. The check runs ahead of every other rule and binds both modes *and* the
  explicit "always discard" list — there is no setting that reaches a non-drop. If the catalogue is
  missing or unreadable the plugin discards nothing at all and the configuration window says so,
  rather than falling back to the old behaviour.

### Added

- `tools/gen_mob_drops.py` builds the allowlist from Garland Tools' per-item drop data, classifying
  only the items that could ever reach the discard call. Responses are cached, so re-running after
  a patch costs almost nothing.

## 1.5.8.1 — Turn-ins fire on the vendor and crafted delivery steps, not just dungeon batches

### Fixed

- **A material quest was not handed in when you had gathered everything.** The report/turn-in step
  only triggered when you were *holding a dungeon drop*, so the delivery steps that carry no drop —
  the first step of each quest (the Bombard Core / Sacred Spring Water / 100k-gil vendor item) and
  the final step (the two HQ "Perfect ..." crafts) — never fired. With all the items on you the run
  stopped with "no dungeon requested; gather the vendor/crafted items and turn in what you have" for
  a turn-in it could have done itself.

  The trigger is now "this quest has no dungeon drop left to farm at its current step" — i.e. a
  delivery is due — which covers every delivery step (dungeon batches, vendor items, and crafted
  items). The turn-in executor already hands over exactly what the game's Request window enables and
  fails with guidance only if an item is genuinely missing, so firing it whenever a delivery is due
  is safe. The same fallback was added to the Manual-pinned-to-Braves selection path, which had the
  identical gap.

## 1.5.8.0 — The run fetches its own quest materials

### Added

- **Braves quest materials are pulled off your retainers automatically.** The Bombard Core, Sacred
  Spring Water, the 100k-gil vendor item and the two HQ "Perfect ..." crafts per quest are bought,
  crafted or desynthed rather than farmed — so they habitually sit on a retainer. The run would then
  reach the report step and stop with "gather the vendor/crafted items" for items you already owned.

  Relicable now checks the quest materials against your bags and the last retainer scan, and when
  something is short but parked, it travels to a summoning bell (Revenant's Toll, where three of the
  four quest NPCs stand anyway), drives it, and cycles every retainer pulling the stacks in — using
  the same engine the Braves planner's Fetch buttons already used. This happens before the dungeons,
  so the materials are in hand long before a turn-in wants them.

  It runs only with "Pull items from retainers" enabled (Config), since with it off the fetch engine
  only *lists* what to drag, which an unattended run cannot act on. One bell trip per run, so a
  material a retainer appears to hold but cannot deliver can never put the run in a loop.

## 1.5.7.1 — All four Braves quests, and why one of them can't be taken

### Fixed

- **"A Treasured Mother" was never picked up.** Two things, and either alone was enough.

  The accept check ran *last*, after the dungeon check — so the moment one material quest was
  accepted at a step that requests a dungeon, that dungeon filled the queue and the accept branch was
  never reached again. Method in His Malice requests a dungeon at its very first step, so the run
  went straight into The Wanderer's Palace and A Treasured Mother, last in the order, was left
  behind. All outstanding stage quests are now taken *before* any dungeon work: they run
  simultaneously and each one accepted adds its own dungeons, so taking them all up front is both
  correct and faster.

  And the quest has a prerequisite nothing here knew about: it is gated behind the sidequest **"One
  Man's Trash"** from Ealdwine at Swiftperch, Western La Noscea. Until that is complete Brangwine
  does not offer it at all, so the trip could only come back empty-handed. That is an ordinary
  sidequest with its own steps — a quest engine's job, not this one's — so Relicable now detects the
  gate, skips the quest, keeps working the other three, and tells you exactly what to complete.

  A quest whose giver refuses it is also remembered for the rest of the run, so one un-offerable
  quest can no longer burn the failure backoff and halt a stage that still had dungeons to do.

## 1.5.7.0 — A Nexus weapon has somewhere to go, and a stopped run says why

### Added

- **Holding a Nexus weapon with nothing accepted now starts the Braves stage instead of stopping
  dead.** The stage's work only exists once its quests are in hand — until "Wherefore Art Thou,
  Zodiac" is taken from Jalzahn none of the four material quests are even offered, and until a
  material quest is accepted none of its dungeons are requested. So a freshly-upgraded Nexus weapon
  had *no* objective, and the run halted with an empty window.

  Relicable now travels to each giver and accepts them itself, in the order the game requires: the
  umbrella quest from Jalzahn at Hyrstmill, then A Ponze of Flesh (Papana), Labor of Love (Guiding
  Star), Method in His Malice (Adkin) and A Treasured Mother (Brangwine). Each one accepted makes its
  dungeons eligible immediately, so the run flows straight on into them. The four are repeatable, so
  a completed one is correctly treated as "needs accepting again" for the next weapon — only the
  one-time umbrella counts as done once it is done.

### Fixed

- **A stopped run showed a blank window.** Every reason Relicable halts for something *you* need to
  do — accept a quest, equip the right weapon, gather an item it does not farm — was written only to
  the debug log, which most people never have open. With no active objective the main window then
  rendered nothing at all, which is indistinguishable from a broken plugin. The reason is now kept
  and shown as "Next step" wherever the objective panel would be.

## 1.5.6.0 — A full Sphere Scroll takes itself to Jalzahn

### Added

- **Filling the Sphere Scroll now finishes the Novus stage.** At 75/75 (Paladin: Curtana 53 and Holy
  Shield 22) the run travels to Jalzahn at Hyrstmill, unequips the Animus weapon so it lists in his
  turn-in menu, takes "Relic Weapon Animus Enhancement", and re-equips the Novus weapon it hands
  back — then carries straight on into the Nexus Light farm. Previously the melding was where the
  Novus stage simply stopped, and the enhancement was yours to remember.

  "Is the scroll full" is read from the game's own infused counter, not from melds Relicable
  performed, so a scroll you melded entirely by hand counts. That counter only exists while the
  scroll's window is open, so it is now recorded whenever it is — open the scroll once and the count
  is remembered from then on. The Novus planner shows it, with a "Go to Jalzahn" button once it is
  full.

- **The Alexandrite farm stops when there is nothing left to spend it on.** Melding consumes one
  Alexandrite per meld, so a finished scroll leaves your stock near zero — which read as "below
  target, farm more" and would have parked the run on treasure maps with the enhancement waiting
  behind it.

### Fixed

- **The Novus planner's "Line" column was blank.** Its table pinned seven columns to hard-coded pixel
  widths with no horizontal scrolling. Those widths do not follow Dalamud's UI scale, and ImGui never
  shrinks a fixed column — so as soon as their total outgrew the window (a narrower window, a scaled
  style, the scrollbar appearing), the last column fell off the right edge, where its cells are
  skipped entirely rather than clipped. The table now sizes itself from real text metrics and scrolls
  sideways instead of dropping a column, and the window has a minimum size.

## 1.5.5.8 — Ask Jalzahn before spending 500 poetics

### Fixed

- **Finishing all nine books still sent the run back to G'Jusana to start over.** 1.5.5.7 fixed half
  of this. The other half was the rule underneath it: when Relicable had no record of driving the
  books itself, it decided between "this weapon's books are done" and "this is a previous relic's
  leftover note" by asking whether you had finished the Animus stage before — reading the "Trials of
  the Braves" quest.

  That quest is not evidence of anything here. It completes *early* in the Animus stage, not at the
  enhancement, so it reads as finished for anyone past their first book — first relic included. The
  rule therefore said "leftover note" to essentially everyone who reached book nine without an
  intact record, and bought book 1.

  The decision is now ordered by what being wrong costs. Guessing "the books are done" and being
  wrong costs one teleport: Jalzahn declines, and Relicable learns from that and buys book 1.
  Guessing "leftover note" and being wrong costs 500 poetics and all nine books again. So the run
  goes to Jalzahn first, every time, and only starts a fresh book run once the game itself has
  refused the enhancement — twice, since his turn-in menu is fiddly and one hiccup should not cost
  you a grind.

  A run that *does* have an intact record is unaffected: it went to Jalzahn before and still does.

## 1.5.5.7 — Finishing the books sends you to Jalzahn, not back to G'Jusana

### Fixed

- **Finishing your ninth book and reloading meant starting all nine again.** A finished Relic Note
  stays active forever, so "note 9, complete, on an Atma weapon" describes two different situations
  that look identical: a new relic carrying the *previous* one's leftover note, or a weapon that
  just finished its own books. Nothing in the game separates them, so Relicable remembered which it
  had driven — in memory only. Reload the plugin or restart the game between the last book and
  pressing Start, and that memory was gone: the run read a freshly-finished book as a leftover and
  bought book 1, for 500 poetics and a nine-book regrind.

  It is now remembered on disk, written the moment it is learned, and cleared once the weapon
  actually reaches Animus — so the next relic on the same job still grinds its own books rather
  than inheriting this one's answer.

- **On a first relic there was never any ambiguity to begin with.** A leftover note requires a
  previous relic to have left it there, which requires having finished the Animus stage before —
  and the game records that. So the first time through, a complete last book on an Atma weapon can
  only be that weapon's own nine books, whether Relicable drove them or you did by hand. It now
  goes straight to the enhancement instead of stopping to ask.

- **The decision says so out loud.** When Relicable does judge a complete note to be a previous
  relic's leftover, it now says that in the log at warning level — visible without turning on the
  debug log — along with what to do if the judgement is wrong. It spends 500 poetics and restarts a
  nine-book grind, which is not something that should happen quietly.

### Added

- **`/relic booksdone`** — tells Relicable that the equipped Atma weapon's nine books are finished,
  for when it has no record of its own: you did the books by hand, or the record was lost with a
  reload. `/relic start` then goes to Jalzahn for the Atma → Animus enhancement. It refuses if the
  weapon is not an Atma, and warns if the active book still has incomplete entries.

## 1.5.5.6 — Something aggroes, something fights back

### Added

- **An aggro watchdog that runs for every step, not just the fighting ones.** Each executor that
  fights already defended itself, but only in the branches that remembered to ask — and the ones
  that got missed were the travelling and waiting branches, which is exactly where a wandering mob
  lands on you. That is why "aggroed enemies aren't getting attacked" kept coming back: the fix was
  always per-branch, and there was always another branch.

  The watchdog watches from outside the executors, so it needs no cooperation from them. When
  something is engaged with you, nothing is engaged with *it*, and that has held for a few seconds,
  the run stops, targets the attacker, marks it and fights back — then hands the step straight back
  when the fight is over, with the step's own deadline credited for the time it spent fighting.

  It is a backstop and behaves like one. It never fires while anything already has the attacker
  targeted, so every path that already works stays untouched. It waits three times as long while
  you are still moving, since a mob picked up riding past a camp usually gives up on its own. And it
  leaves FATEs entirely alone — not just while you are standing in the ring, but for FATE mobs
  generally, including one that chases you back out of it. A FATE enemy you are not level-synced to
  is one your combat plugin will refuse to attack, so grabbing it would only produce a staring
  contest; the FATE step owns getting into the ring and syncing, and keeps that job.

  On by default; the delay is configurable in Settings ▸ Combat assist.

- **"Aggroed" is now read from the enmity table rather than from where a mob happens to be looking.**
  The old check asked whether an enemy's current target was you. That is a live pointer, not an
  enmity record: it reads as nothing at all while a mob is still running at you after pulling, and
  it swings onto a passing NPC or your chocobo for seconds at a time. Every miss meant the run
  concluded nothing was on it and walked away. The nameplate colour — orange for pulled, red for
  engaged — holds for the whole fight regardless, and that is what is checked now.

- **A warning when a fight is going nowhere.** Standing still, in combat, holding the thing that is
  hitting you, and its health has not moved for fifteen seconds: that is a line of sight, level
  sync or combat-plugin problem, and it now says so in the main window instead of looking like
  progress. Deliberately a report and not an intervention — re-sending a rotation command would
  hide the cause rather than fix it.

### Fixed

- **The teleport step stood still for twenty seconds *because* something was hitting it.** Teleport
  is refused in combat, so the step waited for combat to drop — but the mob beating on it was the
  reason combat would not drop, and nothing ever fought back. It now fights first and waits only
  when nothing is actually attacking; neither path spends an attempt.

- **The walk to a map flag now defends itself.** The longest single leg in the run — aetheryte to
  flag, routinely hundreds of yalms through populated zones — read no combat state at all. Worse,
  mounting is refused in combat, so once something aggroed the rest of the route was walked on foot
  with the mob in tow. Its idle-detection clock is frozen while fighting, so a long fight can no
  longer fail the step for a stall that never happened.

- **The leve run fights on the way in.** Every other phase of a leve — the fight, the lure, the
  markers, the protection hold, the escort — already stopped for an ambient hostile. Travel, the
  phase that covers the most ground, was the one that did not, so a mob picked up en route was
  carried all the way to the leve anchor.

- **The flight out to a FATE fights back too.** The approach to a FATE ring is a full cross-zone
  haul with the rotation off, and the only enemies it could see were ones already carrying that
  FATE's id — so an ordinary hostile that aggroed on the way was invisible to it at any distance.
  Its stall clock is frozen while fighting, so a long fight can no longer be mistaken for an
  unreachable ring and rotate you off a FATE that was fine.

## 1.5.5.5 — Teleport waits until it can actually cast

### Fixed

- **"Teleport issued" was never proof a teleport was happening.** `Telepo.Teleport` returning true
  only means the request was queued — everything upstream of it was invisible. Seen live: the
  request accepted, then fifteen seconds with no cast, no pending request and no zoning, while a
  manual Return (a different action, with no gil cost) worked from the same spot.

  The step now asks the action layer whether Teleport can be used *before* spending an attempt on
  it, and waits for the refusal to clear rather than firing blind into it. That is the prevention:
  the cast goes out the moment it can succeed, instead of on a retry clock. If the refusal never
  clears, the step reports the game's own status code instead of a generic failure.

- **A request that produced no cast is retried in three seconds, not fifteen.** The two failures —
  a cast that started and was interrupted, and a request that never produced one — are now told
  apart and each has its own clock. Attempts raised from three to five, so the whole sequence is
  shorter *and* more forgiving than before.

- **Combat is waited out rather than counted as a failure.** Teleport is refused outright in
  combat, which from Telepo's side looks identical to a request that did not take. Bounded, so a
  combat flag that never clears cannot hang the step.

- **Failures say what it would have cost and what you are carrying.** "It will not teleport" and
  "you cannot afford it" looked identical from every other signal.

- The teleport heartbeat now reports the action status and whether a cast was ever seen.

## 1.5.5.4 — The teleport step stands still while it casts

### Fixed

- **Teleports that started casting and then quietly did nothing.** Movement cancels the Teleport
  cast, and the teleport step never stopped moving. A vnavmesh path outlives the executor that
  issued it, so a step handing over mid-route — or a "Run next" click that re-plans while the
  character is still walking — left the run walking straight through its own five-second cast. The
  cast begins, dies silently, and the step then waits out its full fifteen-second attempt window
  before trying the same thing again. Three times. The step now halts any path that is still
  running, on every tick of the cast, so a move issued from anywhere is caught.

- **An interrupted cast is retried in seconds, not after the full attempt window.** A cast that was
  seen to start and then vanished without zoning was interrupted — a known-bad outcome rather than
  a slow one — so it re-casts after 2.5 seconds instead of 15.

- **It will no longer burn attempts where the cast cannot start at all.** Teleport cannot begin
  while airborne, and Telepo still reports the request as issued, so this was invisible: the run
  now lands first. Mid-conversation and cutscenes are waited out rather than counted as failures.
  Being mounted on the ground is fine and does not force a dismount.

- **A teleport heartbeat** (every three seconds) reports which wait condition is holding — casting,
  zoning, request pending, airborne, in an event — plus the destination and current territory, so
  "it will not teleport" is answerable from the log alone.

## 1.5.5.3 — Leve objective points no longer sit under the floor

### Fixed

- **Travelling to a leve, ending up too low to the ground and jittering.** The objective position
  comes from the leve sheet, and its height is regularly a few yalms *under* the walkable floor. The
  character then paths at a point it can never stand on: the arrived check never passes, the move is
  re-issued forever, and the landing probe — which searches *downward* — snaps to an underground
  surface. Up to now the only cure was an authored per-leve correction, which fixes exactly the
  leves someone has already run into and no others.

  The objective point is now resolved onto real walkable ground for every leve. The trick is
  probing from *above* it: vnavmesh keeps only floors at or below the probe height and returns the
  highest, so probing at the sheet height finds whatever lies under the ground and misses the real
  floor over it — which is why the existing downward landing probe could never repair this itself.
  The correction only ever lifts, and only when the ground genuinely comes back above the sheet
  point, so leves that work today are untouched.

- **Short hops to a leve were handed a flight path they could not follow.** Leve travel passed the
  bare "is flying allowed here" gate as the fly flag. On a leg too short to mount for, that gave a
  still-grounded character a 3D path, which vnavmesh stalls on — more shuffling. It now uses the
  same rule as every other travel in the plugin: fly only when already airborne, or mounted with a
  leg long enough to be worth taking off for.

- **A leve approach that cannot make progress now recovers instead of grinding.** Fifteen seconds
  without getting closer re-resolves the objective position and forces a fresh path, rather than
  re-issuing the same dead one until the leve's five-minute timeout.

## 1.5.5.2 — Drop level sync rather than die to a FATE

### Added

- **A FATE you are losing now costs you the FATE, not a death.** Level sync is what lets you fight
  a FATE at all — and it is also what lets a boss FATE kill a relic-geared character, since it
  squashes your health pool and mitigation down to the FATE's level. Below 10% health (adjustable),
  Relicable now turns sync off: you snap back to full level, full health and full mitigation
  against enemies that are suddenly far beneath you.

  That forfeits the FATE's credit, which is the trade — dying costs far more, because recovery
  Returns you to a home aetheryte and restarts the whole objective from its teleport. The run holds
  and defends itself while unsynced (safe, at full level, and the quickest way out of combat and
  into regen), then syncs back in and resumes FATEs once health is back above 60%.

- **It will not bail out of a fight you are about to win.** The threshold alone is not the trigger:
  the run compares time-to-kill against time-to-die, both measured from health that has actually
  moved over the last three seconds rather than assumed. If the enemy dies first, it stays synced
  and finishes the FATE. Rates unknown — nothing landing yet, no target — counts as losing, so a
  fight it cannot read is one it escapes.

  Toggle and both thresholds live under Configuration → Animus.

## 1.5.5.1 — A blocked shot re-paths instead of shuffling on the spot

### Fixed

- **Jittering on the spot against an enemy on another elevation.** When terrain blocked the line to
  a target the run had already closed on, it shuffled in place indefinitely and never attacked.
  Two causes:

  The raycast was acted on frame by frame. On a marginal line — a mob pacing behind a rock, a
  slope, your own drift — it flips constantly, so the run alternated between "close in to clear the
  block" and stopping, once per frame. vnavmesh's stop is not edge-triggered: each call also throws
  away the cached destination, so every one of those flips re-pathed from scratch. That is the
  jitter. The reading is now debounced in both directions before it is acted on, and standing to
  fight stops once rather than every frame.

  And nothing ever timed out. The existing unreachable-mob guard only watches the *approach*, so a
  mob you are standing under a ledge from is "in range" and no guard was ever looking at it.

### Added

- **A blocked shot now escalates on a clock.** Three seconds without a line and the run forces a
  real re-path — and re-aims it. It samples the navmesh in a ring around the target and heads for
  the nearest spot that actually *has* a clear line, which on an elevation break is a spot on the
  target's own tier, so the path routes around and up to it. Re-aiming at the target's centre (what
  closing in does) is useless there — that is the direction you are already pressed into the cliff
  from. Retried every four seconds.

  If fifteen seconds of that still yields no shot, the mob is blacklisted for twenty seconds and
  another is taken — the same non-failing escape the approach guard already had, so a target that
  simply cannot be reached costs the run a few seconds instead of the rest of the session.

## 1.5.5.0 — Escort leves walk instead of shuffling

### Fixed

- **Escort leves ("Pets Are Family Too" and friends) moved in tiny stop-start steps.** Two separate
  stalls, both of them `Stop()` being called every frame:

  The escort NPC follows at roughly your own pace, so the gap to it sits *on* the single 8-yalm
  "has it fallen behind?" threshold and crosses it constantly. Every frame on the far side issued a
  full stop, and every frame on the near side started a fresh path — step, halt, step, halt. That
  check now has two bands: it pauses once the NPC is genuinely 12 yalms back, and only sets off
  again once it has closed to 8. It also stops *once* on entering that wait rather than every
  frame, which matters because vnavmesh's stop is not edge-triggered — each call also throws away
  the cached destination, so the next move re-paths from scratch.

  Separately, arriving at a waypoint stopped dead, gave up the frame, and re-pathed to the next
  one — a twelve-point route meant twelve of those. Waypoints are now consumed without stopping,
  and every point already behind you is consumed at once instead of one per frame.

- **It now covers ground in long legs.** Rather than walking to the very next waypoint, it heads
  for the furthest one within 40 yalms and beckons at the start of each leg. The radius is bounded
  on purpose — vnavmesh paths around the terrain itself, but the authored waypoints are what keep
  the walk inside the corridor the route was captured along, so it follows them rather than making
  a run at the finish. If the NPC will not close the gap at all, the run walks on and keeps
  beckoning after eight seconds instead of holding until the leve times out.

## 1.5.4.9 — Death recovery says why it is stuck

### Added

- **A Return the game refuses is no longer silent.** Recovery now asks whether Return can actually
  be used before firing it, and logs the game's own refusal code when it cannot. A declined
  Return — still on its cooldown, or content it does not work in — used to be indistinguishable
  from one that worked: the character simply stayed a corpse while the 4-second retry loop read as
  the plugin doing nothing at all. The refusal does not consume the retry window either, so
  recovery fires the moment the block clears rather than waiting it out.

- **The "Return to your home point?" confirmation is answered by Relicable itself**, so recovery no
  longer depends on TextAdvance or YesAlready being installed and switched on — without one, the
  prompt just sat there, the character stayed dead, and the retry raised it again. Scoped to the
  few seconds after its own Return while confirmed dead, so it is never a blanket yes.

## 1.5.4.8 — Death recovery no longer mistakes the teleport for the resurrection

### Fixed

- **"You can never resurrect — it keeps dying and respawning dead by the aetheryte."** Death
  recovery decided you were alive again the instant a zone transition started. Pressing Return
  *is* a zone transition, so the check cleared itself 97 milliseconds after issuing Return, while
  the character was still a corpse and the zone had not even changed yet. The run resumed, found
  itself still dead on arrival, latched death again and re-fired Return on its 4-second
  throttle — round and round.

  A zone change now says nothing either way about being dead; the recovery holds its state until
  the transition finishes. Death is read from the game's own unconscious flag rather than
  inferred from HP alone, and neither death nor the revive is acted on until it has actually
  held for a moment, so a single frame of HP 0 as a zone finishes loading can no longer spend a
  Return on a living character. If the recovery is still stuck after 45 seconds it now says so in
  the log — Return on cooldown, or a death window waiting on an answer — instead of retrying
  silently forever.

## 1.5.4.7 — Auto-discard mob drops

### Added

- **Auto-discard (Configuration → Inventory, off by default).** A long unattended run fills the
  bags with mob drops and then quietly stops looting. With this on, the clutter goes as it
  accumulates — **immediately, permanently, and with no confirmation window to answer**. Items are
  dropped through the game's own `InventoryManager` discard call, which is the path the confirm
  dialog sits *in front of*, so no prompt is ever raised; if some item class does put one up it is
  answered automatically, and only ever a prompt naming the exact item just discarded.

  Two modes. *All low-value materials* is the hands-off one: ordinary white, stackable,
  non-usable, tradeable crafting materials worth at most a vendor price you set (100 gil by
  default). *Only my discard list* deletes nothing you have not named.

  Because it cannot be undone, the rules are deliberately narrow and the consequence is shown
  before you commit. Only the four player bags are ever scanned — the armoury, key items, crystals
  and currency are not reachable. Never discarded, in either mode: gear and weapons, anything
  usable (treasure maps, minions, food, aetheryte tickets), HQ, collectables, melded items,
  materia, anything the game itself marks undiscardable, anything untradeable or unique — which
  covers every relic material — and every item id the loaded relic objectives reference, so a new
  stage's material is protected the moment its objective exists. By default it only runs while
  automation is running, so nothing disappears during normal play.

  The settings page lists your bags with the verdict the live rules give each stack, plus per-item
  **Keep** / **Discard** buttons, so you can see exactly what enabling it would delete and correct
  anything you disagree with.

## 1.5.4.6 — A book FATE you just cleared is no longer written off as someone else's

### Fixed

- **"FATE was already finished on arrival ... never participated so no credit" on a FATE you had
  just fought all the way to 100%.** A FATE's reward — and with it the book slot it credits — is
  granted when the FATE *ends*, which is a beat after its progress reads 100. Relicable finished the
  step on progress alone, so it handed the objective back during that gap; the controller re-checks
  the book straight off the note, still saw the slot empty, and re-selected the same FATE. The
  restarted step had no memory of the fight, read a finished FATE it had not been inside for, and
  rotated off it announcing that it had never taken part.

  Nothing was actually lost — the credit landed a moment later and the run moved on — but the
  decision was made on a false reading. A finished FATE is now held until the slot actually credits
  (bounded, and resumed rather than restarted if the objective is handed back), and "we fought this
  one" survives the step restart, so the rotation only ever fires for a FATE that really was cleared
  by someone else.

- **The co-located-FATE shortcut could dive straight back onto a FATE that was already over.** It
  accepted any FATE still flagged Running, including one sitting at 100% waiting to flip, and it
  bypasses the round-robin that stops a rotated-off FATE being re-picked — so it handed back the very
  FATE the executor had just left, twice in a row. Finished FATEs are no longer eligible.

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
