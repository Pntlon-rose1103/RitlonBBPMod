using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// ジョニーストア前のラウンジに立つ女性キャラ。BusPass を渡すと、PartyHat の
/// 「Party実行不可」分岐と同じ抽選(MysteryRoomのアイテムプール)・同じPickup生成で
/// アイテムを緑ロッカーの3マス左あたりにスポーンさせる。BusPassは消費される。
/// 見た目スケールは Biten と同じ 3.25 倍。
/// </summary>
public class LoungeGirl : MonoBehaviour, IItemAcceptor, IClickable<int>
{
    const float SCALE = 3.25f;          // Bitenと同じ
    const float CELL = 10f;
    const float SPAWN_LEFT_CELLS = 3f;
    const bool ONLY_IN_EXTREME = false; // trueなら Extreme 中のみ設置

    float spriteBaseY;
    SpriteRenderer girlRenderer;
    readonly System.Collections.Generic.List<SpriteRenderer> sortedItems = new System.Collections.Generic.List<SpriteRenderer>();
    public static LoungeGirl Instance;
    void Awake() { Instance = this; }
    bool quarterOffer, quarterUsed;
    Pickup quarterDecor;
    const float HUG_DISTANCE = 12f; // 「かなり近づいた」距離
    PropagatedAudioManager audMan;
    SoundObject audHug;
    SoundObject audEnd;
    bool playerNear;
    Vector3 spreadDir = Vector3.right;
    Transform sprite;
    Vector3 itemPos;

    public bool ItemFits(Items item) { return item == Items.BusPass; }

    public void InsertItem(PlayerManager pm, EnvironmentController ec) { Give(2); }

    // ---- クリックで渡す(バスの IClickable と同じ方式) ---- //
    public void Clicked(int player)
    {
        PlayerManager pm = Singleton<CoreGameManager>.Instance.GetPlayer(player);
        if (pm == null) return;
        if (pm.itm.Has(Items.BusPass))
        {
            pm.itm.Remove(Items.BusPass);
            Give(2);
        }
        else if (quarterOffer && !quarterUsed && pm.itm.Has(Items.Quarter))
        {
            // ミス数が 2/4/6/8/10 の時だけ・1回限り: Quarterでアイテム交換(1個のみ)
            pm.itm.Remove(Items.Quarter);
            quarterUsed = true;
            Give(1);
            if (quarterDecor != null)
                quarterDecor.transform.position += Vector3.up * 10f; // 交換後は10上へ
            MyPlugin.Log.LogInfo("[LoungeGirl] Quarter exchange done; decor Quarter raised by 10.");
        }
    }
    public void ClickableSighted(int player) { }
    public void ClickableInSight(int player) { }
    public void ClickableUnsighted(int player) { }
    public bool ClickableHidden() { return false; }
    public bool ClickableRequiresNormalHeight() { return false; }

    void Track(bool spawned)
    {
        if (!spawned || ITM_PartyHat.LastSpawned == null) return;
        SpriteRenderer r = ITM_PartyHat.LastSpawned.itemSprite;
        if (r != null) sortedItems.Add(r);
    }

    // 彼女とアイテムの前後関係: カメラに近い方を手前に描く(ソート順で明示的に制御)
    void UpdateItemSorting()
    {
        Camera cam = Camera.main;
        if (cam == null || girlRenderer == null) return;
        float gd = (transform.position - cam.transform.position).sqrMagnitude;
        sortedItems.RemoveAll(r => r == null);
        foreach (SpriteRenderer r in sortedItems)
        {
            float d = (r.transform.position - cam.transform.position).sqrMagnitude;
            r.sortingOrder = d < gd ? 2 : 0;
        }
    }

    void Give(int count)
    {

        // 何回でも受け取り可。1回につき2個スポーン(少しずらして重ならないように)
        bool a = ITM_PartyHat.SpawnPartyItemAt(itemPos + spreadDir * (count >= 2 ? -2f : 0f));
        Track(a);
        bool b = count >= 2 && ITM_PartyHat.SpawnPartyItemAt(itemPos + spreadDir * 2f);
        Track(b);
        if ((a || b) && audMan != null && audEnd != null)
        {
            SpriteHelper.ApplySubtitleColor(audMan, audEnd);
            audMan.PlaySingle(audEnd);
        }
        MyPlugin.Log.LogInfo($"[LoungeGirl] BusPass received — spawned {(a ? 1 : 0) + (b ? 1 : 0)} item(s) at {itemPos}");
    }

