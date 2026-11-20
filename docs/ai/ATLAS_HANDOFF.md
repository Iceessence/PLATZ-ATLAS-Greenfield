# PLATZ ATLAS Greenfield — Independent Audit & Persistent Agent Handoff

> **Audit role notice:** This document was authored by an independent auditor against the unmodified repository at `gf-011` (`7b9b7a643f1ae4e05ec593d72d7c95e10b7cbd12`). No application source code, tests, or `WORKING.md` were modified during this audit.

---

## 1. Authoritative Checkpoint & Provenance

- **Repository root:** `C:\PLATZ ATLAS\PLATZ-ATLAS-Greenfield`
- **Git `HEAD` / branch / tag:** `7b9b7a643f1ae4e05ec593d72d7c95e10b7cbd12` (`main`, tag `gf-011`)
- **Remote configured:** `origin` → `https://github.com/Iceessence/PLATZ-ATLAS-Greenfield.git` (audit keeps all local work offline; do not push without explicit owner direction)
- **Master specification:** `docs/Ultimate_PLATZ_ATLAS_MASTER_Version_1.docx` (verified SHA-256: `ee95a50f205f85a3867488d7fc4a5e066b694b291f5657764eda8b35a2b32ebc`)
- **Audit evidence location (outside checkout):** `C:\PLATZ ATLAS\.platz-atlas-verification\audit-gf001-011`
- **Reference test host & toolchain:** Windows 11 x64 (`26300.9974`), `.NET SDK 10.0.401`, NVIDIA GeForce RTX 5080 (driver `617.42.0.0`, Vulkan API `1.4.351`, OpenGL `3.3.0 NVIDIA 617.42`), `VK_LAYER_KHRONOS_validation` (`1.4.304` discovered via `C:\Program Files\Epic Games\UE_5.7\Engine\Binaries\ThirdParty\Vulkan\Win64`).
- **Provenance transparency:** Commit `7164666` (`gf-002`) was authored earlier in this session; commits `1582958` (`gf-001`) and `97674a9`..`7b9b7a6` (`gf-003`..`gf-011`) were authored externally. Every milestone GF-001 through GF-011 was re-verified from scratch against `7b9b7a6` using fresh clean offline builds, an independent adversarial C# harness (`AuditHarness`), and real GUI/CLI automation (`audit_gui_and_cli.ps1`).

---

## 2. Accepted Baseline Decision

- **Last independently accepted Greenfield baseline:** **NONE**
- **Current `gf-011` (`7b9b7a6`) audit verdict:** **REPAIRS_REQUIRED** (for promoting a clean Night Museum baseline)

### Why it is not promoted to `ACCEPTED` yet
Core storage (`PlatzAtlas.Core`), Vulkan/OpenGL/CPU rendering (`PlatzAtlas.Rendering`, `PlatzAtlas.RoomView`), Runtime isolation (`PlatzAtlas.Runtime`), and standalone Player packaging (`PlatzAtlas.Player`, `BundlePlayer.ps1`) are well-engineered and passed nearly every adversarial check. However, independent GUI/CLI testing uncovered **three confirmed defects** at boundaries that violate master rules on stale reads, headless CLI reliability, and Studio/Player CLI parity, alongside **unverified environment matrices** (unlocked-desktop keyboard/screen-reader/high-DPI walkthrough, clean second machine, real TDR, non-NVIDIA OpenGL).

Once the three small pre-expansion repairs in Section 6 are fixed and independently re-verified, the Night Museum vertical slice can be promoted to `ACCEPTED_WITH_LIMITATIONS` (bounded to Night Museum schema 1–3 on Windows 11 x64).

---

## 3. Milestone Verdicts (GF-001 through GF-011)

> **Prioritization:** GF-007 through GF-011 are audited in greatest detail first, followed by GF-001 through GF-006.

