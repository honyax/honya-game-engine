"""
Mixamo の FBX を glb に直す Blender のスクリプト(Day 73a)。

このエンジンは FBX を読まない(FBX は仕様が公開されていない閉じた形式で、ローダーを書くと数百行になる)。
glTF なら Day 41 からのローダーがそのまま読めるので、Blender に1回だけ通して直す。

    "C:/Program Files/Blender Foundation/Blender 5.2/blender.exe" -b --python assets/mixamo/fbx_to_glb.py -- assets/mixamo

引数のフォルダにある *.fbx を全部、同じ名前の .glb にする(すでにある .glb は上書き)。

Blender の読み込みで決めていること:
  - automatic_bone_orientation は切る(骨の向きを勝手に直されると、休みの姿勢の回転が Mixamo と変わる。
    どちらにしても読む側は正規化で吸収するが、余計な手は入れない)
  - ignore_leaf_bones は切る(HeadTop_End などの先っぽの骨も残す。読む側が名前の表で捨てる)
  - 単位(cm)はそのまま。Armature が 0.01 倍を持つ形で書き出される

書き出しで決めていること:
  - アニメーションはシーンの時間軸を丸ごと1本(Mixamo の FBX は1ファイル1クリップ)
  - 全フレームをサンプリングして LINEAR で書く(読む側の根の移動を抜く処理は LINEAR だけを扱う)
  - メッシュ(With Skin で落としたときの体)も書き出すが、エンジンは骨とアニメーションしか読まない

**Mixamo からは With Skin で落とす**。休みの姿勢(ノードの姿勢)は、Blender がスキンのバインドポーズ(T ポーズ)から作る。
体の無いファイルだと、手がかりが無いぶん 1 コマ目の姿勢が休みの姿勢になることがあり、読む側の正規化の前提
(休みの姿勢 = T ポーズ)が崩れて腕が上がらなくなる(読む側は「腕が何度垂れているか」を測って知らせる)。

ライセンス: CC0。
"""

import glob
import os
import sys

import bpy


def convert(fbx_path, glb_path):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(
        filepath=fbx_path,
        automatic_bone_orientation=False,
        ignore_leaf_bones=False,
    )
    # **シーンの時間軸を丸ごと1本で書く**。アクションごとに書かせると、FBX の作り方によっては骨1本ずつ別のクリップに割れる。
    # 時間軸の長さは、読み込んだアクションのうち最も長いものに合わせる。
    actions = list(bpy.data.actions)
    if actions:
        bpy.context.scene.frame_start = int(min(a.frame_range[0] for a in actions))
        bpy.context.scene.frame_end = int(max(a.frame_range[1] for a in actions))

    bpy.ops.export_scene.gltf(
        filepath=glb_path,
        export_format="GLB",
        export_animations=True,
        export_animation_mode="SCENE",
        export_force_sampling=True,
        export_yup=True,
    )


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    folder = os.path.abspath(argv[0] if argv else os.path.dirname(os.path.abspath(__file__)))
    files = sorted(glob.glob(os.path.join(folder, "*.fbx")))
    if not files:
        print(f"fbx_to_glb: {folder} に .fbx がありません")
        return

    for fbx_path in files:
        glb_path = os.path.splitext(fbx_path)[0] + ".glb"
        convert(fbx_path, glb_path)
        print(f"fbx_to_glb: {os.path.basename(fbx_path)} -> {os.path.basename(glb_path)}")


main()
