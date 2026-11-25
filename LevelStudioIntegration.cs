using BepInEx.Bootstrap;
using HarmonyLib;
using MTM101BaldAPI;
using MTM101BaldAPI.Registers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// =====================================================================
// Plus Level Studio 連携（Editor / Loader が無くても動く構成）
//
//   ・このファイルは Level Studio の DLL を「コンパイル時に参照しない」。
//     すべてリフレクションで触るので、DLL が無い環境でもビルド・起動できる。
//   ・BepInDependency は SoftDependency。入っていれば先に読み込まれ、
//     無ければ何もしない（ログに 1 行出るだけ）。
//   ・2 段構成:
//       ① Loader（PlusStudioLevelLoader）   … 入っていたら alias を登録
//            → エディタ無しでも、Studio で作ったレベルに Mod の NPC / アイテムを出せる
//       ② Editor（PlusLevelStudio）         … 入っていたら、さらに Editor の
//            NPC / アイテム パレットに Mod のツールを追加する
//   ・どの段が失敗しても、Mod 本体（NPC・アイテム・Extreme）には影響しない。
// =====================================================================

// NPC Register()時のスプライトをキャッシュ（Editor のパレット用）
public static class EditorSprites
{
    static readonly Dictionary<string, Sprite> _d = new Dictionary<string, Sprite>();
    public static void Register(string npcName, Sprite s) { if (s != null) _d[npcName] = s; }
    public static Sprite Get(string npcName) { _d.TryGetValue(npcName, out var s); return s; }
}

public static class StudioLink
{
    public const string LoaderGuid = "mtm101.rulerp.baldiplus.levelstudioloader";
    public const string EditorGuid = "mtm101.rulerp.baldiplus.levelstudio";

    // Mod が追加する NPC（enum 名）。alias は小文字
    public static readonly string[] ModNpcs =
        { "MrWhistle", "Kemoneko", "Anubis", "MrKtPlayer", "Biten", "Minto", "Mogigi", "Kagutsuchi" };

    // Mod が追加するアイテム（enum 名, alias）
    public static readonly (string enumName, string alias)[] ModItems =
    {
        ("ToyBazooka", "toybazooka"),
        ("ManualTeleporter", "manualteleporter"),
        ("Present", "present"),
        ("PartyHat", "partyhat"),
        ("ACola", "acola"),
    };

    public static bool LoaderPresent { get; private set; }
    public static bool EditorPresent { get; private set; }

    // MyPlugin.Awake から 1 回だけ呼ぶ
    public static void Init(Harmony harmony)
    {
        LoaderPresent = Chainloader.PluginInfos.ContainsKey(LoaderGuid);
        EditorPresent = Chainloader.PluginInfos.ContainsKey(EditorGuid);
        MyPlugin.Log.LogInfo($"[LevelStudio] Loader={(LoaderPresent ? "found" : "not found")}, Editor={(EditorPresent ? "found" : "not found")}");

        if (!EditorPresent) return;
        try { EditorBridge.Install(harmony); }
        catch (Exception ex) { MyPlugin.Log.LogError($"[LevelStudio] Editor integration disabled: {ex.GetType().Name}: {ex.Message}"); }
    }