| Milestone | Scope | Independent Verdict | Key Finding / Scope Limitation |
| --- | --- | --- | --- |
| **GF-011** | Portable Windows Player bundle (`-Bundle`, `SHA256SUMS.txt`, notices, outside-checkout execution) | **VERIFIED** *(host-only)* | Deterministic ZIP (`5fa1279...`) across two clean offline builds; 24/24 SHA-256 matches; zero Studio/ATLAS DLLs; Player executes outside checkout with Studio closed. Clean second-machine test is **UNVERIFIED**. |
| **GF-010** | Editable switch behavior (`Toggle` / `TurnOn`), Undo/Redo, Schema 3, Schema 1–2 backward compat | **VERIFIED** | Mixed Schema 1 → 2 → 3 → Undo×3 → Redo×3 → Reopen → Export → Player verified in both `AuditHarness` and Studio GUI (`gui-turnon-room.platz`). |
| **GF-009** | Standalone Player floor movement, clamp bounds, proximity indicator, `E` / button switch use | **VERIFIED** *(UIA/API)* | Walk-into-range, out-of-range gating, exact `0.095` threshold, `SwitchId` tie-breaking, floor clamp `[0.12, 0.88] × [0.78, 0.96]`, `Toggle` & `TurnOn` verified on Vulkan, OpenGL, and CPU. Live unlocked-desktop keyboard (`WASD`/Arrows/`E`) is **UNVERIFIED**. |
| **GF-008** | Standalone `PlatzAtlas.Player.exe`, `--check-room`, tamper rejection, `Restart room` | **REPAIRS_REQUIRED** | Room validation, playback, `Restart room`, and corrupt-room rejection work across Vulkan/OpenGL/CPU. **Defect:** `src/PlatzAtlas.Player/App.xaml.cs:21-23` silently ignores `--renderer` (unlike Studio), `PlayerWindow.xaml.cs` omits `VulkanValidated` logging, and `PlatzAtlas.Player.csproj` lacks `app.manifest` (`PerMonitorV2`). |
| **GF-007** | Studio saved-room `.platzroom` export (GUI & CLI `--export-room`), determinism, validation | **REPAIRS_REQUIRED** | `.platzroom` format, determinism (`f2ac914...`), overwrite/corrupt-dest/public-fixture guards verified. **Defect:** `src/PlatzAtlas.Studio/App.xaml.cs:20-58` pops up a blocking modal `MessageBox` and hangs CLI `--export-room` when `--diagnostics` points to an existing file. Minor: `RoomPackage.cs:69` uses case-sensitive `Ordinal` digest comparison. |
| **GF-006** | Isolated Play mode in Studio (`F5`, `Use West Switch`, Stop, disk/undo isolation) | **VERIFIED** | Play disables authoring (`Objects.IsEnabled == false`), isolates runtime state on Vulkan and OpenGL, preserves exact saved bytes/history, and rejects stale disk head (`StaleRevision`). |
| **GF-005** | Owner-approved ATLAS commit, mutex + pre-replace check, schema 2 receipt, Undo/reopen | **VERIFIED** | Review confirm/cancel, expiry at `ReviewLifetime`, revocation at pre-replace callback, lock retry, and schema 2 receipt + Undo after reopen all verified. Minor behavior note: `oldReader.CommitApprovedLight` idempotent replay keeps `oldReader.Current` at old revision while safely blocking `oldReader.Save()`. |
| **GF-004** | Detached ATLAS light preview (`PreviewSavedLight`), disk isolation, stale rejection | **VERIFIED** | Detached preview leaves disk untouched on `Save` and properly calls `ReadSavedSnapshot()` to reject when external process modifies disk. |
| **GF-003** | Scoped ATLAS question (`"Which lantern does the west switch control?"`), citation, revocation | **REPAIRS_REQUIRED** | `ProjectQuestionHost` & `SwitchEvidenceProfile` pass multi-thread race, prompt-injection, tampering, and revocation checks (minor: `SwitchEvidenceProfile.cs:77` uses `Ordinal` SHA-256 comparison). **Defect:** `MainWindow.Atlas.cs:85-91` (`AtlasAccessClicked`) captures in-memory `_session.Current` without calling `_session.ReadSavedSnapshot()`, allowing Studio to grant and answer from stale in-memory state after an external disk edit. |
| **GF-002** | Khronos Vulkan validation (0 errors/warnings), OpenGL 3.3 Core parity, CPU Reference, resize, fallback | **VERIFIED** *(NVIDIA RTX 5080)* | Probe & Studio re-verified at `gf-011`: `VK_LAYER_KHRONOS_validation` `enabled=True errors=0 warnings=0`, Vulkan/OpenGL pixel match (`#0e1a2c`, `#f0b446`, `#229e5f`), resize, runtime switching, and simulated loss cascades (`Vulkan -> OpenGL`, `OpenGL -> CPU Reference`). Real hardware TDR & non-NVIDIA OpenGL drivers are **UNVERIFIED**. |
| **GF-001** | Core saving, atomic replace, `.previous` recovery, Undo/Redo, stale/target rejection | **VERIFIED** *(core & UIA)* | Verified via `Core.Tests`, `AuditHarness` (concurrent mutex saves, corrupt `.previous` guard, fixture read-only guard), and Studio `.previous` recovery + save. Unlocked-desktop `SendKeys`, screen reader speech, and high-DPI scaling (>96 DPI) are **UNVERIFIED**. |

