#!/usr/bin/env python3
"""
extract_gltf_textures.py  --  LyonVRCity2022 texture optimisation, Stage B

Purpose
-------
The 6EME buildings and the PONT bridges keep their JPEG textures embedded (base64
data URIs) inside the .gltf files. glTFast imports those as uncompressed RGBA32
sub-assets, which is what pushed data.unity3d to ~3.5 GB. This script copies the
ORIGINAL embedded image bytes (no re-encoding) out to ordinary image files so Unity's
TextureImporter can compress them (ASTC on Android).

What it does
------------
* Parses .gltf (JSON) and .glb (binary container); handles images stored as
  - base64 data URIs,
  - external file URIs (relative to the glTF),
  - bufferView images (buffer = data URI, external .bin, or the GLB BIN chunk).
* Writes the exact original bytes to the output folder (never re-encodes).
* Records, per image: source glTF, model ID, image index, name, MIME type, pixel
  dimensions, byte size, SHA-256, referencing texture indices, referencing material
  indices (+ the material slot role), output path, duplicate information.
* Writes texture_extraction_manifest.json and texture_extraction_summary.csv.

It never modifies or deletes any glTF, never overwrites an existing different file,
and never touches .meta files (Unity creates those on import).

Output naming (matches the established 2EME/3EME/5EME convention used by
Assets/Editor/BATIMaterialTool.cs, which matches materials to prefabs by "<PrefabName>_image_"):
    6EME : Assets/Textures/BATI/6EME/<block>/<ID>_image_<index>.<ext>
    PONT : Assets/Textures/PONT/<ID>_image_<index>.<ext>
<ID> is the NAME OF THE PREFAB that references the glTF (looked up through the prefab's
m_SourcePrefab GUID and the glTF's .meta GUID). It equals the glTF file name except for duplicate
file names: e.g. 69386BI158/69386BI158.gltf (no image) and 69386BI158_1/69386BI158.gltf (has an
image) -> the image belongs to the prefab "69386BI158 1". If no unique prefab is found the glTF file
name is used and the record is flagged (id_source = "gltf-name-fallback").

Usage
-----
    python3 extract_gltf_textures.py --project <unity project root> [--dry-run]
"""
import argparse, base64, csv, glob, hashlib, json, os, struct, sys, urllib.parse

GLB_MAGIC = 0x46546C67  # 'glTF'
MIME_EXT = {"image/jpeg": ".jpg", "image/png": ".png", "image/webp": ".webp", "image/ktx2": ".ktx2"}


def sniff(b):
    """Return (mime, width, height, components) by reading magic bytes / headers."""
    if b[:3] == b"\xff\xd8\xff":
        i = 2
        while i + 9 < len(b):
            if b[i] != 0xFF:
                i += 1
                continue
            m = b[i + 1]
            if m in (0xC0, 0xC1, 0xC2, 0xC3):
                h, w = struct.unpack(">HH", b[i + 5:i + 9])
                return "image/jpeg", w, h, b[i + 9]
            if m in (0xD8, 0x01) or 0xD0 <= m <= 0xD7:
                i += 2
                continue
            i += 2 + struct.unpack(">H", b[i + 2:i + 4])[0]
        return "image/jpeg", None, None, None
    if b[:8] == b"\x89PNG\r\n\x1a\n":
        w, h = struct.unpack(">II", b[16:24])
        return "image/png", w, h, None
    if b[:4] == b"RIFF" and b[8:12] == b"WEBP":
        return "image/webp", None, None, None
    if b[:12] == b"\xabKTX 20\xbb\r\n\x1a\n":
        return "image/ktx2", None, None, None
    return None, None, None, None


def load_gltf(path):
    """Return (json_dict, glb_bin_chunk_or_None)."""
    with open(path, "rb") as f:
        raw = f.read()
    if len(raw) >= 12 and struct.unpack("<I", raw[:4])[0] == GLB_MAGIC:
        off, js, binchunk = 12, None, None
        while off + 8 <= len(raw):
            clen, ctype = struct.unpack("<II", raw[off:off + 8])
            data = raw[off + 8:off + 8 + clen]
            if ctype == 0x4E4F534A:
                js = json.loads(data.decode("utf-8"))
            elif ctype == 0x004E4942 and binchunk is None:
                binchunk = data
            off += 8 + clen  # GLB chunk lengths are already 4-byte padded
        return js, binchunk
    return json.loads(raw.decode("utf-8")), None


