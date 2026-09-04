using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 卒業制作の描画。**状態を1文字も書き換えない**。
///
/// Day 19 で引いた線がそのまま効いている——
/// 状態を進めるのは <see cref="SurvivorGame.Update"/> だけで、こちらは読むだけ。
/// だから描画の回数が変わってもゲームの進みは変わらないし、
/// 描画を丸ごと止めても(自己チェックのように)ゲームは動く。
///
/// <b>世界座標から画面座標への変換</b>もここが持つ。
/// ゲーム側はカメラの位置を持っているだけで、画面のことを知らない。
///
/// <code>
///   画面 = 世界 - カメラ + 画面の中心
/// </code>
///
/// 3D なら行列でやることを、2D では引き算1回で済ませている。
/// これが「カメラ」の正体で、Day 14 の <see cref="Camera"/> と
/// やっていることは同じ(あちらは行列を組む)。
/// </summary>
internal sealed class GameView
{
    private readonly SurvivorGame _game;

    /// <summary>絵の種類(<c>Program.SpriteNames</c> の添字)。</summary>
    private readonly int _circleSprite;
    private readonly int _ringSprite;
    private readonly int _starSprite;
    private readonly int _diamondSprite;
    private readonly int _boxSprite;

    public GameView(SurvivorGame game, int circle, int ring, int star, int diamond, int box)
    {
        _game = game;
        _circleSprite = circle;
        _ringSprite = ring;
        _starSprite = star;
        _diamondSprite = diamond;
        _boxSprite = box;
    }

    /// <summary>種類ごとの色。**形だけでは区別しにくい**ので色で分ける。</summary>
    private static readonly Vector4[] EnemyColors =
    [
        new(0.85f, 0.42f, 0.38f, 1.0f),   // 雑魚: くすんだ赤
        new(1.00f, 0.78f, 0.30f, 1.0f),   // 速い: 黄
        new(0.62f, 0.45f, 0.95f, 1.0f),   // 硬い: 紫
    ];

    /// <summary>
    /// 世界を描く。**奥から手前へ**積む(<see cref="SpriteBatch"/> の layer)。
    ///
    /// 積む順ではなく layer で決めているのは、Day 18 の並べ替えに任せているから。
    /// 呼ぶ順を気にせずに書けるのがバッチの値打ちのひとつ。
    /// </summary>
    public void DrawWorld(Action<int, Vector2, Vector2, float, Vector4, float> submit, Vector2 viewSize)
    {
        Vector2 origin = (viewSize * 0.5f) - _game.Camera;

        // --- 経験値のジェム(いちばん奥) ---
        for (int i = 0; i < _game.GemCount; i++)
        {
            ref Gem gem = ref _game.Gems[i];

            // 値打ちの大きいジェムほど大きく、明るく。
            float size = 9.0f + MathF.Min(gem.Value, 5) * 1.6f;

            submit(
                _diamondSprite,
                gem.Position + origin,
                new Vector2(size),
                _game.Elapsed * 2.0f,
                new Vector4(0.45f, 0.95f, 0.75f, 1.0f),
                0.25f);
        }

        // --- 敵 ---
        for (int i = 0; i < _game.EnemyCount; i++)
        {
            ref Enemy enemy = ref _game.Enemies[i];

            Vector4 color = EnemyColors[enemy.Kind];

            // **殴られた直後だけ白く光らせる**。
            // これが無いと、硬い敵に弾が当たっているのか外れているのか分からない。
            // 0.08 秒という短さでも、当たっているという手応えは十分に出る。
            float since = _game.Elapsed - enemy.HitAt;
            if (enemy.HitAt >= 0.0f && since < 0.08f)
            {
                float flash = 1.0f - (since / 0.08f);
                color = Vector4.Lerp(color, Vector4.One, flash);
            }

            // 硬い敵だけ環にして、形でも見分けが付くようにする。
            int sprite = enemy.Kind == GameBalance.KindBrute ? _ringSprite : _circleSprite;

            submit(
                sprite,
                enemy.Position + origin,
                new Vector2(enemy.Radius * 2.0f),
                0.0f,
                color,
                0.4f);
        }

        // --- 弾 ---
        for (int i = 0; i < _game.ProjectileCount; i++)
        {
            ref Projectile projectile = ref _game.Projectiles[i];

            submit(
                _circleSprite,
                projectile.Position + origin,
                new Vector2(GameBalance.ProjectileRadius * 2.0f),
                0.0f,
                new Vector4(1.00f, 0.95f, 0.55f, 1.0f),
                0.6f);
        }

        // --- プレイヤー(いちばん手前) ---
        //
        // **無敵時間の間は点滅させる**。数字を出さずに状態を伝える定番。
        // 8Hz くらいが「点滅している」と分かって、うるさくない境目。
        float alpha = _game.InvulnerableFor > 0.0f
            ? (MathF.Sin(_game.Elapsed * 50.0f) > 0.0f ? 1.0f : 0.35f)
            : 1.0f;

        submit(
            _starSprite,
            _game.PlayerPosition + origin,
            new Vector2(GameBalance.PlayerRadius * 2.4f),

            // 向いている方向へ回す。atan2 は「y を先に渡す」ことに注意。
            MathF.Atan2(_game.PlayerFacing.Y, _game.PlayerFacing.X),
            new Vector4(0.55f, 0.90f, 1.00f, alpha),
            0.7f);
    }