---

## 4. Exact Observed Evidence & Unresolved Defects

### 4.1 Commands & Observed Outcomes
1. **Clean offline rebuild & deterministic bundle (`Build.ps1 -Offline -Test -Bundle` × 2):**
   - `git status --short` empty; `HEAD` = `7b9b7a643f1ae4e05ec593d72d7c95e10b7cbd12`.
   - Both builds succeeded with `0 Warning(s), 0 Error(s)`; all 19 `PlatzAtlas.Core.Tests` groups passed.
   - Both runs produced identical `PlatzAtlas.Player-release.zip` SHA-256: `5fa127982e42ba717fbead1e3c2886b60a5cc552603699c6118c312fac5d11a7`.
   - Extracted bundle (`extracted-player`): 24/24 entries in `SHA256SUMS.txt` matched, 0 extra files, 0 Studio/ATLAS assemblies, 5 license notices (`Silk.NET.txt` + `Microsoft.DotNet.PlatformAbstractions` + `Microsoft.Extensions.DependencyModel`).
2. **Rendering Probe (`PlatzAtlas.Rendering.Probe.exe ... --require-validation`):**
   - `VulkanViewport` (`NVIDIA GeForce RTX 5080`, `B8G8R8A8Unorm`): `enabled=True errors=0 warnings=0` on both initial and recreated lifecycles; resized `280×220 → 340×260`; sampled pixels `#0e1a2c`, `#f0b446`, `#229e5f`.
   - `OpenGlViewport` (`OpenGL 3.3.0 NVIDIA 617.42; profile 3.3 Core; VSync; RGBA8`): resized `280×220 → 340×260`; exact pixel parity with Vulkan (`audit-vulkan-probe.png` / `opengl-probe.png`); simulated loss & recovery passed.
3. **Adversarial C# Harness (`AuditHarness.exe`):**
   - Passed mixed Schema 1 → 2 → 3 → Undo×3 → Redo×3 → Reopen, all malformed JSON/enum/coordinate/checksum/corrupt-`.previous` rejections, multi-thread same-path and case-variant mutex contention, multi-thread `ProjectQuestionHost` `Ask`/`Revoke`/`Allow` race checks, exact `ReviewLifetime` 1-tick expiry boundary, empty room / multi-switch `SwitchId` tie-break / `TurnOn` on already-lit lantern, and `RoomPackage.Save(..., overwrite: true)` refusal over corrupt destination.