    void LateUpdate()
    {
        UpdateHug();
        UpdateItemSorting();
        Camera cam = Camera.main;
        if (cam == null || sprite == null) return;
        Vector3 d = cam.transform.position - transform.position; d.y = 0;
        if (d.sqrMagnitude > 0.01f) sprite.rotation = Quaternion.LookRotation(-d);
        sprite.position = new Vector3(sprite.position.x, spriteBaseY, sprite.position.z);
    }

    // 近づいたら Biten の hug 音を1回再生(離れて再度近づくとまた鳴る)
    void UpdateHug()
    {
        if (audMan == null || audHug == null) return;
        PlayerManager pm = Singleton<CoreGameManager>.Instance != null ? Singleton<CoreGameManager>.Instance.GetPlayer(0) : null;
        if (pm == null) return;
        bool near = (pm.transform.position - transform.position).magnitude < HUG_DISTANCE;
        if (near && !playerNear)
        {
            SpriteHelper.ApplySubtitleColor(audMan, audHug);
            audMan.PlaySingle(audHug);
        }
        playerNear = near;
    }

    // ---------------- 設置 ---------------- //
    public static void PlacePostfix(PitstopGameManager __instance)
    {
        try
        {
            if (ONLY_IN_EXTREME && !ExtremeMode.Active) return;
            if (Object.FindObjectOfType<LoungeGirl>() != null) return;

            EnvironmentController ec = Singleton<BaseGameManager>.Instance.Ec;
            StoreRoomFunction store = Object.FindObjectOfType<StoreRoomFunction>();
            StorageLocker locker = Object.FindObjectOfType<StorageLocker>();
            if (ec == null) return;

            // ロッカーの「左3マス」: ロッカー前に立って向き合った時の左。両側を試し、床がある方を採用
            Vector3 lp = locker != null ? locker.transform.position : Vector3.zero;
            itemPosOut(ec, locker, lp, out Vector3 itemPos);

            // 配置位置: ストアのドア前(ラウンジ側)。取れなければロッカー横
            Vector3 pos = itemPos + (locker != null ? locker.transform.right : Vector3.right) * CELL;
            if (store != null)
            {
                RoomController room = store.GetComponentInParent<RoomController>();
                if (room != null && room.doors != null && room.doors.Count > 0)
                {
                    Door d = room.doors[0];
                    Vector3 outDir = (d.transform.position - room.transform.position); outDir.y = 0;
                    pos = d.transform.position + outDir.normalized * CELL;
                }
            }
            pos.y = 5f;

            GameObject go = new GameObject("LoungeGirl");
            go.transform.position = pos;
            LoungeGirl lg = go.AddComponent<LoungeGirl>();
            lg.itemPos = itemPos;
            if (locker != null) lg.spreadDir = locker.transform.forward;

            GameObject sp = new GameObject("Sprite");
            sp.transform.SetParent(go.transform, false);
            sp.transform.localScale = Vector3.one * SCALE;
            SpriteRenderer sr = sp.AddComponent<SpriteRenderer>();
            sr.sprite = FindBitenSprite() ?? SpriteHelper.LoadSprite("lounge_girl.png");
            sr.sharedMaterial = FindSpriteMaterial();
            lg.sprite = sp.transform;

            // 描画順: アイテム(Pickup)のスプライトと同じキュー/ソート設定に揃え、
            // 透明描画が距離順で正しく前後するようにする(アイテムが彼女を透けて見えるのを防ぐ)
            try
            {
                Pickup pt = ITM_PartyHat.GetPickupTemplate();
                SpriteRenderer ir = pt != null ? pt.itemSprite : null;
                if (ir != null && ir.sharedMaterial != null)
                {
                    Material m = new Material(sr.sharedMaterial);
                    m.renderQueue = ir.sharedMaterial.renderQueue;
                    sr.material = m;
                    sr.sortingLayerID = ir.sortingLayerID;
                    sr.sortingOrder = 1;
                    lg.girlRenderer = sr;
                    MyPlugin.Log.LogInfo($"[LoungeGirl] Render queue matched to pickup: {m.renderQueue} (shader={m.shader.name})");
                }
            }
            catch (System.Exception ex) { MyPlugin.Log.LogWarning("[LoungeGirl] Render order setup failed: " + ex.Message); }

            // hug音: Bitenの audHug を使い回し
            lg.audMan = go.AddComponent<PropagatedAudioManager>();
            lg.audMan.audioDevice = go.AddComponent<AudioSource>();
            lg.audHug = FindBitenHug();
            lg.audEnd = FindBitenEnd();

            // 浮き防止: スプライト下端が床(y=0)に接するように高さを補正
            float bottomGap = sr.bounds.min.y;
            sp.transform.position -= new Vector3(0f, bottomGap, 0f);
            lg.spriteBaseY = sp.transform.position.y;

            // クリック判定用コライダー(ITM_Acceptableのレイキャストが拾うレイヤー)
            int layer = PickClickLayer();
            if (layer >= 0) go.layer = layer;
            CapsuleCollider cap = go.AddComponent<CapsuleCollider>();
            cap.radius = 2f; cap.height = 10f; cap.isTrigger = true;

            SpawnDecor(pos + Vector3.up * 3f, Items.BusPass, -3f, "BusPass");
            // ミス数(Attempts)が 2/4/6/8/10 の時だけ、BusPassの隣にQuarterを飾る。Extremeでは特例で不可。
            int misses = Singleton<CoreGameManager>.Instance.Attempts; // ミス数（やり直した回数）
            lg.quarterOffer = !ExtremeMode.Active && (misses == 2 || misses == 4 || misses == 6 || misses == 8 || misses == 10);
            if (lg.quarterOffer)
                lg.quarterDecor = SpawnDecor(pos + Vector3.up * 3f, Items.Quarter, -6f, "Quarter");

            MyPlugin.Log.LogInfo($"[LoungeGirl] Placed at {pos}, item spawn {itemPos}, layer={layer}");
        }
        catch (System.Exception ex)
        {
            MyPlugin.Log.LogError("[LoungeGirl] Place failed: " + ex);
        }
    }

