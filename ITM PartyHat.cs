using System.Reflection;
using System.Collections.Generic;
using MTM101BaldAPI;
using MTM101BaldAPI.Registers;
using UnityEngine;

/// <summary>
/// パーティハット。使うとバニラの「Party」ランダムイベント(PartyEvent)を
/// その場で手動開始する。
///
/// 通常 PartyEvent はレベル開始時に EnvironmentController.AddEvent() で
/// 事前スケジュールされ、EnvironmentController の非公開コルーチン
/// EventTimer() が時間経過後に Initialize()→Begin() を呼び、
/// currentEvents / currentEventTypes（非公開リスト）に登録する。
/// アイテム使用時に即座に発生させるため、このクラスでは
/// EventTimer() の末尾の処理を手動で再現している：
///   1. シーン上の既存 PartyEvent をテンプレートとして複製
///   2. Initialize(ec, rng) → Begin()
///   3. 非公開の currentEvents / currentEventTypes にリフレクションで追加
///      （EventOver() が正しく動作し、GetEvent(RandomEventType.Party) が
///       他システムから見ても「進行中」と正しく認識されるようにするため）
///
/// Partyイベントには本来オフィス(office)部屋が必要。フロアによっては
/// オフィス部屋が1つも存在せず、本物のPartyイベントを開始できない場合がある。
/// その場合は、シーン上の MysteryRoom が持つアイテム抽選プール（items:
/// WeightedItemObject[]、Mystery Room内で排出されるアイテム一覧）から
/// 重み付き抽選で1つ選び、ITM_Present と同じ要領でプレイヤーの前に
/// 直接スポーンする処理に分岐する（部屋を代用してイベントを無理に
/// 発生させることはしない）。
///
/// 使用音は無し（即座に使い切る）。
/// </summary>
public class ITM_PartyHat : Item
{
    private static readonly FieldInfo fi_currentEvents = typeof(EnvironmentController)
        .GetField("currentEvents", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo fi_currentEventTypes = typeof(EnvironmentController)
        .GetField("currentEventTypes", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo fi_audMan = typeof(EnvironmentController)
        .GetField("audMan", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo fi_audEventNotification = typeof(EnvironmentController)
        .GetField("audEventNotification", BindingFlags.NonPublic | BindingFlags.Instance);

    // Partyイベントが開始できない場合の代替スポーン用：
    // MysteryRoom（Mystery Room の RandomEvent 実装）の非公開フィールド
    // "items"（WeightedItemObject[]、部屋内で排出されるアイテムの抽選プール）
    private static readonly FieldInfo fi_mysteryItems = typeof(MysteryRoom)
        .GetField("items", BindingFlags.NonPublic | BindingFlags.Instance);

    private static Pickup _pickupTemplate = null;

    public override bool Use(PlayerManager pm)
    {
        EnvironmentController ec = pm.ec;

        // 既にPartyイベントが進行中なら二重発生させない
        if (ec.GetEvent(RandomEventType.Party) != null)
        {
            MyPlugin.Log.LogInfo("[PartyHat] Party event already active — not using item.");
            return false;
        }

        // Partyイベントにはオフィス(office)部屋が必要。
        // オフィスが存在しないフロアでは本物のPartyイベントを発生させられないため、
        // その場合はPartyイベントで本来スポーンするアイテムを、Presentと同じ要領で
        // 直接スポーンする処理に分岐する。
        if (ec.offices == null || ec.offices.Count == 0)
        {
            MyPlugin.Log.LogInfo("[PartyHat] No office room on this floor — spawning a party item instead of starting a real Party event.");
            return SpawnPartyItem(pm);
        }

        // シーン上に実在する PartyEvent をテンプレートとして探す
        PartyEvent template = null;
        foreach (PartyEvent pe in Resources.FindObjectsOfTypeAll<PartyEvent>())
        {
            string sceneName = pe.gameObject.scene.name;
            if (!string.IsNullOrEmpty(sceneName))
            {
                template = pe;
                break;
            }
        }

        if (template == null)
        {
            MyPlugin.Log.LogWarning("[PartyHat] No PartyEvent template found in scene — spawning a party item instead.");
            return SpawnPartyItem(pm);
        }

        PartyEvent partyEvent = Object.Instantiate(template, ec.transform);
        System.Random rng = new System.Random();
        partyEvent.Initialize(ec, rng);

        // 非公開リストに登録し、他システムからも「進行中イベント」として認識させる
        try
        {
            if (fi_currentEvents?.GetValue(ec) is List<RandomEvent> currentEvents)
                currentEvents.Add(partyEvent);
            if (fi_currentEventTypes?.GetValue(ec) is List<RandomEventType> currentEventTypes)
                currentEventTypes.Add(partyEvent.Type);
        }
        catch (System.Exception ex)
        {
            MyPlugin.Log.LogWarning($"[PartyHat] Failed to register event in currentEvents: {ex.Message}");
        }

        // バニラのイベント開始演出（ジングル＋HUDアナウンス）を再現
        try
        {
            AudioManager audMan = fi_audMan?.GetValue(ec) as AudioManager;
            SoundObject notifSound = fi_audEventNotification?.GetValue(ec) as SoundObject;
            if (audMan != null)
            {
                audMan.PlaySingle(partyEvent.EventJingleOverride != null ? partyEvent.EventJingleOverride : notifSound);
            }
            Singleton<CoreGameManager>.Instance.GetHud(0).BaldiTv.AnnounceEvent(partyEvent.EventIntro);
        }
        catch (System.Exception ex)
        {
            MyPlugin.Log.LogWarning($"[PartyHat] Failed to play event announcement: {ex.Message}");
        }

        partyEvent.Begin();

        MyPlugin.Log.LogInfo("[PartyHat] Party event started!");

        Object.Destroy(gameObject);
        return true;
    }

    // ------------------------------------------------------------------ //
    //  Partyイベントが開始できない場合の代替処理：
    //  Mystery Room のアイテム抽選プール（items）から重み付き抽選で1つ選び、
    //  プレイヤーの目の前（壁があれば同じマス）にスポーンする（Presentと同じ方式）。
    // ------------------------------------------------------------------ //
    private bool SpawnPartyItem(PlayerManager pm)
    {
        if (!SpawnPartyItemAt(GetSpawnPosition(pm))) return false;
        Object.Destroy(gameObject);
        return true;
    }

    /// <summary>Party実行不可分岐と同じ抽選・同じPickup生成で、指定位置にアイテムをスポーンする。</summary>
    internal static Pickup LastSpawned;
    internal static bool SpawnPartyItemAt(Vector3 spawnPos)
    {
        ItemObject chosen = PickWeightedMysteryRoomItem();

        if (chosen == null)
        {
            MyPlugin.Log.LogWarning("[PartyHat] No Mystery Room item available.");
            return false;
        }

        MyPlugin.Log.LogInfo($"[PartyHat] Chose Mystery Room item: {chosen.name} (nameKey={chosen.nameKey})");

        Pickup template = GetPickupTemplate();
        if (template == null)
        {
            MyPlugin.Log.LogWarning("[PartyHat] No Pickup template found — cannot spawn party item.");
            return false;
        }

        Pickup pickup = Object.Instantiate(template, spawnPos, Quaternion.identity);
        pickup.item = chosen;
        pickup.free = true;
        pickup.gameObject.SetActive(true);
        if (pickup.itemSprite != null)
            pickup.itemSprite.sprite = chosen.itemSpriteLarge;
        pickup.gameObject.name = "Item_" + chosen.itemType;
        LastSpawned = pickup;
        MyPlugin.Log.LogInfo($"[PartyHat] Spawned Mystery Room item Pickup at {spawnPos}");
        return true;
    }

    /// <summary>
    /// シーン上に存在する MysteryRoom インスタンスから items（アイテム抽選プール）を
    /// 取得し、重み付き抽選で1つの ItemObject を返す。見つからない場合は null。
    /// </summary>
    internal static ItemObject PickWeightedMysteryRoomItem()
    {
        MysteryRoom template = null;
        foreach (MysteryRoom mr in Resources.FindObjectsOfTypeAll<MysteryRoom>())
        {
            template = mr;
            break;
        }

        if (template == null)
        {
            MyPlugin.Log.LogWarning("[PartyHat] No MysteryRoom instance found in scene.");
            return null;
        }

        WeightedItemObject[] items = fi_mysteryItems?.GetValue(template) as WeightedItemObject[];
        if (items == null || items.Length == 0)
        {
            MyPlugin.Log.LogWarning("[PartyHat] MysteryRoom item pool (items) is empty or inaccessible.");
            return null;
        }

        int totalWeight = 0;
        foreach (var w in items)
        {
            if (w.selection == null) continue;
            totalWeight += Mathf.Max(0, w.weight);
        }
        if (totalWeight <= 0)
        {
            MyPlugin.Log.LogWarning("[PartyHat] MysteryRoom item pool has no valid weighted entries.");
            return null;
        }

        int roll = Random.Range(0, totalWeight);
        int cumulative = 0;
        foreach (var w in items)
        {
            if (w.selection == null) continue;
            cumulative += Mathf.Max(0, w.weight);
            if (roll < cumulative) return w.selection;
        }

        return null;
    }

    /// <summary>
    /// シーン上の既存 Pickup を探してテンプレートとして返す。
    /// </summary>
    internal static Pickup GetPickupTemplate()
    {
        if (_pickupTemplate != null) return _pickupTemplate;

        foreach (Pickup p in Object.FindObjectsOfType<Pickup>())
        {
            _pickupTemplate = p;
            MyPlugin.Log.LogInfo($"[PartyHat] Pickup template found: {p.gameObject.name}");
            return _pickupTemplate;
        }

        MyPlugin.Log.LogWarning("[PartyHat] No Pickup found in scene.");
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

        if (Physics.Raycast(origin, forward, cellSize * 0.9f))
        {
            MyPlugin.Log.LogInfo("[PartyHat] Wall detected — spawning at player position.");
            return origin;
        }

        return candidate;
    }

    public static void Register()
    {
        Sprite sprite = SpriteHelper.LoadSprite("PartyHat.png");

        GameObject prefabGo = new GameObject("PartyHat_Item");
        Object.DontDestroyOnLoad(prefabGo);
        ITM_PartyHat itemComp = prefabGo.AddComponent<ITM_PartyHat>();

        ItemObject item = ScriptableObject.CreateInstance<ItemObject>();
        item.name            = "PartyHat";
        item.itemType        = EnumExtensions.ExtendEnum<Items>("PartyHat");
        item.item            = itemComp;
        item.itemSpriteSmall = sprite;
        item.itemSpriteLarge = sprite;
        item.price           = 300;
        item.addToInventory  = true;
        item.nameKey         = "PartyHat";
        item.descKey         = "PartyHat_desc";

        ItemMetaData meta = new ItemMetaData(MyPlugin.Instance.Info, item);
        meta.flags = ItemFlags.None;
        ItemMetaStorage.Instance.Add(meta);

        // ローカライズ登録は LocalizationPatch.cs 側で一括して行っている。

        MyPlugin.Log.LogInfo("PartyHat registered.");
    }
}
