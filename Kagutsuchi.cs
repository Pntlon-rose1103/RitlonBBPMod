using HarmonyLib;
using MTM101BaldAPI;
using MTM101BaldAPI.ObjectCreation;
using MTM101BaldAPI.Registers;
using System.Collections.Generic;
using UnityEngine;

// =====================================================================
// Kagutsuchi（カグツチ）
//   ・人見知り: プレイヤーを見つけると逃げる（バニラの Beans と同じ DijkstraMap 方式）
//   ・他の NPC と接触すると、その場に炎（FireWall）を置く
//       （炎が消えるまで、同じマスには新しい炎を置かない）
//   ・ドア／窓のそばを通ると、燃やして消失させる（エレベーターは炎上して入れなくなる）
//   ・プレイヤーが接触する（Anubis と同じ判定）と、30 分間、学校内に炎が発生しなくなる
//
//   炎の中身は FireWall.cs を参照（Minto と共通。Kagutsuchi 自身は炎で弾かれない）。
// =====================================================================
public class Kagutsuchi : NPC
{
    // ---- 調整用の定数 ----
    public const float WANDER_SPEED = 26.25f;     // 通常時の速度（Anubis と同じ: プレイヤー歩行速度 25 × 1.05）
    public const float FLEE_SPEED = 26.25f;       // 逃走時の速度（Anubis と同じ）
    public const float FLEE_RAMP_TIME = 5f;       // 逃げ始めの速度（プレイヤーのダッシュ）から FLEE_SPEED まで加速する秒数
    public const float FLEE_DECEL_TIME = 5f;      // 逃げ始めの速度（プレイヤーのダッシュと同速）から FLEE_SPEED まで落とす秒数
    public const float FLEE_TIME = 10f;           // プレイヤーに見えなくなってから逃げ続ける秒数
    public const float TOUCH_DISTANCE = 3f;       // 接触判定の距離（Anubis と同じ）
    public const float SUPPRESS_SECONDS = 1800f;   // 接触後に炎が出なくなる時間
    public const bool SUPPRESS_MINTO_FIRE = true; // 消火中は Minto の炎も出なくする（false なら Kagutsuchi の炎だけ止める）

    public const float NPC_TOUCH_DISTANCE = 5f;   // 他の NPC にこの距離まで近づいたら「接触」
    public const float NPC_CONTACT_COOLDOWN = 20f; // 同じ NPC との接触で再び炎を置けるまでの秒数
    public const float FIRE_LIFETIME = 20f;       // 炎が残る時間（＝同じマスで再点火できるまでの時間）
    public const bool FIRE_PUSHES = true;         // 炎に弾き飛ばし効果を付けるか（false なら見た目だけ）
    public const float TICK = 0.1f;               // 炎・ドアの判定間隔（秒）

    public const float BURN_RANGE = 8f;           // ドアの中心からこの距離以内に入ると燃やす
    public const float ELEVATOR_RANGE = 28f;      // エレベーターの扉からこの距離以内に入ると燃やす（ドアより広め）
    public const float WINDOW_RANGE = 8f;         // 窓は壁の中にあるので、廊下の中央を通っても届くよう広めにする
    public const float SCALE = 3.25f;             // 見た目の大きさ（Biten と同じ）

    // 消火中の見た目（スプライトの色）
    static readonly Color SUPPRESSED_TINT = new Color(0.6f, 0.75f, 1f, 1f);

    // ---- 全インスタンス共通 ---- //
    static float suppressEnd = 0f;
    public static bool FireSuppressed { get { return Time.time < suppressEnd; } }

    // マスごとの炎（炎が消えるまで同じマスには出さない）
    static readonly Dictionary<long, FireWall> fires = new Dictionary<long, FireWall>();
    // 既に燃やしたドア／窓
    static readonly HashSet<Door> burnt = new HashSet<Door>();

    float tickTimer = 0f;
    float doorRefreshTimer = 0f;
    bool tinted = false;
    readonly List<DoorEntry> doorCache = new List<DoorEntry>();

    struct DoorEntry { public Door door; public Vector3 center; }

    public override void Initialize()
    {
        base.Initialize();
        suppressEnd = 0f;
        fires.Clear();
        burnt.Clear();
        ElevatorBurner.Reset();
        DoorBurner.ResetRecords();
        SetMoveSpeed(WANDER_SPEED);
        behaviorStateMachine.ChangeState(new KagutsuchiState_Wander(this));
        TryAddMapIcon();
    }

    // ── マップにカグツチの位置を矢印で表示 ─────────────────────────────
    // プリンシパルと同じ Map.AddArrow を使う（向いている向きに矢印が回る）。
    // 矢印の絵だけ Icon_KaguArrow.png に差し替える。
    bool mapArrowAdded = false;
    int mapIconTries = 0;
    static Sprite mapSprite;

    void TryAddMapIcon()
    {
        if (mapArrowAdded || ec == null || ec.map == null) return;
        try
        {
            Entity ent = (Navigator != null) ? Navigator.Entity : GetComponent<Entity>();
            if (ent == null) return;

            ec.map.AddArrow(ent, Color.white);
            mapArrowAdded = true;

            // いま追加された矢印（リストの最後）の絵を差し替える
            var fiArrows = AccessTools.Field(typeof(Map), "arrows");
            var fiPre = AccessTools.Field(typeof(Map), "arrowPre");
            List<MapIcon> arrows = fiArrows != null ? fiArrows.GetValue(ec.map) as List<MapIcon> : null;
            MapIcon pre = fiPre != null ? fiPre.GetValue(ec.map) as MapIcon : null;

            if (mapSprite == null)
            {
                Texture2D tex = SpriteHelper.LoadTexture("Icon_KaguArrow.png");
                if (tex == null) { MyPlugin.Log.LogWarning("[Kagutsuchi] Icon_KaguArrow.png not found; using default arrow."); return; }
                tex.filterMode = FilterMode.Point;
                // バニラの矢印と同じ大きさ基準
                float ppu = (pre != null && pre.spriteRenderer != null && pre.spriteRenderer.sprite != null) ? pre.spriteRenderer.sprite.pixelsPerUnit : 16f;
                mapSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), ppu);
            }

