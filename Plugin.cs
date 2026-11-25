using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using MTM101BaldAPI;
using MTM101BaldAPI.Registers;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;

[BepInPlugin("com.ritlon.ritlonbbpmod", "Ritlon's BB+ Mod", "1.0.3")]
[BepInDependency("mtm101.rulerp.bbplus.baldidevapi")]
// Level Studio は「あれば連携する」ソフト依存。無くても Mod は普通に動く（LevelStudioIntegration.cs）
[BepInDependency(StudioLink.LoaderGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(StudioLink.EditorGuid, BepInDependency.DependencyFlags.SoftDependency)]
public class MyPlugin : BaseUnityPlugin
{
    public static MyPlugin Instance;
    internal static ManualLogSource Log;

    private static bool registered = false;
    private static bool hallBuilderLogged = false;

    void Awake()
    {
        Instance = this;
        Log = Logger;
        var harmony = new Harmony("com.ritlon.bbpmod");
        try { harmony.PatchAll(); }
        catch (System.Exception ex) { Log.LogError($"PatchAll failed: {ex.GetType().Name}: {ex.Message}"); }

        // ---- Level Studio 連携（Loader / Editor が入っている場合だけ有効になる） ----
        StudioLink.Init(harmony);

        // ---- Extreme モード（Hide-and-Seek 拡張モード）のパッチ登録 ----
        ExtremeMode.Install(harmony);
        LoungeGirl.Install(harmony);

        // ---- LocalizationManager.GetLocalizedText への手動パッチ登録 ----
        // 属性ベース([HarmonyPatch])のPatchAll()自動検出では、このメソッドに対して
        // なぜかパッチが1件も登録されない現象が実機ログで確認されたため、
        // ここで明示的に harmony.Patch(...) を呼び、成功/失敗を必ずログに残す。
        var getLocTextMethod1 = AccessTools.Method(typeof(LocalizationManager), "GetLocalizedText", new System.Type[] { typeof(string) });
        var getLocTextMethod2 = AccessTools.Method(typeof(LocalizationManager), "GetLocalizedText", new System.Type[] { typeof(string), typeof(bool) });
        Log.LogInfo($"[LocalizationPatch] GetLocalizedText(string) resolved: {getLocTextMethod1 != null}, GetLocalizedText(string,bool) resolved: {getLocTextMethod2 != null}");

        if (getLocTextMethod1 != null)
        {
            try
            {
                var prefix1 = new HarmonyMethod(AccessTools.Method(typeof(LocalizationManager_GetLocalizedText_Patch), "Prefix_OneArg"));
                harmony.Patch(getLocTextMethod1, prefix: prefix1);
                var patchInfo1 = Harmony.GetPatchInfo(getLocTextMethod1);
                Log.LogInfo($"[LocalizationPatch] Manually patched GetLocalizedText(string). Prefixes now={patchInfo1?.Prefixes?.Count ?? 0}");
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[LocalizationPatch] Failed to patch GetLocalizedText(string): {ex.GetType().Name}: {ex.Message}");
            }
        }
        if (getLocTextMethod2 != null)
        {
            try
            {
                var prefix2 = new HarmonyMethod(AccessTools.Method(typeof(LocalizationManager_GetLocalizedText_Patch), "Prefix_TwoArg"));
                harmony.Patch(getLocTextMethod2, prefix: prefix2);
                var patchInfo2 = Harmony.GetPatchInfo(getLocTextMethod2);
                Log.LogInfo($"[LocalizationPatch] Manually patched GetLocalizedText(string,bool). Prefixes now={patchInfo2?.Prefixes?.Count ?? 0}");
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[LocalizationPatch] Failed to patch GetLocalizedText(string,bool): {ex.GetType().Name}: {ex.Message}");
            }
        }

        StartCoroutine(ToyBazookaProjectile.LoadSquishSoundCoroutine());

        GeneratorManagement.Register(this, GenerationModType.Addend,
            (string floorName, int floorNum, SceneObject so) =>
            {
                // Register() はゲーム起動後1回のみ（static フラグで管理）
                if (!registered)
                {
                    registered = true;
                    ITM_ToyBazooka.Register();
                    ITM_ManualTeleporter.Register();
                    ITM_Present.Register();
                    ITM_PartyHat.Register();
                    MrWhistle.Register();
                    Kemoneko.Register();
                    Anubis.Register();
                    MrKtPlayer.Register();
                    Biten.Register();
                    Minto.Register();
                    Mogigi.Register();
                    Kagutsuchi.Register();
                    ITM_ACola.Register();

                    // 全Register()完了後に、Level Studio Loader があれば alias を登録
                    StudioLink.OnModContentRegistered();

                    // ---- デバッグ: 全アイテムの ItemFlags 実際の値をログ出力 ----
                    // この時点ではバニラアイテム・Modアイテムとも ItemMetaStorage に
                    // 登録済みのはずなので、ショップに出る/出ないアイテムの flags を比較できる
                    LogAllItemFlags();
                }
                // ↑ここまでは1回だけ
                // ↓アイテム/NPC追加はゲームロードのたびに毎回実行する必要がある
                // levelObject が null の場合は compatibleObjects / potentialLevelObjects を試す
                var levelObjects = GetLevelObjects(so);

                MyPlugin.Log.LogInfo($"[GeneratorCallback] floor={floorName} num={floorNum} so={so.name} levelObjects={levelObjects.Length}");

                // ---- ジョニーの店（Johnny's Store / YTPショップ）にMod3アイテムを追加 ----
                // Store は LevelObject.shopItems ではなく SceneObject.shopItems を直接参照しているため
                // （StoreRoomFunction.Restock() が storeData.shopItems を使用。storeData は
                //  sceneObject.storeUsesNextLevelData に応じて nextLevel か sceneObject 自身）
                // 全フロアの SceneObject に対して毎回追加しておくことで、
                // どちらが storeData として使われても確実に候補に入るようにする。
                // ※自販機（LevelObject.shopItems）側への追加はいったん見送り、
                //   ジョニーの店にのみ出すようにしている。
                // ※確率(weight)はユーザー指示により125に統一（Present・AColaは225＝価格350帯）
                AddItemToSceneObjShop(so, "ToyBazooka", 125);
                AddItemToSceneObjShop(so, "ManualTeleporter", 125);
                AddItemToSceneObjShop(so, "Present", 225);
                AddItemToSceneObjShop(so, "PartyHat", 125);
                AddItemToSceneObjShop(so, "ACola", 225);

                if (levelObjects.Length == 0) return;

                // Anubis・MrKtPlayer: 1F以外のフロアで許可（元の仕様）
                bool allowMidTierNpcs = floorNum != 0;

                // Biten・Minto: 1F・2F以外のフロアで許可（元の仕様）
                bool allowHighTierNpcs = floorNum != 0 && floorNum != 1;

                foreach (var lvlObj in levelObjects)
                {
                    // PartyHat: F1は必ず(100%)、F2以降は従来通り33%の確率で追加する。
                    if (floorNum == 0 || UnityEngine.Random.value < 0.33f)
                        AddForcedItemToObj(lvlObj, "PartyHat");

                    // ToyBazooka / ManualTeleporter: 校内にも確率で生成(各アイテム別々に判定)
                    //   F1=15% / F2=30% / F3以降=60%
                    float toolChance = floorNum >= 2 ? 0.60f : (floorNum == 1 ? 0.30f : 0.15f);
                    if (UnityEngine.Random.value < toolChance)
                        AddForcedItemToObj(lvlObj, "ToyBazooka");
                    if (UnityEngine.Random.value < toolChance)
                        AddForcedItemToObj(lvlObj, "ManualTeleporter");

                    // Present: 校内（フロア上の部屋）に直接スポーンさせる
                    AddForcedItemToObj(lvlObj, "Present");

                    // ACola: 校内（フロア上の部屋）にも直接スポーンさせる
                    AddForcedItemToObj(lvlObj, "ACola");

                    // MrWhistle
                    AddForcedNpcToObj(lvlObj, "MrWhistle");

                    // Kemoneko
                    AddForcedNpcToObj(lvlObj, "Kemoneko");

                    // Anubis
                    if (allowMidTierNpcs)
                        AddForcedNpcToObj(lvlObj, "Anubis");

                    // MrKtPlayer
                    if (allowMidTierNpcs)
                        AddForcedNpcToObj(lvlObj, "MrKtPlayer");

                    // Biten
                    if (allowHighTierNpcs)
                        AddForcedNpcToObj(lvlObj, "Biten");

                    // Minto
                    if (allowHighTierNpcs)
                        AddForcedNpcToObj(lvlObj, "Minto");

                    // Kagutsuchi: F3以降
                    if (allowHighTierNpcs)
                        AddForcedNpcToObj(lvlObj, "Kagutsuchi");
                }

                // Mogigi: 中盤以降のフロア限定。F2(floorNum=1)=3体、F3以降(floorNum>=2)=5体。
                // ※同じ部屋テンプレートに複数体を詰め込むと、1つの部屋（廊下など）に
                //   まとめてスポーンしてしまい、互いに重なって壁にめり込みスタックする
                //   原因になっていたため、「異なる」部屋テンプレートを複数選び、
                //   それぞれに1体ずつ（重複チェックあり）スポーンさせる方式に変更。
                if (allowMidTierNpcs)
                {
                    int mogigiCount = (floorNum == 1) ? 3 : 5;
                    var mogigiTargets = levelObjects
                        .OrderBy(_ => UnityEngine.Random.value)
                        .Take(mogigiCount);
                    foreach (var target in mogigiTargets)
                        AddForcedNpcToObj(target, "Mogigi");
                }
            });

        Logger.LogInfo("Mod loaded!");
    }

    // 全アイテムの ItemFlags を一覧ログ出力する（デバッグ用）
    static void LogAllItemFlags()
    {
        foreach (var meta in ItemMetaStorage.Instance.All())
        {
            if (meta == null || meta.itemObjects == null || meta.itemObjects.Length == 0) continue;
            var io = meta.itemObjects[0];
            Log.LogInfo($"[ItemFlagsDebug] {io.name} itemType={io.itemType} flags={meta.flags} (int={(int)meta.flags})");
        }
    }

    // 自販機ビルダーを追加する（現在は未使用：自販機対応はいったん見送り中）
    // 既存の DietBsodaHallBuilder / ZestyHallBuilder を複製し、
    // min/max を増やした新しいビルダーを specialHallBuilders に追加する
    // （各自販機につき追加で VENDING_EXTRA_MIN〜VENDING_EXTRA_MAX 個生成）
    const int VENDING_EXTRA_MIN = 2;
    const int VENDING_EXTRA_MAX = 4;
    const int VENDING_EXTRA_WEIGHT = 100;

    static readonly FieldInfo fi_objectPlacer = typeof(GenericHallBuilder)
        .GetField("objectPlacer", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo fi_prefab = typeof(ObjectPlacer)
        .GetField("prefab", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo fi_min = typeof(ObjectPlacer)
        .GetField("min", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo fi_max = typeof(ObjectPlacer)
        .GetField("max", BindingFlags.NonPublic | BindingFlags.Instance);

    static void AddExtraVendingMachines(LevelObject lvlObj)
    {
        var extras = new System.Collections.Generic.List<WeightedObjectBuilder>();

        foreach (var wb in lvlObj.specialHallBuilders)
        {
            string name = wb.selection?.name ?? "";
            if (name != "DietBsodaHallBuilder" && name != "ZestyHallBuilder") continue;

            GenericHallBuilder original = wb.selection as GenericHallBuilder;
            if (original == null) continue;

            // 元の ObjectPlacer を取得
            ObjectPlacer origPlacer = fi_objectPlacer.GetValue(original) as ObjectPlacer;
            if (origPlacer == null)
            {
                Log.LogWarning($"[VendingAdd] objectPlacer is null on {name}");
                continue;
            }

            // 元の prefab を取得
            GameObject prefab = fi_prefab.GetValue(origPlacer) as GameObject;
            if (prefab == null)
            {
                Log.LogWarning($"[VendingAdd] prefab is null on {name}");
                continue;
            }

            // 新しい ObjectPlacer を作成（元のコピー + min/max を上書き）
            ObjectPlacer newPlacer = new ObjectPlacer();
            fi_prefab.SetValue(newPlacer, prefab);
            fi_min.SetValue(newPlacer, VENDING_EXTRA_MIN);
            fi_max.SetValue(newPlacer, VENDING_EXTRA_MAX);

            // eligibleShapes / coverage / フラグは元から引き継ぐ
            newPlacer.eligibleShapes = origPlacer.eligibleShapes;
            newPlacer.coverage       = origPlacer.coverage;
            typeof(ObjectPlacer).GetField("useWallDir",   BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(newPlacer, typeof(ObjectPlacer).GetField("useWallDir",   BindingFlags.NonPublic | BindingFlags.Instance).GetValue(origPlacer));
            typeof(ObjectPlacer).GetField("useOpenDir",   BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(newPlacer, typeof(ObjectPlacer).GetField("useOpenDir",   BindingFlags.NonPublic | BindingFlags.Instance).GetValue(origPlacer));
            typeof(ObjectPlacer).GetField("includeOpen",  BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(newPlacer, typeof(ObjectPlacer).GetField("includeOpen",  BindingFlags.NonPublic | BindingFlags.Instance).GetValue(origPlacer));
            typeof(ObjectPlacer).GetField("group",        BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(newPlacer, typeof(ObjectPlacer).GetField("group",        BindingFlags.NonPublic | BindingFlags.Instance).GetValue(origPlacer));

            // 新しい GenericHallBuilder を作成
            GameObject go = new GameObject($"Extra_{name}");
            Object.DontDestroyOnLoad(go);
            GenericHallBuilder newBuilder = go.AddComponent<GenericHallBuilder>();
            fi_objectPlacer.SetValue(newBuilder, newPlacer);

            extras.Add(new WeightedObjectBuilder
            {
                selection = newBuilder,
                weight    = VENDING_EXTRA_WEIGHT
            });

            Log.LogInfo($"[VendingAdd] Added extra builder for {name} (min={VENDING_EXTRA_MIN} max={VENDING_EXTRA_MAX} weight={VENDING_EXTRA_WEIGHT})");
        }

        if (extras.Count == 0)
        {
            Log.LogWarning($"[VendingAdd] No vending machine builders found in {lvlObj.name}");
            return;
        }

        // specialHallBuilders に追加
        var list = new System.Collections.Generic.List<WeightedObjectBuilder>(lvlObj.specialHallBuilders);
        list.AddRange(extras);
        lvlObj.specialHallBuilders = list.ToArray();
    }

    // specialHallBuilders の内容をログ出力する（現在は未使用）
    static void LogHallBuilders(LevelObject lvlObj)
    {
        Log.LogInfo($"[HallBuilderLog] lvlObj={lvlObj.name}");
        Log.LogInfo($"[HallBuilderLog] forcedSpecialHallBuilders count={lvlObj.forcedSpecialHallBuilders.Length}");
        for (int i = 0; i < lvlObj.forcedSpecialHallBuilders.Length; i++)
        {
            var b = lvlObj.forcedSpecialHallBuilders[i];
            Log.LogInfo($"  forced[{i}] type={b?.GetType().FullName ?? "null"} name={b?.name ?? "null"}");
        }

        Log.LogInfo($"[HallBuilderLog] specialHallBuilders count={lvlObj.specialHallBuilders.Length}");
        for (int i = 0; i < lvlObj.specialHallBuilders.Length; i++)
        {
            var w = lvlObj.specialHallBuilders[i];
            var b = w.selection;
            Log.LogInfo($"  special[{i}] weight={w.weight} type={b?.GetType().FullName ?? "null"} name={b?.name ?? "null"}");
        }

        Log.LogInfo($"[HallBuilderLog] forcedStructures count={lvlObj.forcedStructures.Length}");
        for (int i = 0; i < lvlObj.forcedStructures.Length; i++)
        {
            var s = lvlObj.forcedStructures[i];
            Log.LogInfo($"  forcedStruct[{i}] type={s?.GetType().FullName ?? "null"}");
        }

        Log.LogInfo($"[HallBuilderLog] potentialStructures count={lvlObj.potentialStructures.Length}");
        for (int i = 0; i < lvlObj.potentialStructures.Length; i++)
        {
            var w = lvlObj.potentialStructures[i];
            Log.LogInfo($"  potentialStruct[{i}] weight={w.weight} type={w.selection?.GetType().FullName ?? "null"}");
        }
    }

    // SceneObject から有効な LevelObject を全て取得
    // levelObject が null の場合は compatibleObjects 等のフィールドをリフレクションで探す
    static LevelObject[] GetLevelObjects(SceneObject so)
    {
        if (so.levelObject != null)
            return new[] { so.levelObject };

        // リフレクションで LevelObject[] / List<LevelObject> フィールドを探す
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var field in so.GetType().GetFields(flags))
        {
            if (field.FieldType == typeof(LevelObject[]))
            {
                var arr = field.GetValue(so) as LevelObject[];
                if (arr != null && arr.Length > 0)
                {
                    MyPlugin.Log.LogInfo($"[GetLevelObjects] Found {field.Name}: {arr.Length} objects");
                    return arr;
                }
            }
            if (field.FieldType == typeof(System.Collections.Generic.List<LevelObject>))
            {
                var list = field.GetValue(so) as System.Collections.Generic.List<LevelObject>;
                if (list != null && list.Count > 0)
                {
                    MyPlugin.Log.LogInfo($"[GetLevelObjects] Found {field.Name}: {list.Count} objects");
                    return list.ToArray();
                }
            }
        }
        // WeightedLevelObject[] の可能性も確認
        foreach (var field in so.GetType().GetFields(flags))
        {
            if (field.FieldType.IsArray)
            {
                var arr = field.GetValue(so) as System.Array;
                if (arr == null || arr.Length == 0) continue;
                var elem = arr.GetValue(0);
                if (elem == null) continue;
                // WeightedLevelObject.selection フィールドを探す
                var selField = elem.GetType().GetField("selection", flags)
                            ?? elem.GetType().GetField("levelObject", flags)
                            ?? elem.GetType().GetField("obj", flags);
                if (selField != null && selField.FieldType == typeof(LevelObject))
                {
                    var lvlObjs = new System.Collections.Generic.List<LevelObject>();
                    foreach (var item in arr)
                    {
                        var lo = selField.GetValue(item) as LevelObject;
                        if (lo != null) lvlObjs.Add(lo);
                    }
                    if (lvlObjs.Count > 0)
                    {
                        MyPlugin.Log.LogInfo($"[GetLevelObjects] Found weighted {field.Name}: {lvlObjs.Count} objects");
                        return lvlObjs.ToArray();
                    }
                }
            }
        }

        MyPlugin.Log.LogWarning($"[GetLevelObjects] No LevelObject found in {so.name}. Fields:");
        foreach (var f in so.GetType().GetFields(flags))
            MyPlugin.Log.LogInfo($"  field: {f.Name} ({f.FieldType.Name})");
        return new LevelObject[0];
    }

    // アイテムを LevelObject に追加（重複チェックあり）（現在は未使用：自販機対応はいったん見送り中）
    static void AddItemToLevelObj(SceneObject so, LevelObject lvlObj, string itemEnumName, int shopWeight)
    {
        ItemMetaData meta = ItemMetaStorage.Instance.FindByEnum(
            EnumExtensions.GetFromExtendedName<Items>(itemEnumName));
        if (meta == null || meta.itemObjects.Length == 0)
        {
            MyPlugin.Log.LogWarning($"[AddItemToLevelObj] meta not found for {itemEnumName}");
            return;
        }
        ItemObject item = meta.itemObjects[0];

        bool inShop = false;
        foreach (var w in lvlObj.shopItems)
            if (w.selection == item) { inShop = true; break; }
        if (!inShop)
        {
            var shopList = lvlObj.shopItems.ToList();
            shopList.Add(new WeightedItemObject { selection = item, weight = shopWeight });
            lvlObj.shopItems = shopList.ToArray();
            MyPlugin.Log.LogInfo($"[AddItemToLevelObj] Added {itemEnumName} to {lvlObj.name} shopItems (weight={shopWeight}). shopItems count now={lvlObj.shopItems.Length}");
        }
        else
        {
            MyPlugin.Log.LogInfo($"[AddItemToLevelObj] {itemEnumName} already in {lvlObj.name} shopItems.");
        }
        if (!lvlObj.forcedItems.Contains(item))
            lvlObj.forcedItems.Add(item);
    }

    // アイテムを SceneObject.shopItems に追加（ジョニーの店＝Johnny's Store専用。
    // LevelObject.shopItems（自販機用）とは別フィールードなので個別に追加が必要）
    static void AddItemToSceneObjShop(SceneObject so, string itemEnumName, int shopWeight)
    {
        ItemMetaData meta = ItemMetaStorage.Instance.FindByEnum(
            EnumExtensions.GetFromExtendedName<Items>(itemEnumName));
        if (meta == null || meta.itemObjects.Length == 0)
        {
            MyPlugin.Log.LogWarning($"[AddItemToSceneObjShop] meta not found for {itemEnumName}");
            return;
        }
        ItemObject item = meta.itemObjects[0];

        if (so.shopItems == null)
        {
            so.shopItems = new WeightedItemObject[0];
        }

        bool inShop = false;
        foreach (var w in so.shopItems)
            if (w.selection == item) { inShop = true; break; }

        if (!inShop)
        {
            var shopList = so.shopItems.ToList();
            shopList.Add(new WeightedItemObject { selection = item, weight = shopWeight });
            so.shopItems = shopList.ToArray();
            MyPlugin.Log.LogInfo($"[AddItemToSceneObjShop] Added {itemEnumName} to {so.name} shopItems (weight={shopWeight}). shopItems count now={so.shopItems.Length}");
        }
        else
        {
            MyPlugin.Log.LogInfo($"[AddItemToSceneObjShop] {itemEnumName} already in {so.name} shopItems.");
        }
    }

    // アイテムを LevelObject.forcedItems に追加（校内のフロア上に直接スポーンさせる。重複チェックあり）
    static void AddForcedItemToObj(LevelObject lvlObj, string itemEnumName)
    {
        ItemMetaData meta = ItemMetaStorage.Instance.FindByEnum(
            EnumExtensions.GetFromExtendedName<Items>(itemEnumName));
        if (meta == null || meta.itemObjects.Length == 0)
        {
            MyPlugin.Log.LogWarning($"[AddForcedItemToObj] meta not found for {itemEnumName}");
            return;
        }
        ItemObject item = meta.itemObjects[0];

        if (lvlObj.forcedItems.Contains(item))
        {
            MyPlugin.Log.LogInfo($"[AddForcedItemToObj] {itemEnumName} already in {lvlObj.name} forcedItems.");
            return;
        }

        lvlObj.forcedItems.Add(item);
        MyPlugin.Log.LogInfo($"[AddForcedItemToObj] Added {itemEnumName} to {lvlObj.name} forcedItems.");
    }

    // NPC を LevelObject に追加（重複チェックあり）
    static void AddForcedNpcToObj(LevelObject lvlObj, string enumName)
    {
        Character ch = EnumExtensions.GetFromExtendedName<Character>(enumName);

        foreach (NPC existing in lvlObj.forcedNpcs)
            if (existing != null && existing.Character == ch) return;

        NPC found = null;
        foreach (NPC candidate in Resources.FindObjectsOfTypeAll<NPC>())
        {
            if (candidate.Character != ch) continue;
            string sceneName = candidate.gameObject.scene.name;
            if (string.IsNullOrEmpty(sceneName) || sceneName == "DontDestroyOnLoad")
            { found = candidate; break; }
        }
        if (found == null)
        {
            MyPlugin.Log.LogWarning($"[AddForcedNpc] NPC prefab not found: {enumName}");
            return;
        }

        var list = lvlObj.forcedNpcs.ToList();
        list.Add(found);
        lvlObj.forcedNpcs = list.ToArray();
        MyPlugin.Log.LogInfo($"[AddForcedNpc] Added {enumName} to {lvlObj.name} forcedNpcs.");
    }
}
