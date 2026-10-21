using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HonyaEngine;

/// <summary>
/// シーンをテキストに書き出し、テキストから組み立て直す。
///
/// **Phase 4 の締めがここ**。Day 22 で GameObject を、Day 23 で ECS を作ったが、
/// どちらも「コードでシーンを組む」ままだった。
/// それをファイルに追い出せて初めて、
///   - レベルを作るのにビルドが要らなくなる
///   - エディタが書ける(GUI が吐くのはただのファイル)
///   - バグの再現に「このシーンで落ちる」を添付できる
///   - セーブとロードが同じ仕組みで載る
/// が全部つながる。**エンジンとゲームの境目は、ここで初めてはっきりする**。
///
/// 形式は JSON にした。理由は2つ。
///   - .NET に <c>System.Text.Json</c> が最初から入っていて、依存が増えない
///   - **人が読めて、diff が取れる**。シーンはコードと同じくらい人が触るもので、
///     バイナリにすると「昨日と何が変わったか」が分からなくなる
/// 実行時に何千体も読むならバイナリのほうが速いが、それは Day 24 の話ではない。
///
/// 難しいのは形式ではなく、次の3つ。
///   1. **多態** … 要素ごとに型が違う(<see cref="ComponentRegistry"/>)
///   2. **参照** … 親子関係のような「別のオブジェクトを指す」情報
///   3. **何を保存するか** … 位置は保存する。速度は? クールダウンは?
/// </summary>
internal static class SceneSerializer
{
    /// <summary>
    /// 形式のバージョン。**最初から書いておく**。
    ///
    /// 後から足そうとすると「バージョンが書いていないファイル」を
    /// 特別扱いする分岐が永遠に残る。1バイトの節約より、
    /// 「読めないと分かること」のほうがはるかに価値がある。
    /// </summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions ComponentOptions = new()
    {
        WriteIndented = true,

        // 既定値のプロパティも書き出す。
        // 省略すると小さくなるが、**ファイルを読んだだけでは
        // 「既定値なのか書き忘れなのか」が分からなくなる**。
        // シーンファイルは人が読むものなので、冗長でも全部書く。
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,

        // **この2行が無いと、中身が丸ごと {} になる**。
        //
        // <c>System.Text.Json</c> は既定で**プロパティしか見ない**。フィールドは無視する。
        // ところがこのエンジンには、フィールドで持っている型が2種類ある。
        //   - <c>System.Numerics</c> のベクトル(X / Y / Z / W はフィールド)
        //   - ECS のコンポーネント(Transform2D などは全部フィールド。Day 23)
        // どちらも黙って空のオブジェクトになる。**例外も警告も出ない**。
        //
        // この作業中、同じ罠に2回はまった。しかも1回目は
        // 往復チェックのテキスト比較を**通ってしまった**
        // ——保存も再保存も同じように {} を書くので、当然一致する。
        // 「往復して一致する」は「正しく保存できている」を意味しない、
        // というのが今日いちばんの教訓(要点5)。
        //
        // <c>IncludeFields</c> だけでもデータは残るが、
        // ベクトルが { "X": 1, "Y": 2 } と3行に膨らむ。
        // 変換器を足して [1, 2] の1行にしている。
        IncludeFields = true,
        Converters =
        {
            new Vector2Converter(),
            new Vector3Converter(),
            new Vector4Converter(),
            new QuaternionConverter(),
        },
    };

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    // ===== 書き出し =====

    public static string Save(Scene scene, World? world, string name)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", CurrentVersion);
            writer.WriteString("name", name);
            WriteVector2(writer, "bounds", scene.Bounds);

            WriteGameObjects(writer, scene);