            if (arrows != null && arrows.Count > 0)
            {
                MapIcon icon = arrows[arrows.Count - 1];
                if (icon != null && icon.spriteRenderer != null)
                    icon.spriteRenderer.sprite = mapSprite;
            }
            MyPlugin.Log.LogInfo("[Kagutsuchi] Map arrow added.");
        }
        catch (System.Exception ex)
        {
            MyPlugin.Log.LogError("[Kagutsuchi] Map arrow failed: " + ex.Message);
        }
    }

    public bool IsSquished
    {
        get
        {
            Entity ent = GetComponent<Entity>();
            return ent != null && ent.Squished;
        }
    }

    public void SetMoveSpeed(float speed)
    {
        navigator.SetSpeed(speed);
        navigator.maxSpeed = speed;
    }

    // ── 毎フレーム: 炎・ドアの処理（TICK ごと） ───────────────────────
    private void LateUpdate()
    {
        UpdateTint();
        if (!placed) TryPlaceAtElevator();
        if (!mapArrowAdded && mapIconTries < 300) { mapIconTries++; TryAddMapIcon(); }

        tickTimer += Time.deltaTime * TimeScale;
        if (tickTimer < TICK) return;
        tickTimer = 0f;

        if (IsSquished) return;

        // 消火モード中: 通りがかりに、燃やしたドア／窓を元に戻す
        if (FireSuppressed)
        {
            try { RestoreNearbyDoors(); }
            catch (System.Exception ex) { MyPlugin.Log.LogError("[Kagutsuchi] RestoreNearbyDoors failed: " + ex.Message); }
            return;
        }

        try { FireOnNpcContact(); }
        catch (System.Exception ex) { MyPlugin.Log.LogError("[Kagutsuchi] FireOnNpcContact failed: " + ex.Message); }
        try { BurnNearbyDoors(); }
        catch (System.Exception ex) { MyPlugin.Log.LogError("[Kagutsuchi] BurnNearbyDoors failed: " + ex.Message); }
    }

    // 消火中だけ体の色を変える（見た目で分かるように）
    void UpdateTint()
    {
        bool want = FireSuppressed;
        if (want == tinted) return;
        tinted = want;
        if (spriteRenderer != null && spriteRenderer.Length > 0 && spriteRenderer[0] != null)
            spriteRenderer[0].color = want ? SUPPRESSED_TINT : Color.white;
    }

    // ── 出現: 開始エレベーターの中（プレイヤーの初期位置と同じ）から始まる ──
    bool placed = false;
    int placeTries = 0;
    float noTouchUntil = 0f;
    public const float SPAWN_GRACE = 8f;          // 出現直後は、重なっていても接触扱いにしない秒数

    void TryPlaceAtElevator()
    {
        Elevator spawnEl = null;
        foreach (Elevator el in FindObjectsOfType<Elevator>())
            if (el != null && el.IsSpawn) { spawnEl = el; break; }

        if (spawnEl == null)
        {
            if (++placeTries > 120) placed = true; // 開始エレベーターが無いレベルは諦める
            return;
        }
        placed = true;

        Vector3 pos = ElevatorBurner.GetInsidePosition(spawnEl);

        // プレイヤーが開始エレベーター内にいれば、その位置にぴったり合わせる
        if (ec != null && ec.Players != null)
        {
            foreach (PlayerManager pm in ec.Players)
            {
                if (pm == null) continue;
                Vector3 d = pm.transform.position - pos;
                d.y = 0f;
                if (d.magnitude < 15f) { pos = pm.transform.position; break; }
            }
        }
        pos.y = transform.position.y;

        Entity ent = GetComponent<Entity>();
        if (ent != null) ent.Teleport(pos);
        else transform.position = pos;
        noTouchUntil = Time.time + SPAWN_GRACE;
        MyPlugin.Log.LogInfo("[Kagutsuchi] Spawned inside start elevator " + pos);
    }

    // ── 他の NPC と接触したら、その場に炎を置く ───────────────────────
    readonly Dictionary<NPC, float> npcCooldown = new Dictionary<NPC, float>();

    void FireOnNpcContact()
    {
        if (ec == null) return;
        foreach (NPC other in ec.Npcs)
        {
            if (other == null || other == this || other is Kagutsuchi) continue;
            if (!other.gameObject.activeInHierarchy) continue;

            float until;
            if (npcCooldown.TryGetValue(other, out until) && Time.time < until) continue;

            Vector3 d = other.transform.position - transform.position;
            d.y = 0f;
            if (d.magnitude > NPC_TOUCH_DISTANCE) continue;

            npcCooldown[other] = Time.time + NPC_CONTACT_COOLDOWN;
            PlaceFire((transform.position + other.transform.position) * 0.5f);
        }
    }

    // ── 指定位置のマスに炎を置く ──────────────────────────────────────
    void PlaceFire(Vector3 at)
    {
        if (ec == null) return;
        if (ec.GetEvent(RandomEventType.Flood) != null) return; // 洪水中は燃えない

        Cell cell = ec.CellFromPosition(at);
        if (cell == null || cell.Null) return;

        Vector3 fp = cell.FloorWorldPosition;
        long key = (long)Mathf.RoundToInt(fp.x / 10f) * 100000L + Mathf.RoundToInt(fp.z / 10f);

        // 炎が残っている間は、そのマスに新しい炎を出さない（クールタイム = 炎が残る時間）
        FireWall existing;
        if (fires.TryGetValue(key, out existing) && existing != null) return;

        SpriteRenderer template = (spriteRenderer != null && spriteRenderer.Length > 0) ? spriteRenderer[0] : null;
        fires[key] = FireWall.Spawn(ec, new Vector3(fp.x, transform.position.y, fp.z), template, FIRE_LIFETIME, FIRE_PUSHES);
    }

    // ── ドア／窓を燃やす ─────────────────────────────────────────────
    void BurnNearbyDoors()
    {
        if (ec == null) return;

        doorRefreshTimer -= TICK;
        if (doorRefreshTimer <= 0f)
        {
            doorRefreshTimer = 2f;
            RefreshDoorCache();
        }

        Vector3 me = transform.position;
        for (int i = doorCache.Count - 1; i >= 0; i--)
        {
            DoorEntry e = doorCache[i];
            if (e.door == null) { doorCache.RemoveAt(i); continue; }
            if (burnt.Contains(e.door)) { doorCache.RemoveAt(i); continue; }

            Vector3 d = e.center - me;
            d.y = 0f;
            if (d.magnitude > (e.door is Window ? WINDOW_RANGE : (e.door is ElevatorDoor ? ELEVATOR_RANGE : BURN_RANGE))) continue;

            // エレベーターは消えずに「炎上」する（消火されたらまた燃やせる）
            if (e.door is ElevatorDoor)
            {
                SpriteRenderer tpl = (spriteRenderer != null && spriteRenderer.Length > 0) ? spriteRenderer[0] : null;
                ElevatorBurner.Ignite(ec, e.door, e.center, transform.position.y, tpl);
                continue;
            }

            DoorBurner.Burn(e.door);
            burnt.Add(e.door);
            doorCache.RemoveAt(i);
        }
    }

    // 消火モード中に近くを通った、燃えたドア／窓を元に戻す
    void RestoreNearbyDoors()
    {
        if (burnt.Count == 0) return;
        Vector3 me = transform.position;
        foreach (Door door in new List<Door>(burnt))
        {
            if (door == null) { burnt.Remove(door); continue; }
            Vector3 center;
            if (!DoorBurner.TryGetCenter(door, out center)) continue;
            Vector3 d = center - me;
            d.y = 0f;
            if (d.magnitude > (door is Window ? WINDOW_RANGE : BURN_RANGE)) continue;

            DoorBurner.Restore(door);
            burnt.Remove(door); // 消火モードが終わったら、また燃やせる
        }
    }

    void RefreshDoorCache()
    {
        doorCache.Clear();
        foreach (Door d in FindObjectsOfType<Door>())
        {
            if (d == null || burnt.Contains(d)) continue;
            // 普通のドア・スイングドア・窓・エレベーターのドア（ロックダウンのドアなどは対象外）
            if (!(d is StandardDoor) && !(d is SwingDoor) && !(d is Window) && !(d is ElevatorDoor)) continue;
            Vector3 center;
            if (!DoorBurner.TryGetCenter(d, out center)) continue;
            doorCache.Add(new DoorEntry { door = d, center = center });
        }
    }

    // 消火の水音（FloodEvent の audWater）を二重に鳴らす
    //   1つ目: プレイヤー用のオーディオマネージャー（距離に関係なく聞こえる）
    //   2つ目: 一時的な PropagatedAudioManager（別の音源なので、重なって鳴る）
    static void PlayUnderwaterSound()
    {
        try
        {
            SoundObject water = FireWall.GetWaterSound();
            if (water == null || Singleton<CoreGameManager>.Instance == null) return;

            Singleton<CoreGameManager>.Instance.audMan.PlaySingle(water);

            PlayerManager pm = Singleton<CoreGameManager>.Instance.GetPlayer(0);
            Vector3 pos = pm != null ? pm.transform.position : Vector3.zero;

            GameObject go = new GameObject("KagutsuchiWater");
            go.SetActive(false);
            go.transform.position = pos;
            AudioSource src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            PropagatedAudioManager pam = go.AddComponent<PropagatedAudioManager>();
            pam.audioDevice = src;
            go.SetActive(true);
            pam.PlaySingle(water);
            Object.Destroy(go, 8f);
        }
        catch (System.Exception ex) { MyPlugin.Log.LogWarning("[Kagutsuchi] water sound failed: " + ex.Message); }
    }

    public static bool IsBurnt(Door d) { return burnt.Contains(d); }

    public const float AHEAD_MAX = 45f;           // 進行方向のこの距離以内にあるドアを対象にする
    public const float AHEAD_ANGLE = 35f;         // 進行方向からこの角度以内にあるドアだけ

    // 逃げている現在の進行方向（dir）の先にある普通のドア（StandardDoor）を探す。
    // プレイヤーより自分の方が近いもので、いちばん近いもの。無ければ null
    public Door FindDoorAhead(Vector3 dir, Vector3 playerPos, HashSet<Door> visited, out Vector3 center)
    {
        center = Vector3.zero;
        Door best = null; float bestD = float.MaxValue;
        foreach (StandardDoor d in FindObjectsOfType<StandardDoor>())
        {
            if (d == null || burnt.Contains(d) || visited.Contains(d)) continue;
            Vector3 c;
            if (!DoorBurner.TryGetCenter(d, out c)) continue;
            Vector3 to = c - transform.position; to.y = 0f;
            float dist = to.magnitude;
            if (dist < BURN_RANGE || dist > AHEAD_MAX) continue;          // 近すぎるものは通常の判定で燃える
            if (Vector3.Angle(dir, to) > AHEAD_ANGLE) continue;            // 進行方向から外れたものは無視
            Vector3 pl = c - playerPos; pl.y = 0f;
            if (dist >= pl.magnitude) continue;                            // プレイヤーの方が近いドアには向かわない
            if (dist < bestD) { bestD = dist; best = d; center = c; }
        }
        return best;
    }

    // ── 接触（Anubis と同じ: 距離 or トリガー） ────────────────────────
    public void TryTouch(PlayerManager pm)
    {
        if (pm == null) return;
        if (IsSquished) return;
        if (SpriteHelper.HasNametag(pm)) return;
        if (FireSuppressed) return; // 消火中の再接触は無視（延長しない）
        if (Time.time < noTouchUntil) return; // 出現直後は重なっていても無視

        suppressEnd = Time.time + SUPPRESS_SECONDS;

        // 接触時点で、学校内の炎（Minto の炎も含む）をすべて消す
        int cleared = 0;
        foreach (FireWall fw in Object.FindObjectsOfType<FireWall>())
        {
            if (fw == null) continue;
            Object.Destroy(fw.gameObject);
            cleared++;
        }
        fires.Clear();
        PlayUnderwaterSound(); // 消火した瞬間だけ（消火後の再接触はここに来ない）
        MyPlugin.Log.LogInfo($"[Kagutsuchi] Touched by player. Cleared {cleared} fires. No fire for {SUPPRESS_SECONDS}s.");
    }

    public static void Register()
    {
        Texture2D poster = SpriteHelper.LoadTexture("kagutsuchi_poster.png");

        Kagutsuchi npc = new NPCBuilder<Kagutsuchi>(MyPlugin.Instance.Info)
            .SetName("Kagutsuchi")
            .SetEnum("Kagutsuchi")
            .SetMetaName("Kagutsuchi")
            .SetPoster(
                poster,
                "Kagutsuchi",
                "A shy master of flames.\nMaybe you can make\nher stop the burning?")
            .AddTrigger()
            .AddLooker()
            .AddSpawnableRoomCategories(RoomCategory.Hall)
            .Build();

        Sprite sprite = SpriteHelper.LoadSprite("kagutsuchi.png");
        npc.spriteRenderer[0].sprite = sprite;
        EditorSprites.Register("Kagutsuchi", sprite);
        npc.spriteRenderer[0].transform.localScale = new Vector3(SCALE, SCALE, SCALE);

        MyPlugin.Log.LogInfo("Kagutsuchi registered.");
    }
}

