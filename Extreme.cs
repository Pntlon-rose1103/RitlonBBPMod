using HarmonyLib;
using MTM101BaldAPI;
using MTM101BaldAPI.Registers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;

// =====================================================================
// Extreme モード
//   Hide-and-Seek を拡張した新モード。モード選択画面（Endless mode / Challenges /
//   Field Trips / Tutorial が並ぶ画面）に「Extreme」ボタンを追加する。
//
//   仕様:
//     ・F1 から F9 まで（F9 クリアで完全クリア＝バニラのエンディングへ）
//     ・各フロアのノート数 = フロア階数 + 4（F1=5 冊 … F9=13 冊）
//     ・出口（エレベーター）数は全フロア 4 つ
//     ・NPC は常に全員出現（バニラ NPC ＋ Mod NPC）。Mogigi は複数体
//     ・フロア間のジョニーストア（Pitstop）はバニラ同様、F1〜F8 クリア後に必ず生成
//     ・残機の概念なし。やられても無限にリトライ（ゲームオーバーにならない）
//     ・Play Style / チャレンジ設定は Hide-and-Seek 画面の設定をそのまま引き継ぐ
//     ・シードは選べず、常にランダム
//     ・Extreme 中はセーブしない（通常の Hide-and-Seek のセーブデータには触れない）
//
//   実装方針:
//     バニラの F3 の SceneObject / LevelObject を複製して F1〜F9 を作る
//     （複製は Extreme ボタンを初めて押した時に1回だけ行う。この時点で他の Mod の
//      生成補正（GeneratorManagement）は適用済みなので、それらも引き継がれる）。
//     メニューからの開始手順は Endless mode と同じ
//     （GameLoader.Initialize → LoadLevel）で、ApplyGameSettingsForHideSeek() で
//     Play Style を引き継ぐ。
// =====================================================================
public static class ExtremeMode
{
    // ---- 調整用の定数 ----
    public const int FLOOR_COUNT = 9;           // F9 クリアで完全クリア
    public const int EXIT_COUNT = 4;            // 全フロアの出口（エレベーター）数
    public const int NOTES_OFFSET = 4;          // ノート数 = フロア階数 + NOTES_OFFSET
    public const float TIME_LIMIT_SECONDS = 99f * 60f; // 制限時間 99 分（1Min Limit 有効時はバニラ側で 60 秒に上書きされる）
    public const int MOGIGI_COUNT = 3;          // Mogigi を同時に何体出すか
    public const float SIZE_GROWTH_PER_FLOOR = 0.1f; // F4 以降、1 フロアごとにマップ規模を何割広げるか
                                                      // （ノート数が増えるぶん部屋が必要なため）

    // 新ボタンの配置（Field Trips / Endless mode / Tutorial の位置が取れなかった時の代替）
    static readonly Vector2 FALLBACK_BUTTON_OFFSET = new Vector2(-300f, 0f);

    // Extreme の周回中かどうか（開始時に true、通常のゲーム開始やメニュー復帰で false）
    public static bool Active = false;

    static SceneObject[] extremeScenes = null;
    static bool starting = false;

    static readonly BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // ==========================================================
    //  Harmony パッチの登録（Plugin.Awake から呼ぶ）
    // ==========================================================
    public static void Install(Harmony harmony)
    {
        Patch(harmony, typeof(MainMenu), "Start", null, nameof(MainMenuStartPostfix));
        Patch(harmony, typeof(GameLoader), "Initialize", null, nameof(StartOfOtherModePostfix));
        Patch(harmony, typeof(GameLoader), "LoadSavedGame", null, nameof(StartOfOtherModePostfix));
        Patch(harmony, typeof(CoreGameManager), "ReturnToMenu", null, nameof(ReturnToMenuPostfix));
        Patch(harmony, typeof(CoreGameManager), "EndSequence", nameof(EndSequencePrefix), null);
        Patch(harmony, typeof(ElevatorScreen), "UpdateLives", nameof(UpdateLivesPrefix), null);
        Patch(harmony, typeof(PlayerFileManager), "Find", nameof(FindPrefix), null);

        // 死んだ時（リトライ）・フロアクリア時（次のフロアへ）にシードを引き直す

        // Extreme 中は Hide-and-Seek のセーブデータに一切触れない
        Patch(harmony, typeof(CoreGameManager), "Save", nameof(SkipSavePrefix), null);
        Patch(harmony, typeof(MainGameManager), "BeginPlay", nameof(KeepSaveFlagPrefix), nameof(KeepSaveFlagPostfix));
        Patch(harmony, typeof(MainGameManager), "BeginPlay", null, nameof(SpawnBusPassPostfix));
        Patch(harmony, typeof(Baldi), "get_Speed", null, nameof(BaldiSpeedPostfix));
        Patch(harmony, typeof(PlaceholderWinManager), "BeginPlay", nameof(KeepSaveFlagPrefix), nameof(KeepSaveFlagPostfix));
    }