4. **GUI, CLI & Standalone Player Audit (`audit_gui_and_cli.ps1` + recovery/loss script):**
   - Studio CLI `--export-room` deterministic (`f2ac914d2f15c7927d1cca19b5bd3f19ef2b84eb438d2c30352a20dbebb20d96`), properly exits `1` on missing `--project`, public fixture, existing output, and wrong extension.
   - Studio GUI exercised runtime switching (`Vulkan → OpenGL → CPU → Vulkan`) + resize (`770×555` / `1120×720`), ATLAS `Allow → Ask → Show in scene → Preview → Save (no disk write) → Review → Cancel → Confirm (schema 2 r2)`, `SwitchModeTurnOn → Undo → Redo → Save (schema 3)`, Play mode (Vulkan & OpenGL), `.platz.previous` recovery, and simulated device loss (`Vulkan → OpenGL`, `OpenGL → CPU`) with `ErrorCount=0, WarningCount=0` on Vulkan validation.
   - Extracted `extracted-player\PlatzAtlas.Player.exe` (with zero Studio processes running) passed `--check-room` (valid exit `0`; tampered, bad profile, missing exit `1`), full UIA walk (`Walk left`×5, `Walk up`×2 → `In range · West Switch`), twice switch use (`Toggle`: `Off → On → Off`; `TurnOn`: `Off → On → On`, East untouched), `Restart room` at `625×460`, and corrupt room rejection across Vulkan, OpenGL, and CPU.

### 4.2 Confirmed Unresolved Defects (Repair Before Expansion)
1. **[Medium — Security/Freshness] Stale-disk read in Studio ATLAS grant (`src/PlatzAtlas.Studio/MainWindow.Atlas.cs:85-91`):**
   - **Observed:** When `gui-room.platz` was modified externally from `r2` (west on) to `r3` (west off), clicking **Allow project read** → **Ask ATLAS** in Studio still returned `"Light is on."` from stale in-memory `_session.Current` (`r2`), whereas **Preview** and **Play** properly rejected with `"The saved project changed on disk. Reopen it."`
   - **Root cause:** `AtlasAccessClicked` reads `var saved = _session.Current;` directly instead of calling `_session.ReadSavedSnapshot()`.
   - **Fix:** Validate freshness via `_session.ReadSavedSnapshot()` inside `AtlasAccessClicked` (and re-check inside `PublishSwitchEvidence` callback if desired) and show the standard error status if stale.
2. **[Medium — CLI Automation] `--export-room` hangs on modal GUI `MessageBox` (`src/PlatzAtlas.Studio/App.xaml.cs:20-58`):**
   - **Observed:** Running `PlatzAtlas.Studio.exe --project <valid.platz> --export-room <out.platzroom> --diagnostics <existing_file>` popped up a blocking modal `"PLATZ ATLAS Studio"` dialog instead of writing to `stderr` and exiting `1`.
   - **Root cause:** `RunRoomExport` constructs `new StudioDiagnostics(diagnosticsPath)` before its `try` block; `FileMode.CreateNew` throws `IOException` to `App.OnStartup`, which calls `MessageBox.Show(...)`.
   - **Fix:** Move `new StudioDiagnostics(diagnosticsPath)` inside `RunRoomExport`'s `try` block (or construct diagnostics after validating CLI mode) and ensure CLI mode (`--export-room`) never displays modal WPF dialogs.
3. **[Low — CLI/Platform Parity] `PlatzAtlas.Player` CLI & manifest parity (`src/PlatzAtlas.Player/App.xaml.cs`, `PlayerWindow.xaml.cs`, `PlatzAtlas.Player.csproj`, `tools/BundlePlayer.ps1`):**
   - **Observed:** `PlatzAtlas.Player.exe --renderer cpu` silently ignored `--renderer` and launched on `Vulkan`. Also `PlatzAtlas.Player.csproj` lacks `<ApplicationManifest>` (`PerMonitorV2` DPI awareness), `PlayerWindow.xaml.cs` does not log `Viewport.VulkanValidated`, and `START-HERE.txt` omits `--opengl`/`--vulkan`.
   - **Fix:** Share or match Studio's `--renderer` parser, add `app.manifest` (`PerMonitorV2`) to Player, log `VulkanValidated`, and update `START-HERE.txt`.