// =====================================================================
// 徘徊 — プレイヤーを見たら逃げる
// =====================================================================
public class KagutsuchiState_Wander : NpcState, IEntityTrigger
{
    Kagutsuchi kagu;

    public KagutsuchiState_Wander(Kagutsuchi npc) : base(npc) { kagu = npc; }

    public override void Enter()
    {
        base.Enter();
        kagu.SetMoveSpeed(Kagutsuchi.WANDER_SPEED);
        ChangeNavigationState(new NavigationState_WanderRandom(npc, 0));
    }

    public override void Update()
    {
        base.Update();
        if (npc.ec.Players == null || npc.ec.Players.Length == 0) return;
        PlayerManager pm = npc.ec.Players[0];
        if (pm == null) return;

        if (Vector3.Distance(npc.transform.position, pm.transform.position) < Kagutsuchi.TOUCH_DISTANCE)
            kagu.TryTouch(pm);

        if (kagu.IsSquished || SpriteHelper.HasNametag(pm)) return;
        if (Kagutsuchi.FireSuppressed) return; // 消火後は逃げない（完全ランダム放浪のまま）

        if (npc.looker.PlayerInSight())
        {
            MyPlugin.Log.LogInfo("[Kagutsuchi] Noticed player! Fleeing.");
            npc.behaviorStateMachine.ChangeState(new KagutsuchiState_Flee(kagu));
        }
    }

