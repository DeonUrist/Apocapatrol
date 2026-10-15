# Bus passenger pose editor

Spawn a bus with **Spawn template** for a stationary setup with inactive crew AI.
Stand inside or near it and press **Insert**. Change the binding in **Debug → Bus passenger
pose editor key**, or enable **Bus passenger pose editor** directly in settings.
The nearest seated bus passenger within 80 m is selected automatically.

| Key | Action |
| --- | --- |
| Num0 | Next joint: thigh, knee, upper arm, elbow, wrist, seat position |
| Num/ | Both sides mirrored, left only, right only |
| Num8 / Num2 | Increase / decrease forward bend |
| Num6 / Num4 | Increase / decrease sideways swing |
| Num9 / Num7 | Increase / decrease twist |
| Shift | 5 times larger steps |
| Num5 | Save the current preset |
| Num. | Restore defaults live; Num5 makes the reset persistent |
| Insert (or configured key) | Close the editor |

With **Seat position** selected, Num8/2 moves forward/back, Num6/4 moves right/left,
and Num9/7 moves up/down relative to the passenger's chair. Normal steps are 1 degree
or 1 cm. Disable MerchantSpotter's item editor while using these numpad controls;
Apocapatrol's turret spot editor pauses automatically while this editor is open.

Changes preview immediately on all seated bus passengers. Save writes
`BepInEx/plugins/Apocapatrol/BusPassengerPose.json`, shared by General, Mechanic and Organic
merchants and other bus passenger types. Existing item placements and turret anchors are
independent. Defaults keep legs uncrossed and lower arms toward the knees.

Armed bus passengers retain their aiming arms during combat; the selected passenger previews
the relaxed pose while the editor is open. The editor does not enable or disable combat AI.
Passengers promoted to drivers return to the driver pose. Roof and trailer turrets retain
their crouched/standing selection.
