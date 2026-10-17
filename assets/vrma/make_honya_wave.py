"""
honya-wave.vrma を作るスクリプト(Day 71)。

VRM アニメーション(.vrma)は「アニメーションだけを持つ glTF」で、VRMC_vrm_animation 拡張が
「どのノードがどの骨か」「どのノードがどの表情か」「どのノードが視線か」を書く。
このスクリプトは、その最小の形を標準ライブラリだけで書き出す。

    python assets/vrma/make_honya_wave.py

できるクリップ(4 秒でループする):
  - 右手を挙げて手を振る(右の上腕を 75 度上げ、前腕を 1.5 Hz で振る)
  - 腰を左右にひねりながら、少し上下に揺れる(腰の移動のキーを持つ)
  - 首をかしげる
  - 表情: happy が 0.5〜1.0 秒で出て 3.0〜3.5 秒で消える。1.0〜1.4 秒に aa(「やあ」)
  - 視線: 左右に 20 度、少し上を見る

わざと AvatarSample_A と違えてあるところ:
  - 背丈。腰の高さが 1.0m(AvatarSample_A は 0.895m)。腰の移動は背丈の比で縮めて当てる
  - 骨の休みの回転は全部単位(UniVRM の書き出しと同じ形)。three-vrm の test.vrma は Blender 流で、休みの回転がばらばら

ライセンス: CC0(このスクリプトと、書き出した honya-wave.vrma の両方)。
"""

import json
import math
import os
import struct

DURATION = 4.0
FPS = 30
KEYS = int(DURATION * FPS) + 1
OMEGA = 2.0 * math.pi / DURATION

# ---- 骨(T ポーズ。+Z を向き、左手が +X)----
# (名前, 親, 親から見た位置)。回転は全部単位。
BONES = [
    ("hips", None, (0.0, 1.0, 0.0)),
    ("spine", "hips", (0.0, 0.10, 0.0)),
    ("chest", "spine", (0.0, 0.12, 0.0)),
    ("upperChest", "chest", (0.0, 0.12, 0.0)),
    ("neck", "upperChest", (0.0, 0.13, 0.0)),
    ("head", "neck", (0.0, 0.10, 0.0)),
    ("leftShoulder", "upperChest", (0.03, 0.10, 0.0)),
    ("leftUpperArm", "leftShoulder", (0.10, 0.0, 0.0)),
    ("leftLowerArm", "leftUpperArm", (0.28, 0.0, 0.0)),
    ("leftHand", "leftLowerArm", (0.25, 0.0, 0.0)),
    ("rightShoulder", "upperChest", (-0.03, 0.10, 0.0)),
    ("rightUpperArm", "rightShoulder", (-0.10, 0.0, 0.0)),
    ("rightLowerArm", "rightUpperArm", (-0.28, 0.0, 0.0)),
    ("rightHand", "rightLowerArm", (-0.25, 0.0, 0.0)),
    ("leftUpperLeg", "hips", (0.09, -0.06, 0.0)),
    ("leftLowerLeg", "leftUpperLeg", (0.0, -0.45, 0.0)),
    ("leftFoot", "leftLowerLeg", (0.0, -0.43, 0.0)),
    ("rightUpperLeg", "hips", (-0.09, -0.06, 0.0)),
    ("rightLowerLeg", "rightUpperLeg", (0.0, -0.45, 0.0)),
    ("rightFoot", "rightLowerLeg", (0.0, -0.43, 0.0)),
]

EXPRESSIONS = ["happy", "aa"]


def axis_angle(axis, degrees):
    """軸まわりの回転のクォータニオン (x, y, z, w)。"""
    half = math.radians(degrees) * 0.5
    s = math.sin(half)
    return (axis[0] * s, axis[1] * s, axis[2] * s, math.cos(half))


def multiply(a, b):
    """ハミルトン積 a * b(b を先に回す)。"""
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
        aw * bw - ax * bx - ay * by - az * bz,
    )


def smoothstep(edge0, edge1, x):
    t = min(max((x - edge0) / (edge1 - edge0), 0.0), 1.0)
    return t * t * (3.0 - 2.0 * t)


X = (1.0, 0.0, 0.0)
Y = (0.0, 1.0, 0.0)
Z = (0.0, 0.0, 1.0)


def rotations(t):
    """時刻 t の、骨ごとのローカル回転(休みの回転が単位なので、そのまま正規化された回転でもある)。"""
    s = math.sin(OMEGA * t)
    return {
        "hips": axis_angle(Y, 6.0 * s),
        "spine": axis_angle(Z, -3.0 * s),
        "head": axis_angle(Z, 8.0 * s),
        # 右腕は -X を向いている。Z 軸まわりに -75 度回すと、ほぼ真上を向く。
        "rightUpperArm": axis_angle(Z, -75.0),
        # 前腕を 1.5 Hz で左右に振る(4 秒に 6 回。ループの継ぎ目でちょうど元に戻る)。
        "rightLowerArm": axis_angle(Z, -20.0 + 25.0 * math.sin(6.0 * OMEGA * t)),
        # 左腕は +X を向いている。Z 軸まわりに -72 度回すと、体の横へ下りる。
        "leftUpperArm": axis_angle(Z, -72.0),
    }