    public void EntityTriggerEnter(Entity otherEntity, Collider other, bool validCollision)
    {
        if (!validCollision) return;
        PlayerManager pm = otherEntity != null ? otherEntity.GetComponent<PlayerManager>() : null;
        if (pm == null && other != null) pm = other.GetComponentInParent<PlayerManager>();
        if (pm != null) kagu.TryTouch(pm);
    }

    public void EntityTriggerStay(Entity otherEntity, Collider other, bool validCollision)
    {
        EntityTriggerEnter(otherEntity, other, validCollision);
    }

    public void EntityTriggerExit(Entity otherEntity, Collider other, bool validCollision) { }
}

// =====================================================================
// 逃走 — プレイヤーから離れる（バニラの Beans と同じ DijkstraMap 方式）
//   プレイヤーが見えている間は逃げ続け、見えなくなって FLEE_TIME 秒たつと徘徊に戻る
// =====================================================================
public class KagutsuchiState_Flee : NpcState, IEntityTrigger
{
    Kagutsuchi kagu;
    float timer;
    Door targetDoor;
    Vector3 targetPos;
    readonly HashSet<Door> visited = new HashSet<Door>();

    float fleeElapsed = 0f;
    float fleeStartSpeed = Kagutsuchi.FLEE_SPEED;

