# 移動用クリップ(idle / walk / run)— Day 73a 用

「移動のリターゲット」のページは、このフォルダの `.glb` から **idle・walk・run** の3本を選んで、速さで混ぜて流す。
どの役目かはファイル名で決める(名前に `idle` / `walk` / `run`(`jog`・`sprint` も走り)を含むもの)。
F3(素材)で、代役(`honya-` で始まる3本)と Mixamo のファイル(それ以外)を切り替える。

## リポジトリに入っているもの

### honya-idle.glb / honya-walk.glb / honya-run.glb(Mixamo の代役)

- 同じフォルダの `make_honya_locomotion.py` で作ったもの(`python assets/mixamo/make_honya_locomotion.py` で作り直せる)
- Mixamo のクリップを Blender で glb にしたものと、わざと同じ形にしてある
  - 骨の名前が `mixamorig:Hips` などの 65 本(ヒューマノイドの名前ではない)
  - 単位が cm(親の `Armature` が 0.01 倍を持つ)
  - 骨の休みの回転がばらばら(Blender は骨の +Y を子の向きへ向けて骨を作る)
  - 歩きと走りは前へ進む(根の移動を持つ。Mixamo で In Place を付けずに落としたのと同じ)
- 長さは idle 4.0 秒・walk 1.1 秒・run 0.7 秒。腰の高さは 1.02m(AvatarSample_A は 0.895m)
- 進む速さは脚の動きから求めた(walk 1.10 m/s・run 2.69 m/s)。この速さで運ぶと、接地した足が床の上で止まって見える
- ライセンス: CC0(スクリプトと3本の glb の両方)

### fbx_to_glb.py(Blender のスクリプト)

Mixamo の FBX を glb に直す。このエンジンは FBX を読まないので、Blender に1回だけ通す。ライセンス: CC0。

## 各自で入手して置くもの(リポジトリに入れない)

### Mixamo の Idle / Walking / Running

- 入手元: https://www.mixamo.com/ (Adobe のアカウントが要る。無料)
- 利用条件で、作品の中で使うのは自由だが、**アニメーションのファイルそのものを配ることはできない**。
  `.gitignore` で `assets/mixamo/*.fbx` と、`honya-` で始まらない `*.glb` を除外してある

手順:

1. Mixamo の Animations で、たとえば「Breathing Idle」「Walking」「Running」を選ぶ(キャラクターは既定の Y Bot のままでよい)
2. 歩きと走りは **In Place のチェックを外したまま**にする(根の移動から、足が滑らない速さを測るため)
3. Download で、次のとおりに落とす
   - Format: FBX Binary (.fbx)
   - Skin: **With Skin**(休みの姿勢を T ポーズにするため。下の注意)
   - Frames per Second: 30
   - Keyframe Reduction: none
4. 落とした `.fbx` をこのフォルダに置き、ファイル名に `idle` / `walk` / `run` が入るようにする(Mixamo の名前のままなら入っている)
5. Blender(5.2 で確かめた)で glb に直す

```
"C:/Program Files/Blender Foundation/Blender 5.2/blender.exe" -b --python assets/mixamo/fbx_to_glb.py -- assets/mixamo
```

6. アプリで「移動のリターゲット」の F3 を押して Mixamo に切り替え、F11(内訳)で確かめる

注意:

- **腕の垂れが 0 度付近であること**を内訳で確かめる。正規化の式は「ファイルの休みの姿勢 = T ポーズ」を前提にしている。
  休みの姿勢の腕が下がっている(Without Skin の FBX や、ほかの道具を通したファイルで起きうる)と、
  腕を下ろす動きが「動いていない」ことになり、歩いている間ずっと腕が T ポーズのまま残る
- In Place を付けて落とした歩き・走りは、根の移動が無いので速さを測れない。決め打ちの速さ(歩き 1.2 m/s・走り 3.0 m/s)で混ぜるので、足が滑ることがある