    // 全 Mod コンテンツの Register() が終わったあとに呼ぶ（何度呼んでも安全）
    public static void OnModContentRegistered()
    {
        if (!LoaderPresent) return;
        try { LoaderBridge.RegisterAliases(); }
        catch (Exception ex) { MyPlugin.Log.LogError($"[LevelStudio] Loader alias registration failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    // 小文字 alias
    public static string NpcKey(string enumName) { return enumName.ToLower(); }

    // NPC のプレハブ（シーンに属さないもの）を探す
    public static NPC FindNpcPrefab(string enumName)
    {
        Character ch = EnumExtensions.GetFromExtendedName<Character>(enumName);
        foreach (NPC candidate in Resources.FindObjectsOfTypeAll<NPC>())
        {
            if (candidate == null || candidate.Character != ch) continue;
            string sn = candidate.gameObject.scene.name;
            if (string.IsNullOrEmpty(sn) || sn == "DontDestroyOnLoad") return candidate;
        }
        return null;
    }

    public static ItemObject FindItemObject(string enumName)
    {
        Items itemEnum = EnumExtensions.GetFromExtendedName<Items>(enumName);
        ItemMetaData meta = ItemMetaStorage.Instance.FindByEnum(itemEnum);
        return (meta != null && meta.itemObjects != null && meta.itemObjects.Length > 0) ? meta.itemObjects[0] : null;
    }
}

// =====================================================================
// ① Loader: npcAliases / itemObjects に Mod のものを登録
// =====================================================================
static class LoaderBridge
{
    static object Plugin
    {
        get
        {
            return Chainloader.PluginInfos.TryGetValue(StudioLink.LoaderGuid, out var info) && info != null
                ? (object)info.Instance : null;
        }
    }

    static T GetDict<T>(object plugin, string field) where T : class
    {
        FieldInfo f = plugin.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return f != null ? f.GetValue(plugin) as T : null;
    }

    public static void RegisterAliases()
    {
        object plugin = Plugin;
        if (plugin == null) { MyPlugin.Log.LogWarning("[LevelStudio Loader] plugin instance not available."); return; }

        // ---- NPC ----
        var npcAliases = GetDict<Dictionary<string, NPC>>(plugin, "npcAliases");
        if (npcAliases == null) MyPlugin.Log.LogWarning("[LevelStudio Loader] npcAliases not found.");
        else
        {
            foreach (string name in StudioLink.ModNpcs)
            {
                try
                {
                    string key = StudioLink.NpcKey(name);
                    if (npcAliases.ContainsKey(key)) continue;
                    NPC prefab = StudioLink.FindNpcPrefab(name);
                    if (prefab == null) { MyPlugin.Log.LogWarning($"[LevelStudio Loader] NPC prefab not found: {name}"); continue; }
                    npcAliases[key] = prefab;
                    MyPlugin.Log.LogInfo($"[LevelStudio Loader] npcAliases[{key}] = {name}");
                }
                catch (Exception ex) { MyPlugin.Log.LogError($"[LevelStudio Loader] NPC {name}: {ex.Message}"); }
            }
        }

        // ---- アイテム ----
        var itemObjects = GetDict<Dictionary<string, ItemObject>>(plugin, "itemObjects");
        if (itemObjects == null) MyPlugin.Log.LogWarning("[LevelStudio Loader] itemObjects not found.");
        else
        {
            foreach (var (enumName, alias) in StudioLink.ModItems)
            {
                try
                {
                    if (itemObjects.ContainsKey(alias)) continue;
                    ItemObject io = StudioLink.FindItemObject(enumName);
                    if (io == null) { MyPlugin.Log.LogWarning($"[LevelStudio Loader] item not found: {enumName}"); continue; }
                    itemObjects[alias] = io;
                    MyPlugin.Log.LogInfo($"[LevelStudio Loader] itemObjects[{alias}] = {enumName}");
                }
                catch (Exception ex) { MyPlugin.Log.LogError($"[LevelStudio Loader] item {enumName}: {ex.Message}"); }
            }
        }
    }
}

// =====================================================================
// ② Editor: EditorInterfaceModes.AddVanillaNPCs / AddVanillaItems の Postfix で
//    Editor のパレットに Mod の NPC / アイテムを追加する
//    （Editor が入っているときだけ、Harmony で手動パッチ）
// =====================================================================
static class EditorBridge
{
    static Assembly editorAsm;
    static Type tModes, tInterface, tEditorTool, tNpcTool, tItemTool;
    static MethodInfo miAddToolToCategory;

    static Type FindType(string simpleName, params string[] fullNames)
    {
        foreach (string fn in fullNames)
        {
            Type t = editorAsm.GetType(fn);
            if (t != null) return t;
        }
        try { return editorAsm.GetTypes().FirstOrDefault(x => x.Name == simpleName); }
        catch (ReflectionTypeLoadException e) { return e.Types.FirstOrDefault(x => x != null && x.Name == simpleName); }
    }

    public static void Install(Harmony harmony)
    {
        var info = Chainloader.PluginInfos[StudioLink.EditorGuid];
        editorAsm = info.Instance.GetType().Assembly;

        tModes = FindType("EditorInterfaceModes", "PlusLevelStudio.EditorInterfaceModes");
        tInterface = FindType("EditorInterface", "PlusLevelStudio.EditorInterface");
        tEditorTool = FindType("EditorTool", "PlusLevelStudio.Editor.EditorTool");
        tNpcTool = FindType("NPCTool", "PlusLevelStudio.Editor.Tools.NPCTool");
        tItemTool = FindType("ItemTool", "PlusLevelStudio.Editor.Tools.ItemTool");

        try { EditorVolume.Install(harmony, FindType("EditorController", "PlusLevelStudio.Editor.EditorController")); }
        catch (Exception e) { MyPlugin.Log.LogWarning("[EditorVolume] install failed: " + e.Message); }

        if (tModes == null) { MyPlugin.Log.LogWarning("[LevelStudio Editor] EditorInterfaceModes not found. Editor integration skipped."); return; }

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        MethodInfo addNpcs = tModes.GetMethod("AddVanillaNPCs", all);
        MethodInfo addItems = tModes.GetMethod("AddVanillaItems", all);
        miAddToolToCategory = tModes.GetMethods(all).FirstOrDefault(m => m.Name == "AddToolToCategory" && m.GetParameters().Length == 4);

        if (miAddToolToCategory == null) { MyPlugin.Log.LogWarning("[LevelStudio Editor] AddToolToCategory not found. Editor integration skipped."); return; }

        if (addNpcs != null)
        {
            harmony.Patch(addNpcs, postfix: new HarmonyMethod(typeof(EditorBridge), nameof(NpcPostfix)));
            MyPlugin.Log.LogInfo("[LevelStudio Editor] Patched AddVanillaNPCs.");
        }
        else MyPlugin.Log.LogWarning("[LevelStudio Editor] AddVanillaNPCs not found.");

        if (addItems != null)
        {
            harmony.Patch(addItems, postfix: new HarmonyMethod(typeof(EditorBridge), nameof(ItemPostfix)));
            MyPlugin.Log.LogInfo("[LevelStudio Editor] Patched AddVanillaItems.");
        }
        else MyPlugin.Log.LogWarning("[LevelStudio Editor] AddVanillaItems not found.");
    }

    static void AddTool(object mode, string category, object tool)
    {
        miAddToolToCategory.Invoke(null, new object[] { mode, category, tool, true });
    }

    // ---- NPC パレット ----
    static void NpcPostfix(object modeToModify)
    {
        try
        {
            // compliant モード（バニラのみ）には追加しない
            var fVc = modeToModify?.GetType().GetField("vanillaComplaint",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fVc != null && (bool)fVc.GetValue(modeToModify)) return;

            // ツールが参照する alias を先に登録しておく
            StudioLink.OnModContentRegistered();

            MethodInfo addNpcVisual = tInterface?.GetMethod("AddNPCVisual",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(NPC) }, null);
            ConstructorInfo ctorSprite = null, ctorPlain = null;
            if (tNpcTool != null)
            {
                foreach (var c in tNpcTool.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var ps = c.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) ctorPlain = c;
                    else if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(Sprite)) ctorSprite = c;
                }
            }
            if (addNpcVisual == null || (ctorPlain == null && ctorSprite == null))
            {
                MyPlugin.Log.LogWarning("[LevelStudio Editor] NPC palette API not found. NPCs not added to Editor.");
                return;
            }

            foreach (string name in StudioLink.ModNpcs)
            {
                try
                {
                    string key = StudioLink.NpcKey(name);
                    NPC prefab = StudioLink.FindNpcPrefab(name);
                    if (prefab == null) { MyPlugin.Log.LogWarning($"[LevelStudio Editor] NPC prefab not found: {name}"); continue; }

                    // ビジュアル登録（登録済みなら無視）
                    try { addNpcVisual.Invoke(null, new object[] { key, prefab }); }
                    catch (TargetInvocationException ex)
                    {
                        if (!(ex.InnerException is ArgumentException)) throw;
                    }

                    Sprite sprite = EditorSprites.Get(name);
                    if (sprite == null)
                    {
                        var srs = prefab.GetComponentsInChildren<SpriteRenderer>(true);
                        if (srs != null && srs.Length > 0 && srs[0] != null) sprite = srs[0].sprite;
                    }

                    object tool;
                    if (sprite != null && ctorSprite != null) tool = ctorSprite.Invoke(new object[] { key, sprite });
                    else if (ctorPlain != null) tool = ctorPlain.Invoke(new object[] { key });
                    else tool = ctorSprite.Invoke(new object[] { key, null });
                    AddTool(modeToModify, "npcs", tool);
                    MyPlugin.Log.LogInfo($"[LevelStudio Editor] NPC tool added: {name}");
                }
                catch (Exception ex)
                {
                    MyPlugin.Log.LogError($"[LevelStudio Editor] NPC {name}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            MyPlugin.Log.LogError($"[LevelStudio Editor] NPC palette failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- アイテム パレット ----
    static void ItemPostfix(object modeToModify)
    {
        try
        {
            StudioLink.OnModContentRegistered();

            ConstructorInfo ctor = tItemTool?.GetConstructor(new[] { typeof(string), typeof(Sprite), typeof(bool) });
            if (ctor == null)
            {
                MyPlugin.Log.LogWarning("[LevelStudio Editor] ItemTool(string, Sprite, bool) not found. Items not added to Editor.");
                return;
            }

            foreach (var (enumName, alias) in StudioLink.ModItems)
            {
                try
                {
                    ItemObject io = StudioLink.FindItemObject(enumName);
                    if (io == null) { MyPlugin.Log.LogWarning($"[LevelStudio Editor] item not found: {enumName}"); continue; }

                    object tool = ctor.Invoke(new object[] { alias, io.itemSpriteSmall, true });
                    AddTool(modeToModify, "items", tool);
                    MyPlugin.Log.LogInfo($"[LevelStudio Editor] Item tool added: {enumName}");
                }
                catch (Exception ex)
                {
                    MyPlugin.Log.LogError($"[LevelStudio Editor] item {enumName}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            MyPlugin.Log.LogError($"[LevelStudio Editor] Item palette failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
