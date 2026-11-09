# Texture optimisation report — 6EME buildings and PONT bridges

Project `LyonVRCity2022` · Unity 2022.3.53f1 · target Meta Quest Pro (Android ARM64)
Date: 2026-11-09 · Branch `main` @ `f688476b` · **Nothing committed or pushed.**

**Status in one line:** the embedded glTF textures of 6EME (201 used) and PONT (16) were extracted
byte-for-byte, re-imported as normal Unity textures with an Android ASTC 6x6 override, and the prefabs now
override only the textured material slot. Verified in the Editor (batch mode, default platform) on a clone of the
project. **Not yet verified:** the Android build itself (Windows), on-device appearance, runtime GPU memory.

---

## 1. Why the build failed (recap, verified from `LastBuild.buildreport`)

`:launcher:compressReleaseAssets` → `Required array size too large`: `data.unity3d` was 3 593.41 MiB, one file above the
~2 GiB Java array limit. The second Gradle failure (`StackOverflowError`) ran in parallel and is most likely a
consequence of the same oversized archive (the 3.7 GB `out.aar` lint copy); it has not been re-tested.

### What the build-report numbers mean (they are not interchangeable)

| Quantity | Where it comes from | Example (PONT texture) |
|---|---|---|
| Source JPEG bytes | the image embedded in the glTF | 3–8 MiB per file (86.3 MiB for all 16, 6 distinct images) |
| Decoded size | 6250 × 6250 px | RGB24 111.8 MiB / RGBA32 149.0 MiB |
| Unity serialized size (`packedSize` in the report) | what is written into `sharedassets0.assets.resS` | **149.004 MiB** = 6250² × 4 B → uncompressed RGBA32, **no mip chain** |
| Archive contribution | `data.unity3d` is a compressed archive of all of that | old: 3 593 MiB for 6 259 MiB serialized (ratio 0.574) |
| Final APK | not measured here | — |

Corrections to the earlier summary: the report lists every texture twice (a 0-byte stub in `sharedassets0.assets` plus the
payload in `sharedassets0.assets.resS`). Real texture counts are therefore **201 (6EME), 16 (PONT) and 1 110 (2/3/5EME
JPGs)**, not 402 / 32 / 2 220. Sizes were unaffected. The 2/3/5EME ASTC figures in the report match
`ceil(w/6)·ceil(h/6)·16 B` + full mip chain exactly (2048×2048 → 2.38 MiB), which confirms they are already ASTC 6x6.

---

## 2. Stage A — audit before any change

* Project root `/Users/qiaoy/Desktop/project/environment/LyonVRCity2022`, branch `main`, commit `f688476b…`, **clean tree**.
* A Unity Editor (pid 18282) had the project open → **all Unity work was done on an APFS clone** of the project
  (`cp -cR`, `Temp/` removed) and only the intended files were copied back (`rsync --files-from`, allow-list checked).
* glTF sources: 207 `.gltf` in 6EME and 16 in PONT, real JSON (not LFS pointers; `*.gltf` is not LFS-tracked). All use
  `KHR_draco_mesh_compression`, base64 `data:image/jpeg` images, no `.glb`.
* Prefabs: 207 (6EME) + 16 (PONT), each a prefab *variant* of its glTF. The scene `LyonVRVity35.unity` instantiates the
  **prefabs** (205 + 16 distinct, 359 instances), never the glTFs; its only references to the glTF GUIDs are `m_Mesh` and
  nested-prefab modification targets, and it has no material overrides. No other asset references the glTF materials.
* Existing material pipeline: `Assets/Editor/BATIMaterialTool.cs`. Step 1 builds a URP/Lit material per texture named
  `<ID>_image_<i>` (only `_BaseMap` set, otherwise Unity defaults). Step 2 writes material *i* into slot *i* of **every**
  renderer of the prefab whose name starts with `<ID>_image_`. Materials for 2/3/5EME live in
  `Assets/Materials/BATI/<arr>/<block>/`, textures in `Assets/Textures/BATI/<arr>/<block>/`.
* glTF structure (all 6EME/PONT files): one image → one texture → material 0 (`baseColorTexture` only, `texCoord 0`,
  OPAQUE, not double-sided, metallic 0, no roughness factor, no normal/occlusion/emissive maps, sampler REPEAT + linear).
  6EME files have 2 materials / 2 primitives (textured + plain grey `default_ai_…`, base colour 0.8); PONT has 1.
  5 of 207 6EME files have no image at all.
