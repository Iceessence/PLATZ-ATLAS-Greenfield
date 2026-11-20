# PLATZ ATLAS Studio

PLATZ ATLAS Studio is a local Windows editor for the Night Museum scene. Studio identifies its two identically named lanterns by gallery and stable object ID. ATLAS can answer a question about the saved museum, preview a proposed light change and save it after you approve it.

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

## Try the museum

1. Choose **New museum** or open [fixtures/NightMuseum.platz](fixtures/NightMuseum.platz). Both start an editable museum with a new project ID and the same starting layout. **West Gallery** starts off and **East Gallery** starts on.
2. Choose **Save as…** and write a working copy to a folder outside the source checkout. Keep the original fixture for repeatable tests.
3. Select **Lantern** in **West Gallery** using the scene list or the viewport. The inspector shows **West Switch**, its target, and the selected object's ID. Choose **Turn light on**. Both the west lantern's preview and state label change. East stays on.
4. Choose **Undo**, then **Redo**, or use **Ctrl+Z** and **Ctrl+Y**. Watch the west light and the current revision. The original revision remains visible beside it.
5. Save with **Ctrl+S**, close Studio, then launch it again and open your working copy. The west light should be on, the east light should still be on, and the saved revision and object IDs should match.
6. After replacing a saved revision, use the **Recover previous** file filter in **Open…** to open the adjacent `.platz.previous` backup. Save the recovered scene to a new file.

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

**Save** writes the actual scene when a preview is open. Approved edits add their receipt to project schema 2. Current Studio opens schemas 1 and 2. After the first approved edit on a schema 1 file, its adjacent previous revision still uses schema 1.

[WORKING.md](WORKING.md) records which paths were exercised, the evidence and the next milestone.

For local verification, launch Studio with `--diagnostics 'C:\path\studio.ndjson'` to record startup, open, save, validation, and render events. Choose a new filename in an existing directory. Add `--measure-frames` to warm the stationary viewport for 10 frames and log 120 measured redraws. Add `--simulate-device-loss` in a separate run to exercise automatic fallback (`Vulkan -> OpenGL` or `OpenGL -> CPU Reference`) after the first frame. Pass `--save-target 'C:\path\copy.platz'` when testing automated Save/Save As flows without opening the file dialog.

## Dependencies and provenance

| Dependency | Version and license |
| --- | --- |
| Microsoft .NET SDK and Desktop Runtime | SDK 10.0.401 pinned in [global.json](global.json). The tested desktop runtime is 10.0.12. Microsoft SDK and runtime distributions include their own license and third-party notices. WPF and the .NET runtime source use MIT. [WPF license](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT), [.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| Silk.NET Vulkan bindings | `Silk.NET.Vulkan`, `Silk.NET.Vulkan.Extensions.KHR` and transitive `Silk.NET.Core` 2.23.0, MIT. Package metadata identifies source commit `94605142f7b7bd6e69c9201e8e721d245c69eb7e`. [Silk.NET license](https://github.com/dotnet/Silk.NET/blob/main/LICENSE.md) |
| Microsoft NuGet transitive dependencies | `Microsoft.DotNet.PlatformAbstractions` 3.1.6 and `Microsoft.Extensions.DependencyModel` 9.0.9, MIT. The packages include license and third-party notice files. Exact application package digests are in the [renderer lockfile](src/PlatzAtlas.Rendering/packages.lock.json). |
| Optional shader rebuild | `Silk.NET.Shaderc` 2.23.0, MIT, and `Silk.NET.Shaderc.Native` 2.23.0, Apache-2.0. [Shader build lockfile](tools/ShaderBuild/packages.lock.json), [shaderc license](https://github.com/google/shaderc/blob/main/LICENSE) |

If you change the GLSL source, restore the locked shader tool packages and regenerate the embedded SPIR-V.

```powershell
dotnet restore tools/ShaderBuild --locked-mode
dotnet run --no-restore --project tools/ShaderBuild -- src/PlatzAtlas.Rendering/Shaders
```

The museum fixture and room artwork were created for this project. The product specification is preserved byte-for-byte in [docs](docs/Ultimate_PLATZ_ATLAS_MASTER_Version_1.docx).
