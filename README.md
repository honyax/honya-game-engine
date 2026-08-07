# honya-game-engine

C#による自作ゲームエンジン学習プロジェクト。ソフトウェアラスタライザから始めて、生OpenGL、Silk.NETベースのエンジン本体、AAA品質を目指すグラフィックスデモまでをDay制の写経形式で進める。

全体計画: [docs/roadmap.md](docs/roadmap.md)

## 構成

- `docs/plans/DayXX.md` — 各Dayの計画書
- `reference/DayXX/` — リファレンスコード(各Day独立でビルド・実行可能)
- `work/` — 写経用(SoftwareRasterizer / RawGL / HonyaEngine / Labs)
- `assets/` — 共有素材

## 使い方

```
dotnet run --project reference/Day05   # リファレンスの実行
git diff --no-index reference/Day04 reference/Day05   # 前Dayとの差分
```

Claude Codeで「Day 5 を作成して」「Phase 1 のDayをすべて作成して」(create-day)。

## 進捗

### Phase 0〜1: ソフトウェアラスタライザ (Day 1〜10)

- [ ] Day 01 環境構築、ピクセルバッファ60fps表示
- [ ] Day 02 線分描画(Bresenham)
- [ ] Day 03 三角形の塗りつぶし
- [ ] Day 04 バリセントリック座標と属性補間
- [ ] Day 05 ベクトル・行列数学の自作
- [ ] Day 06 透視投影パイプライン
- [ ] Day 07 Zバッファ
- [ ] Day 08 テクスチャマッピング
- [ ] Day 09 シェーディング(ランバート+フォン)
- [ ] Day 10 objロード、カリング — **ソフトラスタライザ完成**

### Phase 2: 生OpenGLバインディング (Day 11〜13)

- [ ] Day 11 Win32 P/Invokeでウィンドウ
- [ ] Day 12 wglコンテキスト、OpenGL関数ロード
- [ ] Day 13 生OpenGLで三角形 — **バインディング体験完了**

### Phase 3: レンダラ層 (Day 14〜18)

- [ ] Day 14 Silk.NET移行、シェーダー管理
- [ ] Day 15 メッシュ/テクスチャ/マテリアル抽象化
- [ ] Day 16 カメラと3Dシーン表示
- [ ] Day 17 スプライトバッチ
- [ ] Day 18 スプライトバッチ最適化 — **レンダラ完成**

### Phase 4: エンジンコア (Day 19〜24)

- [ ] Day 19 ゲームループ(固定タイムステップ)
- [ ] Day 20 入力システム
- [ ] Day 21 リソース管理
- [ ] Day 22 GameObject + Component
- [ ] Day 23 ECS化
- [ ] Day 24 シーン管理 — **エンジンコア完成**

### Phase 5: ゲームが作れる状態に (Day 25〜30)

- [ ] Day 25 2D衝突判定
- [ ] Day 26 簡易物理
- [ ] Day 27 オーディオ
- [ ] Day 28 テキスト描画
- [ ] Day 29 卒業制作(前半)
- [ ] Day 30 卒業制作(後半) — **ゲーム1本完成**

### デモ必須編 (Day 31〜40)

- [ ] Day 31 FBO、HDR、トーンマッピング、ブルーム
- [ ] Day 32 glTF読み込み、アセット導入
- [ ] Day 33 シャドウマッピング
- [ ] Day 34 法線マップ・視差マッピング
- [ ] Day 35 PBR
- [ ] Day 36 IBL
- [ ] Day 37 SSAO
- [ ] Day 38 FXAA+カラーグレーディング
- [ ] Day 39 デモv1組み上げ(1)
- [ ] Day 40 デモv1組み上げ(2) — **必須構成のデモ完成**

### デモ推奨編 (Day 41〜46)

- [ ] Day 41 スキニングアニメーション
- [ ] Day 42 ディファードレンダリング
- [ ] Day 43 Forward+/クラスタード
- [ ] Day 44 TAA
- [ ] Day 45 被写界深度・モーションブラー
- [ ] Day 46 最終デモ — **AAA品質デモ完成**

### 教養編 (Day 47〜57、任意・順不同)

- [ ] Day 47 コンピュートシェーダ
- [ ] Day 48 レイマーチング(SDF)
- [ ] Day 49 CPUレイトレーサ(1)
- [ ] Day 50 CPUレイトレーサ(2)
- [ ] Day 51 GPUパストレーサ
- [ ] Day 52 ハードウェアレイトレーシング
- [ ] Day 53 ジオメトリシェーダ+テッセレーション
- [ ] Day 54 メッシュシェーダ
- [ ] Day 55 GPU駆動レンダリング
- [ ] Day 56 モダンライティング理論講読
- [ ] Day 57 3D Gaussian Splatting
