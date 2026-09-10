# assets/models のライセンスと入手元

このフォルダの 3D モデルは、Khronos Group が公開しているサンプルアセットから取得したもの。
**Day 32(glTF 読み込み)以降のリファレンスで使う**。
Day 41 で、リグとアニメーションを持つ5体(CesiumMan / Fox / RiggedSimple /
BoxAnimated / SimpleSkin)を足した。

- 入手元: https://github.com/KhronosGroup/glTF-Sample-Assets
- 取得日: 2026-08-27(Day 32 のぶん) / 2026-09-06(Day 41 のリグ済み・アニメーション付きのぶん)

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

## CesiumMan.glb

- 出典: https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/CesiumMan
- (c) 2017 Cesium — [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)

**Day 41(スキニングアニメーション)の主役**。19 関節のリグ済み人型が歩く。
関節数・チャンネル数がほどよく、glTF の skin と animation を一通り通せる。

## Fox.glb

- 出典: https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/Fox
- (c) 2014 PixelMannen — [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/)(モデル)
- (c) 2014 tomkranis — [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)(リグとアニメーション)

**クリップが3本入っている**(Survey / Walk / Run)唯一のモデル。
Day 41 でクリップの切り替えに使い、Day 42 のブレンドでも続けて使う。
NORMAL を持たないので、法線を生成する経路もこのモデルで通る。

## RiggedSimple.glb

- 出典: https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/RiggedSimple
- (c) 2017 Cesium — [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)

関節2本の曲がる棒。**スキニングが壊れたときに原因を切り分ける**ためのモデルで、
関節が2本しかないので、重みの混ざり方を目で追える。

## BoxAnimated.glb

- 出典: https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/BoxAnimated
- (c) 2017 Cesium — [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)

**スキンを持たない**アニメーション。ノードの TRS が動くだけで、頂点は変形しない。
「アニメーション」と「スキニング」が別の仕組みであることを1体で示すために置いてある。

## SimpleSkin/

- 出典: https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/SimpleSkin
- (c) 2017 Marco Hutter — [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/)

glTF チュートリアルに出てくる**仕様の最小例**。頂点 10 個・関節2本・三角形8枚で、
バッファの中身を手で追える大きさ。JSON 1ファイルに base64 で全部入っている。

## torus.obj

Day 10 用に手続き生成したもの。外部由来ではない。