    // プレイヤーのダッシュ速度（取れなければ FLEE_SPEED）
    float GetPlayerRunSpeed()
    {
        try
        {
            if (npc.ec.Players != null && npc.ec.Players.Length > 0 && npc.ec.Players[0] != null && npc.ec.Players[0].plm != null)
                return Mathf.Max(npc.ec.Players[0].plm.runSpeed, Kagutsuchi.FLEE_SPEED);
        }
        catch { }
        return Kagutsuchi.FLEE_SPEED;
    }

    Vector3 lastPos;
    Vector3 moveDir = Vector3.zero;
    float checkTimer = 0f;
    float stuckTimer = 0f;
    float lastRemain = float.MaxValue;
    float startSpeed;
    float rampElapsed;

    // 通常の逃走（プレイヤーから離れる）
    void StartFlee()
    {
        targetDoor = null;
        PlayerManager pm = (npc.ec.Players != null && npc.ec.Players.Length > 0) ? npc.ec.Players[0] : null;
        if (pm != null && pm.DijkstraMap != null)
            ChangeNavigationState(new NavigationState_WanderFlee(npc, 63, pm.DijkstraMap));
        else
            ChangeNavigationState(new NavigationState_WanderRandom(npc, 0));
    }

    // 現在の進行経路の先に普通のドアがあれば、そこへ入る
    void CheckDoorAhead(PlayerManager pm)
    {
        Vector3 pos = npc.transform.position;
        Vector3 delta = pos - lastPos; delta.y = 0f;
        lastPos = pos;
        if (delta.magnitude > 0.5f) moveDir = delta.normalized;

        if (targetDoor != null)
        {
            Vector3 dd = targetPos - pos; dd.y = 0f;
            float remain = dd.magnitude;

            // 着いた／燃えた → 通常の逃走に戻る（次のドアは進行方向の先でまた探す）
            if (Kagutsuchi.IsBurnt(targetDoor) || remain < Kagutsuchi.BURN_RANGE - 0.5f)
            {
                visited.Add(targetDoor);
                StartFlee();
                return;
            }

            // 進んでいない（スタック）→ そのドアは諦めて通常の逃走に戻る
            if (remain < lastRemain - 1f) { lastRemain = remain; stuckTimer = 0f; }
            else
            {
                stuckTimer += 0.3f;
                if (stuckTimer >= 1.5f) { visited.Add(targetDoor); StartFlee(); }
            }
            return;
        }

        if (pm == null || moveDir == Vector3.zero) return;
        targetDoor = kagu.FindDoorAhead(moveDir, pm.transform.position, visited, out targetPos);
        if (targetDoor != null)
        {
            lastRemain = float.MaxValue;
            stuckTimer = 0f;
            ChangeNavigationState(new NavigationState_TargetPosition(npc, 63, targetPos));
        }
    }

    public KagutsuchiState_Flee(Kagutsuchi npc) : base(npc) { kagu = npc; }

    public override void Enter()
    {
        base.Enter();
        timer = Kagutsuchi.FLEE_TIME;
        lastPos = npc.transform.position;

        // 初速はプレイヤーのダッシュと同じ速さ。FLEE_RAMP_TIME 秒かけて FLEE_SPEED に変速する
        startSpeed = Kagutsuchi.FLEE_SPEED;
        PlayerManager p0 = (npc.ec.Players != null && npc.ec.Players.Length > 0) ? npc.ec.Players[0] : null;
        if (p0 != null && p0.plm != null)
        {
            try
            {
                var fi = AccessTools.Field(typeof(PlayerMovement), "runSpeed");
                if (fi != null) startSpeed = (float)fi.GetValue(p0.plm);
            }
            catch { }
        }
        rampElapsed = 0f;
        kagu.SetMoveSpeed(startSpeed);

        StartFlee();
    }

    public override void Update()
    {
        base.Update();
        if (npc.ec.Players == null || npc.ec.Players.Length == 0) return;
        PlayerManager pm = npc.ec.Players[0];

        // 逃げ始めはダッシュ速度、FLEE_RAMP_TIME 秒かけて FLEE_SPEED まで徐々に変速
        if (rampElapsed < Kagutsuchi.FLEE_RAMP_TIME)
        {
            rampElapsed += Time.deltaTime * npc.TimeScale;
            float t = Mathf.Clamp01(rampElapsed / Kagutsuchi.FLEE_RAMP_TIME);
            kagu.SetMoveSpeed(Mathf.Lerp(startSpeed, Kagutsuchi.FLEE_SPEED, t));
        }

        // 逃げ始めはダッシュと同速 → FLEE_DECEL_TIME 秒かけて FLEE_SPEED まで減速
        if (fleeElapsed < Kagutsuchi.FLEE_DECEL_TIME)
        {
            fleeElapsed += Time.deltaTime * npc.TimeScale;
            float t = Mathf.Clamp01(fleeElapsed / Kagutsuchi.FLEE_DECEL_TIME);
            kagu.SetMoveSpeed(Mathf.Lerp(fleeStartSpeed, Kagutsuchi.FLEE_SPEED, t));
        }

        // 0.3 秒ごとに、進行経路上のドアを確認
        checkTimer += Time.deltaTime * npc.TimeScale;
        if (checkTimer >= 0.3f)
        {
            checkTimer = 0f;
            CheckDoorAhead(pm);
        }

        if (pm != null && Vector3.Distance(npc.transform.position, pm.transform.position) < Kagutsuchi.TOUCH_DISTANCE)
            kagu.TryTouch(pm);

        // 名札中・消火後は逃げない（すぐ徘徊に戻る）
        if (Kagutsuchi.FireSuppressed || (pm != null && SpriteHelper.HasNametag(pm)))
        {
            npc.behaviorStateMachine.ChangeState(new KagutsuchiState_Wander(kagu));
            return;
        }

        if (npc.looker.PlayerInSight())
            timer = Kagutsuchi.FLEE_TIME;
        else
            timer -= Time.deltaTime * npc.TimeScale;

        if (timer <= 0f)
            npc.behaviorStateMachine.ChangeState(new KagutsuchiState_Wander(kagu));
    }