def hips_translation(t):
    # 0.5 秒周期で 2cm 沈む(4 秒に 8 回)。左右へ 2cm。
    bob = 0.02 * 0.5 * (1.0 - math.cos(8.0 * OMEGA * t))
    return (0.02 * math.sin(OMEGA * t), 1.0 - bob, 0.0)


def expression_weights(t):
    happy = smoothstep(0.5, 1.0, t) * (1.0 - smoothstep(3.0, 3.5, t))
    aa = 0.6 * math.sin(math.pi * min(max((t - 1.0) / 0.4, 0.0), 1.0))
    return {"happy": happy, "aa": aa}


def look_at(t):
    # 視線ノードの回転。+Z が正面。左(+X)へ yaw、上へ pitch(X 軸まわりの負の回転で +Z が上を向く)。
    yaw = 20.0 * math.sin(OMEGA * t)
    pitch = 5.0
    return multiply(axis_angle(Y, yaw), axis_angle(X, -pitch))


def build():
    times = [i / FPS for i in range(KEYS)]
    blob = bytearray()
    buffer_views = []
    accessors = []

    def add_accessor(values, kind):
        components = {"SCALAR": 1, "VEC3": 3, "VEC4": 4}[kind]
        offset = len(blob)
        flat = [c for v in values for c in (v if components > 1 else (v,))]
        blob.extend(struct.pack("<" + "f" * len(flat), *flat))
        buffer_views.append({"buffer": 0, "byteOffset": offset, "byteLength": len(flat) * 4})
        accessor = {"bufferView": len(buffer_views) - 1, "componentType": 5126, "count": len(values), "type": kind}
        if kind == "SCALAR":
            accessor["min"] = [min(values)]
            accessor["max"] = [max(values)]
        accessors.append(accessor)
        return len(accessors) - 1

    # ---- ノード ----
    nodes = []
    index = {}
    for name, parent, position in BONES:
        index[name] = len(nodes)
        nodes.append({"name": name, "translation": list(position)})
    for name, parent, _ in BONES:
        if parent is not None:
            nodes[index[parent]].setdefault("children", []).append(index[name])
    for name in EXPRESSIONS:
        index["expression:" + name] = len(nodes)
        nodes.append({"name": name})
    index["lookAt"] = len(nodes)
    nodes.append({"name": "lookAt"})

    # ---- アニメーション ----
    time_accessor = add_accessor(times, "SCALAR")
    samplers = []
    channels = []

    def add_channel(node, path, values, kind):
        samplers.append({"input": time_accessor, "output": add_accessor(values, kind), "interpolation": "LINEAR"})
        channels.append({"sampler": len(samplers) - 1, "target": {"node": node, "path": path}})

    add_channel(index["hips"], "translation", [hips_translation(t) for t in times], "VEC3")
    for bone in rotations(0.0):
        add_channel(index[bone], "rotation", [rotations(t)[bone] for t in times], "VEC4")
    for name in EXPRESSIONS:
        # 表情は「表情ノードの translation.x = 重み」。y と z は 0。
        add_channel(index["expression:" + name], "translation",
                    [(expression_weights(t)[name], 0.0, 0.0) for t in times], "VEC3")
    add_channel(index["lookAt"], "rotation", [look_at(t) for t in times], "VEC4")

    gltf = {
        "asset": {"version": "2.0", "generator": "honya-game-engine make_honya_wave.py (Day 71)"},
        "extensionsUsed": ["VRMC_vrm_animation"],
        "extensions": {
            "VRMC_vrm_animation": {
                "specVersion": "1.0",
                "humanoid": {"humanBones": {name: {"node": index[name]} for name, _, _ in BONES}},
                "expressions": {"preset": {name: {"node": index["expression:" + name]} for name in EXPRESSIONS}},
                "lookAt": {"node": index["lookAt"]},
            }
        },
        "scene": 0,
        "scenes": [{"nodes": [index["hips"]] + [index["expression:" + n] for n in EXPRESSIONS] + [index["lookAt"]]}],
        "nodes": nodes,
        "animations": [{"name": "wave", "channels": channels, "samplers": samplers}],
        "accessors": accessors,
        "bufferViews": buffer_views,
        "buffers": [{"byteLength": len(blob)}],
    }

    json_bytes = json.dumps(gltf, separators=(",", ":")).encode("utf-8")
    json_bytes += b" " * ((4 - len(json_bytes) % 4) % 4)
    blob += b"\0" * ((4 - len(blob) % 4) % 4)

    total = 12 + 8 + len(json_bytes) + 8 + len(blob)
    out = bytearray()
    out += struct.pack("<III", 0x46546C67, 2, total)
    out += struct.pack("<II", len(json_bytes), 0x4E4F534A) + json_bytes
    out += struct.pack("<II", len(blob), 0x004E4942) + blob
    return bytes(out)


if __name__ == "__main__":
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "honya-wave.vrma")
    with open(path, "wb") as f:
        f.write(build())
    print(f"wrote {path}")