def read_uri(uri, base_dir):
    if uri.startswith("data:"):
        head, payload = uri.split(",", 1)
        mime = head[5:].split(";")[0] or None
        data = base64.b64decode(payload) if ";base64" in head else urllib.parse.unquote_to_bytes(payload)
        return data, mime
    p = os.path.join(base_dir, urllib.parse.unquote(uri))
    with open(p, "rb") as f:
        return f.read(), None


def texture_roles(gltf):
    """Map texture index -> list of (material index, role)."""
    roles = {}

    def add(info, mi, role):
        if isinstance(info, dict) and "index" in info:
            roles.setdefault(info["index"], []).append((mi, role))

    for mi, m in enumerate(gltf.get("materials", [])):
        pbr = m.get("pbrMetallicRoughness", {})
        add(pbr.get("baseColorTexture"), mi, "baseColor")
        add(pbr.get("metallicRoughnessTexture"), mi, "metallicRoughness")
        for k in ("normalTexture", "occlusionTexture", "emissiveTexture"):
            add(m.get(k), mi, k[:-7])
        for ename, ext in m.get("extensions", {}).items():
            if isinstance(ext, dict):
                for k, v in ext.items():
                    add(v, mi, ename + "." + k)
    return roles


def meta_guid(path):
    try:
        with open(path + ".meta", "r", errors="ignore") as f:
            for line in f:
                if line.startswith("guid:"):
                    return line.split(":", 1)[1].strip()
    except OSError:
        pass
    return None