    public void EntityTriggerEnter(Entity otherEntity, Collider other, bool validCollision)
    {
        if (!validCollision) return;
        PlayerManager pm = otherEntity != null ? otherEntity.GetComponent<PlayerManager>() : null;
        if (pm == null && other != null) pm = other.GetComponentInParent<PlayerManager>();
        if (pm != null) kagu.TryTouch(pm);
    }

    public void EntityTriggerStay(Entity otherEntity, Collider other, bool validCollision)
    {
        EntityTriggerEnter(otherEntity, other, validCollision);
    }

    public void EntityTriggerExit(Entity otherEntity, Collider other, bool validCollision) { }
}

// =====================================================================
// ドア／窓を燃やして消す
//   ・ドア: 鍵を外して開けっぱなしにし、ドアの絵（オーバーレイ）を透明にする
//   ・窓: 割って（Break）、ガラスの絵を透明にする
//   ※ 他の Mod やバニラの処理（Mogigi の全ドアロックなど）で閉じられても、
//     BurntDoor が 0.5 秒ごとに開け直す。
//   ※ Door のメンバーは公開範囲が不明なものが多いので、リフレクションで触る。
// =====================================================================
public static class DoorBurner
{
    static readonly System.Reflection.PropertyInfo piATile = AccessTools.Property(typeof(Door), "aTile");
    static readonly System.Reflection.PropertyInfo piBTile = AccessTools.Property(typeof(Door), "bTile");
    static readonly System.Reflection.FieldInfo fiLocked = AccessTools.Field(typeof(Door), "locked");
    static readonly System.Reflection.FieldInfo fiBreakable = AccessTools.Field(typeof(Window), "breakable");

    static Texture2D clearTex;

    static Texture2D ClearTex
    {
        get
        {
            if (clearTex == null)
            {
                clearTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                clearTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
                clearTex.Apply();
                clearTex.filterMode = FilterMode.Point;
            }
            return clearTex;
        }
    }

    // ドアの中心 = 両側のマスの中間
    public static bool TryGetCenter(Door d, out Vector3 center)
    {
        center = d.transform.position;
        try
        {
            Cell a = piATile != null ? piATile.GetValue(d, null) as Cell : null;
            Cell b = piBTile != null ? piBTile.GetValue(d, null) as Cell : null;
            if (a != null && b != null)
            {
                center = (a.FloorWorldPosition + b.FloorWorldPosition) * 0.5f;
                return true;
            }
            if (a != null) { center = a.FloorWorldPosition; return true; }
        }
        catch { }
        return true; // 取れなければドア自身の位置で代用
    }

    public static void Burn(Door d)
    {
        if (d == null) return;
        try
        {
            // 鍵付きでも燃やす
            bool locked = fiLocked != null && (bool)fiLocked.GetValue(d);
            if (locked) d.Unlock();

            Record(d); // 元に戻せるよう、燃やす前の見た目を保存

            Window w = d as Window;
            if (w != null)
            {
                if (fiBreakable != null) fiBreakable.SetValue(w, true); // 割れない窓も割る
                w.Break(false);
            }
            else
            {
                d.OpenTimed(1000000f, false); // 実質ずっと開けっぱなし
            }

            Vanish(d);
            BurntDoor guard = d.gameObject.AddComponent<BurntDoor>();
            guard.door = d;

            MyPlugin.Log.LogInfo($"[Kagutsuchi] Burned {d.GetType().Name} at {d.transform.position}");
        }
        catch (System.Exception ex)
        {
            MyPlugin.Log.LogError("[Kagutsuchi] Burn failed: " + ex);
        }
    }

    // ── 復元用の記録 ──────────────────────────────────────────────
    class BurnRecord
    {
        public Renderer[] renderers;
        public Material[][] mats;
        public Dictionary<string, Material[]> overlays = new Dictionary<string, Material[]>();
        public Collider[] colliders;
        public bool[] colliderEnabled;
    }
    static readonly Dictionary<Door, BurnRecord> records = new Dictionary<Door, BurnRecord>();
    static readonly System.Reflection.FieldInfo fiBroken = AccessTools.Field(typeof(Window), "broken");

    public static void ResetRecords() { records.Clear(); }

    static void Record(Door d)
    {
        if (records.ContainsKey(d)) return;
        BurnRecord rec = new BurnRecord();
        rec.renderers = GetField<Renderer[]>(d, "doors") ?? GetField<Renderer[]>(d, "windows");
        if (rec.renderers != null)
        {
            rec.mats = new Material[rec.renderers.Length][];
            for (int i = 0; i < rec.renderers.Length; i++)
                rec.mats[i] = rec.renderers[i] != null ? rec.renderers[i].sharedMaterials : null;
        }
        foreach (string name in new[] { "overlayOpen", "overlayShut", "overlayLocked" })
        {
            Material[] arr = GetField<Material[]>(d, name);
            if (arr != null) rec.overlays[name] = (Material[])arr.Clone();
        }
        rec.colliders = GetField<Collider[]>(d, "colliders");
        if (rec.colliders != null)
        {
            rec.colliderEnabled = new bool[rec.colliders.Length];
            for (int i = 0; i < rec.colliders.Length; i++)
                rec.colliderEnabled[i] = rec.colliders[i] != null && rec.colliders[i].enabled;
        }
        records[d] = rec;
    }