4. **[Low — Consistency] Case-sensitive SHA-256 comparison (`src/PlatzAtlas.Atlas/SwitchEvidenceProfile.cs:77`, `src/PlatzAtlas.Runtime/RoomPackage.cs:69`):**
   - **Observed:** Both use `Ordinal` comparison (`!=` / `StringComparison.Ordinal`), whereas `ProjectFormat.cs:41` uses `OrdinalIgnoreCase`. Align to `OrdinalIgnoreCase`.

### 4.3 Unverified Items (Honest Scope Boundaries)
- **Clean second Windows 11 x64 machine:** Installing `.NET 10 Windows Desktop Runtime` only (no SDK) and running extracted `PlatzAtlas.Player.exe`.
- **Interactive unlocked-desktop UX:** Physical keyboard (`Tab`, `WASD`/Arrows/`E`, `Ctrl+Z/Y/S`), Narrator/NVDA screen reader announcements, high-DPI scaling (`125%`–`200%`), and uncoached novice walkthrough (`SendKeys`/`CopyFromScreen` are blocked in the locked session).
- **Hardware / non-NVIDIA GPU paths:** Real OS GPU TDR/device-loss and Intel/AMD OpenGL 3.3 Core drivers (Intel UHD 770 present on host but unselected).

---

## 5. Architectural Bottlenecks Blocking Real Scenes, Games, and ATLAS Intelligence

The repository currently delivers a tightly polished vertical slice, **but it is structurally specialized around the two-lantern Night Museum room** and cannot yet create genuinely new scenes or games:

1. **Fixed `Lantern` / `LightSwitch` Data Model (`src/PlatzAtlas.Core/MuseumProject.cs:3-32`, `src/PlatzAtlas.Runtime/RoomPackage.cs:10-18`):**
   - `MuseumProject` hard-codes `IReadOnlyList<Lantern> Objects` and `IReadOnlyList<LightSwitch> Switches`. `Lantern` only stores `(Id, Name, Location, X, Y, IsOn, AssetId)`. There is no generic `Entity`, no component collection, no parent/hierarchy, no doors, walls, colliders, variables, or rule graphs.
   - `MuseumPlaySession.cs:39-40` derives switch marker coordinates dynamically from target lantern `X` and hardcoded `Y = 0.80`; switches do not even own editable `(X, Y)` transforms.
2. **No General Editable Scene & Component System (`src/PlatzAtlas.Core/ProjectSession.cs`):**
   - `ProjectSession` only exposes `Apply(SetLight)` and `Apply(SetSwitchBehavior)`. There is no typed `CreateEntity`, `DeleteEntity`, `SetProperty`, `SetTransform`, or `BindInteraction` command pipeline, nor a property metadata/descriptor registry (as required by Master **Phase 02 Detail**).
3. **No Arbitrary Object Creation, Transforms, or Physics/Doors:**
   - Every session starts from the embedded `NightMuseum.platz` template. Users cannot place new sprites/props/doors/lights, drag/scale/rotate entities in the viewport, or define collision bounds (`MuseumPlaySession` only clamps visitor `X/Y` to a fixed rectangle `[0.12, 0.88] × [0.78, 0.96]`).
4. **No Asset Import, Pixel/Sprite Authoring, or GPU Texture Pipeline (`src/PlatzAtlas.RoomView/MuseumArt.cs`, `src/PlatzAtlas.Rendering/*`):**
   - `ProjectAsset` stores a validated PBM base64 blob (`a8909ebb...`), **but `MuseumArt.cs` never reads `project.Assets`**—it hard-codes every wall, lunar painting, and lantern vector triangle in C#.
   - `RenderSnapshot` only carries flat un-textured colored `RenderTriangle`s; `VulkanViewport` and `OpenGlViewport` have no texture/image/sampler/descriptor support yet.
