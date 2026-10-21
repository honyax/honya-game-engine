"""
honya-idle.glb / honya-walk.glb / honya-run.glb を作るスクリプト(Day 73a)。

Mixamo の移動用クリップ(Idle / Walking / Running)の**代役**。Mixamo のファイルは再配布できないので、
同じ「形」を持つクリップを標準ライブラリだけで書き出し、リポジトリにはこちらを入れる。

    python assets/mixamo/make_honya_locomotion.py

Mixamo のクリップを Blender で glb にしたもの(fbx_to_glb.py)と、わざと同じにしてあるところ:
  - 骨の名前が mixamorig:Hips / mixamorig:LeftArm ...(65 本。ヒューマノイドの名前ではない)
  - 単位が cm。骨の木の親(Armature)が 0.01 倍して m に直す
  - 骨の休みの回転がばらばら。Blender は「骨の +Y を子の向きへ」向けて骨を作るので、
    T ポーズでも腕の骨は 90 度、脚の骨は 180 度回った回転を持つ
  - 歩きと走りは**前へ進む**(腰の移動のキーに根の移動が入っている。Mixamo で In Place を付けずに落としたのと同じ)
  - 30 fps で、最初と最後のキーが同じ姿勢(ループの継ぎ目)

できるクリップ:
  - honya-idle.glb  4.0 秒。息をする・重心を少し左右へ
  - honya-walk.glb  1.1 秒(2 歩)
  - honya-run.glb   0.7 秒(2 歩)

歩きと走りの進む速さは、手で決めずに**脚の動きから求める**(planted_speed)。地面を蹴っている足が、
腰に対して後ろへ動く速さの平均を測り、それと同じ速さで腰を前へ進める。こうすると接地中の足が床の上で(平均して)止まって見える
(Mixamo のモーションキャプチャーの根の移動も、この関係を満たしている)。

腰の高さは 1.02m(AvatarSample_A は 0.895m)。歩きの速さはアバターに当てると背丈の比で縮む。

ライセンス: CC0(このスクリプトと、書き出した3本の glb の両方)。
"""

import json
import math
import os
import struct

FPS = 30
PREFIX = "mixamorig:"

# ---- 骨(T ポーズ。+Z を向き、左手が +X。単位は cm)----
# (名前, 親, 親から見た位置)。位置は世界の向きで書き、休みの回転を決めたあと親の向きへ直す。
BONES = [
    ("Hips", None, (0.0, 102.0, 0.0)),
    ("Spine", "Hips", (0.0, 10.0, 0.0)),
    ("Spine1", "Spine", (0.0, 12.0, 0.0)),
    ("Spine2", "Spine1", (0.0, 13.0, 0.0)),
    ("Neck", "Spine2", (0.0, 15.0, 0.0)),
    ("Head", "Neck", (0.0, 10.0, 1.0)),
    ("HeadTop_End", "Head", (0.0, 18.0, 0.0)),
    ("LeftUpLeg", "Hips", (9.0, -6.0, 0.0)),
    ("LeftLeg", "LeftUpLeg", (0.0, -45.0, 0.5)),
    ("LeftFoot", "LeftLeg", (0.0, -43.0, -1.0)),
    ("LeftToeBase", "LeftFoot", (0.0, -7.0, 13.0)),
    ("LeftToe_End", "LeftToeBase", (0.0, 0.0, 7.0)),
    ("LeftShoulder", "Spine2", (6.0, 10.0, 0.0)),
    ("LeftArm", "LeftShoulder", (12.0, 0.0, 0.0)),
    ("LeftForeArm", "LeftArm", (27.0, 0.0, 0.0)),
    ("LeftHand", "LeftForeArm", (26.0, 0.0, 0.0)),
]

# 指は「付け根の位置」と「節の長さ 3 つ + 先」で書く(Mixamo の 1〜4)。
FINGERS = [
    ("Thumb", (3.0, -1.5, 3.0), (1.0, -0.2, 0.8), [3.0, 3.0, 2.5, 2.0]),
    ("Index", (9.5, 0.0, 2.5), (1.0, 0.0, 0.0), [3.5, 2.5, 2.2]),
    ("Middle", (9.5, 0.0, 0.5), (1.0, 0.0, 0.0), [4.0, 2.8, 2.4]),
    ("Ring", (9.0, 0.0, -1.5), (1.0, 0.0, 0.0), [3.6, 2.6, 2.2]),
    ("Pinky", (8.0, 0.0, -3.3), (1.0, 0.0, 0.0), [2.8, 2.0, 1.8]),
]