    // ---- Extreme 専用: バルディの速度をフロアごとに倍率変動 ---- //
    //  F1-2: 0.8倍 / F3-4: 0.9倍 / F5-6: 1倍 / F7-8: 1.05倍 / F9: 1.1倍
    static readonly FieldInfo fiSceneObject = AccessTools.Field(typeof(CoreGameManager), "sceneObject");

    static float BaldiSpeedMultiplier(int floor)
    {
        if (floor <= 2) return 0.8f;
        if (floor <= 4) return 0.9f;
        if (floor <= 6) return 1.0f;
        if (floor <= 8) return 1.05f;
        return 1.1f;
    }

    public static void BaldiSpeedPostfix(ref float __result)
    {
        if (!Active) return;
        try
        {
            SceneObject so = fiSceneObject?.GetValue(Singleton<CoreGameManager>.Instance) as SceneObject;
            if (so == null || so.name == null || !so.name.StartsWith("Extreme")) return; // ジョニーストア等は対象外
            __result *= BaldiSpeedMultiplier(so.levelNo + 1);
        }
        catch { }
    }

    // 各フロアの廊下のどこかに BusPass を必ず1つスポーン(Extremeのみ)
    public static void SpawnBusPassPostfix()
    {
        if (!Active) return;
        try
        {
            EnvironmentController ec = Singleton<BaseGameManager>.Instance.Ec;
            ItemMetaData meta = MTM101BaldAPI.Registers.ItemMetaStorage.Instance.FindByEnum(Items.BusPass);
            Pickup template = ITM_PartyHat.GetPickupTemplate();
            if (ec == null || meta == null || template == null)
            {
                MyPlugin.Log.LogWarning("[Extreme] BusPass spawn skipped (ec/meta/template missing).");
                return;
            }
            var halls = new System.Collections.Generic.List<Cell>();
            for (int x = 0; x < ec.levelSize.x; x++)
                for (int z = 0; z < ec.levelSize.z; z++)
                {
                    Cell c = ec.cells[x, z];
                    if (c != null && !c.Null && c.room != null && c.room.type == RoomType.Hall) halls.Add(c);
                }
            if (halls.Count == 0) return;
            Cell cell = halls[Random.Range(0, halls.Count)];
            Vector3 pos = cell.FloorWorldPosition; pos.y = 5f;

            Pickup p = Object.Instantiate(template, pos, Quaternion.identity);
            p.item = meta.value;
            p.free = true;
            p.gameObject.SetActive(true);
            if (p.itemSprite != null) p.itemSprite.sprite = meta.value.itemSpriteLarge;
            p.gameObject.name = "Item_BusPass";
            MyPlugin.Log.LogInfo($"[Extreme] BusPass spawned at {pos}");
        }
        catch (System.Exception ex) { MyPlugin.Log.LogError("[Extreme] BusPass spawn failed: " + ex); }
    }

