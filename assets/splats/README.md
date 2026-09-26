# 学習済みの 3D Gaussian Splatting(Day 67 用)

Day 67 のビューアの場面4(`4` キー)は、このフォルダの `.ply` のうち**名前がいちばん若いもの**を読む。
起動の引数でパスを渡せば、そちらが優先される。

**`.ply` はリポジトリに入れない**(`.gitignore` で除外してある)。1本が数百 MB あり、配布元ごとにライセンスも違うため。
各自でダウンロードしてここに置く。

## 動作確認に使ったもの

[Voxel51/gaussian_splatting](https://huggingface.co/datasets/Voxel51/gaussian_splatting)(Hugging Face)の `train` シーン。
元論文の実装(graphdeco-inria/gaussian-splatting)で学習したもので、写真は Tanks and Temples の「Train」(機関車 Western Pacific 713)。

| ファイル | 学習の回数 | 楕円体の数 | 大きさ |
|---|---|---|---|
| `FO_dataset/train/point_cloud/iteration_7000/point_cloud.ply` | 7,000 回 | 741,883 個 | 184 MB |
| `FO_dataset/train/point_cloud/iteration_30000/point_cloud.ply` | 30,000 回 | (未確認) | 267 MB |

計画書の数字は **7,000 回のほう**で取った。どちらも球面調和は次数 3(1個 62 列 = 248 バイト)。

```bash
# リポジトリ直下で
curl -L -o assets/splats/train-7000.ply \
  "https://huggingface.co/datasets/Voxel51/gaussian_splatting/resolve/main/FO_dataset/train/point_cloud/iteration_7000/point_cloud.ply"
```

- ライセンス: データセットのページには Apache-2.0 と書かれている(取得日 2026-09-26)。
  元の写真の条件は Tanks and Temples の配布元で確かめること。**ここでは手元で学習に使うだけで、再配布はしない**
- 同じデータセットに `truck` / `playroom` / `drjohnson` もある(370〜790 MB)

## ほかのシーンを使うとき

元論文の実装と同じ列(`x y z` / `f_dc_*` / `f_rest_*` / `opacity` / `scale_*` / `rot_*`)を持つ
`binary_little_endian` の `.ply` なら読める。圧縮形式(`.splat`・`.ksplat`・SuperSplat の圧縮 `.ply` など)は読めない。

学習済みのシーンは COLMAP の座標なので、**たいてい −Y が上**。逆さまに見えたら `U` で上を裏返す。
最初の立ち位置は楕円体の位置から見当で決めているので、外れたら WASD で動くか、別の場所から撮ったシーンなら `R` で戻ってから探す。