* Existing 2/3/5EME texture import settings (reference): default platform 2048 max, automatic compressed, sRGB, mipmaps,
  not readable, repeat/bilinear, no Android override (Automatic = ASTC 6x6 on this project).

## 3. Findings that changed the plan

1. **`BATIMaterialTool` Step 2 is not applied as-is.** Each 6EME prefab has two child renderers: `geometry0` (textured)
   and `geometry0/geometry0_1` (plain grey). The tool puts the textured material on *both*. That is what happened in
   2EME/3EME/5EME (1 108 prefabs carry two slot-0 overrides — recorded, **not changed**). For 6EME/PONT the new
   `GltfTextureOptimizer` reuses Step 1 of the tool via reflection and replaces **only the slot that currently holds the
   glTF textured material**; the grey material and all slot counts stay as they are.
2. **Duplicate file names.** `69386BI158/69386BI158.gltf` (no image) and `69386BI158_1/69386BI158.gltf` (has an image)
   share a name; the image belongs to the prefab **`69386BI158 1`**. Extracted files are therefore named after the
   referencing **prefab** (resolved through the glTF `.meta` GUID and the prefab's `m_SourcePrefab`), which keeps the tool's
   `<PrefabName>_image_<i>` rule valid: `Assets/Textures/BATI/6EME/69386BI/69386BI158 1_image_0.jpg`. A name-based
   mapping would have attached that texture to the wrong prefab. `69386BI158` itself is correctly left unchanged.
3. **PONT: 10 of the 16 images are byte-identical to another bridge's** (6 distinct images). Per-model files were kept
   (naming rule); the duplicates are flagged in the manifest (`duplicate_of`). Merging them would save about 23 MiB at 2048.

## 4. Stage B/C — extraction and import

`Documentation/TextureOptimization/extract_gltf_textures.py` (handles `.gltf`/`.glb`, data URIs, external files,
bufferViews; no re-encoding; refuses to overwrite a different file; read-back SHA-256 check).

| | 6EME | PONT |
|---|---|---|
| glTF files scanned | 207 | 16 |
| images extracted | **202** (5 files have none) | **16** |
| image types | JPEG, 3 components (all) | JPEG, 3 components (all) |
| source bytes | 57.9 MiB | 86.3 MiB |
| decoded RGBA32 (what the old build serialized) | 2 430 MiB | 2 384 MiB |
| dimensions | 2048×1024 ×59, 2048² ×54, 1024×2048 ×46, 1024² ×19, 4096×2048 / 2048×4096 ×12, 4096² ×4, smaller ×8 | 6250² ×16 |
| errors | 0 | 0 |

Output: `Assets/Textures/BATI/6EME/<block>/<Prefab>_image_<i>.jpg` and `Assets/Textures/PONT/<Prefab>_image_<i>.jpg`.
Manifest: `texture_extraction_manifest.json` (+ `texture_extraction_summary.csv`) with glTF path, ID, image index/name,
MIME, dimensions, bytes, SHA-256, texture and material references (all `baseColor` of material 0), output path,
duplicate info. Original glTF files were not touched (`git status` shows no change under `Assets/Models`). The `.meta`
files / GUIDs were generated by Unity.

## 5. Stage D/E — materials, prefabs, Android compression

* Unity batch run (`GltfTextureOptimizer.RunAllBatch`) on the clone: **218 materials created** (131 + 71 in 6EME blocks
  `69386BM` / `69386BI`, 16 PONT), **218 prefabs updated**, 5 untextured 6EME prefabs left unchanged
  (`69386BI81 1`, `69386BI152`, `69386BI158`, `69386BI172`, `69386BI316`).
* Each prefab diff is exactly +4 lines: one `m_Materials.Array.data[0]` override for the textured renderer
  (fileID `-5796455465781500386`, the same renderer the 2/3/5EME prefabs override).
* Before/after snapshot of every renderer (223 prefabs, `verification/snapshot_*.json`): renderer paths, mesh names,
  submesh counts and slot counts identical; **218 slots changed, 212 unchanged, 0 unexpected** — every changed slot went
  from the glTF textured material to `<Prefab>_image_0` (URP/Lit, `_BaseMap` = the matching extracted JPG).
* Material values: URP/Lit, base colour white, `_Metallic 0`, **`_Smoothness 0`**. The glTF has metallic 0 and no
  roughness factor (= glTF default 1.0, i.e. smoothness 0); `BATIMaterialTool` defaults to smoothness 0.5, which would
  make 6EME/PONT slightly glossier than before. Set `TEXOPT_MATCH_GLTF=0` to get the 2/3/5EME-style 0.5 instead.
  *Consequence:* 6EME/PONT are matte as before, while 2/3/5EME keep their existing smoothness 0.5.
* Import settings of all 218 textures: default-platform and Standalone settings unchanged (automatic compressed, max 2048,
  sRGB, mipmaps, not readable); **Android override**: `overridden: 1`, **ASTC 6x6**, compression quality 50 (Normal),
  Max Size **4096 for 6EME (= original size)** and **2048 for PONT** (evaluation policy). 218 / 218 textures have the
  Android override; 0 do not.

## 6. Stage F — verification performed

| Check | Result |
|---|---|
| All 218 prefab material slots resolve, no missing material/texture | ✔ (snapshot) |
| Each texture is on the prefab it came from (including the `69386BI158 1` case) | ✔ (by construction + render) |
| Geometry / UV / transforms / colliders / NavMesh / scenes changed | ✔ none — only `*.prefab` (material overrides), new assets and tooling changed |
| Existing 2/3/5EME materials, textures, prefabs | ✔ untouched (`git status` empty for those folders) |
| Rendered before/after of 8 6EME and 3 PONT prefabs, 2 views each, empty test scene, default platform | ✔ mapping, UV orientation and colours match (`verification/sheet_*.png`); unchanged control prefab `69386BI158` renders **identically (Δ = 0)** |
| Pixel difference on rendered foreground | mean abs Δ 3.8–6.5 / 255 for buildings (new materials ≈ 2 levels brighter), up to ~20 for the small PONT foreground; caused by DXT1 + 2048 limit of the default platform and the shader change, not by mapping errors |

**Not verified (needs you):** Android ASTC rendering quality, the river crossings / banks / Organic–Grid areas in the real
scene on the headset, teleport anchors and signs, NPC behaviour, Parts 1–3 — none of those assets were modified, but they
were not run. The Editor preview of PONT uses the default 2048 setting, so it says nothing about 2048-vs-4096 quality on
device.

## 7. Stage G — expected size (estimate, not a build result)

Serialized ASTC 6x6 size per texture = `Σ ceil(w/6)·ceil(h/6)·16 B` over the full mip chain (validated against the old
report for 2/3/5EME).

| Group | Textures | Old serialized | New serialized (estimate) |
|---|---:|---:|---:|
| 6EME embedded → extracted, Android max 4096 | 201 | 2 422 MiB | **≈ 362 MiB** (at max 2048: 291) |
| PONT embedded → extracted, Android max 2048 | 16 | 2 384 MiB | **≈ 38 MiB** (at max 4096: 152) |
| 2/3/5EME JPG (unchanged) | 1 110 | 1 331 MiB | 1 331 MiB |
| TIN, NPC, UI, other | — | ≈ 122 MiB | ≈ 122 MiB |
| **Total serialized** | | **6 259 MiB** | **≈ 1 853 MiB** |

Largest remaining individual textures: four 6EME at 4096² (≈ 9.5 MiB each); everything else ≤ 4.8 MiB.

**`data.unity3d` is compressed**, so the archive will be smaller than the serialized total. Measured on a sample
(48 textures encoded with `EditorUtility.CompressTexture` ASTC 6x6, chunked LZ4): ratio ≈ 0.49 for 2/3/5EME textures,
≈ 0.41 for 6EME, ≈ 0.99 for PONT aerial imagery. The same method applied to the old RGBA32 data reproduces the observed
0.57 within a few percent, so the assumption (LZ4) looks right.

| Scenario | `data.unity3d` |
|---|---|
| Expected (sampled ratios, ±15 %) | **≈ 0.9–1.1 GiB** |
| Worst case (no compression at all) | ≈ 1.8 GiB (still < 2 GiB, only ~6 % margin) |

This is an engineering estimate, **not** a guarantee: the sample is small, the Editor encoder differs from the build
encoder, and the 2 GiB limit applies to the real file. If the Windows build still reports `data.unity3d` above ~1.5 GiB,
lower `TEXOPT_6EME_MAX` to 2048 (−71 MiB) and/or merge the 10 duplicate PONT images, and re-run step 1.

Runtime note (not a build-size matter): ≈ 1.85 GiB of ASTC textures is still a lot for Quest Pro GPU memory if everything is
resident at once; check frame time / memory on device.

## 8. Files

**Modified (218 prefab variants, +4 lines each):** `Assets/Prefabs/BATI/6EME/**/*.prefab` (202: 71 in block 69386BI + 131 in 69386BM)
and `Assets/Prefabs/PONT/*.prefab` (16). The 5 untextured 6EME prefabs are not modified. (The old build report listed 201 6EME
textures in the build; 202 were extracted because one 6EME prefab is not instantiated in the scene.)

**Created:** `Assets/Textures/BATI/6EME/**` (202 JPG + .meta), `Assets/Textures/PONT/**` (16 + .meta),
`Assets/Materials/BATI/6EME/**` (202 .mat + .meta), `Assets/Materials/PONT/**` (16), folder metas
(`Assets/Textures/BATI/6EME.meta`, `Assets/Textures/PONT.meta`, `Assets/Materials/BATI/6EME.meta`, `Assets/Materials/PONT.meta`,
`Assets/Editor/TextureOptimization.meta`), `Assets/Editor/TextureOptimization/GltfTextureOptimizer.cs(.meta)`,
`Documentation/TextureOptimization/**`.

**Sync to the Windows PC:** everything above (commit on this Mac and pull, or copy the same folders). `*.jpg` is Git LFS —
make sure `git lfs` is active. Keep each `.meta` with its asset. Do **not** delete the original glTFs, old materials or old
textures until the Windows build and a Quest Pro test have passed.

## 9. How to run / re-run (Editor)

* Menu `Tools → BATI Materials → Texture Optimisation → 1 - Apply Android Texture Settings` and `2 - Create + Apply Materials`
  (idempotent: existing materials are updated, already-applied prefabs show "no-texture/unchanged").
* Batch: `Unity -batchmode -quit -projectPath <p> -executeMethod GltfTextureOptimizer.RunAllBatch` with optional
  `TEXOPT_PONT_MAX`, `TEXOPT_6EME_MAX`, `TEXOPT_MATCH_GLTF`, `TEXOPT_OUT`.
* Extraction: `python3 Documentation/TextureOptimization/extract_gltf_textures.py --project <root>`.

## 10. Verify on the Windows PC

1. Pull/copy, open the project, let Unity import (Android target active), check the Console for errors.
2. Build Android Release; afterwards run
   `powershell -NoProfile -Command "Get-ChildItem 'C:\Users\lrkj3636\LyonVRCity2022\Library\Bee\Android\Prj\IL2CPP\Gradle\unityLibrary\src\main\assets\bin\Data' -File | Sort-Object Length -Descending | Select-Object -First 5 Name,@{n='MiB';e={[int]($_.Length/1MB)}}"`.
   Expected: `data.unity3d` ≈ 1 GiB, comfortably < 2 GiB; Gradle `BUILD SUCCESSFUL`.
3. On the headset: walk 6EME streets and each bridge; compare facade/bridge sharpness against 2/3/5EME and the old build;
   check frame rate and memory.

## 11. Unresolved / risks

* Android build not run (by design); sizes are estimates.
* PONT at 2048 is a 3× downscale of the 6250² source; decide after looking at the bridges on device (switch to 4096: +114 MiB serialized).
* 6EME/PONT are now matte exactly like their original glTF; 2/3/5EME materials keep smoothness 0.5 and show the building
  texture on their second (grey) renderer — a pre-existing property of those arrondissements, left as is.
* Old embedded textures remain inside the glTF files (they are no longer referenced by the scene's prefabs; the 2/3/5EME
  precedent shows they then drop out of the build). Confirm in the Windows build report.
* The Editor on this Mac was open during the copy; refocus it and let it import before pressing Play.
