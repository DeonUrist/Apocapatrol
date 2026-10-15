# Apocapatrol 2.12.0 faction battle prototype

Vehicle editor: ExplodeChance is a percentage, saved as `explodeChance`. Shipped vehicles default to
50%, with War_rig and Truck_War_Rig at 0%. General → Always explode cars defaults OFF and forces the
probability to 100%; it does not bypass driver death, living occupant, alive bailout or player capture checks.
The old exploding-cars and preserved-template settings are no longer exposed.

Patrol/convoy editor: Faction defaults to Scrapyard; Coyotes is available. The JSON group property is
`faction`. Coyotes attack the player only while the game's FactionRelation_Coyotes state marks them hostile.
Map routes and names are red for Scrapyard patrols, orange for Scrapyard convoys, green for Coyote
patrols and light blue for Coyote convoys. Hide route type makes all four gray; yellow is reserved and unused.
Mounted combat and simple dismounted NPC Overhaul AI select opposing traffic crew when nearby.
Convoy drivers continue toward their destination and retain the never-bail rule.

Opposed travelling groups within 120 m create a battle at their midpoint in stable world coordinates.
Without a player nearby, the prototype resolves after 20 simulated seconds. A bike is worth 1, a small
car or semibike 2, and a big car 6. The stronger side retains whole vehicles approximating the strength
difference; ties kill both sides. Only survivors resume travel. Physical battles use real crew combat;
living dismounted participants keep fighting even after their vehicle has lost its crew.

When approached, both factions share Max cars in double event, each also constrained by Max cars in
event and the total real traffic cap. Shares reflect their original vehicle counts, with at least one
vehicle on each side when the cap allows. Failed builds retry after 15 seconds rather than every tick.

Resolved sites last five minutes of active simulation and appear as skulls on the game's debug map.
Dead vehicles and corpse prefabs materialize only within the traffic loading range. Casualty cars roll
their original template's explosion chance once at resolution. Known alive driver bailouts leave intact
cars. Offscreen casualties represent dead crews. Looted/stripped or player-captured cars stay real salvage
and are not rebuilt from pristine templates. The skull and temporary representation expire together.

Battle records, factions, casualties, explosion rolls and timers survive saves; vehicle save schema 12
also reads earlier schemas. The native verifier covers production construction, faction assignment,
caps, serialization, offscreen resolution, lazy aftermath and expiry. Its physics simulation is disabled
to keep construction fixtures stable; live driving and firefight balance still require gameplay testing.
