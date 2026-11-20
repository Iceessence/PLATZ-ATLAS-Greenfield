# PLATZ ATLAS owner report · live creation and play

The Windows Studio now supports working-room ATLAS reads, approved edits and F5 Play before the first Save. The outliner and Inspector remain visible alongside a dedicated ATLAS workspace. Creators can configure reusable presets before placement, identify objects by number, zoom into the scene and animate sprites used by the standalone Player.

## Try the complete workflow

1. Open Studio to a blank room. Choose **Allow scene read**, enter `Build a keyed gate named Birch on the left` and click **Ask ATLAS**.
2. Choose **Preview change**. The outliner remains empty while the canvas shows the candidate. **Working source details** lists all eight edits. Choose **Apply to room** within one minute.
3. Press **F5**, **Up** six times, **E**, and **Up** up to twelve times. The switch opens Birch door and the key admits the visitor to Birch exit. **Restart** restores the initial state. **F5** returns to the working document.
4. Use **Undo** once to remove the entire plan, **Redo** to restore it, and **Save** to keep it. **Export room…** packages the saved revision for the standalone Player.
5. Try **Browse presets…** to find a Signal lamp and set its size and initial state. Place it, click **Frame selected**, and ask ATLAS to inspect the readable number in the outliner. Settings contains Room settings, grid spacing and renderer selection.

In **Assets → Draw sprite…**, select a canvas from 16 to 256 pixels per side, draw with an eight-digit RGBA value and use **Duplicate** in the Frames strip. Edit the second frame and set its duration. **Play frames** loops the preview. **Export sheet…** provides the PNG sheet and timing JSON. **Add to assets**, then **Place selected image**, makes the clip part of the room.

## Candidate and limits

| Scope | Commit | What remains |
| --- | --- | --- |
| Pixel Editor repair | `c1a41d6` | Independent reproduction of interrupted pointer/keyboard input. |
| GF-022 live working document | `21c0a79` | Ordinary scene reads, approvals, and Play are author-tested before the first save. The historical museum's explicit saved-evidence workflow is available under Examples. |
| GF-023 Studio workspace | `bc981bc` and `bf42c09` | Persisted layout preferences, full docking, multi-selection, and the general typed variable and descriptor system. Preset configuration covers existing typed components. |
| GF-024 sprite animation | `0b27714` | HSV and saved palettes, drawing shapes, layers, onion skin, animation tags, authored shape colliders, held-key control, and an added gameplay trigger. A sprite stays square while editing. Multi-frame size is chosen before adding frames. |
| GF-025 3D | Pending | Native 3D scene authoring, camera navigation, depth/picking, manipulation, persistence and Play. |
| GF-026 bounded ATLAS authoring | `731b949` | Conversational memory, general plan composition, coding and test workspace, and a newly qualified original specialist. The supported working-room plan accepts a named left, center or right lane on an empty scene. |

The implementation candidate is `bf42c09`. The accepted baseline is `e77ef40`, and independent acceptance of this candidate remains pending. See the [canonical handoff](ATLAS_HANDOFF.md) for the full commit hashes, the preserved original backlog, and Neon's next review.

## Native intelligence

This batch established no new original-model advantage. The keyed-gate workflow is deterministic and uses the working-room grant, typed operations, validated staging and a human review. It uses no model API, trained inference, external source or execution tool.

The current read-only ATLAS Next reference at `C:\ATLAS\ATLAS-Next` reported `main` at `712d88bace582c252f407049c21f76945dafab25` through its Git ref. Its canonical handoff records NEXT-022 at `cea141cf9cb0bc0c1d2918c97a60cc8aed903833` as accepted with limitations. The original task classifier scored **19/24** on protected evaluation, tied the linear control at **19/24**, and produced two wrong shadow outputs. The exact-typed control scored **17/24**. The source of the later procedure ranker and team routing still continues past Primary's `withhold` decision. That branch remains unsuitable for integration. No repository files there were changed and evaluator-only cases were left in their existing custody.

The earlier Greenfield GF-021 candidate remains quarantined. Its independent test routed **0/30** supported prompts with **0/30** unsafe accepts, while the bigram baseline got **28/30** supported with **2/30** unsafe accepts. Preserve both model artifacts and use fresh evaluator-owned project families with task completion and wrong-change outcomes for future promotion. The numerical comparison and original Greenfield evaluation remain in the [specialist record](../../tools/AtlasSpecialist/README.md).

## Build and verification receipts

A fresh source export of `bf42c09` was built from the pinned SDK and locked offline cache. Git's shell helper could not launch during the attempted local clone in this host sandbox, so the clean verification used `git archive` from the exact commit. `Build.ps1 -Offline -Test -Bundle` passed **31 test groups**, compared with 27 at the previous implementation candidate. The displayed WPF regression runner passed its five scenarios. Its test evidence includes the working-room preview, the configured preset, the sprite timeline and the keyed-gate win.

The extracted Player accepted v1, v2 and v3 room packages and rejected the tampered v2 input. A Windows keyboard run won and restarted Birch gate with Vulkan validation at zero errors and warnings. Both authored animation frames were visible in the standalone Vulkan Player, and the door still opened. The isolated native probe passed Vulkan/OpenGL alpha and paint-order checks, including `#1a5a7fff` and `#f783d2ff` readbacks.

The evidence root is `C:\PLATZ ATLAS\.platz-atlas-verification\gf-022-026`. Read `clean-build.txt`, `clean-gui.txt`, `clean-rendering.txt`, `compatibility.json`, `birch-player.ndjson` and `animated-player.ndjson`. The extracted bundle verified **24/24** manifest entries and its ZIP SHA-256 is `55b49d19833d40cfe0acfc3d7fb0d308d5e6d65fa232378172982ccd53c1807b`.

```powershell
$evidence = 'C:\PLATZ ATLAS\.platz-atlas-verification\gf-022-026'
& "$evidence\source-bf42c09\artifacts\bin\PlatzAtlas.Studio\release\PlatzAtlas.Studio.exe"
& "$evidence\final-player\PlatzAtlas.Player.exe" --room "$evidence\clean-ui\birch-gate.platzroom"
```

To see the animated art in Player, use `clean-ui\switchback-animation.platzroom`; the animated panel is at the upper right. Press **E** from the start to operate the dock shutter.
