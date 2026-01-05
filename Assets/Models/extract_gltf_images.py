#!/usr/bin/env python3
"""
Batch extract images embedded in .gltf or .glb files
and put ALL extracted images into ONE target folder.

Usage:
    python3 extract_gltf_images.py <gltf_folder> <output_folder>

Example:
    python3 extract_gltf_images.py Assets/Models/BATI Assets/Textures/BATI

python3 /Users/qiaoy/Desktop/project/environment/LyonVRCity2022/Assets/Models/extract_gltf_images.py \
/Users/qiaoy/Desktop/project/environment/LyonVRCity2022/Assets/Models/BATI/2EME/69382AH \
/Users/qiaoy/Desktop/project/environment/LyonVRCity2022/Assets/Textures/BATI/2EME/69382AH
"""

import os
import sys
import json
import base64
import struct

def read_file_bytes(path):
    with open(path, 'rb') as f:
        return f.read()

def load_gltf_and_buffers(path):
    ext = os.path.splitext(path)[1].lower()

    # ---------- GLB ----------
    if ext == '.glb':
        data = read_file_bytes(path)

        magic, version, length = struct.unpack_from('<4sII', data, 0)
        if magic != b'glTF':
            raise RuntimeError("Not a valid GLB file")

        offset = 12
        json_chunk = None
        bin_chunk = None

        while offset < len(data):
            chunk_len, chunk_type = struct.unpack_from('<II', data, offset)
            offset += 8
            chunk_data = data[offset:offset + chunk_len]
            offset += chunk_len

            if chunk_type == 0x4E4F534A:   # JSON
                json_chunk = chunk_data.decode('utf-8')
            elif chunk_type == 0x004E4942: # BIN
                bin_chunk = chunk_data

        gltf = json.loads(json_chunk)
        buffers = [bin_chunk if bin_chunk else b'']
        return gltf, buffers

    # ---------- GLTF ----------
    else:
        with open(path, 'r', encoding='utf-8') as f:
            gltf = json.load(f)

        buffers = []
        for b in gltf.get('buffers', []):
            uri = b.get('uri')
            if not uri:
                buffers.append(b'')
            elif uri.startswith('data:'):
                buffers.append(base64.b64decode(uri.split(',', 1)[1]))
            else:
                bufpath = os.path.join(os.path.dirname(path), uri)
                buffers.append(read_file_bytes(bufpath))

        return gltf, buffers

def extract_images(gltf_path, output_dir):
    gltf, buffers = load_gltf_and_buffers(gltf_path)
    images = gltf.get('images', [])
    if not images:
        return

    model_name = os.path.splitext(os.path.basename(gltf_path))[0]

    for idx, img in enumerate(images):
        image_name = img.get('name') or f"image_{idx}"

        # ---------- data URI ----------
        if 'uri' in img and img['uri'].startswith('data:'):
            header, b64 = img['uri'].split(',', 1)
            mime = header.split(';')[0].split(':')[1]
            ext = 'jpg' if 'jpeg' in mime else mime.split('/')[-1]
            data = base64.b64decode(b64)

        # ---------- external file ----------
        elif 'uri' in img:
            src = os.path.join(os.path.dirname(gltf_path), img['uri'])
            if not os.path.exists(src):
                print("Missing external image:", src)
                return
            with open(src, 'rb') as f:
                data = f.read()
            ext = os.path.splitext(src)[1].lstrip('.')

        # ---------- bufferView ----------
        elif 'bufferView' in img:
            bv = gltf['bufferViews'][img['bufferView']]
            buf = buffers[bv.get('buffer', 0)]
            start = bv.get('byteOffset', 0)
            length = bv['byteLength']
            data = buf[start:start + length]
            mime = img.get('mimeType', 'image/jpeg')
            ext = 'jpg' if 'jpeg' in mime else mime.split('/')[-1]

        else:
            continue

        # ---------- write file ----------
        out_name = f"{model_name}_{image_name}.{ext}"
        out_path = os.path.join(output_dir, out_name)

        with open(out_path, 'wb') as f:
            f.write(data)

        print("Extracted:", out_path)

def main():
    if len(sys.argv) != 3:
        print("Usage: python3 extract_gltf_images.py <gltf_folder> <output_folder>")
        sys.exit(1)

    gltf_folder = sys.argv[1]
    output_folder = sys.argv[2]

    if not os.path.isdir(gltf_folder):
        print("Invalid gltf folder:", gltf_folder)
        sys.exit(1)

    os.makedirs(output_folder, exist_ok=True)

    for root, _, files in os.walk(gltf_folder):
        for f in files:
            if f.lower().endswith(('.gltf', '.glb')):
                path = os.path.join(root, f)
                try:
                    extract_images(path, output_folder)
                except Exception as e:
                    print("Error processing", path, e)

if __name__ == "__main__":
    main()
