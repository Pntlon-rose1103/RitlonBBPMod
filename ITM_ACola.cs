using System.Reflection;
using MTM101BaldAPI;
using MTM101BaldAPI.Registers;
using UnityEngine;

// =====================================================================
// A Cola
//   使用すると、正面にDiet Bsodaを放ちながら（バニラのダイエットBsodaと
//   同じ「踏んだ相手を前方に押し流す」効果付き）、同時にその場に
//   チョークイレーザーと同じ煙幕（CoverCloud）を展開する。
//
//   実装方針:
//     バニラのDietBsoda・ChalkEraserの各ItemObject.item（既にシーン上に
//     インスタンス化・シリアライズ済みのGameObject）をそれぞれ複製して
//     使い回すテンプレートプレハブとして保持する（ToyBazookaがBsodaの
//     プレハブを複製する手法と同じ）。Instantiate()はシリアライズされた
//     子オブジェクト・フィールド参照（DietBsodaのmoveMod/entity、
//     ChalkEraserのcloud等）をそのまま引き継ぐため、追加の設定は不要。
//     複製したGameObject上のITM_BSODA.Use() / ChalkEraser.Use()を
//     そのまま呼び出すことで、バニラと全く同じ挙動を再現する。
// =====================================================================
public class ITM_ACola : Item
{
    private static GameObject dietBsodaPrefab = null;
    private static GameObject chalkEraserPrefab = null;

    // 通常のBsoda（DietBsodaではない）の飲む音。A Cola自体を使った演出として再生する
    private static SoundObject regularBsodaSound = null;

    public override bool Use(PlayerManager pm)
    {
        if (dietBsodaPrefab == null)
            dietBsodaPrefab = CreateClonePrefab(Items.DietBsoda, "ACola_DietBsodaProj_Prefab");
        if (chalkEraserPrefab == null)
            chalkEraserPrefab = CreateClonePrefab(Items.ChalkEraser, "ACola_ChalkEraserProj_Prefab");

        // Bsoda（通常版）のサウンドを再生
        if (regularBsodaSound != null)
            Singleton<CoreGameManager>.Instance.audMan.PlaySingle(regularBsodaSound);

        FireDietBsoda(pm);
        DeploySmokeScreen(pm);

        pm.itm.Remove(0);
        Destroy(gameObject);
        return true;
    }

    // 正面にDiet Bsodaを放つ（バニラのITM_BSODA.Use()をそのまま呼び出す）
    private void FireDietBsoda(PlayerManager pm)
    {
        if (dietBsodaPrefab == null)
        {
            MyPlugin.Log.LogWarning("[ACola] DietBsoda prefab not found — skipping.");
            return;
        }

        GameObject go = Instantiate(dietBsodaPrefab);
        go.SetActive(true);

        ITM_BSODA bsoda = go.GetComponent<ITM_BSODA>();
        if (bsoda != null)
            bsoda.Use(pm);
        else
            MyPlugin.Log.LogWarning("[ACola] ITM_BSODA component missing on clone.");
    }

    // その場に煙幕を展開する（バニラのChalkEraser.Use()をそのまま呼び出す）
    private void DeploySmokeScreen(PlayerManager pm)
    {
        if (chalkEraserPrefab == null)
        {
            MyPlugin.Log.LogWarning("[ACola] ChalkEraser prefab not found — skipping.");
            return;
        }

        GameObject go = Instantiate(chalkEraserPrefab);
        go.SetActive(true);

        ChalkEraser eraser = go.GetComponent<ChalkEraser>();
        if (eraser != null)
            eraser.Use(pm);
        else
            MyPlugin.Log.LogWarning("[ACola] ChalkEraser component missing on clone.");
    }

    // バニラアイテムのItemObject.itemのGameObjectを複製し、使い回し用プレハブを作る
    static GameObject CreateClonePrefab(Items vanillaItemType, string cloneName)
    {
        ItemObject vanillaItem = null;
        foreach (var io in Resources.FindObjectsOfTypeAll<ItemObject>())
        {
            if (io.itemType == vanillaItemType && io.item != null)
            {
                vanillaItem = io;
                break;
            }
        }
        if (vanillaItem == null)
        {
            MyPlugin.Log.LogWarning($"[ACola] Vanilla item not found: {vanillaItemType}");
            return null;
        }

        vanillaItem.item.gameObject.SetActive(false);
        GameObject go = Instantiate(vanillaItem.item.gameObject);
        vanillaItem.item.gameObject.SetActive(true);

        go.name = cloneName;
        DontDestroyOnLoad(go);
        go.SetActive(false);
        return go;
    }

    public static void Register()
    {
        Sprite spriteSmall = SpriteHelper.LoadSprite("acolaIcon_Small.png");
        Sprite spriteLarge = SpriteHelper.LoadSprite("acolaIcon_Large.png");

        // 通常のBsoda（DietBsodaではない）からsoundフィールドを拝借
        foreach (var io in Resources.FindObjectsOfTypeAll<ItemObject>())
        {
            if (io.itemType == Items.Bsoda && io.item != null)
            {
                ITM_BSODA b = io.item as ITM_BSODA;
                if (b != null)
                {
                    regularBsodaSound = (SoundObject)typeof(ITM_BSODA)
                        .GetField("sound", BindingFlags.NonPublic | BindingFlags.Instance)
                        .GetValue(b);
                }
                break;
            }
        }
        if (regularBsodaSound == null)
            MyPlugin.Log.LogWarning("[ACola] Regular Bsoda sound not found.");

        GameObject prefabGo = new GameObject("ACola_Item");
        DontDestroyOnLoad(prefabGo);
        ITM_ACola itemComp = prefabGo.AddComponent<ITM_ACola>();

        ItemObject item = ScriptableObject.CreateInstance<ItemObject>();
        item.name = "ACola";
        item.itemType = EnumExtensions.ExtendEnum<Items>("ACola");
        item.item = itemComp;
        item.itemSpriteSmall = spriteSmall;
        item.itemSpriteLarge = spriteLarge;
        item.price = 150;
        item.addToInventory = true;
        item.nameKey = "ACola";
        item.descKey = "ACola_desc";

        ItemMetaData meta = new ItemMetaData(MyPlugin.Instance.Info, item);
        meta.flags = ItemFlags.None;
        ItemMetaStorage.Instance.Add(meta);

        // ローカライズ登録は LocalizationPatch.cs 側で一括して行っている。

        MyPlugin.Log.LogInfo("ACola registered.");
    }
}