    // 燃えたドア／窓を元に戻す（絵・当たり判定・割れ・開閉状態）
    public static void Restore(Door d)
    {
        if (d == null) return;
        try
        {
            foreach (BurntDoor g in d.GetComponents<BurntDoor>()) Object.Destroy(g);

            BurnRecord rec;
            if (records.TryGetValue(d, out rec))
            {
                if (rec.renderers != null && rec.mats != null)
                    for (int i = 0; i < rec.renderers.Length; i++)
                        if (rec.renderers[i] != null && rec.mats[i] != null)
                            rec.renderers[i].sharedMaterials = rec.mats[i];

                foreach (KeyValuePair<string, Material[]> kv in rec.overlays)
                {
                    Material[] live = GetField<Material[]>(d, kv.Key);
                    if (live == null) continue;
                    for (int i = 0; i < live.Length && i < kv.Value.Length; i++) live[i] = kv.Value[i];
                }

                if (rec.colliders != null && rec.colliderEnabled != null)
                    for (int i = 0; i < rec.colliders.Length; i++)
                        if (rec.colliders[i] != null) rec.colliders[i].enabled = rec.colliderEnabled[i];

                records.Remove(d);
            }

            Window w = d as Window;
            if (w != null && fiBroken != null) fiBroken.SetValue(w, false);
            d.Shut(); // 閉じて、絵（閉じた状態）も張り直す

            MyPlugin.Log.LogInfo($"[Kagutsuchi] Restored {d.GetType().Name} at {d.transform.position}");
        }
        catch (System.Exception ex)
        {
            MyPlugin.Log.LogError("[Kagutsuchi] Restore failed: " + ex);
        }
    }

    // ドアの絵（オーバーレイ）を透明にする
    public static void Vanish(Door d)
    {
        // 1) 実際に描画しているメッシュの 2 番目のマテリアル（オーバーレイ）
        Renderer[] rs = GetField<Renderer[]>(d, "doors") ?? GetField<Renderer[]>(d, "windows");
        Material clear = null;
        if (rs != null)
        {
            foreach (Renderer r in rs)
            {
                if (r == null) continue;
                Material[] mats = r.sharedMaterials;
                if (mats == null || mats.Length < 2 || mats[1] == null) continue;
                if (clear == null) clear = MakeClear(mats[1]);
                if (mats[1] != clear)
                {
                    mats[1] = clear;
                    r.sharedMaterials = mats;
                }
            }
        }

        // 2) 後から開閉された時に絵が戻らないよう、ドアが持つオーバーレイ素材も透明に差し替える
        if (clear != null)
        {
            foreach (string name in new[] { "overlayOpen", "overlayShut", "overlayLocked" })
            {
                Material[] arr = GetField<Material[]>(d, name);
                if (arr == null) continue;
                for (int i = 0; i < arr.Length; i++) arr[i] = clear;
            }
        }

        // 3) コライダーは無効化（通れるように）
        Collider[] cols = GetField<Collider[]>(d, "colliders");
        if (cols != null)
            foreach (Collider c in cols)
                if (c != null) c.enabled = false;
    }

    static Material MakeClear(Material source)
    {
        Material m = new Material(source);
        m.mainTexture = ClearTex;
        try
        {
            if (m.HasProperty("_Color"))
            {
                Color c = m.color;
                m.color = new Color(c.r, c.g, c.b, 0f);
            }
        }
        catch { }
        return m;
    }

    static T GetField<T>(object o, string name) where T : class
    {
        try
        {
            System.Reflection.FieldInfo f = AccessTools.Field(o.GetType(), name);
            return f != null ? f.GetValue(o) as T : null;
        }
        catch { return null; }
    }

    public static bool IsLocked(Door d)
    {
        try { return fiLocked != null && (bool)fiLocked.GetValue(d); }
        catch { return false; }
    }
}

// 燃やしたドア／窓が元に戻らないよう見張る
public class BurntDoor : MonoBehaviour
{
    public Door door;
    float timer = 0f;

    void Update()
    {
        timer += Time.deltaTime;
        if (timer < 0.5f) return;
        timer = 0f;
        if (door == null) return;

        try
        {
            if (DoorBurner.IsLocked(door)) door.Unlock();
            if (!door.IsOpen) door.Open(true, false);
            DoorBurner.Vanish(door);
        }
        catch { }
    }
}


// ── エレベーターの炎上 ─────────────────────────────────────────────
// 炎上中は OutOfOrder 状態に固定（扉閉・入れない）。炎（FireWall）が
// BSODA／洪水／カグツチ接触で消えたら、元の状態に戻す。
public static class ElevatorBurner
{
    static readonly System.Reflection.MethodInfo miSetState = AccessTools.Method(typeof(Elevator), "SetState");
    static readonly System.Reflection.FieldInfo fiState = AccessTools.Field(typeof(Elevator), "currentState");

    public static void Reset() { nextTry.Clear(); }

    static readonly System.Reflection.FieldInfo fiGate = AccessTools.Field(typeof(Elevator), "gateCollider");
    static readonly System.Reflection.PropertyInfo piPos = AccessTools.Property(typeof(Elevator), "position");
    static readonly System.Reflection.PropertyInfo piA = AccessTools.Property(typeof(Door), "aTile");
    static readonly System.Reflection.PropertyInfo piB = AccessTools.Property(typeof(Door), "bTile");