    static void Patch(Harmony harmony, System.Type type, string method, string prefix, string postfix)
    {
        try
        {
            MethodInfo target = AccessTools.Method(type, method);
            if (target == null)
            {
                MyPlugin.Log.LogWarning($"[Extreme] Patch target not found: {type.Name}.{method}");
                return;
            }
            harmony.Patch(target,
                prefix: prefix != null ? new HarmonyMethod(typeof(ExtremeMode), prefix) : null,
                postfix: postfix != null ? new HarmonyMethod(typeof(ExtremeMode), postfix) : null);
            MyPlugin.Log.LogInfo($"[Extreme] Patched {type.Name}.{method}");
        }
        catch (System.Exception ex)
        {
            MyPlugin.Log.LogError($"[Extreme] Failed to patch {type.Name}.{method}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ==========================================================
    //  パッチ本体
    // ==========================================================

    // メインメニュー表示時に Extreme ボタンを追加
    public static void MainMenuStartPostfix()
    {
        try { AddMenuButton(); }
        catch (System.Exception ex) { MyPlugin.Log.LogError($"[Extreme] AddMenuButton failed: {ex}"); }
    }

    // 通常のゲーム開始（Hide-and-Seek / Endless / セーブからの再開）では Extreme を解除する。
    // ※ Extreme 自身の開始は Initialize の「後」に Active = true にする。
    public static void StartOfOtherModePostfix()
    {
        Active = false;
    }

    public static void ReturnToMenuPostfix()
    {
        Active = false;
        starting = false;
    }

    // やられた時の処理（EndSequence）の直前に残機を 1 に戻す。
    // バニラは「lives < 1 かつ extraLives < 1 ならメニューへ戻る、そうでなければ
    // lives を 1 減らしてリトライ」なので、毎回 1 に戻しておけば永遠にリトライできる。
    public static void EndSequencePrefix(CoreGameManager __instance)
    {
        if (!Active) return;
        AccessTools.Field(typeof(CoreGameManager), "lives")?.SetValue(__instance, 1);
        AccessTools.Field(typeof(CoreGameManager), "extraLives")?.SetValue(__instance, 0);
    }

    // ---- シードの引き直し ----
    // 死んだ時（MainGameManager.RestartLevel）とフロアクリア時（LoadNextLevel）に、
    // 次のフロアを作る前に CoreGameManager のシードをランダムに引き直す。
    // （途中でやめた場合は、次に Extreme を始める時の GameLoader.Initialize で毎回ランダムになる）

    // ---- セーブの保護 ----
    // Pitstop の Save & Quit ドアや Pitstop を出る時の自動セーブ（CoreGameManager.Save）は、
    // Extreme の進行状況（F4 以降など）を Hide-and-Seek のセーブ枠に書き込んでしまうため、Extreme 中は丸ごとスキップする。
    // （Save & Quit のドアはセーブされずにメニューへ戻るだけになる）
    public static bool SkipSavePrefix()
    {
        return !Active;
    }

    // MainGameManager.BeginPlay / PlaceholderWinManager.BeginPlay は、フロア開始・クリアの度に
    // savedGameData.saveAvailable = false にして保存する。Extreme で遊んだだけで既存の
    // Hide-and-Seek の「Continue」が消えないよう、実行前の値を覚えて実行後に戻す。
    public static void KeepSaveFlagPrefix(ref bool __state)
    {
        __state = false;
        if (!Active) return;
        var data = Singleton<PlayerFileManager>.Instance.savedGameData;
        if (data != null) __state = data.saveAvailable;
    }

    public static void KeepSaveFlagPostfix(bool __state)
    {
        if (!Active) return;
        var data = Singleton<PlayerFileManager>.Instance.savedGameData;
        if (data == null) return;
        data.saveAvailable = __state;
        Singleton<PlayerFileManager>.Instance.Save();
    }

    // エレベーター画面の残機表示を隠す（残機の概念が無いため）
    public static bool UpdateLivesPrefix(ElevatorScreen __instance)
    {
        if (!Active) return true;
        Component img = AccessTools.Field(typeof(ElevatorScreen), "livesImage")?.GetValue(__instance) as Component;
        if (img != null) img.gameObject.SetActive(false);
        return false; // 元の処理（残機アイコンの更新）はスキップ
    }

    // バニラは「クリア済みフロアの記録」を bool 配列に levelNo で書き込むが、
    // 配列は 3 フロア分程度しか無いため F4 以降で範囲外アクセスになる。範囲外なら何もしない。
    public static bool FindPrefix(object[] __args)
    {
        if (__args == null || __args.Length < 2) return true;
        System.Array arr = __args[0] as System.Array;
        if (arr == null) return true;
        int index;
        try { index = System.Convert.ToInt32(__args[1]); }
        catch { return true; }
        return index >= 0 && index < arr.Length;
    }

    // ==========================================================
    //  メニューボタンの追加
    // ==========================================================
    static string KeyOf(Component c)
    {
        TextLocalizer tl = c.GetComponentInChildren<TextLocalizer>(true);
        return tl != null ? tl.key : null;
    }

    static bool Matches(StandardMenuButton b, string word)
    {
        string name = b.gameObject.name;
        string key = KeyOf(b);
        return (name != null && name.IndexOf(word, System.StringComparison.OrdinalIgnoreCase) >= 0)
            || (key != null && key.IndexOf(word, System.StringComparison.OrdinalIgnoreCase) >= 0);
    }

    static void AddMenuButton()
    {
        StandardMenuButton[] all = Object.FindObjectsOfType<StandardMenuButton>(true);

        // 既に追加済み（メニューへ戻って再度 Start が呼ばれた場合など）なら何もしない
        if (all.Any(b => b.gameObject.name == "ExtremeButton")) return;

        // 「Tutorial」ボタン（Endless mode と同じ親の下にあるものを優先）を複製元にする
        StandardMenuButton tutorial = all.FirstOrDefault(b =>
                Matches(b, "tutorial") && b.transform.parent != null &&
                all.Any(o => o != b && o.transform.parent == b.transform.parent && Matches(o, "endless")))
            ?? all.FirstOrDefault(b => Matches(b, "tutorial"));

        if (tutorial == null)
        {
            MyPlugin.Log.LogWarning("[Extreme] Tutorial button not found — Extreme button was not added. Buttons in scene:");
            foreach (StandardMenuButton b in all)
                MyPlugin.Log.LogInfo($"  button: name='{b.gameObject.name}' parent='{b.transform.parent?.name}' key='{KeyOf(b)}'");
            return;
        }

        StandardMenuButton endless = all.FirstOrDefault(b => b != tutorial && b.transform.parent == tutorial.transform.parent && Matches(b, "endless"));
        StandardMenuButton field = all.FirstOrDefault(b => b != tutorial && b.transform.parent == tutorial.transform.parent && Matches(b, "field"));

        GameObject go = Object.Instantiate(tutorial.gameObject, tutorial.transform.parent);
        go.name = "ExtremeButton";
        // 他のボタンと同じ描画順にする（末尾のままだとカーソルより手前に描かれてしまう）
        go.transform.SetSiblingIndex(tutorial.transform.GetSiblingIndex());

        StandardMenuButton btn = go.GetComponent<StandardMenuButton>();

        // 説明欄（カーソルを合わせた時に文章が出る TextLocalizer）は、複製元 Tutorial のハイライト時イベントの対象から取得する
        TextLocalizer descBox = null;
        for (int i = 0; i < tutorial.OnHighlight.GetPersistentEventCount(); i++)
        {
            descBox = tutorial.OnHighlight.GetPersistentTarget(i) as TextLocalizer;
            if (descBox != null) break;
        }
        if (descBox == null)
            MyPlugin.Log.LogWarning("[Extreme] Description text box not found from Tutorial's OnHighlight — description will not change.");

        // 複製元のイベント（Tutorial 開始や説明文の表示など）を全て捨て、Extreme 用に差し替える
        foreach (FieldInfo f in typeof(StandardMenuButton).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.FieldType == typeof(UnityEvent))
                f.SetValue(btn, new UnityEvent());
        }
        btn.OnPress.AddListener(StartRun);
        btn.eventOnHigh = true;   // ハイライト時に説明文を表示する
        if (descBox != null)
            btn.OnHighlight.AddListener(() => descBox.GetLocalizedText("Men_Extreme_Desc"));

        // ボタンの文字（TextLocalizer が複製元のキーで上書きしないよう、キーも差し替える）
        foreach (TextLocalizer tl in go.GetComponentsInChildren<TextLocalizer>(true))
            tl.key = "Men_Extreme";
        // ※ btn.text は TMP_Text 型（Unity.TextMeshPro への参照が必要）なので、型名を書かずリフレクションで設定する
        object tmp = typeof(StandardMenuButton).GetField("text", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(btn);
        if (tmp != null)
            tmp.GetType().GetProperty("text")?.SetValue(tmp, "EX", null);

        // 配置：Field Trips と Tutorial の間（同じ行）（取れなければ Tutorial からの固定オフセット）
        RectTransform rt = go.GetComponent<RectTransform>();
        RectTransform tutRt = tutorial.GetComponent<RectTransform>();
        if (rt != null && tutRt != null)
        {
            RectTransform fieldRt = field != null ? field.GetComponent<RectTransform>() : null;
            if (fieldRt != null)
            {
                // Field Trips と Tutorial の間（同じ行・中間）
                rt.anchoredPosition = new Vector2(
                    (fieldRt.anchoredPosition.x + tutRt.anchoredPosition.x) * 0.5f,
                    (fieldRt.anchoredPosition.y + tutRt.anchoredPosition.y) * 0.5f);
            }
            else
            {
                rt.anchoredPosition = tutRt.anchoredPosition + FALLBACK_BUTTON_OFFSET;
            }
        }

        go.SetActive(tutorial.gameObject.activeSelf);
        MyPlugin.Log.LogInfo($"[Extreme] Extreme button added next to '{tutorial.gameObject.name}'.");
    }

    // ==========================================================
    //  Extreme 開始（Extreme ボタンを押した時）
    //    Endless mode の開始手順（GameLoader.Initialize → LoadLevel）と同じ流れ。
    // ==========================================================
    public static void StartRun()
    {
        if (starting) return;

        MyPlugin.Log.LogInfo("[Extreme] StartRun called.");

        // 非アクティブなオブジェクト上の GameLoader も探す（シーン上のもののみ）
        GameLoader loader = Object.FindObjectOfType<GameLoader>();
        if (loader == null)
            loader = Resources.FindObjectsOfTypeAll<GameLoader>().FirstOrDefault(g => g != null && g.gameObject.scene.IsValid());
        if (loader == null)
        {
            MyPlugin.Log.LogError("[Extreme] GameLoader not found.");
            return;
        }
        MyPlugin.Log.LogInfo($"[Extreme] GameLoader found on '{loader.gameObject.name}' (active={loader.gameObject.activeInHierarchy}).");

        SceneObject[] scenes = BuildScenes();
        if (scenes == null || scenes.Length == 0 || scenes[0] == null)
        {
            MyPlugin.Log.LogError("[Extreme] Could not build Extreme floors — aborting.");
            return;
        }

        starting = true;
        try
        {
            loader.useSeed = false;                      // シードは常にランダム
            loader.Initialize(2);                        // CoreGameManager の生成・乱数シード設定
            loader.ApplyGameSettingsForHideSeek();       // Play Style / チャレンジ設定を引き継ぐ
            Active = true;                               // ※ Initialize の後に立てる（Initialize が解除するため）

            // バニラの Hide-and-Seek と同じく、メニューのシーン内にある ElevatorScreen（非アクティブ）を探して有効化する。
            //   1. GameLoader に ElevatorScreen を登録（エレベーター演出が終わったら Game シーンが読み込まれる）
            //   2. LoadLevel で最初のフロアを設定（登録済みなので、この時点では Game を読み込まない）
            //   3. ElevatorScreen を有効化して演出を開始
            // ※ 非アクティブなオブジェクトは FindObjectOfType では見つからないため FindObjectsOfTypeAll を使う。
            ElevatorScreen screen = FindElevatorScreen();
            if (screen != null)
            {
                loader.AssignElevatorScreen(screen);
                loader.LoadLevel(scenes[0]);
                screen.gameObject.SetActive(true);
                MyPlugin.Log.LogInfo($"[Extreme] Extreme run started via ElevatorScreen '{screen.gameObject.name}'.");
            }
            else
            {
                // 見つからない場合は Endless mode と同様にエレベーター無しで直接開始する（この場合ゲーム開始の操作が出ない可能性あり）
                MyPlugin.Log.LogError("[Extreme] ElevatorScreen not found — starting without elevator.");
                loader.LoadLevel(scenes[0]);
            }
        }
        catch (System.Exception ex)
        {
            starting = false;
            Active = false;
            MyPlugin.Log.LogError($"[Extreme] Failed to start Extreme run: {ex}");
        }
    }

    // メニューのシーン内の ElevatorScreen を探す（非アクティブでも可。プレハブ資産は除く）
    static ElevatorScreen FindElevatorScreen()
    {
        ElevatorScreen[] all = Resources.FindObjectsOfTypeAll<ElevatorScreen>();
        foreach (ElevatorScreen e in all)
            MyPlugin.Log.LogInfo($"[Extreme] ElevatorScreen candidate: '{e.gameObject.name}' scene='{e.gameObject.scene.name}' valid={e.gameObject.scene.IsValid()} active={e.gameObject.activeSelf}");
        return all.FirstOrDefault(e => e != null && e.gameObject.scene.IsValid());
    }

    // ==========================================================
    //  F1〜F9 の生成
    // ==========================================================
    static SceneObject FindScene(SceneObject[] all, string title)
    {
        return all.FirstOrDefault(s => s != null && s.levelTitle == title && !s.name.StartsWith("Extreme"));
    }

    static IEnumerable<LevelObject> LevelObjectsOf(SceneObject so)
    {
        if (so.levelObject != null) yield return so.levelObject;
        if (so.randomizedLevelObject != null)
        {
            foreach (var w in so.randomizedLevelObject)
                if (w != null && w.selection != null) yield return w.selection;
        }
    }

    static SceneObject[] BuildScenes()
    {
        if (extremeScenes != null && extremeScenes.All(s => s != null))
            return extremeScenes;

        SceneObject[] all = Resources.FindObjectsOfTypeAll<SceneObject>();
        SceneObject f1 = FindScene(all, "F1");
        SceneObject f2 = FindScene(all, "F2");
        SceneObject f3 = FindScene(all, "F3");

        SceneObject template = f3 ?? f2 ?? f1;
        if (template == null)
        {
            MyPlugin.Log.LogError("[Extreme] No vanilla floor (F1/F2/F3) found. SceneObjects in memory:");
            foreach (SceneObject s in all)
                if (s != null) MyPlugin.Log.LogInfo($"  scene: name='{s.name}' title='{s.levelTitle}' levelNo={s.levelNo}");
            return null;
        }

        LevelObject baseLevel = LevelObjectsOf(template).FirstOrDefault();
        if (baseLevel == null)
        {
            MyPlugin.Log.LogError($"[Extreme] Template floor '{template.name}' has no LevelObject.");
            return null;
        }

        NPC[] npcs = CollectAllNpcs(new[] { f1, f2, f3 });
        MyPlugin.Log.LogInfo($"[Extreme] Template={template.name}, NPCs for every floor: {npcs.Length} ({string.Join(", ", npcs.Select(n => n.Character.ToString()))})");

        var scenes = new SceneObject[FLOOR_COUNT];
        for (int i = 0; i < FLOOR_COUNT; i++)
        {
            int floor = i + 1;

            SceneObject so = Object.Instantiate(template);
            so.name = "Extreme_F" + floor;
            so.levelTitle = "F" + floor;
            so.levelNo = i;
            so.hideFlags |= HideFlags.DontUnloadUnusedAsset;

            // 前のフロアの NPC を引き継ぐ仕組みは使わず、各フロアの LevelObject に全員を直接入れる
            EmptyCollection(so, "previousLevels");
            EmptyCollection(so, "forcedNpcs");
            EmptyCollection(so, "potentialNPCs");
            EmptyCollection(so, "randomizedLevelObject"); // 空にすると levelObject の方が使われる
            so.additionalNPCs = 0;

            so.levelObject = MakeLevelObject(baseLevel, floor, npcs);
            scenes[i] = so;
        }

        // フロアをつなぐ。F9 の次はバニラの最終フロアの次（＝エンディング）
        for (int i = 0; i < FLOOR_COUNT - 1; i++)
            scenes[i].nextLevel = scenes[i + 1];
        scenes[FLOOR_COUNT - 1].nextLevel = template.nextLevel;

        extremeScenes = scenes;
        MyPlugin.Log.LogInfo($"[Extreme] Built {FLOOR_COUNT} floors. F{FLOOR_COUNT}.nextLevel={(template.nextLevel != null ? template.nextLevel.name : "null")}");
        return extremeScenes;
    }

    static LevelObject MakeLevelObject(LevelObject baseLevel, int floor, NPC[] npcs)
    {
        // API の CustomLevelObject は MakeClone() で複製する（Mod が付けた拡張データも引き継ぐため）
        CustomLevelObject customBase = baseLevel as CustomLevelObject;
        LevelObject lo = customBase != null ? customBase.MakeClone() : Object.Instantiate(baseLevel);
        lo.name = "ExtremeLevel_F" + floor;
        lo.hideFlags |= HideFlags.DontUnloadUnusedAsset;

        // 出口の数・最終フロア判定
        lo.exitCount = EXIT_COUNT;
        lo.timeLimit = TIME_LIMIT_SECONDS;        // 通常は 99 分。1Min Limit はバニラが 60 秒を優先する
        lo.finalLevel = (floor == FLOOR_COUNT);   // F1〜F8 は false ＝ クリア後にジョニーストアが生成される

        // ノート数 = フロア階数 + 4。
        // ノートは「activity を持つ部屋（教室）」1 部屋につき 1 冊なので、
        // その部屋グループの部屋数を固定する。
        int notes = floor + NOTES_OFFSET;
        bool notesSet = false;
        if (lo.roomGroup != null)
        {
            foreach (RoomGroup rg in lo.roomGroup)
            {
                if (rg == null || !HasActivityRooms(rg)) continue;
                rg.minRooms = notes;
                rg.maxRooms = notes;
                notesSet = true;
                break;
            }
        }
        if (!notesSet)
            MyPlugin.Log.LogWarning($"[Extreme] F{floor}: classroom RoomGroup not found — notebook count not changed.");

        // 部屋が増えるぶん、F4 以降はマップ規模も広げる
        float grow = 1f + SIZE_GROWTH_PER_FLOOR * Mathf.Max(0, floor - 3);
        if (grow > 1f)
        {
            ScaleField(lo, "minSize", grow);
            ScaleField(lo, "maxSize", grow);
            ScaleField(lo, "minPlots", grow);
            ScaleField(lo, "maxPlots", grow);
        }

        // NPC は常に全員。Mogigi は複数体。
        var list = new List<NPC>(npcs);
        Character mogigiChar = EnumExtensions.GetFromExtendedName<Character>("Mogigi");
        NPC mogigi = npcs.FirstOrDefault(n => n.Character == mogigiChar);
        if (mogigi != null)
        {
            for (int k = 1; k < MOGIGI_COUNT; k++) list.Add(mogigi);
        }
        lo.forcedNpcs = list.ToArray();

        return lo;
    }

    static bool HasActivityRooms(RoomGroup rg)
    {
        if (rg.potentialRooms == null) return false;
        foreach (var w in rg.potentialRooms)
        {
            if (w != null && w.selection != null && w.selection.hasActivity)
                return true;
        }
        return false;
    }

    // バニラ F1〜F3 に登場しうる全 NPC ＋ Mod で登録された全 NPC（Character ごとに 1 つ）
    static NPC[] CollectAllNpcs(SceneObject[] vanillaScenes)
    {
        var result = new List<NPC>();
        void Add(NPC n)
        {
            if (n == null) return;
            if (n.Character == Character.Baldi || n.Character == Character.Null) return;
            if (result.Any(x => x.Character == n.Character)) return;
            result.Add(n);
        }

        foreach (SceneObject so in vanillaScenes)
        {
            if (so == null) continue;

            if (so.forcedNpcs != null)
                foreach (NPC n in so.forcedNpcs) Add(n);
            if (so.potentialNPCs != null)
                foreach (var w in so.potentialNPCs) if (w != null) Add(w.selection);

            foreach (LevelObject lo in LevelObjectsOf(so))
                if (lo.forcedNpcs != null)
                    foreach (NPC n in lo.forcedNpcs) Add(n);
        }

        // Mod で追加された NPC（拡張 enum）は全て追加する
        foreach (var meta in NPCMetaStorage.Instance.All())
        {
            if (meta == null || meta.value == null) continue;
            if (System.Enum.IsDefined(typeof(Character), meta.character)) continue; // バニラは上で収集済み
            Add(meta.value);
        }

        return result.ToArray();
    }

    // ==========================================================
    //  リフレクション補助
    // ==========================================================

    // 配列／List 型のフィールドを空にする（型を決め打ちしないための補助）
    static void EmptyCollection(object target, string fieldName)
    {
        FieldInfo f = target.GetType().GetField(fieldName, AllInstance);
        if (f == null) return;
        System.Type t = f.FieldType;
        if (t.IsArray)
            f.SetValue(target, System.Array.CreateInstance(t.GetElementType(), 0));
        else if (typeof(System.Collections.IList).IsAssignableFrom(t))
            f.SetValue(target, System.Activator.CreateInstance(t));
    }

    // int / IntVector2 のフィールドを倍率で拡大する
    static void ScaleField(object target, string fieldName, float grow)
    {
        FieldInfo f = target.GetType().GetField(fieldName, AllInstance);
        if (f == null) return;
        object v = f.GetValue(target);
        if (v is int)
            f.SetValue(target, Mathf.CeilToInt((int)v * grow));
        else if (v is IntVector2)
        {
            IntVector2 iv = (IntVector2)v;
            f.SetValue(target, new IntVector2(Mathf.CeilToInt(iv.x * grow), Mathf.CeilToInt(iv.z * grow)));
        }
    }
}