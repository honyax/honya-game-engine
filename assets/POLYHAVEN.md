# Poly Haven からの素材(Day 39 のデモ v1 用)

デモ v1(`assets/scenes/demo-v1.json`)で使っている HDRI・モデル・テクスチャは、
すべて [Poly Haven](https://polyhaven.com/) の配布物。

- **ライセンス: [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/)**
  (Poly Haven のすべての素材が CC0。表示義務は無いが、作者への敬意として下に記す)
- 取得日: 2026-09-06
- 取得方法: [Poly Haven の公開 API](https://redocly.github.io/redoc/?url=https://api.polyhaven.com/api-docs.json)
  (`https://api.polyhaven.com/files/<スラッグ>` が返す URL をそのまま取得)

## 解像度の選び方

**モデルとテクスチャは 1k、HDRI は 2k** で入れてある。合計およそ 35MB。

- 1080p で 10m 先の壁を見るのに、それ以上の解像度は絵として差が出ない
- 学習用リポジトリのクローンに 150MB(2k / 4k の場合)を払わせたくない
- HDRI だけ 2k なのは、**太陽が数画素しかない**ため。
  1k だと太陽の芯が 4 画素になり、`SkyAnalysis` の抽出が
  画素の粒に振り回される(2k なら 9 画素)

より高い解像度が欲しければ、URL の `1k` / `2k` を `2k` / `4k` に替えて取り直せばよい。
ファイル名に解像度が入っているので、`demo-v1.json` のパスも合わせて直すこと。

## HDRI(`assets/hdri/`)

| ファイル | 出典 | 作者 | このリポジトリでの役割 |
|---|---|---|---|
| `spruit_sunrise_2k.hdr` | [Spruit Sunrise](https://polyhaven.com/a/spruit_sunrise) | Greg Zaal | **デモの既定**。太陽がきれいに撮れていて、抽出が素直に決まる(視半径 0.30 度) |
| `venice_sunset_2k.hdr` | [Venice Sunset](https://polyhaven.com/a/venice_sunset) | Greg Zaal | 太陽がかすんでいる例。抽出はできるが、光の 4% しか担っていない |
| `the_sky_is_on_fire_2k.hdr` | [The Sky Is On Fire](https://polyhaven.com/a/the_sky_is_on_fire) | Greg Zaal, Rico Cilliers | **抽出が破綻する例**。撮影時に太陽が飽和していて、視半径が 53 度になる |

## モデル(`assets/models/`)

| フォルダ | 出典 | 作者 |
|---|---|---|
| `street_lamp_01/` | [Street Lamp 01](https://polyhaven.com/a/street_lamp_01) | Josh Dean |
| `metal_trash_can/` | [Metal Trash Can](https://polyhaven.com/a/metal_trash_can) | GurJas Studios |
| `fire_hydrant/` | [Fire Hydrant](https://polyhaven.com/a/fire_hydrant) | Gonçalo Felício |
| `concrete_road_barrier/` | [Concrete Road Barrier](https://polyhaven.com/a/concrete_road_barrier) | Amal Kumar |
| `old_tyre/` | [Old Tyre](https://polyhaven.com/a/old_tyre) | MP |

配布物からの変更点は1つだけ。**頂点データの `.bin` に解像度のタグを付けた**
(`street_lamp_01.bin` → `street_lamp_01_1k.bin`。`.gltf` の `buffers[0].uri` も合わせて書き換え)。
`.gitattributes` の LFS 規則を、既にコミット済みの `BoxTextured0.bin` に
当てずに新しいものだけへ当てるため。

`metal_trash_can` と `fire_hydrant` には**「きれいな版」と「錆びた版」が
1ファイルに並べて入っている**。デモ側はノード名で選り分けている
(`demo-v1.json` の `skipNodes`)。

## テクスチャ(`assets/textures/pbr/`)

| フォルダ | 出典 | 作者 |
|---|---|---|
| `pavement_02/` | [Pavement 02](https://polyhaven.com/a/pavement_02) | Dario Barresi, Charlotte Baglioni |
| `red_brick_03/` | [Red Brick 03](https://polyhaven.com/a/red_brick_03) | Rob Tuytel |

各フォルダに4枚。`_diff`(ベースカラー)・`_nor_gl`(法線。**OpenGL 向きで緑が上**)・
`_arm`(R=AO / G=粗さ / B=金属度)・`_disp`(高さ。視差マッピング用)。

`_arm` の詰め方は glTF が想定する ORM の並びとちょうど同じなので、
**1枚を `occlusionTexture` と `metallicRoughnessTexture` の両方に割り当てられる**
(`DemoScene.BuildMaterial`)。

`_nor_dx`(DirectX 向き。緑が下)も配布されているが、そちらは取っていない。
取り違えると凹凸が反転する——`Alt+8`(Day 34)でわざと再現できる。