    // エレベーター扉の外側（廊下側）のマス位置
    public static bool TryGetFrontPosition(Elevator el, out Vector3 pos)
    {
        pos = Vector3.zero;
        try
        {
            Door d = el.Door;
            if (d == null) return false;
            Cell a = piA != null ? piA.GetValue(d, null) as Cell : null;
            Cell b = piB != null ? piB.GetValue(d, null) as Cell : null;
            if (a == null || b == null) return false;
            Vector3 inside = el.transform.position;
            float da = (a.FloorWorldPosition - inside).sqrMagnitude;
            float db = (b.FloorWorldPosition - inside).sqrMagnitude;
            pos = da > db ? a.FloorWorldPosition : b.FloorWorldPosition;
            return true;
        }
        catch { return false; }
    }

    // エレベーター内部の位置
    public static Vector3 GetInsidePosition(Elevator el)
    {
        try { if (piPos != null) return (Vector3)piPos.GetValue(el, null); } catch { }
        return el.transform.position;
    }

    public static int GetState(Elevator el)
    {
        try { return (int)fiState.GetValue(el); } catch { return -1; }
    }

    public static void SetState(Elevator el, int state)
    {
        try { miSetState.Invoke(el, new object[] { System.Enum.ToObject(typeof(ElevatorState), state) }); }
        catch (System.Exception e) { MyPlugin.Log.LogWarning("[Kagutsuchi] Elevator.SetState failed: " + e.Message); }
    }

    static readonly Dictionary<Door, float> nextTry = new Dictionary<Door, float>();

    public static void Ignite(EnvironmentController ec, Door door, Vector3 center, float y, SpriteRenderer template)
    {
        if (ec == null || door == null) return;
        // 毎ティック FindObjectsOfType しないよう、同じドアは 0.3 秒に 1 回だけ判定
        float t;
        if (nextTry.TryGetValue(door, out t) && Time.time < t) return;
        nextTry[door] = Time.time + 0.3f;
        if (ec.GetEvent(RandomEventType.Flood) != null) return;

        Elevator target = null;
        foreach (Elevator el in Object.FindObjectsOfType<Elevator>())
            if (el != null && el.Door == door) { target = el; break; }
        if (target == null) return;
        if (target.GetComponent<ElevatorFireGuard>() != null) return; // すでに炎上中

        int st = GetState(target);
        if (st == (int)ElevatorState.OutOfOrder || st == (int)ElevatorState.NoPower || st == (int)ElevatorState.FinishingLevel) return;
        if (target.ColliderGroup != null && target.ColliderGroup.HasPlayer) return; // 中にプレイヤーがいる間は燃やさない

        // 消えるまで残る炎を 2 つ（寿命なし・弾き飛ばしなし）: シャッターの位置 と エレベーター内部
        Vector3 gatePos = center;
        Vector3 inPos = center;
        try
        {
            Collider gc = fiGate != null ? fiGate.GetValue(target) as Collider : null;
            if (gc != null) gatePos = gc.bounds.center;
            Vector3 ip = piPos != null ? (Vector3)piPos.GetValue(target, null) : target.transform.position;
            inPos = ip;
        }
        catch { }

        FireWall fireGate = FireWall.Spawn(ec, new Vector3(gatePos.x, y, gatePos.z), template, float.MaxValue, false);
        FireWall fireIn = FireWall.Spawn(ec, new Vector3(inPos.x, y, inPos.z), template, float.MaxValue, false);
        if (fireGate == null || fireIn == null)
        {
            if (fireGate != null) Object.Destroy(fireGate.gameObject);
            if (fireIn != null) Object.Destroy(fireIn.gameObject);
            return;
        }

        ElevatorFireGuard g = target.gameObject.AddComponent<ElevatorFireGuard>();
        g.elevator = target;
        g.fire = fireGate;
        g.fire2 = fireIn;
        g.pending = st;
        SetState(target, (int)ElevatorState.OutOfOrder);
        MyPlugin.Log.LogInfo("[Kagutsuchi] Elevator set on fire (was state " + st + ").");
    }
}

public class ElevatorFireGuard : MonoBehaviour
{
    public Elevator elevator;
    public FireWall fire;   // シャッター側の炎
    public FireWall fire2;  // エレベーター内部の炎
    public int pending;

    void Update()
    {
        if (elevator == null) { Destroy(this); return; }

        int cur = ElevatorBurner.GetState(elevator);

        // どちらかの炎が消えたら、もう片方も連動して消す
        bool out1 = fire == null || fire.Extinguished;
        bool out2 = fire2 == null || fire2.Extinguished;
        if (out1 != out2)
        {
            FireWall other = out1 ? fire2 : fire;
            if (other != null && !other.Extinguished) other.Extinguish();
            out1 = out2 = true;
        }

        // 炎が消えた → 元の状態に戻す
        if (out1 && out2)
        {
            if (cur == (int)ElevatorState.OutOfOrder)
                ElevatorBurner.SetState(elevator, pending);
            MyPlugin.Log.LogInfo("[Kagutsuchi] Elevator fire out. Restored state " + pending + ".");
            Destroy(this);
            return;
        }

        // 炎上中に他の処理が状態を変えたら（出口が開く等）、予定として記録して閉じ直す
        if (cur != (int)ElevatorState.OutOfOrder)
        {
            if (cur == (int)ElevatorState.FinishingLevel) { Destroy(this); return; }
            pending = cur;
            ElevatorBurner.SetState(elevator, (int)ElevatorState.OutOfOrder);
        }
    }
}
