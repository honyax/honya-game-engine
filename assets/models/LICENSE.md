# assets/models のライセンスと入手元

このフォルダの 3D モデルは、Khronos Group が公開しているサンプルアセットから取得したもの。
**Day 32(glTF 読み込み)以降のリファレンスで使う**。

- 入手元: https://github.com/KhronosGroup/glTF-Sample-Assets
- 取得日: 2026-08-27

`.glb` は Git LFS で管理している(リポジトリ直下の `.gitattributes` を参照)。
クローンした直後に `git lfs install` と `git lfs pull` を一度だけ実行すること。

## DamagedHelmet.glb

- 出典: https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/DamagedHelmet
- (c) 2018 ctxwing — [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)
  (glTF への再構築・変換)
- (c) 2016 theblueturtle\_ — [CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/)
  (元になったモデル)

**派生元が CC BY-NC 4.0 なので、実質的に非商用限定**として扱う。
本リポジトリは学習目的なので問題にならないが、成果物を商用に使う場合は差し替えること。

## WaterBottle.glb

- 出典: https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/WaterBottle
- (c) 2017 Microsoft — [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/)

## Lantern.glb

- 出典: https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/Lantern
- (c) 2017 Microsoft / Frank Galligan — [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/)

## BoxTextured/

- 出典: https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/BoxTextured
- (c) 2017 Analytical Graphics, Inc. — [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)
  (モデル。ロゴは Cesium の商標)

`.gltf` + `.bin` + `.png` の3ファイル構成。
**外部参照の経路を試すために置いてある**唯一のモデルで、
ノードの変換が TRS ではなく `matrix` 形式で書かれている点でも他と違う。

## torus.obj

Day 10 用に手続き生成したもの。外部由来ではない。
