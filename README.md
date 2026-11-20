# PLATZ ATLAS Studio

PLATZ ATLAS Studio is a local Windows room editor. Draw sprite artwork, give a room an objective, play it in Studio, and export the saved revision to PLATZ Player. Ask ATLAS to inspect the saved room or preview an edit before approving it.

## Build and launch

Use an ordinary Windows 11 x64 account with PowerShell, Git for working with the source, and .NET SDK **10.0.401**. The SDK includes the .NET Desktop Runtime used by Studio. For a fresh machine, extract the portable SDK into a writable tools directory, put that directory at the front of the current shell's `PATH`, and set `DOTNET_ROOT_X64` to the same folder so the built executable can find its runtime. Microsoft's download page supplies the ZIP and its SHA-512 checksum. [Windows x64 SDK 10.0.401](https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/sdk-10.0.401-windows-x64-binaries-zip), [.NET app host environment variables](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-environment-variables#dotnet_root-dotnet_rootx86-dotnet_root_x86-dotnet_root_x64)

Run these commands from the project root in PowerShell.

```powershell
dotnet --version
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\Build.ps1 -Test
& '.\artifacts\bin\PlatzAtlas.Studio\release\PlatzAtlas.Studio.exe'
```

`Build.ps1` defaults to Release. Add `-Run` to launch after the build. Add `-Run -Cpu`, `-Run -OpenGl`, or `-Run -Vulkan` to choose a renderer backend, or `-Run -Project 'C:\path\NightMuseum.platz'` to open a particular file. The Studio executable accepts `--renderer <auto|vulkan|opengl|cpu>`, `--vulkan`, `--opengl`, `--cpu`, and `--project 'C:\path\NightMuseum.platz'`. It can be launched by absolute path from another working directory.

The first restore uses public NuGet packages and checks the versions and content hashes recorded in the lockfiles. Reuse that cache with `-Offline`. To prepare a transferable cache, set `$env:NUGET_PACKAGES` to an empty writable directory outside the checkout and run the build once with access to NuGet.org. Copy that directory to the offline machine, point `$env:NUGET_PACKAGES` at the copy, and run the build command with `-Offline -Test`. Offline restore uses only the specified cache and omits the online NuGet advisory lookup. The executable opens local project files without a package restore. [NuGet package cache](https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders), [Locked restore and NuGet Audit](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-restore)

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\Build.ps1 -Offline -Test
```

Vulkan presentation uses the machine's Vulkan loader and compatible display driver, with OpenGL 3.3 Core compatibility and CPU Reference fallbacks. The Inspector includes a **Renderer** selector (**Auto**, **Vulkan**, **OpenGL**, **CPU**) and Studio reports the active backend in the status bar. The source build uses checked-in SPIR-V, so the editor build needs no Vulkan SDK. Set `PLATZ_ATLAS_VK_VALIDATION=1` in the environment to enable `VK_LAYER_KHRONOS_validation` diagnostics when the Khronos validation layer is available on the host.

## Create and play an original room

1. Start with **Blank project**. In **Assets**, choose **Draw sprite…**, paint a symbol, and choose **Add to assets**. Select it and choose **Place selected image**. On the canvas, drag the object to move, the square handle to resize, and the diamond handle to rotate. **Snap to grid** uses 0.05 room units and 15-degree angles. **Ctrl+D** duplicates the placement. The Inspector has **Bring forward**, **Send backward**, and **Room appearance**. Try the name **Lumen Dash** and floor color **1C303C**.
2. To reproduce the Lumen Dash game, add and rename the objects below. Enter the fractions in the Inspector and choose **Apply transform**. Keep the artwork off the center path.

   | Type and name | X | Y | Width | Height | Rotation |
   | --- | ---: | ---: | ---: | ---: | ---: |
   | Spawn · Dock entrance | 0.50 | 0.90 | 0.065 | 0.105 | 0° |
   | Wall · West bulkhead | 0.20 | 0.60 | 0.36 | 0.09 | 0° |
   | Wall · East bulkhead | 0.80 | 0.60 | 0.36 | 0.09 | 0° |
   | Door · Relay shutter | 0.50 | 0.60 | 0.21 | 0.09 | 0° |
   | Lamp · Power signal | 0.22 | 0.24 | 0.09 | 0.15 | 0° |
   | Key · Copper key | 0.50 | 0.78 | 0.056 | 0.096 | 0° |
   | Goal · Launch bay | 0.50 | 0.24 | 0.17 | 0.15 | 0° |
   | Switch · Relay console | 0.50 | 0.70 | 0.045 | 0.077 | 45° |

3. Start **Relay shutter** closed and **Power signal** off. Set **Relay console** to target **Relay shutter** and **Power signal**, choose **Turn on / Open**, and select **Apply switch link**. Set **Launch bay** to require **1** key and choose **Apply goal rule**.
4. **Save**, then choose **Play**. Press **Up** six times to pick up the key and stop at the shutter. Press **E** to open the shutter and light the signal. Press **Up** up to twelve more times to reach **Goal complete · Launch bay**. **Stop** returns to the editor with the saved state.
5. Choose **Export room…** and open the package in Player. Repeat the same steps. **Restart room** puts the key back, closes the shutter, and turns the signal off.

Studio supports project schemas 1–5. A blank room starts in schema 4 and uses schema 5 when you add the new mechanics or styling features. Player reads room packages v1–v3. **Open… → Recover previous** can reopen the adjacent backup.

## Draw, edit, and place PNG artwork

**Assets → Draw sprite…** starts a transparent 16 × 16 or 32 × 32 image. Use **Paint**, **Erase**, **Eyedropper**, and **Fill** with a palette swatch or an eight-digit `RRGGBBAA` color. A continuous stroke takes one Undo. **Edit image copy…** starts a new editable square from a selected catalog image. A larger source is resampled into the canvas. **Add to assets** adds a new project asset, and **Export PNG…** writes a transparent PNG file. Once you place the asset, the room export embeds it for Player.

Open **Assets** and choose **Import PNG…**. Select a PNG up to 256 × 256 pixels and 2 MiB. The browser lists each imported image with its dimensions. Select one and choose **Place selected image**. Place the same art again for a second instance. Each instance has its own stable ID, position, size and rotation. Studio stores the decoded RGBA pixels in the project and includes referenced images in the room export. The project supports up to 262,144 imported pixels, including imports retained in history.

Transparent pixels blend with the scene in Vulkan, OpenGL and CPU Reference. The Studio status bar identifies the renderer handling the current scene. Import feedback reports invalid files before adding them to the asset browser.

## Ask ATLAS about an original room

1. **Save**. Select any object in the Scene list and open **ATLAS → Allow scene read**. Try **Inspect selection**, **Where is Bench light?**, or **What does Badge reader control?** with matching names in your room. Use the full object ID if multiple objects share a name.
2. **Show object** and **Show linked target** navigate to the objects cited by the answer. **Saved source details** shows the project, scene, revision, stable object ID, exact transform and evidence digest.
3. Choose **Check scene health** to find unlinked switches, duplicate names, a blocked spawn, or goals that need more keys than the room has.
4. Try **Wire Relay console to Power signal**, **Unlink Relay console**, **Resize Relay shutter to 0.22, 0.09**, **Rotate Relay console to 30**, or **Add Lamp named Service beacon at 0.82, 0.18**. **Duplicate Service beacon** makes a distinct object. Choose **Preview change** and inspect the named file and change. Choose **Confirm and save** within one minute or **Cancel preview**. The commit records the stable object and operation IDs. Undo and Redo work after reopen.

Studio uses deterministic reads scoped to the granted saved revision. The original router and the grounded two-stage candidate both failed their qualification gates and are available for offline reproduction. [Datasets and evaluation](tools/AtlasSpecialist/README.md)

## Try the museum

1. Choose **New museum** or open [fixtures/NightMuseum.platz](fixtures/NightMuseum.platz). Both start an editable museum with a new project ID and the same starting layout. **West Gallery** starts off and **East Gallery** starts on.
2. Choose **Save as…** and write a working copy to a folder outside the source checkout. Keep the original fixture for repeatable tests.
3. Select **Lantern** in **West Gallery** using the scene list or the viewport. The inspector shows **West Switch**, its target, and the selected object's ID. Choose **Turn light on**. Both the west lantern's preview and state label change. East stays on.
4. Choose **Undo**, then **Redo**, or use **Ctrl+Z** and **Ctrl+Y**. Watch the west light and the current revision. The original revision remains visible beside it.
5. Save with **Ctrl+S**, close Studio, then launch it again and open your working copy. The west light should be on, the east light should still be on, and the saved revision and object IDs should match.
6. After replacing a saved revision, use the **Recover previous** file filter in **Open…** to open the adjacent `.platz.previous` backup. Save the recovered scene to a new file.

## Play the saved museum

1. Open a saved museum, or save the current revision first. Choose **Play** above the viewport or press **F5**.
2. Under **Switches**, choose **Use West Switch** twice. With the default **Toggle** behavior, the west lantern changes each time. The panel reports the gallery and old and new light states. East stays as it was in the saved room.
3. Choose **Stop** or press **F5** again to return to the saved view. For an easy comparison, use the switch a third time before stopping. Closing Studio during Play also ends that session.

Play starts from the exact saved revision and keeps its switch state in memory. If another window saved a newer revision before you started, reopen the file to Play that revision.

## Export and play a room

1. In Studio, save a museum with the west lantern off, then choose **Export room…** and save a `.platzroom` file. Export uses the saved revision and is available outside Play when the editor has no unsaved changes.
2. Launch Player and open the file. From the repository root, the Release build accepts the package path directly.

   ```powershell
   & '.\artifacts\bin\PlatzAtlas.Player\release\PlatzAtlas.Player.exe' --room 'C:\path\NightMuseum.platzroom'
   ```

3. From the spawn position in the default layout, press **Left** five times and **Up** twice. You can also use **WASD** or the on-screen arrows. When the west diamond turns gold, press **E** twice. With **Toggle**, West goes off → on → off. **Restart room** restores the original light states and visitor position.
4. To try **Turn on**, select the West Gallery lantern in Studio and choose **Turn on** under **Switch behavior in Play**. Undo and redo with **Ctrl+Z** and **Ctrl+Y**. Save and export to a second file. Open it in Player and repeat step 3. West stays on after the second activation.

For a repeatable command-line export from a saved project, choose a new output filename. Player's `--check-room` validates a package and exits with code `0` on success or `1` on rejection.

```powershell
$studio = '.\artifacts\bin\PlatzAtlas.Studio\release\PlatzAtlas.Studio.exe'
& $studio --project 'C:\path\NightMuseum.platz' --export-room 'C:\path\NightMuseum.platzroom'
& '.\artifacts\bin\PlatzAtlas.Player\release\PlatzAtlas.Player.exe' --check-room 'C:\path\NightMuseum.platzroom'
```

`Build.ps1 -Offline -Test -Bundle` produces `artifacts\distribution\PlatzAtlas.Player-release.zip` from the cached dependencies. Extract the ZIP, run `PlatzAtlas.Player.exe`, and open the exported room. The bundle requires the .NET 10 Windows Desktop Runtime on Windows x64. It contains a quick-start file, the dependency notices and a SHA-256 manifest. A room export contains the scene and switch links for the Night Museum profile, with its source revision and a payload checksum. Player keeps a running session in memory and reads its artwork from the shared room renderer.

## Ask about the saved museum

1. Open a saved museum or save a new one. Select the east lantern first if you want to see citation navigation change the selection.
2. Open the **ATLAS** tab beside **Inspector**. Choose **Allow project read** and **Ask ATLAS** with the supplied question, “Which lantern does the west switch control?”
3. The answer identifies the West Gallery lantern and its saved light state. Expand **Source details** for the switch target, object field references, project and scene IDs, and snapshot checksum. Choose **Show in scene** to select the cited lantern and open its Inspector.
4. Return to **ATLAS** and choose **Revoke access**. The answer and source details clear. After a manual edit, save the new revision and allow a new read to ask again.

This first version uses a deterministic local lookup for the supplied question. Studio grants access to one saved project, scene and revision, then sends a bounded checksummed JSON snapshot inside the app. The lookup receives switch links, lantern identities, locations and light state. Edit history and asset contents stay in Core.

## Preview the proposed light change

1. With the saved museum answer visible, check **Proposed light change** and choose **Preview light change**. The viewport highlights the cited lantern in teal and shows its proposed state. The label below the scene shows both the preview state and the saved state.
2. Choose **Exit preview** to restore the saved view. You can also revoke access in **ATLAS**, or make an edit with the normal Inspector controls. An edit clears the preview and retires the grant.
3. While previewing, choose **Review save…** in **ATLAS**. Check the named file and light change, then choose **Confirm and save** within one minute. Studio saves a new revision, preserves the previous file and displays **Latest saved ATLAS edit** with the operation ID and payload checksum.
4. Reopen the saved project to inspect the receipt or choose **Undo** to reverse the light change. Save the reversal to keep it. **Cancel review** returns to the preview. If another window has saved a newer revision, reopen that revision before starting a new review.

**Save** writes the actual scene when a preview is open. The first approved museum edit on a schema 1 file saves its receipt in schema 2. The first museum switch behavior edit uses schema 3 and preserves earlier receipts. The adjacent `.platz.previous` file retains the preceding saved bytes.

[WORKING.md](WORKING.md) records which paths were exercised, the evidence and the next milestone.

For local verification, launch Studio with `--diagnostics 'C:\path\studio.ndjson'` to record startup, open, save, validation, and render events. Choose a new filename in an existing directory. Add `--measure-frames` to warm the stationary viewport for 10 frames and log 120 measured redraws. Add `--simulate-device-loss` in a separate run to exercise automatic fallback (`Vulkan -> OpenGL` or `OpenGL -> CPU Reference`) after the first frame. Pass `--save-target 'C:\path\copy.platz'` when testing automated Save/Save As flows without opening the file dialog.

## Dependencies and provenance

| Dependency | Version and license |
| --- | --- |
| Microsoft .NET SDK and Desktop Runtime | SDK 10.0.401 pinned in [global.json](global.json). The tested desktop runtime is 10.0.12. Microsoft SDK and runtime distributions include their own license and third-party notices. WPF and the .NET runtime source use MIT. [WPF license](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT), [.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| Silk.NET Vulkan bindings | `Silk.NET.Vulkan`, `Silk.NET.Vulkan.Extensions.KHR` and transitive `Silk.NET.Core` 2.23.0, MIT. Package metadata identifies source commit `94605142f7b7bd6e69c9201e8e721d245c69eb7e`. [Silk.NET license](https://github.com/dotnet/Silk.NET/blob/94605142f7b7bd6e69c9201e8e721d245c69eb7e/LICENSE.md) |
| Microsoft NuGet transitive dependencies | `Microsoft.DotNet.PlatformAbstractions` 3.1.6 and `Microsoft.Extensions.DependencyModel` 9.0.9, MIT. The packages include license and third-party notice files. Exact application package digests are in the [renderer lockfile](src/PlatzAtlas.Rendering/packages.lock.json). |
| Optional shader rebuild | `Silk.NET.Shaderc` 2.23.0, MIT, and `Silk.NET.Shaderc.Native` 2.23.0, Apache-2.0. [Shader build lockfile](tools/ShaderBuild/packages.lock.json), [shaderc license](https://github.com/google/shaderc/blob/main/LICENSE) |

If you change the GLSL source, restore the locked shader tool packages and regenerate the embedded SPIR-V.

```powershell
dotnet restore tools/ShaderBuild --locked-mode
dotnet run --no-restore --project tools/ShaderBuild -- src/PlatzAtlas.Rendering/Shaders
```

The museum fixture and room artwork were created for this project. The product specification is preserved byte-for-byte in [docs](docs/Ultimate_PLATZ_ATLAS_MASTER_Version_1.docx).