    // 彼女の頭上に、取れない高さ(コライダー無効)で BusPass を飾る
    const float DECOR_HEIGHT = 8f; // 天井(約10)の手前 = 見えるが、コライダー無効で取れない
    static Pickup SpawnDecor(Vector3 near, Items itemEnum, float sideMul, string label)
    {
        try
        {
            ItemMetaData meta = MTM101BaldAPI.Registers.ItemMetaStorage.Instance.FindByEnum(itemEnum);
            Pickup template = ITM_PartyHat.GetPickupTemplate();
            if (meta == null || template == null) return null;
            Vector3 side = Vector3.right; Camera cam = Camera.main; if (cam != null) side = cam.transform.right; side.y = 0; side.Normalize();
            Vector3 p = new Vector3(near.x, DECOR_HEIGHT, near.z) + side * sideMul; // 少し横にずらして彼女と重ならないように
            Pickup bp = Object.Instantiate(template, p, Quaternion.identity);
            bp.item = meta.value;
            bp.free = true;
            bp.gameObject.SetActive(true);
            if (bp.itemSprite != null) bp.itemSprite.sprite = meta.value.itemSpriteLarge;
            bp.gameObject.name = "Item_" + label + "_Unreachable";
            if (Instance != null && bp.itemSprite != null) Instance.sortedItems.Add(bp.itemSprite);
            foreach (Collider c in bp.GetComponentsInChildren<Collider>(true)) c.enabled = false; // 絶対に取れない
            MyPlugin.Log.LogInfo($"[LoungeGirl] Unreachable {label} placed at {p}");
            return bp;
        }
        catch (System.Exception ex) { MyPlugin.Log.LogWarning("[LoungeGirl] Decor " + label + " failed: " + ex.Message); }
        return null;
    }

