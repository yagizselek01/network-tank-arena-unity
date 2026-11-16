# NetAim — Latency-Resilient Turret Synchronization

**Network Programming · Individual Assignment**  
**Based on:** Network Tank Arena (two-player Host/Client starter)  
**Engine:** Unity 6.3.26f1 (6000.3.26f1)

NetAim investigates a specific problem in server-authoritative multiplayer games: **how to make aiming feel immediate for the owning player while keeping remote turret motion visually stable under latency and jitter**.

Rather than changing gameplay authority, the project separates **authoritative simulation** from **visual presentation**. It adds an immediate local turret aim override, compares two remote smoothing algorithms against direct replication, and investigates a lower turret-state update rate.

## Final implementation decision

**Selected remote presentation: `Mathf.SmoothDampAngle`, with `Turret Smooth Time = 0.1 s`.** This configuration produced the smallest observed maximum per-frame angle change in our three-way comparison. It is a practical selection based on the available evidence, **not proof that SmoothDamp has the lowest end-to-end latency or is universally optimal**.

| Setting | Final configuration |
|---|---|
| Local visual aim override | Enabled for the owning Client |
| `Enable Remote Smoothing` | On |
| `Use Smooth Damp` | On |
| `Turret Smooth Time` | `0.1` seconds |
| `Turret Smoothing Speed` | `18` (retained for Exponential comparison) |
| `Limit Turret Replication Rate` | **Off** |
| `Input Send Rate` | `20` Hz |
| Firing and gameplay authority | Server |

The **Original** and **Exponential** modes remain available as comparison baselines. The optional 10 Hz replication limit remains implemented but is **not selected for the final configuration** because bandwidth savings were not quantified and visual firing-direction mismatch became more noticeable in some tests.

> **Before recording or submitting:** verify that the saved scene/prefab Inspector settings match the final configuration above. Changing values only during Play Mode does not establish the submitted defaults.

## Requirements and setup

Open this repository with **Unity 6.3.26f1** using Unity Hub → **Add project from disk**. Open `Assets/Scenes/SampleScene.unity`.

Starter package versions:

| Package | Version |
|---|---|
| Netcode for GameObjects | 2.13.3 |
| Unity Transport | 2.7.4 |
| Multiplayer Tools | 2.2.12 |
| Multiplayer Play Mode | 2.0.2 |

**Combat setup:** See [`docs/NETWORK_COMBAT.md`](docs/NETWORK_COMBAT.md) for Inspector wiring. The original starter README warned that the committed scene/prefabs could still require combat wiring; **confirm and save these connections before the final ZIP is made**. Missing combat target setup can prevent startup.

### Start Host + Client

1. Open `Assets/Scenes/SampleScene.unity`.
2. Open **Window → Play Mode → Scenarios** (or **Configure play mode scenarios** in the Play Mode dropdown).
3. Select or create a scenario with **Main Editor** and exactly one **Additional Editor Instance**; set Initial Scene to `SampleScene`.
4. Enter Play Mode. In the Main Editor select **Start Host**; in the Additional Editor select **Start Client (localhost)**.
5. Verify that both players connect and two tanks spawn with distinct Owner Client IDs. Focus the appropriate Game view before providing input.

**Controls:** `W/S` move, `A/D` rotate the chassis, mouse aims, left click requests a shot. Each process should control only its owned tank. Stop Client before Host, and allow shutdown before restarting.

The Host owns its local tank without a genuine remote transport round trip; **control the Client-owned tank** when evaluating the owning player's latency. To inspect remote presentation, watch the Client-owned tank on the Host.

## Baseline and networking architecture

### Original starter baseline

Network Tank Arena already provided two-player authoritative movement and combat. The owner sends movement/aim intent; the server simulates both tanks, and clients receive replicated state. `NetworkTransform` interpolates movement, but the separately replicated turret yaw was originally applied **directly**, without dedicated turret smoothing. The original starter did not implement client movement prediction or reconciliation.

Under impaired network conditions, the owning Client's visual turret feedback could lag and a remote observer could see abrupt turret yaw changes.

### What NetAim changes

- **Local presentation prediction:** The owning Client computes yaw immediately from its local mouse target and applies a visual-only turret override. This does **not** make the Client authoritative for tank state.
- **Remote presentation smoothing:** Non-local views render the latest replicated `turretYaw` through Original (direct), Exponential (`Mathf.LerpAngle` with frame-rate-independent weighting), or SmoothDamp (`Mathf.SmoothDampAngle` with persistent velocity).
- **Authoritative combat preserved:** A reliable fire request carries the requested world-space target. The server validates the request, determines shot yaw, and creates networked projectiles. The remote smoothed *visual* yaw does not control projectile direction.
- **Replication-rate experiment:** An optional rate limiter caps writes to the server's `turretYaw` NetworkVariable at approximately 10 Hz while leaving the server simulation and firing path unchanged. This does **not** guarantee a 10 Hz packet send rate.
- **Diagnostics:** The project records local-vs-replicated aim differences, accepted projectile-producing shot directions, remote angular-change metrics, and a limited catch-up diagnostic.

Relevant implementation files include `Assets/Scripts/Networking/NetworkTankController.cs` and `Assets/Scripts/Tanks/TankPresentation.cs`.

**Authority distinction:** Movement and canonical turret state are persistent server-owned simulation state. Movement intent and firing are client requests; firing is a discrete server-authoritative event. The local override and remote smoothing affect rendering only.

## Reproduce network conditions

Use **Multiplayer Tools → Network Simulator**. Do not use obsolete Unity Transport `DebugSimulator` fields for this project.

If presets are not present, create `NetworkSimulatorPresetAsset` files under `Assets/Settings/NetworkConditions`, with **Packet Loss Interval = 0**:

| Preset | Delay | Jitter | Random loss |
|---|---:|---:|---:|
| Clean localhost | 0 ms | 0 ms | 0% |
| Moderate latency | 100 ms | 0 ms | 0% |
| **Latency plus jitter (quantitative tests)** | **100 ms** | **50 ms** | **0%** |
| Lossy connection | 0 ms | 0 ms | 10% |
| Combined impairment | 100 ms | 50 ms | 5% |

Place a single Network Simulator component on a scene object **outside** the spawned tank prefabs. During the measured impaired runs, enable the simulator **only on the Main Editor Host**; leave the Client instance's simulator disabled to avoid compounding delays. Start from a disabled/clean configuration and restore it afterward. A configured delay of 100 ms on each path can contribute roughly 200 ms of network RTT, plus scheduling and simulation delays.

The documented quantitative comparison uses **100 ms delay + 50 ms jitter + 0% configured packet loss**; the other preset rows describe available reproducible conditions, not necessarily fully benchmarked NetAim outcomes.

## Benchmark procedure

1. Start Host + Client and apply the same network preset for each run.
2. In the Host's Hierarchy, select the **Client-owned** tank and its `TankPresentation` component.
3. Choose a presentation mode:
   - **Original:** `Enable Remote Smoothing = false`.
   - **Exponential:** `Enable Remote Smoothing = true`, `Use Smooth Damp = false`, speed `18`.
   - **SmoothDamp:** `Enable Remote Smoothing = true`, `Use Smooth Damp = true`, smooth time `0.1`.
4. Invoke **Start Aim Test** from the component's Inspector context menu. It resets the angular-change counters, measures for approximately **10 seconds**, then prints one `[AIM METRICS]` line.
5. On the Client, move the mouse through continuous rotation, rapid reversal, and sudden stops. Capture the Console output.
6. Repeat for another mode with otherwise identical settings. Restart Play Mode when necessary to avoid inherited smoothing velocity/state.

The benchmark records **average and maximum absolute frame-to-frame displayed yaw change**, plus sampled frame count. This measures visible angular movement per rendered frame; **it is not a direct packet-jitter measure** and depends on frame rate and input movement. The test inputs were manually repeated, not identical recorded trajectories.

## Observed results

### Three-way comparison (representative 10-second runs)

| Presentation mode | Average change / frame | Maximum change / frame | Samples |
|---|---:|---:|---:|
| Original | 5.79° | 179.08° | 1,125 |
| Exponential (speed 18) | 4.99° | 46.96° | 1,023 |
| **SmoothDamp (0.1 s)** | **3.63°** | **17.52°** | **1,026** |

Additional Original-vs-Exponential runs showed similarly lower **maximum** jumps with smoothing:

| Run | Original max | Exponential max |
|---|---:|---:|
| A | 176.34° | 34.28° |
| B | 176.95° | 31.33° |

These observations motivated selecting **SmoothDamp** for the final remote presentation. Because mouse trajectories and frame rates were not held identical, the figures **do not establish a controlled percentage performance improvement**, and the difference between Exponential and SmoothDamp cannot be attributed only to the algorithms. Catch-up delay was not comparably benchmarked across both smoothing methods.

### Gameplay validation

Under 100 ms delay and 50 ms jitter, the final check found **immediate local aim**, **visually smooth remote aim**, and **projectiles visible in both instances**. No fire-direction/visual-turret mismatch was noticed in that final run, but such mismatch **had been observed earlier** during fast turning and remains a potential limitation. Server shot-yaw logs closely matched corresponding Client-requested yaw for sampled projectile-producing requests; this is not a claim that every fire request succeeds.

### 10 Hz replication experiment

The optional 10 Hz state-update limit preserved acceptable visual turret motion in the observed test, but firing-versus-visible-turret mismatch could become more conspicuous. Unity NGO Profiler captures (`NGO Messages` and `NGO Objects`) were collected. However, no reliable, like-for-like numeric bandwidth reduction was established from those captures. **Do not interpret the 10 Hz setting as a verified bandwidth optimization.** The final configuration leaves this limit **off**.

## Trade-offs and limitations

- **Responsiveness vs. state consistency:** The owning player's visual aim can advance ahead of the most recent replicated server angle. This is intentional visual prediction, not authoritative client simulation.
- **Smoothness vs. catch-up:** Smoothing reduces abrupt remote yaw steps but can lag behind the latest replicated target. The catch-up diagnostic resets when that target changes, so it does not measure full action-to-settlement latency.
- **Firing vs. visible turret alignment:** The server fires toward its validated request; a remote observer's smoothed turret may not yet point in the same direction.
- **Replication rate vs. visual fidelity:** Lower-frequency turret-state writes may increase visible discrepancy. Network packet/byte savings were not quantitatively established.
- **Scope:** No movement prediction/reconciliation, tank health/death/respawn, score, Relay, or dedicated-server mode. Exactly two localhost players are supported (`127.0.0.1:7777`). NGO scene management is disabled and instances load `SampleScene` themselves.
- **Other starter behavior:** Very short-lived replicated projectiles may vanish before a remote frame displays them. The server treats movement intent as neutral after about 0.5 seconds without a newly accepted intent.

## Conclusion

NetAim demonstrates a deliberate separation between **server-authoritative gameplay** and **latency-resilient presentation**. Immediate local visual aiming improves owner feedback; remote smoothing reduces observed abrupt turret motion. Comparing Original, Exponential, and SmoothDamp highlighted real trade-offs, while the 10 Hz experiment showed why an optimization should not be claimed without trustworthy network-cost measurements.

**Final choice:** visual-only local aim override + **SmoothDampAngle (0.1 s)** on remote turrets, retaining server-authoritative combat and normal replicated turret update behavior.