5. **Hard-Coded ATLAS Question & Evidence Profile (`src/PlatzAtlas.Atlas/ProjectQuestionHost.cs:47,99-101,227-231`):**
   - `Ask()` only accepts exact string match (trimmed, trailing `?`) to `"Which lantern does the west switch control"` and searches for a switch literally named `"West Switch"`.
   - `SwitchEvidenceProfile` only serializes `SwitchSource[]` and `LanternSource[]` (`NightMuseum`-specific records).
6. **Zero Trainable Local ATLAS Models or Evaluation Dataset Pipeline:**
   - Master **U6 / ATLAS** goals require consented task trajectory logging, baseline comparison (exact match vs retrieval vs linear ranker vs local trained specialist), and shadow-mode evaluation on unseen scenes before granting authority. None of that infrastructure exists yet.
7. **Missing Persistent Cross-Agent Handoff Rules:**
   - Prior to this file (`docs/ai/ATLAS_HANDOFF.md`), there was no canonical handoff separating author claims from independent audit truth, tracking unresolved defects, or enforcing pre-expansion repair gates.

---

## 6. Persistent Cross-Agent Startup & Handoff Protocol

Every AI / GPT session resuming `C:\PLATZ ATLAS\PLATZ-ATLAS-Greenfield` MUST execute this sequence before coding:

1. **Inspect actual state first:** Run `git status --short`, `git rev-parse HEAD`, `git tag -l`, and verify `docs/Ultimate_PLATZ_ATLAS_MASTER_Version_1.docx` SHA-256 (`ee95a50f205f85a3867488d7fc4a5e066b694b291f5657764eda8b35a2b32ebc`).
2. **Read `docs/ai/ATLAS_HANDOFF.md` and `WORKING.md`:** Never trust historical claims without checking current code and running `Build.ps1 -Offline -Test -Bundle`.
3. **Preserve strict boundaries:**
   - Keep **owner accounts, credentials, personal data, private projects, and money** strictly isolated—never access external accounts or push to remotes without explicit owner approval.
   - Keep **authoring local-first and offline** (`-Offline` with locked NuGet caches); never ship silent model auto-commits (`CommitConfirmed` always requires explicit owner confirmation + valid capability grant + pre-replace check).
   - Keep all test fixtures, scratch projects, and verification captures outside the checkout under `C:\PLATZ ATLAS\.platz-atlas-verification\<milestone>`.
4. **Repair before expanding:** Close listed `REPAIRS_REQUIRED` blockers before promoting a baseline or adding new features on top of broken boundaries.
5. **Never self-approve:** Mark new implementation milestones `<ID> · PENDING_INDEPENDENT_REVIEW` in `WORKING.md` and leave independent acceptance in `docs/ai/ATLAS_HANDOFF.md` to an independent audit pass.

---

## 7. Next GPT Development Batch (Safe Starting Point & Five High-Impact Improvements)

### 7.1 Safest Starting Point & Retention Guidance
- **Start at tag `gf-011` (`7b9b7a6`) + this audit commit.**
- **Retain (proven architecture):**
  - `PlatzAtlas.Core/ProjectStore.cs` (single-path mutex, flush-before-replace, `.previous` backup preservation) and `ProjectFormat.cs` (strict JSON envelope, SHA-256 verification, duplicate/unmapped field rejection).
  - `PlatzAtlas.Rendering/VulkanViewport*.cs` + `OpenGlViewport.cs` (native HWND lifetime isolation, `VK_EXT_debug_utils` Khronos validation tracking, on-demand pixel readback, and cascaded fallback in `RoomViewport.cs`).
  - `PlatzAtlas.Atlas/ProjectQuestionHost.cs` (serialized grant/revocation/expiry critical section and two-phase review/commit pattern).
  - `Build.ps1` + `tools/BundlePlayer.ps1` (locked offline restore, deterministic ZIP + `SHA256SUMS.txt` + license bundling).
- **Step 0 (Pre-Expansion Repair Gate — do first and verify):**
  Fix the 4 small defects in Section 4.2 (`MainWindow.Atlas.cs` `ReadSavedSnapshot()` on `Allow`; `App.xaml.cs` non-modal CLI `--export-room` error handling; `PlatzAtlas.Player` `--renderer` flag + `app.manifest` + `VulkanValidated` + `START-HERE.txt`; case-insensitive SHA-256 check in `SwitchEvidenceProfile` & `RoomPackage`).