def prefab_index(root, prefab_glob):
    """gltf guid -> [prefab names] for prefab variants whose source is that glTF."""
    idx = {}
    for pp in glob.glob(os.path.join(root, prefab_glob), recursive=True):
        if not pp.endswith(".prefab"):
            continue
        with open(pp, "r", errors="ignore") as f:
            for line in f:
                if "m_SourcePrefab:" in line:
                    g = line.split("guid:")[1].split(",")[0].strip() if "guid:" in line else None
                    if g:
                        idx.setdefault(g, []).append(os.path.splitext(os.path.basename(pp))[0])
                    break
    return idx


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--project", required=True)
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--manifest-dir", default=None,
                    help="default: <project>/Documentation/TextureOptimization")
    a = ap.parse_args()
    root = os.path.abspath(a.project)
    man_dir = a.manifest_dir or os.path.join(root, "Documentation", "TextureOptimization")

    sets = [
        # (label, glob under project, output folder builder)
        ("6EME", "Assets/Models/BATI/6EME/**/*.gl*", "Assets/Prefabs/BATI/6EME/**/*.prefab", lambda gid, gpath: os.path.join(
            root, "Assets/Textures/BATI/6EME", os.path.basename(os.path.dirname(os.path.dirname(gpath))))),
        ("PONT", "Assets/Models/PONT/**/*.gl*", "Assets/Prefabs/PONT/**/*.prefab", lambda gid, gpath: os.path.join(root, "Assets/Textures/PONT")),
    ]

    records, errors, seen_sha = [], [], {}
    for label, pat, prefab_pat, outdir_fn in sets:
        pidx = prefab_index(root, prefab_pat)
        files = sorted(f for f in glob.glob(os.path.join(root, pat), recursive=True)
                       if f.lower().endswith((".gltf", ".glb")))
        for gpath in files:
            gname = os.path.splitext(os.path.basename(gpath))[0]
            gguid = meta_guid(gpath)
            prefabs = pidx.get(gguid, [])
            if len(prefabs) == 1:
                gid, id_source = prefabs[0], "prefab-name"
            else:
                gid, id_source = gname, "gltf-name-fallback"
            try:
                gltf, glbbin = load_gltf(gpath)
            except Exception as e:  # noqa
                errors.append({"gltf": gpath, "error": f"parse failed: {e}"})
                continue
            gdir = os.path.dirname(gpath)
            buffers = gltf.get("buffers", [])
            bufcache = {}

            def get_buffer(i):
                if i in bufcache:
                    return bufcache[i]
                b = buffers[i]
                if "uri" in b:
                    data, _ = read_uri(b["uri"], gdir)
                else:
                    data = glbbin
                bufcache[i] = data
                return data

            roles = texture_roles(gltf)
            tex_by_image = {}
            for ti, t in enumerate(gltf.get("textures", [])):
                srcs = [t.get("source")]
                for ext in t.get("extensions", {}).values():
                    if isinstance(ext, dict) and "source" in ext:
                        srcs.append(ext["source"])
                for s in srcs:
                    if s is not None:
                        tex_by_image.setdefault(s, []).append(ti)

            for ii, im in enumerate(gltf.get("images", [])):
                rec = {"set": label, "gltf": os.path.relpath(gpath, root), "gltf_name": gname, "gltf_guid": gguid,
                       "referencing_prefabs": prefabs, "id_source": id_source, "model_id": gid,
                       "image_index": ii, "image_name": im.get("name"), "declared_mime": im.get("mimeType")}
                try:
                    if "uri" in im:
                        data, uri_mime = read_uri(im["uri"], gdir)
                        rec["storage"] = "data-uri" if im["uri"].startswith("data:") else "external-file"
                    else:
                        bv = gltf["bufferViews"][im["bufferView"]]
                        buf = get_buffer(bv["buffer"])
                        o = bv.get("byteOffset", 0)
                        data, uri_mime = buf[o:o + bv["byteLength"]], None
                        rec["storage"] = "bufferView"
                    mime, w, h, comps = sniff(data)
                    mime = mime or im.get("mimeType") or uri_mime
                    if mime not in MIME_EXT:
                        raise ValueError(f"unsupported/unknown image type: {mime}")
                    rec.update(mime_type=mime, width=w, height=h, jpeg_components=comps,
                               bytes=len(data), sha256=hashlib.sha256(data).hexdigest())
                    tis = tex_by_image.get(ii, [])
                    rec["gltf_texture_indices"] = tis
                    rec["gltf_material_refs"] = [{"material_index": mi, "role": r}
                                                 for ti in tis for (mi, r) in roles.get(ti, [])]
                    odir = outdir_fn(gid, gpath)
                    out = os.path.join(odir, f"{gid}_image_{ii}{MIME_EXT[mime]}")
                    rec["output_path"] = os.path.relpath(out, root)
                    dup = seen_sha.get(rec["sha256"])
                    rec["duplicate_of"] = dup
                    rec["deduplicated"] = False  # every model keeps its own file: BATIMaterialTool maps by <ID>_image_<i>
                    seen_sha.setdefault(rec["sha256"], rec["output_path"])
                    if not a.dry_run:
                        os.makedirs(odir, exist_ok=True)
                        if os.path.exists(out):
                            with open(out, "rb") as f:
                                if hashlib.sha256(f.read()).hexdigest() != rec["sha256"]:
                                    raise FileExistsError("output exists with different content")
                            rec["status"] = "already-present"
                        else:
                            with open(out, "wb") as f:
                                f.write(data)
                            rec["status"] = "written"
                        # read-back verification
                        with open(out, "rb") as f:
                            back = f.read()
                        if hashlib.sha256(back).hexdigest() != rec["sha256"]:
                            raise IOError("read-back SHA mismatch")
                    else:
                        rec["status"] = "dry-run"
                except Exception as e:  # noqa
                    rec["status"] = "error"
                    rec["error"] = str(e)
                    errors.append({"gltf": rec["gltf"], "image_index": ii, "error": str(e)})
                records.append(rec)

    os.makedirs(man_dir, exist_ok=True)
    if not a.dry_run:
        with open(os.path.join(man_dir, "texture_extraction_manifest.json"), "w") as f:
            json.dump({"records": records, "errors": errors}, f, indent=1)
        cols = ["set", "model_id", "id_source", "gltf", "image_index", "image_name", "mime_type", "width", "height", "bytes",
                "sha256", "gltf_texture_indices", "gltf_material_refs", "output_path", "duplicate_of",
                "deduplicated", "status"]
        with open(os.path.join(man_dir, "texture_extraction_summary.csv"), "w", newline="") as f:
            wr = csv.writer(f)
            wr.writerow(cols)
            for r in records:
                wr.writerow([json.dumps(r.get(c)) if isinstance(r.get(c), (list, dict)) else r.get(c, "")
                             for c in cols])
    by = {}
    for r in records:
        s = by.setdefault(r["set"], {"images": 0, "bytes": 0, "errors": 0, "dups": 0})
        s["images"] += 1
        s["bytes"] += r.get("bytes", 0)
        s["errors"] += r["status"] == "error"
        s["dups"] += bool(r.get("duplicate_of"))
    for k, v in by.items():
        print(f"{k}: {v['images']} images, {v['bytes']/1048576:.1f} MiB source JPEG/PNG bytes, "
              f"{v['dups']} identical-to-earlier, {v['errors']} errors")
    if errors:
        print("ERRORS:", json.dumps(errors[:5], indent=1))
        sys.exit(2)


if __name__ == "__main__":
    main()
