# VRM アニメーション(.vrma)— Day 71 用

「VRMのアニメーション」のページの F2(クリップ)は、このフォルダの `.vrma` を全部読んで順に流す。
並びは `honya-wave` → `test` → それ以外(名前順)。

## リポジトリに入っているもの

### honya-wave.vrma(今日の自作)

- 同じフォルダの `make_honya_wave.py` で作ったもの(`python assets/vrma/make_honya_wave.py` で作り直せる)
- 4 秒でループする「右手を挙げて手を振る」。腰の移動・6本の骨の回転・表情(happy と aa)・視線を持つ
- 骨の休みの回転は全部単位(UniVRM の書き出しと同じ形)。腰の高さは 1.0m で、AvatarSample_A(0.895m)と違えてある
- ライセンス: CC0(スクリプトと .vrma の両方)

### test.vrma(three-vrm の見本)

- 出典: https://github.com/pixiv/three-vrm/blob/dev/packages/three-vrm-animation/examples/models/test.vrma
- 取得日: 2026-10-17
- 3 秒。右の上腕の回転・happy・視線の3本だけのキーを持つ。Blender(VRM Add-on for Blender)で書かれていて、
  **骨の休みの回転が全部ばらばら**(正規化が要る理由がいちばんよく見えるファイル)
- ライセンス: MIT License, Copyright (c) 2019-2026 pixiv Inc.

```
MIT License

Copyright (c) 2019-2026 pixiv Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 各自で入手して置くもの(リポジトリに入れない)

### VRoid Project「VRMアニメーション7種セット」

- 入手元: https://vroid.booth.pm/items/5512385 (BOOTH。無料)
- zip の中の `VRMA_01.vrma` 〜 `VRMA_07.vrma` を、**このフォルダに直接**置く
- `.gitignore` で除外してある。利用条件で、改変・商用は可だが、**取り出せる形での二次配布が禁止**されているため
- **クレジットの表記が必須**: 「キャラクターアニメーション: ピクシブ株式会社 VRoidプロジェクト」。
  7種セットのクリップを流している間は、HUD の VRMA の行にこの文言を出す