### 7.2 Five Substantial, User-Visible Improvements (Next Batch Target)
Rather than another tiny Night Museum tweak, aim the next coherent batch (split across clean, testable vertical slices if needed for budget safety) at breaking out of the Night Museum box:

1. **General Editable Scene & Entity/Component Model (Schema 4 with Lossless v1–v3 Migration):**
   - Generalize `MuseumProject` into a `PlatzProject` (`schemaVersion: 4`) supporting arbitrary `SceneEntity` records (stable `Guid`, display name, room/group tag, 2D transform `X, Y, Width, Height, Rotation`, and typed components: `SpriteVisual`, `LightEmitter`, `SwitchControl`, `DoorObstacle`, `BoxCollider`, `SpawnPoint`) plus a generic `SetEntityProperty` / `CreateEntity` / `DeleteEntity` undoable transaction pipeline.
   - Automatically migrate Schema 1–3 Night Museum files in memory on load (preserving all IDs, receipts, and history) and guard migration + `.previous` recovery with round-trip tests.
2. **Real Scene Creation, Direct Viewport Manipulation & Playable Doors/Collisions (U3):**
   - Add **Blank Project** + **Add Entity** palette (Lantern/Lamp, Switch, Door, Wall/Obstacle, Sprite Prop) and **Delete Entity** in Studio, plus mouse drag-to-move in the viewport and numeric Transform/Link/Behavior editing in the Inspector (all Undoable).
   - Extend `RuntimeRoom` / `.platzroom` v2 and `MuseumPlaySession` (`Play` & `PlatzAtlas.Player`) with solid wall/door AABB collision and **Switches targeting Doors (`Open`/`Close`/`Toggle`) as well as Lights**, supporting both `.platzroom` v1 (`night-museum/1`) and v2 (`room-scene/2`) in Player.
3. **Asset Import, Built-In 32×32 Pixel/Sprite Editor & GPU Textured Quad Rendering (Phase 03/06):**
   - Allow importing PNG/PBM sprites into `ProjectAsset`s and add an interactive **Pixel Sprite Editor** panel/tab (32×32 or variable bounded canvas, 16-color/RGBA palette, click/drag paint + single-history-entry drag commit, Undo/Redo, PNG export, non-destructive source asset preservation).
   - Extend `RenderSnapshot` with optional textured quads/atlases and add real GPU texture/sampler upload + alpha-blended sprite drawing to **both** `VulkanViewport` (passing Khronos validation with 0 errors/warnings!) and `OpenGlViewport`, plus `CpuPreview` parity.
4. **Generalized Scene-Grounded ATLAS Queries & Typed Multi-Target Proposals (U2):**
   - Replace the literal `"West Switch"` / `"Which lantern..."` gate in `PlatzAtlas.Atlas` with a general scene-graph query & proposal profile (`SceneEvidenceProfile` v2): resolve switches, targets, doors, unused/broken links, lights, positions, and asset dependents by stable IDs across **any user-created room** (including duplicate display names).
   - Allow ATLAS to propose typed `EntityPatch` changes (e.g., move entity, relink switch to door/lamp, toggle/configure behavior, swap sprite asset) using the exact same detached preview + 1-minute review/commit + receipt pipeline.
5. **Offline Consented Trajectory Capture & Trainable Local Specialist Benchmark (U6 Foundation):**
   - Add an explicit opt-in local trajectory log (outside Git, sanitized of personal paths/secrets) recording `(task prompt, scene graph digest, candidate entities/actions, deterministic vs model choice, owner accept/correct outcome)`.
   - Add a standalone reproducible local benchmark script/harness comparing exact match, lexical retrieval, a linear ranker, and a tiny locally trained specialist model on held-out multi-room scenes—strictly **shadow-only** until outperforming simpler controls with zero wrong-object edits.
