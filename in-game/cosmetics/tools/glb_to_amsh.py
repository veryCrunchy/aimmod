"""Convert a Blender glTF binary (.glb) into an AimMod runtime mesh (.amsh).

    python glb_to_amsh.py piece.glb ../meshes/piece.amsh

Blender export settings: glTF Binary (.glb), +Y Up (the default), Apply
Modifiers, Normals on, UVs on, vertex colours optional, one mesh, triangulated,
no armature, no animation. Model in metres with the origin at the attach point
and the front of the piece along Blender's -Y (glTF +Z).

glTF is Y-up, right-handed, metres; the .amsh is UE's frame: X forward,
Y right, Z up, centimetres. Only the first mesh's first primitive is read;
TRIANGLES mode only. The output layout is documented in
in-game/native-mod/core/include/aimmod/Mesh.hpp.
"""
import json
import struct
import sys

COMPONENT = {5120: 'b', 5121: 'B', 5122: 'h', 5123: 'H', 5125: 'I', 5126: 'f'}
COUNT = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}
MAX_VERTICES, MAX_INDICES = 65536, 3 * 65536


def read_glb(path):
    data = open(path, 'rb').read()
    magic, version, length = struct.unpack_from('<4sII', data, 0)
    if magic != b'glTF' or version != 2:
        raise SystemExit('not a glTF 2 binary')
    offset, doc, binary = 12, None, b''
    while offset < length:
        size, kind = struct.unpack_from('<I4s', data, offset)
        chunk = data[offset + 8:offset + 8 + size]
        if kind == b'JSON':
            doc = json.loads(chunk)
        elif kind == b'BIN\x00':
            binary = chunk
        offset += 8 + size
    return doc, binary


def accessor(doc, binary, index):
    a = doc['accessors'][index]
    view = doc['bufferViews'][a['bufferView']]
    fmt, n = COMPONENT[a['componentType']], COUNT[a['type']]
    size = struct.calcsize('<' + fmt)
    stride = view.get('byteStride', size * n)
    start = view.get('byteOffset', 0) + a.get('byteOffset', 0)
    out = []
    for i in range(a['count']):
        values = struct.unpack_from('<' + fmt * n, binary, start + i * stride)
        if a.get('normalized') and fmt in 'BH':
            values = tuple(v / (255.0 if fmt == 'B' else 65535.0) for v in values)
        out.append(values)
    return out


def convert(src, dst):
    doc, binary = read_glb(src)
    prim = doc['meshes'][0]['primitives'][0]
    if prim.get('mode', 4) != 4:
        raise SystemExit('only triangles')
    attrs = prim['attributes']
    pos = accessor(doc, binary, attrs['POSITION'])
    nrm = accessor(doc, binary, attrs['NORMAL']) if 'NORMAL' in attrs else [(0.0, 1.0, 0.0)] * len(pos)
    uv = accessor(doc, binary, attrs['TEXCOORD_0']) if 'TEXCOORD_0' in attrs else [(0.0, 0.0)] * len(pos)
    col = accessor(doc, binary, attrs['COLOR_0']) if 'COLOR_0' in attrs else [(1.0, 1.0, 1.0, 1.0)] * len(pos)
    idx = [i[0] for i in accessor(doc, binary, prim['indices'])] if 'indices' in prim else list(range(len(pos)))
    if not pos or len(pos) > MAX_VERTICES or len(idx) % 3 or len(idx) > MAX_INDICES:
        raise SystemExit('mesh too large or not triangles')
    # glTF (x, y, z) metres, Y up -> UE (z, -x, y) centimetres. The mirror flips the winding.
    ue = lambda v, s: (v[2] * s, -v[0] * s, v[1] * s)
    out = bytearray(b'AMSH')
    out += struct.pack('<III', 1, len(pos), len(idx))
    for p in pos:
        out += struct.pack('<3f', *ue(p, 100.0))
    for n in nrm:
        out += struct.pack('<3f', *ue(n, 1.0))
    for t in uv:
        out += struct.pack('<2f', t[0], t[1])
    for c in col:
        rgba = list(c) + [1.0] * (4 - len(c))
        out += bytes(max(0, min(255, round(v * 255))) for v in rgba[:4])
    for i in range(0, len(idx), 3):
        out += struct.pack('<3I', idx[i], idx[i + 2], idx[i + 1])
    open(dst, 'wb').write(out)
    print(f'{dst}: {len(pos)} vertices, {len(idx) // 3} triangles')


if __name__ == '__main__':
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    convert(sys.argv[1], sys.argv[2])
