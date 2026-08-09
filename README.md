# honya-game-engine

C#による自作ゲームエンジン学習プロジェクト。ソフトウェアラスタライザから始めて、生OpenGL、Silk.NETベースのエンジン本体、AAA品質を目指すグラフィックスデモまでをDay制の写経形式で進める。

全体計画: [docs/roadmap.md](docs/roadmap.md)

## 構成

- `docs/plans/DayXX.md` — 各Dayの計画書
- `reference/DayXX/` — リファレンスコード(各Day独立でビルド・実行可能)
- `work/` — 写経用(SoftwareRasterizer / RawGL / HonyaEngine / Labs)
- `assets/` — 共有素材

## 実行方法(VSCode)

`.vscode/` に設定済み。**エディタで動かしたいプロジェクトのファイルを1つ開いた状態で:**

| 操作 | 動作 |
|---|---|
| **Ctrl+Shift+B** | 開いているファイルのプロジェクトを Release でビルドして起動 |
| **F5** | 同じプロジェクトを Debug でデバッグ実行(ブレークポイントが効く) |

開いているファイルから親フォルダを辿って一番近い `.csproj` を探すので、
`reference/DayXX` でも `work/` でも同じ操作で動く。Dayが増えても設定変更は不要。

ファイルを開いていないときは Ctrl+Shift+P → `Tasks: Run Task` から:

- `実行: reference の Day を指定 (Release)` — Day番号を入力して起動
- `ビルド: reference の全Day (Release)` — 全Dayが壊れていないか一括確認

FPSを評価するときは必ず Release で。Debug はソフトウェアラスタライザだと目に見えて遅い。

## 使い方(コマンドライン)

```
dotnet run --project reference/Day05                  # リファレンスの実行
dotnet run --project reference/Day05 -c Release       # FPSを見るときはRelease
git diff --no-index reference/Day04 reference/Day05   # 前Dayとの差分
```

Claude Codeで「Day 5 を作成して」「Phase 1 のDayをすべて作成して」(create-day)。

## 進捗

### Phase 0〜1: ソフトウェアラスタライザ (Day 1〜10)

- [x] Day 01 環境構築、ピクセルバッファ60fps表示
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

### 3Dゲーム編 (Day 41〜50): キャラクターが動き回るプレイアブルデモへ

- [ ] Day 41 スキニングアニメーション
- [ ] Day 42 アニメーション制御(ブレンド、ステートマシン)
- [ ] Day 43 剛体力学、Sphere/Plane衝突とインパルス解決
- [ ] Day 44 Box(OBB)衝突(SAT)
- [ ] Day 45 Capsule衝突とキャラクターコントローラ
- [ ] Day 46 Heightmapコライダとブロードフェーズ
- [ ] Day 47 摩擦・反発、Sequential Impulses — **ミニ物理エンジン完成**
- [ ] Day 48 パーティクルシステム
- [ ] Day 49 エフェクト応用(トレイル、ソフトパーティクル)
- [ ] Day 50 プレイアブルデモ組み上げ — **プレイアブルデモ完成**

### デモ推奨編 (Day 51〜55)

- [ ] Day 51 ディファードレンダリング
- [ ] Day 52 Forward+/クラスタード
- [ ] Day 53 TAA
- [ ] Day 54 被写界深度・モーションブラー
- [ ] Day 55 最終デモ — **AAA品質プレイアブルデモ完成**

### 教養編 (Day 56〜66、任意・順不同)

- [ ] Day 56 コンピュートシェーダ
- [ ] Day 57 レイマーチング(SDF)
- [ ] Day 58 CPUレイトレーサ(1)
- [ ] Day 59 CPUレイトレーサ(2)
- [ ] Day 60 GPUパストレーサ
- [ ] Day 61 ハードウェアレイトレーシング
- [ ] Day 62 ジオメトリシェーダ+テッセレーション
- [ ] Day 63 メッシュシェーダ
- [ ] Day 64 GPU駆動レンダリング
- [ ] Day 65 モダンライティング理論講読
- [ ] Day 66 3D Gaussian Splatting