            if (world is not null && world.AliveCount > 0)
            {
                WriteEcs(writer, world);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static void SaveToFile(Scene scene, World? world, string path, string name)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Save(scene, world, name));
    }

    private static void WriteGameObjects(Utf8JsonWriter writer, Scene scene)
    {
        // **参照を番号に置き換える**。ここが要点2。
        //
        // 親子関係は C# のオブジェクト参照でつながっているが、
        // 参照はメモリ上のアドレスなので、そのままでは書き出せない。
        // 「配列の何番目か」に翻訳してから書く。
        // Day 21 のハンドル、Day 23 のエンティティ番号と発想は同じ——
        // **持ち歩ける形にするには、番号にするしかない**。
        IReadOnlyList<GameObject> gameObjects = scene.GameObjects;
        var indexOf = new Dictionary<GameObject, int>(gameObjects.Count);
        for (int i = 0; i < gameObjects.Count; i++)
        {
            indexOf[gameObjects[i]] = i;
        }

        writer.WriteStartArray("gameObjects");

        for (int i = 0; i < gameObjects.Count; i++)
        {
            GameObject gameObject = gameObjects[i];
            Transform transform = gameObject.Transform;

            writer.WriteStartObject();
            writer.WriteNumber("id", i);
            writer.WriteString("name", gameObject.Name);
            writer.WriteBoolean("active", gameObject.ActiveSelf);

            // 親がいなければ -1。null を書いてもよいが、
            // 数値で統一したほうが読む側の分岐が減る。
            writer.WriteNumber(
                "parent",
                transform.Parent is not null && indexOf.TryGetValue(transform.Parent.GameObject, out int parent)
                    ? parent
                    : -1);

            WriteVector3(writer, "position", transform.LocalPosition);
            WriteQuaternion(writer, "rotation", transform.LocalRotation);
            WriteVector3(writer, "scale", transform.LocalScale);

            writer.WriteStartArray("components");
            foreach (Component component in gameObject.Components)
            {
                writer.WriteStartObject();
                writer.WriteString("type", ComponentRegistry.NameOf(component.GetType()));
                writer.WritePropertyName("data");

                // **具体的な型を渡す**のがポイント。
                // 静的な型(Component)で渡すと基底クラスのぶんしか書かれない。
                JsonSerializer.Serialize(writer, component, component.GetType(), ComponentOptions);

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// ECS 側を書き出す。**こちらは驚くほど簡単**。
    ///
    /// コンポーネントはただの構造体で、しかも種類ごとに配列に詰まっている。
    /// だから「配列を4本、そのまま書く」で終わる。
    /// 多態も参照も出てこない。
    ///
    /// ただし**4本の並びが一致していること**が前提になる(Day 23 要点4)。
    /// 一致していないと、何番目がどのエンティティなのかを別に書く必要がある。
    /// 揃えておく利得が、性能だけでなく保存の単純さにも効いてくる。
    /// </summary>
    private static void WriteEcs(Utf8JsonWriter writer, World world)
    {
        ComponentStore<Transform2D> transforms = world.Store<Transform2D>();
        ComponentStore<Previous2D> previous = world.Store<Previous2D>();
        ComponentStore<Velocity2D> velocities = world.Store<Velocity2D>();
        ComponentStore<Sprite2D> sprites = world.Store<Sprite2D>();

        bool aligned =
            EcsSystems.AreAligned(transforms, previous)
            && EcsSystems.AreAligned(transforms, velocities)
            && EcsSystems.AreAligned(transforms, sprites);

        if (!aligned)
        {
            throw new InvalidOperationException(
                "ストアの並びが一致していないので、この単純な形式では書き出せません"
                + "(エンティティ番号を各コンポーネントに添えるか、並びを揃え直してください)");
        }

        writer.WriteStartObject("ecs");
        writer.WriteNumber("count", transforms.Count);
        WriteStructArray(writer, "transform2D", transforms.Values);
        WriteStructArray(writer, "previous2D", previous.Values);
        WriteStructArray(writer, "velocity2D", velocities.Values);
        WriteStructArray(writer, "sprite2D", sprites.Values);
        writer.WriteEndObject();
    }

    private static void WriteStructArray<T>(Utf8JsonWriter writer, string name, Span<T> values)
        where T : struct
    {
        writer.WritePropertyName(name);
        JsonSerializer.Serialize(writer, values.ToArray(), ComponentOptions);
    }

    // ===== 読み込み =====

    public static Scene Load(string json, World? world)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        int version = root.GetProperty("version").GetInt32();
        if (version > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"シーンの形式が新しすぎます(ファイル {version} / このエンジン {CurrentVersion})");
        }

        var scene = new Scene();
        if (root.TryGetProperty("bounds", out JsonElement bounds))
        {
            scene.Bounds = ReadVector2(bounds);
        }

        JsonElement gameObjects = root.GetProperty("gameObjects");
        var created = new List<GameObject>(gameObjects.GetArrayLength());

        // **3周する**。順番に意味がある。
        //
        //   1周目: オブジェクトを作る(親はまだ結ばない)
        //   2周目: 親を結ぶ
        //   3周目: コンポーネントを付ける
        //
        // 1と2を分けるのは、**子が親より先に書いてあっても読めるように**するため。
        // ファイルの並び順に依存する読み込みは、手で編集した瞬間に壊れる。
        //
        // 2と3を分けるのは、コンポーネントの <c>OnEnable</c> が
        // <see cref="GameObject.ActiveInHierarchy"/> を見るから。
        // 親を結ぶ前に付けると、親が無効なのに OnEnable が走ってしまう。

        // 1周目
        foreach (JsonElement element in gameObjects.EnumerateArray())
        {
            GameObject gameObject = scene.CreateGameObject(element.GetProperty("name").GetString() ?? "GameObject");
            Transform transform = gameObject.Transform;

            transform.LocalPosition = ReadVector3(element.GetProperty("position"));
            transform.LocalRotation = ReadQuaternion(element.GetProperty("rotation"));
            transform.LocalScale = ReadVector3(element.GetProperty("scale"));
            transform.Snapshot();

            if (!element.GetProperty("active").GetBoolean())
            {
                gameObject.SetActive(false);
            }

            created.Add(gameObject);
        }

        // 2周目
        int index = 0;
        foreach (JsonElement element in gameObjects.EnumerateArray())
        {
            int parent = element.GetProperty("parent").GetInt32();
            if (parent >= 0 && parent < created.Count)
            {
                created[index].Transform.SetParent(created[parent].Transform);
            }

            index++;
        }

        // 3周目
        index = 0;
        foreach (JsonElement element in gameObjects.EnumerateArray())
        {
            foreach (JsonElement componentElement in element.GetProperty("components").EnumerateArray())
            {
                string typeName = componentElement.GetProperty("type").GetString() ?? string.Empty;
                Type? type = ComponentRegistry.TypeOf(typeName);

                if (type is null)
                {
                    // **知らない型は読み飛ばす**。落とさない。
                    //
                    // 新しいエンジンで作ったシーンを古いエンジンで開く、
                    // という状況は普通に起きる(ブランチを行き来するだけで起きる)。
                    // そこで例外を投げると、1つ知らない部品があるだけで
                    // シーン全体が開けなくなる。
                    Console.WriteLine($"[scene] 知らないコンポーネント \"{typeName}\" を読み飛ばしました");
                    continue;
                }

                var component = (Component)JsonSerializer.Deserialize(
                    componentElement.GetProperty("data").GetRawText(), type, ComponentOptions)!;

                created[index].AttachComponent(component);
            }

            index++;
        }

        if (world is not null)
        {
            LoadEcs(root, world);
        }

        return scene;
    }

    public static Scene LoadFromFile(string path, World? world) => Load(File.ReadAllText(path), world);

    private static void LoadEcs(JsonElement root, World world)
    {
        world.Clear();

        if (!root.TryGetProperty("ecs", out JsonElement ecs))
        {
            return;
        }

        Transform2D[] transforms = ReadStructArray<Transform2D>(ecs, "transform2D");
        Previous2D[] previous = ReadStructArray<Previous2D>(ecs, "previous2D");
        Velocity2D[] velocities = ReadStructArray<Velocity2D>(ecs, "velocity2D");
        Sprite2D[] sprites = ReadStructArray<Sprite2D>(ecs, "sprite2D");

        // **書いたときと同じ順で付け直す**。
        // そうすれば並びがまた一致し、速い経路(Day 23 要点4)に戻れる。
        for (int i = 0; i < transforms.Length; i++)
        {
            Entity entity = world.CreateEntity();
            world.Add(entity, transforms[i]);
            world.Add(entity, previous[i]);
            world.Add(entity, velocities[i]);
            world.Add(entity, sprites[i]);
        }
    }

    private static T[] ReadStructArray<T>(JsonElement parent, string name)
        where T : struct =>
        parent.TryGetProperty(name, out JsonElement element)
            ? JsonSerializer.Deserialize<T[]>(element.GetRawText(), ComponentOptions) ?? []
            : [];

    // ===== ベクトルの読み書き =====
    //
    // Vector2 / Vector3 / Quaternion をそのまま JsonSerializer に渡すと
    // { "X": 1, "Y": 2, "Z": 0 } になる。間違いではないが、
    // **座標が3行に膨らんでシーンファイルがひどく読みにくくなる**。
    // 配列で [1, 2, 0] と書けば1行に収まる。
    // 「人が読める」を目的にした以上、ここは手で書く価値がある。

    private static void WriteVector2(Utf8JsonWriter writer, string name, Vector2 value)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteEndArray();
    }

    private static void WriteVector3(Utf8JsonWriter writer, string name, Vector3 value)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteEndArray();
    }

    private static void WriteQuaternion(Utf8JsonWriter writer, string name, Quaternion value)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteNumberValue(value.W);
        writer.WriteEndArray();
    }

    private static Vector2 ReadVector2(JsonElement element) =>
        new(element[0].GetSingle(), element[1].GetSingle());

    private static Vector3 ReadVector3(JsonElement element) =>
        new(element[0].GetSingle(), element[1].GetSingle(), element[2].GetSingle());

    private static Quaternion ReadQuaternion(JsonElement element) =>
        new(element[0].GetSingle(), element[1].GetSingle(), element[2].GetSingle(), element[3].GetSingle());

    // ===== ベクトルの変換器 =====
    //
    // 既定の書き方 { "X": 1, "Y": 2, "Z": 0, "W": 1 } を [1, 2, 0, 1] に変える。
    // **フィールドを拾わせる**のが第一の目的で、読みやすさは副産物。
    // 4つとも中身は同じ形なので、1つ読めば残りは流し読みでよい。

    private sealed class Vector2Converter : JsonConverter<Vector2>
    {
        public override Vector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // 呼ばれた時点で reader は「配列の始まり」を指している。
            // 数値を必要な数だけ読み、最後に配列の終わりまで進める。
            reader.Read();
            float x = reader.GetSingle();
            reader.Read();
            float y = reader.GetSingle();
            reader.Read();
            return new Vector2(x, y);
        }

        public override void Write(Utf8JsonWriter writer, Vector2 value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Y);
            writer.WriteEndArray();
        }
    }

    private sealed class Vector3Converter : JsonConverter<Vector3>
    {
        public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            reader.Read();
            float x = reader.GetSingle();
            reader.Read();
            float y = reader.GetSingle();
            reader.Read();
            float z = reader.GetSingle();
            reader.Read();
            return new Vector3(x, y, z);
        }

        public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Y);
            writer.WriteNumberValue(value.Z);
            writer.WriteEndArray();
        }
    }

    private sealed class Vector4Converter : JsonConverter<Vector4>
    {
        public override Vector4 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            reader.Read();
            float x = reader.GetSingle();
            reader.Read();
            float y = reader.GetSingle();
            reader.Read();
            float z = reader.GetSingle();
            reader.Read();
            float w = reader.GetSingle();
            reader.Read();
            return new Vector4(x, y, z, w);
        }

        public override void Write(Utf8JsonWriter writer, Vector4 value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Y);
            writer.WriteNumberValue(value.Z);
            writer.WriteNumberValue(value.W);
            writer.WriteEndArray();
        }
    }

    private sealed class QuaternionConverter : JsonConverter<Quaternion>
    {
        public override Quaternion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            reader.Read();
            float x = reader.GetSingle();
            reader.Read();
            float y = reader.GetSingle();
            reader.Read();
            float z = reader.GetSingle();
            reader.Read();
            float w = reader.GetSingle();
            reader.Read();
            return new Quaternion(x, y, z, w);
        }

        public override void Write(Utf8JsonWriter writer, Quaternion value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Y);
            writer.WriteNumberValue(value.Z);
            writer.WriteNumberValue(value.W);
            writer.WriteEndArray();
        }
    }
}
