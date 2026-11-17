# Ground alignment and fall recovery (P4 fix, v2)

Components on the XR Origin: `AutoGroundAlign` (terrain following, Update) and `GroundFallRecovery` (safety net, LateUpdate). Neither changes frame rate, physics settings, NPCs, locomotion speed or any teleport rule.

## AutoGroundAlign
* Samples the ground under the **CharacterController capsule footprint**, not the rig root (the head / capsule can be > 1 m from the origin).
* Accepts only finite, non-trigger, walkable (slopeLimit) hits; rejects a sample that is lower than the last accepted ground by more than `stepOffset + horizontalDistance*tan(slopeLimit) + 0.1 m` (an abrupt quay edge / wall can never become a target).
* Frame-time independent smoothing (`1-exp(-speed*dt)`), correction capped at 2.5 m/s; on any rejected / missing sample the rig Y is left untouched. Stood down for 2 s after a recovery (`SuspendAlignment`).

## GroundFallRecovery (triggers; all restore to a validated position >= 2 m earlier on the route)
* `fell_below_safe_y` / `under_terrain`: unsupported for >= 0.6 s and >= 1 m below the last safe ground (a fast fall also overrides the 2 s cooldown).
* `abrupt_drop_landing`: after an unsupported episode, landing lower than the continuity allowance above (quay edge, wall, cliff) - a gradual connected descent never loses support and is not affected.
* `hard_floor` (20 m below the last safe ground, backstop), `void_displacement` (a jump whose destination has no ground), `verify_failed_try_older` (restored position unsupported -> next older anchor).
* A move that is merely what gravity produces for a long frame is a fall, not a teleport (frame stalls).

## Recording (no schema change of the existing files)
* `events.csv`: one row per recovery, `event_type = ground_recovery` (Part 2 / Part 3; Part 1 research events are dropped by the existing capture rule). `label` = trigger reason, `value_num` = vertical speed (m/s), `from_pos_*` / `to_pos_*` = rig before / after, `value_str` = `reason=..;from=x|y|z;cc_before=..;cc_after=..;last_safe=x|y|z;vy=..;ground_valid=..;ground_y=..;teleport_pending=..;cc_is_grounded=..;attempt=..;count=..`.
* **`teleport_epoch` is NOT incremented** and no `teleport` / `river_crossing` event is written: a recovery is not a teleport.
* `ground_recovery_<UTC>.csv` in `Application.persistentDataPath` (diagnostics, open/write/close per row, clock = `Time.realtimeSinceStartupAsDouble` = `t_abs`): original 15 columns, then `vertical_velocity,ground_valid,ground_y,head_to_origin_xz,teleport_pending,footprint_x,footprint_z,attempt,session_id`, then (v2) `part,phase,recovery_index`. Events written: `init`, `support_lost`, `support_regained`, `state_unsupported`, `jump_detected`, `abrupt_drop_landing_detected`, `recovery`, `recovery_verified`, `recovery_cycle`, ... Part 1 recoveries exist only in this file.

## Analysis handling
See `T2/analysis/documentation/ground_recovery_handling.md` (policy: fall samples and the restore step are never walking and never a teleport; the raw files are untouched).