    /// <summary>
    /// HUD の図形(帯)を描く。**文字とはバッチが違う**ので関数も分ける。
    ///
    /// 分けているのは、グリフのアトラスが1チャンネルで
    /// スプライトと同じシェーダでは描けないため(Day 28 の要点4)。
    /// **同じ HUD が2つのバッチにまたがる**ことになるが、
    /// 積む順ではなく layer で重なりを決めているので破綻しない。
    /// </summary>
    public void DrawHudShapes(Action<int, Vector2, Vector2, float, Vector4, float> submit, Vector2 viewSize)
    {
        if (_game.Phase == GamePhase.Title)
        {
            return;
        }

        // --- 体力バー ---
        //
        // **バーは数字より速く読める**。残り 3 割を「30/100」と読ませるより、
        // 赤い帯が短いほうが一目で伝わる。数字は補助として横に出す。
        float ratio = MathF.Max(0.0f, _game.Health / GameBalance.PlayerMaxHealth);
        DrawBar(submit, new Vector2(14.0f, 34.0f), new Vector2(220.0f, 14.0f), ratio,
            new Vector4(0.90f, 0.28f, 0.28f, 0.95f));

        // --- 経験値バー ---
        float experienceRatio = _game.ExperienceToNext == 0
            ? 0.0f
            : (float)_game.Experience / _game.ExperienceToNext;

        DrawBar(submit, new Vector2(14.0f, 54.0f), new Vector2(220.0f, 8.0f), experienceRatio,
            new Vector4(0.40f, 0.85f, 0.95f, 0.95f));
    }

