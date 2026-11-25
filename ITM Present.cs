using System.Collections.Generic;
using System.Reflection;
using MTM101BaldAPI;
using MTM101BaldAPI.Registers;
using UnityEngine;

/// <summary>
/// Birthday Bash のプレゼントアイテム。
/// 使うと Apple・Present・Nothing 以外のランダムなアイテムを
/// プレイヤーの目の前（壁があれば同じマス）にスポーンする。
/// </summary>
public class ITM_Present : Item
{
    // ------------------------------------------------------------------ //
    //  除外リスト: ItemObject.nameKey で指定
    // ------------------------------------------------------------------ //
    private static readonly HashSet<string> ExcludedNameKeys = new HashSet<string>
    {
        "Itm_Apple",    // Apple の nameKey（バニラ）※大文字小文字に注意
        "Present",      // 自分自身
        "None",         // 空スロット
        "Itm_Nothing",  // Nothing アイテム（何も出ない）
    };

    // ベースにする Pickup プレハブ（シーンにある既存の Pickup をキャッシュ）
    private static Pickup _pickupTemplate = null;

    [SerializeField]
    private SoundObject audUse;

    public override bool Use(PlayerManager pm)
    {
        List<ItemObject> candidates = GetCandidates();

        if (candidates.Count == 0)
        {
            MyPlugin.Log.LogWarning("[Present] No candidate items found — keeping Present.");
            return false;
        }

        ItemObject chosen = candidates[Random.Range(0, candidates.Count)];
        MyPlugin.Log.LogInfo($"[Present] Chose: {chosen.name} (nameKey={chosen.nameKey})");

        // スポーン先を決定：目の前のマス → 壁なら自分のマス
        Vector3 spawnPos = GetSpawnPosition(pm);

        // 既存 Pickup をテンプレートとしてクローン
        Pickup template = GetPickupTemplate();
        if (template != null)
        {
            Pickup pickup = Object.Instantiate(template, spawnPos, Quaternion.identity);
            pickup.item = chosen;
            pickup.free = true;
            pickup.gameObject.SetActive(true);
            // Start() が itemSprite.sprite を設定するので Start 前に item を入れておく
            // Start() 未実行なら手動で設定
            if (pickup.itemSprite != null)
                pickup.itemSprite.sprite = chosen.itemSpriteLarge;
            pickup.gameObject.name = "Item_" + chosen.itemType;
            MyPlugin.Log.LogInfo($"[Present] Spawned Pickup at {spawnPos}");
        }
        else
        {
            MyPlugin.Log.LogWarning("[Present] No Pickup template found — cannot spawn.");
            return false;
        }

        if (audUse != null)
            Singleton<CoreGameManager>.Instance.audMan.PlaySingle(audUse);

        Object.Destroy(gameObject);
        return true;
    }

    /// <summary>
    /// シーン上の既存 Pickup を探してテンプレートとして返す。
    /// </summary>
    private static Pickup GetPickupTemplate()
    {
        if (_pickupTemplate != null) return _pickupTemplate;

        // シーン上のすべての Pickup を検索
        foreach (Pickup p in Object.FindObjectsOfType<Pickup>())
        {
            _pickupTemplate = p;
            MyPlugin.Log.LogInfo($"[Present] Pickup template found: {p.gameObject.name}");
            return _pickupTemplate;
        }

        MyPlugin.Log.LogWarning("[Present] No Pickup found in scene.");
        return null;
    }

    /// <summary>
    /// プレイヤーの向いている方向の1マス先を返す。
    /// そのマスが壁なら（または範囲外なら）プレイヤーと同じマスを返す。
    /// </summary>
    private static Vector3 GetSpawnPosition(PlayerManager pm)
    {
        Vector3 origin = pm.transform.position;
        Vector3 forward = pm.transform.forward;

        // BBPのセルサイズ = 10f
        const float cellSize = 10f;
        Vector3 candidate = origin + forward * cellSize;

        // レイキャストで壁チェック
        if (Physics.Raycast(origin, forward, cellSize * 0.9f))
        {
            MyPlugin.Log.LogInfo("[Present] Wall detected — spawning at player position.");
            return origin;
        }

        return candidate;
    }

    // ------------------------------------------------------------------ //
    //  候補アイテムを収集
    // ------------------------------------------------------------------ //
    private static List<ItemObject> GetCandidates()
    {
        var result = new List<ItemObject>();
        foreach (var meta in ItemMetaStorage.Instance.All())
        {
            if (meta == null || meta.itemObjects == null || meta.itemObjects.Length == 0)
                continue;
            ItemObject io = meta.itemObjects[0];
            if (io == null) continue;

            string key = io.nameKey ?? io.name ?? "";
            bool excluded = ExcludedNameKeys.Contains(key);
            MyPlugin.Log.LogInfo($"[Present] item: name={io.name} nameKey={io.nameKey} excluded={excluded}");

            if (excluded) continue;
            result.Add(io);
        }
        MyPlugin.Log.LogInfo($"[Present] candidates total={result.Count}");
        return result;
    }

    // ------------------------------------------------------------------ //
    //  登録
    // ------------------------------------------------------------------ //
    public static void Register()
    {
        Sprite sprite = SpriteHelper.LoadSprite("Present.png");

        // --- ZestyBar から audUse を流用 ---
        SoundObject useSound = null;
        foreach (ItemObject io in Resources.FindObjectsOfTypeAll<ItemObject>())
        {
            if (io.itemType == Items.ZestyBar && io.item != null)
            {
                ITM_ZestyBar zb = io.item as ITM_ZestyBar;
                if (zb != null)
                {
                    useSound = (SoundObject)typeof(ITM_ZestyBar)
                        .GetField("audEat",
                            BindingFlags.NonPublic | BindingFlags.Instance)
                        .GetValue(zb);
                }
                break;
            }
        }

        GameObject prefabGo = new GameObject("Present_Item");
        Object.DontDestroyOnLoad(prefabGo);
        ITM_Present itemComp = prefabGo.AddComponent<ITM_Present>();
        if (useSound != null)
        {
            typeof(ITM_Present)
                .GetField("audUse",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(itemComp, useSound);
        }

        ItemObject item = ScriptableObject.CreateInstance<ItemObject>();
        item.name         = "Present";
        item.itemType     = EnumExtensions.ExtendEnum<Items>("Present");
        item.item         = itemComp;
        item.itemSpriteSmall = sprite;
        item.itemSpriteLarge = sprite;
        item.price        = 350;
        item.addToInventory = true;
        item.nameKey      = "Present";
        item.descKey      = "Present_desc";

        ItemMetaData meta = new ItemMetaData(MyPlugin.Instance.Info, item);
        meta.flags = ItemFlags.None;
        ItemMetaStorage.Instance.Add(meta);

        // ローカライズ登録は LocalizationPatch.cs（LoadLocalizedText の Postfix）側で
        // 一括して行っている。LoadLocalizedText は辞書を丸ごと作り直すため、
        // ここで直接 Add しても意味がない（Start() 実行時に消される）。

        MyPlugin.Log.LogInfo("Present registered.");
    }
}