    static void itemPosOut(EnvironmentController ec, StorageLocker locker, Vector3 lp, out Vector3 result)
    {
        result = lp; // 失敗時はロッカー位置
        if (locker == null) return;
        Vector3[] cands = { -locker.transform.right, locker.transform.right };
        foreach (Vector3 c in cands)
        {
            Vector3 p = lp + c * CELL * SPAWN_LEFT_CELLS; p.y = 5f;
            Cell cell = ec.CellFromPosition(p);
            if (cell != null && !cell.Null) { result = p; return; }
        }
        result = lp + (-locker.transform.right) * CELL; result.y = 5f; // 範囲外なら1マス左
    }

    // Bitenの画像を使い回す(登録済みNPCプレハブから取得)
    static Sprite FindBitenSprite()
    {
        try
        {
            Character c = MTM101BaldAPI.EnumExtensions.GetFromExtendedName<Character>("Biten");
            var meta = MTM101BaldAPI.Registers.NPCMetaStorage.Instance.Get(c);
            if (meta != null && meta.value.spriteRenderer.Length > 0) return meta.value.spriteRenderer[0].sprite;
        }
        catch (System.Exception ex) { MyPlugin.Log.LogWarning("[LoungeGirl] Biten sprite lookup failed: " + ex.Message); }
        return null;
    }

    // プレイヤーのクリックレイキャストが拾うレイヤー(PlayerClick.ClickLayers)から選ぶ
    static int PickClickLayer()
    {
        try
        {
            PlayerManager pm = Singleton<CoreGameManager>.Instance.GetPlayer(0);
            object v = pm.pc.ClickLayers;
            int bits = v is LayerMaskObject lmo ? (int)lmo.mask : (v is LayerMask lm ? lm.value : 0);
            int first = -1;
            for (int i = 0; i < 32; i++)
            {
                if ((bits & (1 << i)) == 0) continue;
                string n = LayerMask.LayerToName(i);
                if (first < 0) first = i;
                if (n != null && n.Contains("Click")) return i;
            }
            return first;
        }
        catch (System.Exception ex) { MyPlugin.Log.LogWarning("[LoungeGirl] Click layer lookup failed: " + ex.Message); }
        return LayerMask.NameToLayer("ClickableEntities");
    }

    static SoundObject FindBitenEnd()
    {
        try
        {
            Character c = MTM101BaldAPI.EnumExtensions.GetFromExtendedName<Character>("Biten");
            var meta = MTM101BaldAPI.Registers.NPCMetaStorage.Instance.Get(c);
            Biten b = meta != null ? meta.value as Biten : null;
            if (b != null) return b.audEnd;
        }
        catch (System.Exception ex) { MyPlugin.Log.LogWarning("[LoungeGirl] Biten end lookup failed: " + ex.Message); }
        return null;
    }

    static SoundObject FindBitenHug()
    {
        try
        {
            Character c = MTM101BaldAPI.EnumExtensions.GetFromExtendedName<Character>("Biten");
            var meta = MTM101BaldAPI.Registers.NPCMetaStorage.Instance.Get(c);
            Biten b = meta != null ? meta.value as Biten : null;
            if (b != null) return b.audHug;
        }
        catch (System.Exception ex) { MyPlugin.Log.LogWarning("[LoungeGirl] Biten hug lookup failed: " + ex.Message); }
        return null;
    }

    static Material FindSpriteMaterial()
    {
        try
        {
            foreach (NPC n in Resources.FindObjectsOfTypeAll<NPC>())
                if (n.spriteRenderer != null && n.spriteRenderer.Length > 0 && n.spriteRenderer[0].sharedMaterial != null)
                    return n.spriteRenderer[0].sharedMaterial;
        }
        catch { }
        return new Material(Shader.Find("Sprites/Default"));
    }

    public static void Install(Harmony harmony)
    {
        var t = AccessTools.Method(typeof(PitstopGameManager), "BeginPlay");
        if (t == null) { MyPlugin.Log.LogWarning("[LoungeGirl] PitstopGameManager.BeginPlay not found"); return; }
        harmony.Patch(t, postfix: new HarmonyMethod(typeof(LoungeGirl), nameof(PlacePostfix)));
        MyPlugin.Log.LogInfo("[LoungeGirl] Patched PitstopGameManager.BeginPlay");
    }
}