    /// <summary>
    /// HUD の文字を描く。**Day 28 の出番**。
    ///
    /// 数字が出せないゲームは成立しない——
    /// 残り HP も、レベルも、生き延びた時間も、
    /// 全部「文字か図形で伝える」しかない情報になっている。
    /// </summary>
    public void DrawHudText(TextRenderer text, SpriteBatch textBatch, Vector2 viewSize)
    {
        if (_game.Phase == GamePhase.Title)
        {
            DrawCentered(text, textBatch, viewSize,
                "HONYA SURVIVORS",
                "矢印キーで移動。攻撃は自動。\nEnter で開始 / Backspace でデモへ戻る",
                new Vector4(0.95f, 0.97f, 1.00f, 1.0f));
            return;
        }

        // --- 時間(いちばん大きく) ---
        //
        // **これがスコア**なので、いちばん目立つ場所に大きく出す。
        int minutes = (int)(_game.Elapsed / 60.0f);
        int seconds = (int)(_game.Elapsed % 60.0f);

        text.Draw(
            textBatch,
            $"{minutes}:{seconds:D2}",
            new Vector2(viewSize.X * 0.5f, 8.0f),
            32,
            new Vector4(0.96f, 0.97f, 1.00f, 1.0f),
            TextAlign.Center);

        text.Draw(
            textBatch,
            $"HP {(int)MathF.Max(0.0f, _game.Health)} / {(int)GameBalance.PlayerMaxHealth}",
            new Vector2(242.0f, 32.0f),
            16,
            new Vector4(0.95f, 0.80f, 0.80f, 1.0f));

        text.Draw(
            textBatch,
            $"Lv.{_game.Level}  {_game.Experience}/{_game.ExperienceToNext}",
            new Vector2(242.0f, 52.0f),
            14,
            new Vector4(0.75f, 0.92f, 0.98f, 1.0f));

        // --- 内訳。**エンジンの数字をそのまま出す** ---
        //
        // ゲームとしては要らない情報だが、この行があると
        // 「敵が 600 体いても候補は数千組」が遊びながら見える。
        // Day 26 で作ったものが効いていることを、遊びながら確かめられる場所。
        text.Draw(
            textBatch,
            $"敵 {_game.EnemyCount}  弾 {_game.ProjectileCount}  ジェム {_game.GemCount}  "
            + $"撃破 {_game.Kills}\n"
            + $"格子 {_game.GridColumns}x{_game.GridRows}  最大{_game.GridMaxPerCell}/マス  "
            + $"候補 {_game.PairCandidates:N0}",
            new Vector2(14.0f, viewSize.Y - 40.0f),
            14,
            new Vector4(0.62f, 0.70f, 0.80f, 1.0f));

        if (_game.Phase == GamePhase.GameOver)
        {
            DrawCentered(text, textBatch, viewSize,
                "GAME OVER",
                $"{minutes}分{seconds:D2}秒 生き延びた / 撃破 {_game.Kills}\nEnter でもう一度",
                new Vector4(1.00f, 0.72f, 0.60f, 1.0f));
        }
    }

    /// <summary>
    /// 帯を1本描く。**枠 → 中身**の順で2枚重ねる。
    ///
    /// 枠が無いと「空のバー」と「バーが無い」が見分けられない。
    /// 残りが 0 のときに何も出ないと、死んだのか表示が壊れたのか分からなくなる。
    /// </summary>
    private void DrawBar(
        Action<int, Vector2, Vector2, float, Vector4, float> submit,
        Vector2 topLeft,
        Vector2 size,
        float ratio,
        Vector4 color)
    {
        submit(
            _boxSprite,
            topLeft + (size * 0.5f),
            size,
            0.0f,
            new Vector4(0.10f, 0.12f, 0.16f, 0.85f),
            0.85f);

        float filled = MathF.Max(0.0f, MathF.Min(ratio, 1.0f)) * (size.X - 4.0f);
        if (filled <= 0.0f)
        {
            return;
        }

        submit(
            _boxSprite,

            // **左端を固定して伸ばす**。中心を固定すると両側から伸びてしまう。
            new Vector2(topLeft.X + 2.0f + (filled * 0.5f), topLeft.Y + (size.Y * 0.5f)),
            new Vector2(filled, size.Y - 4.0f),
            0.0f,
            color,
            0.9f);
    }

    private static void DrawCentered(
        TextRenderer? text,
        SpriteBatch? textBatch,
        Vector2 viewSize,
        string title,
        string body,
        Vector4 color)
    {
        if (text is null || textBatch is null)
        {
            return;
        }

        var center = new Vector2(viewSize.X * 0.5f, viewSize.Y * 0.42f);

        text.Draw(textBatch, title, center, 48, color, TextAlign.Center);

        text.Draw(
            textBatch,
            body,
            center + new Vector2(0.0f, 64.0f),
            18,
            new Vector4(0.80f, 0.85f, 0.92f, 1.0f),
            TextAlign.Center);
    }
}