def build_bones():
    bones = list(BONES)
    for finger, base, direction, lengths in FINGERS:
        length = math.sqrt(sum(c * c for c in direction))
        unit = tuple(c / length for c in direction)
        bones.append((f"LeftHand{finger}1", "LeftHand", base))
        segments = lengths if len(lengths) == 4 else lengths + [lengths[-1] * 0.8]
        for i, segment in enumerate(segments[:3]):
            bones.append((f"LeftHand{finger}{i + 2}", f"LeftHand{finger}{i + 1}", tuple(c * segment for c in unit)))

    # 右は左の鏡(X を反転)。
    for name, parent, offset in list(bones):
        if name.startswith("Left"):
            mirrored_parent = parent.replace("Left", "Right") if parent.startswith("Left") else parent
            bones.append((name.replace("Left", "Right"), mirrored_parent, (-offset[0], offset[1], offset[2])))
    return bones


# ---- クォータニオン (x, y, z, w) ----

def axis_angle(axis, degrees):
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


def inverse(q):
    return (-q[0], -q[1], -q[2], q[3])


def rotate(q, v):
    p = multiply(multiply(q, (v[0], v[1], v[2], 0.0)), inverse(q))
    return (p[0], p[1], p[2])


def from_to(a, b):
    """a を b へ回す最短の回転。真逆なら X 軸まわりに 180 度(Blender の脚の骨と同じ向き)。"""
    dot = sum(x * y for x, y in zip(a, b))
    if dot < -0.9999:
        return (1.0, 0.0, 0.0, 0.0)
    cross = (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
    q = (cross[0], cross[1], cross[2], 1.0 + dot)
    n = math.sqrt(sum(c * c for c in q))
    return tuple(c / n for c in q)


X = (1.0, 0.0, 0.0)
Y = (0.0, 1.0, 0.0)
Z = (0.0, 0.0, 1.0)
IDENTITY = (0.0, 0.0, 0.0, 1.0)


def rest_pose(bones):
    """
    休みの世界の回転(骨の +Y を最初の子へ向ける。Blender の骨の作り方)と、ローカルの TRS。
    子が無い骨(先っぽ)は、親から自分への向きを使う。
    """
    children = {}
    for name, parent, offset in bones:
        if parent is not None:
            children.setdefault(parent, []).append(offset)

    world = {}
    local = {}
    for name, parent, offset in bones:
        direction = children[name][0] if name in children else offset
        length = math.sqrt(sum(c * c for c in direction))
        world[name] = from_to(Y, tuple(c / length for c in direction))
        parent_world = world[parent] if parent is not None else IDENTITY
        local[name] = (rotate(inverse(parent_world), offset), multiply(inverse(parent_world), world[name]))
    return world, local


# ---- 動き ----
#
# 書くのは「正規化された回転」= T ポーズからの回り方を、世界の向きの軸で書いたもの(VRM の正規化された骨と同じ約束)。
# X 軸まわりの正の回転は「上の端を前(+Z)へ、下の端を後ろへ」倒す。脚を前へ振るのは負の回転。

def arms_down(side, swing, elbow, down=72.0):
    """腕を下ろして前後に振る。swing が正なら手が後ろへ。"""
    sign = 1.0 if side == "Left" else -1.0
    return {
        f"{side}Arm": multiply(axis_angle(X, swing), axis_angle(Z, -sign * down)),
        # 肘は「下ろした腕の前」へ曲げる。下ろした後の世界の軸で書くので、親(上腕)の回り方の後ろに掛かる。
        f"{side}ForeArm": axis_angle(Y, -sign * elbow),
    }


def relaxed_fingers(side, curl):
    sign = 1.0 if side == "Left" else -1.0
    turns = {}
    for finger, _, _, _ in FINGERS:
        for i in range(1, 4):
            amount = curl * (0.5 if finger == "Thumb" else 1.0)
            turns[f"{side}Hand{finger}{i}"] = axis_angle(Z, -sign * amount)
    return turns


def idle(t, duration):
    phase = 2.0 * math.pi * t / duration
    breath = math.sin(phase * 2.0)
    sway = math.sin(phase)
    turns = {
        "Hips": axis_angle(Z, 1.5 * sway),
        "Spine": axis_angle(X, 1.0 + 1.0 * breath),
        "Spine1": axis_angle(X, 0.8 * breath),
        "Neck": axis_angle(X, -1.0 * breath),
        "Head": axis_angle(Y, 4.0 * sway),
        "LeftUpLeg": axis_angle(Z, -1.5 * sway),
        "RightUpLeg": axis_angle(Z, -1.5 * sway),
        "LeftLeg": axis_angle(X, 3.0 + 2.0 * max(sway, 0.0)),
        "RightLeg": axis_angle(X, 3.0 + 2.0 * max(-sway, 0.0)),
    }
    turns.update(arms_down("Left", 2.0 * breath, 12.0))
    turns.update(arms_down("Right", 2.0 * breath, 12.0))
    turns.update(relaxed_fingers("Left", 18.0))
    turns.update(relaxed_fingers("Right", 18.0))
    hips = (1.2 * sway, 102.0 - 0.5 - 0.5 * breath, 0.0)
    return turns, hips


def stride(t, duration, hip, knee, arm, lean, bob):
    """歩きと走り。左脚は phase = 0 で前へ振り出し始める。腰はその場(前へ進めるのは build が足す)。"""
    phase = 2.0 * math.pi * t / duration
    left = math.sin(phase)
    right = -left

    def leg(side, s, c):
        # s > 0 で前へ。膝は振り出す途中(c > 0)で曲げ、足首は足の裏を地面へ向け直す。
        thigh = -hip * s
        bend = knee * (0.15 + 0.85 * max(c, 0.0) ** 1.5)
        return {
            f"{side}UpLeg": axis_angle(X, thigh),
            f"{side}Leg": axis_angle(X, bend),
            f"{side}Foot": axis_angle(X, -(thigh + bend) * 0.6),
        }

    turns = {
        "Hips": axis_angle(Y, 6.0 * left),
        "Spine": axis_angle(X, lean),
        "Spine1": axis_angle(Y, -5.0 * left),
        "Spine2": axis_angle(Y, -3.0 * left),
        "Neck": axis_angle(X, -lean * 0.5),
        "Head": axis_angle(X, -lean * 0.3),
    }
    turns.update(leg("Left", left, math.cos(phase)))
    turns.update(leg("Right", right, -math.cos(phase)))
    turns.update(arms_down("Left", arm * left, 15.0 + arm * 0.6 * max(-left, 0.0)))
    turns.update(arms_down("Right", arm * right, 15.0 + arm * 0.6 * max(-right, 0.0)))
    turns.update(relaxed_fingers("Left", 25.0))
    turns.update(relaxed_fingers("Right", 25.0))

    # 脚がいちばん開いた所(sin = ±1)で腰がいちばん低い。1 歩に 1 回、上下する。
    height = 102.0 - bob * (left * left)
    hips = (1.5 * left, height, 0.0)
    return turns, hips


# (名前, 秒, 動き, 前へ進むか)
CLIPS = [
    ("honya-idle", 4.0, lambda t, d: idle(t, d), False),
    ("honya-walk", 1.1, lambda t, d: stride(t, d, 24.0, 55.0, 18.0, 3.0, 3.0), True),
    ("honya-run", 0.7, lambda t, d: stride(t, d, 38.0, 95.0, 40.0, 12.0, 6.0), True),
]


def foot_positions(bones, motion, duration, steps):
    """
    左の足首(LeftFoot)の世界の位置を、1 周ぶん steps 個。腰はその場。
    正規化された回転は世界の向きで書いてあるので、順運動学は「親の位置 + 親までの回り方 x T ポーズの骨の向き」で済む。
    """
    parent_of = {n: p for n, p, _ in bones}
    offset_of = {n: o for n, _, o in bones}
    chain = []
    bone = "LeftFoot"
    while bone is not None:
        chain.append(bone)
        bone = parent_of[bone]
    chain.reverse()

    positions = []
    for i in range(steps):
        t = duration * i / steps
        turns, hips = motion(t, duration)
        position = hips
        rotation = turns.get("Hips", IDENTITY)
        for bone in chain[1:]:
            moved = rotate(rotation, offset_of[bone])
            position = tuple(p + m for p, m in zip(position, moved))
            rotation = multiply(rotation, turns.get(bone, IDENTITY))
        positions.append(position)
    return positions


def planted_speed(motion, duration, steps=240):
    """
    **足が滑らない速さ** [cm/s]。足首が腰に対して後ろへ動いている間(= 地面を蹴っている半周)の、後ろへ動く速さの平均。
    後ろへ動いた距離(歩幅)/ その時間、と同じ。腰をこの速さで前へ進めると、接地中の足は平均して床の上で止まる。
    """
    positions = foot_positions(build_bones(), motion, duration, steps)
    dt = duration / steps
    speeds = []
    for i in range(steps):
        a = positions[i]
        b = positions[(i + 1) % steps]
        if b[2] < a[2]:
            speeds.append((a[2] - b[2]) / dt)
    return sum(speeds) / len(speeds)


def build(name, duration, motion, travels):
    bones = build_bones()

    # 前へ進むクリップは、腰の移動に「足が滑らない速さ x 時刻」を足す(根の移動)。
    speed = planted_speed(motion, duration) if travels else 0.0
    walk = motion

    def motion(t, d):
        turns, hips = walk(t, d)
        return turns, (hips[0], hips[1], hips[2] + speed * t)

    world, local = rest_pose(bones)
    parent_of = {n: p for n, p, _ in bones}
    keys = int(round(duration * FPS)) + 1
    times = [i * duration / (keys - 1) for i in range(keys)]

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

    # ---- ノード: 0 が Armature(0.01 倍)、その下に骨 ----
    nodes = [{"name": "Armature", "scale": [0.01, 0.01, 0.01], "children": []}]
    index = {}
    for bone, parent, _ in bones:
        index[bone] = len(nodes)
        translation, rotation = local[bone]
        nodes.append({"name": PREFIX + bone, "translation": list(translation), "rotation": list(rotation)})
    for bone, parent, _ in bones:
        owner = nodes[index[parent]] if parent is not None else nodes[0]
        owner.setdefault("children", []).append(index[bone])

    # ---- アニメーション ----
    # ファイルに書くのはローカルの回転: r = W^-1 x N x W x R(W = 親の休みの世界の回転、R = 休みのローカルの回転)。
    # Day 71 の VrmAnimation.Denormalize と同じ式。読む側は正規化の式でこれを N に戻す。
    samples = [motion(t, duration) for t in times]
    time_accessor = add_accessor(times, "SCALAR")
    samplers = []
    channels = []

    def add_channel(node, path, values, kind):
        samplers.append({"input": time_accessor, "output": add_accessor(values, kind), "interpolation": "LINEAR"})
        channels.append({"sampler": len(samplers) - 1, "target": {"node": node, "path": path}})

    add_channel(index["Hips"], "translation", [hips for _, hips in samples], "VEC3")
    for bone, _, _ in bones:
        if bone.endswith("_End") or bone.endswith("4"):
            continue
        parent = parent_of[bone]
        parent_world = world[parent] if parent is not None else IDENTITY
        rest_local = local[bone][1]
        values = []
        for turns, _ in samples:
            n = turns.get(bone, IDENTITY)
            values.append(multiply(multiply(multiply(inverse(parent_world), n), parent_world), rest_local))
        add_channel(index[bone], "rotation", values, "VEC4")

    gltf = {
        "asset": {"version": "2.0", "generator": "honya-game-engine make_honya_locomotion.py (Day 73a)"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": nodes,
        "animations": [{"name": name, "channels": channels, "samplers": samplers}],
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
    folder = os.path.dirname(os.path.abspath(__file__))
    for name, duration, motion, travels in CLIPS:
        path = os.path.join(folder, name + ".glb")
        with open(path, "wb") as f:
            f.write(build(name, duration, motion, travels))
        speed = planted_speed(motion, duration) / 100.0 if travels else 0.0
        print(f"wrote {path}  ({duration:.2f} s, {speed:.3f} m/s)")
