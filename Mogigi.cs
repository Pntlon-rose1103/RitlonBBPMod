using MTM101BaldAPI;
using MTM101BaldAPI.ObjectCreation;
using MTM101BaldAPI.Registers;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// =====================================================================
// モギギ
//   スポーンした瞬間からプレイヤーに向かって接近する。
//   移動はBiten（美天）のBitenState_Chaseと同じ方式：NavigationState_TargetPlayer
//   でナビゲーション（ナビメッシュ経由）に追わせ、毎フレームUpdatePosition()で
//   プレイヤーの現在位置に追従させる。直進のみだったEntity.AddForce方式を廃止し、
//   角を曲がれないという欠点を解消した。
//   接近速度：初期値1f。プレイヤーを一度発見（視認）したら、以後は視認が
//   外れても常時1フレームごとに1fずつ加速（上限35f）。
//   触れた瞬間にフロア上の「全ての」ドア
//   （青・茶・黄、その他の種類も問わず）を一斉に20秒間ロックして発動する。
//   （バニラ ChalkFace のドアロック演出に近いが、対象は1部屋ではなく
//    フロア全体のドア）
//
//   発動は各個体につき1回限り：触れて発動した瞬間にその個体自身は
//   完全に消滅し、以後そのフロアには一切影響を及ぼさない
//   （MrKtPlayerと同じ即時デスポーン方式）。
//
//   1フロアに複数体スポーンする（Plugin.cs側で制御）：
//     F2 = 3体、F3以降 = 5体、それぞれフロア上にバラバラにスポーンする。
//
//   ※スプライトはアップロードされた1枚絵をそのまま通常時の見た目として
//     使用（専用の発動時差分スプライトは無し）。
//   ※専用の発動音は未用意のため、バニラ ChalkFace の private フィールド
//     audSpawn（Chalkboardのせり上がり演出に使われる音）をリフレクションで
//     流用し、発動時にだけ1回再生する。
// =====================================================================
public class Mogigi : NPC
{
    public const float LOCK_TIME = 20f;        // 全ドアのロック時間
    public const float START_SPEED = 1f;       // 接近速度の初期値
    public const float MAX_SPEED = 35f;        // 接近速度の上限
    public const float ACCEL_PER_FRAME = 1f;    // プレイヤーを一度発見した後、1フレームごとに加算される速度
    public const float TOUCH_DISTANCE = 2.5f;  // 「触れた」と判定する距離
    public const float COLLIDER_RADIUS = 0.75f; // 当たり判定が大きすぎて廊下の壁などに
                                                 // めり込みやすかったため縮小する

    // Register()で読み込んだスプライト（全インスタンス共通）
    public static Sprite sprNormal;

    // ChalkFace.audSpawn を流用した発動音（全インスタンス共通）
    private static SoundObject audSpawn;

    private bool activated = false;
    private Entity selfEntity;

    // 一度でもプレイヤーを視認（発見）したか。発見後は視認が外れても常時加速する
    public bool hasSpotted = false;

    // 現在の接近速度。プレイヤーを発見した後はどんどん加速していく
    private float currentSpeed = START_SPEED;

    public override void Initialize()
    {
        base.Initialize();
        selfEntity = GetComponent<Entity>();
        currentSpeed = START_SPEED;
        hasSpotted = false;

        // コリジョン（物理的な当たり判定）が大きすぎると、廊下の中央を進んでいても
        // 左右の壁やオブジェクトに接触してめり込み、スタックしやすくなる。
        // そのためCapsuleColliderの半径を縮小する（トリガー/非トリガーは変更しない）。
        foreach (Collider col in GetComponentsInChildren<Collider>())
        {
            CapsuleCollider cap = col as CapsuleCollider;
            if (cap != null)
                cap.radius = Mathf.Min(cap.radius, COLLIDER_RADIUS);
        }

        behaviorStateMachine.ChangeState(new MogigiState_Chase(this));
    }

    public bool IsSquished
    {
        get
        {
            Entity ent = selfEntity != null ? selfEntity : GetComponent<Entity>();
            return ent != null && ent.Squished;
        }
    }

    public void SetMoveSpeed(float speed)
    {
        navigator.SetSpeed(speed);
        navigator.maxSpeed = speed;
    }

    // プレイヤー発見後、毎フレーム呼ばれる想定。速度をACCEL_PER_FRAMEずつ、
    // MAX_SPEEDを上限として加速し、その速度を即座にナビゲーションへ反映する。
    public void Accelerate()
    {
        currentSpeed = Mathf.Min(currentSpeed + ACCEL_PER_FRAME, MAX_SPEED);
        SetMoveSpeed(currentSpeed);
    }

    // mogigiから対象方向へ視線が通っているか（壁などに遮られていないか）を判定する
    public bool CanSee(Vector3 diff, float dist)
    {
        Vector3 origin = transform.position + Vector3.up * 1f;
        if (dist > 0.01f && Physics.Raycast(origin, diff / dist, out RaycastHit hit, dist))
        {
            // プレイヤーの手前（壁など）で止まっている場合は視認できていない
            if (hit.distance < dist - 1f) return false;
        }
        return true;
    }

    // ── 発動：全ドアロック → 完全消滅 ────────────────────────────────
    public void Activate()
    {
        if (activated) return;
        activated = true;

        LockAllDoors();

        // 発動音を1回だけ再生（ChalkFaceのaudSpawnを流用）
        if (audSpawn != null)
            Singleton<CoreGameManager>.Instance.audMan.PlaySingle(audSpawn);

        // 発動後は完全消滅。以後このフロアには一切影響を及ぼさない。
        MyPlugin.Log.LogInfo("[Mogigi] Touched player — activated, despawning permanently.");
        gameObject.SetActive(false);
        ec.Npcs.Remove(this);
        Object.Destroy(gameObject);
    }

    // ── フロア上の全ドアをロック ─────────────────────────────────────
    private void LockAllDoors()
    {
        var seen = new HashSet<Door>();
        int count = 0;
        foreach (RoomController room in ec.rooms)
        {
            if (room == null || room.doors == null) continue;
            foreach (Door door in room.doors)
            {
                if (door == null || seen.Contains(door)) continue;
                seen.Add(door);
                door.Shut();
                door.LockTimed(LOCK_TIME);
                count++;
            }
        }
        MyPlugin.Log.LogInfo($"[Mogigi] Locked {count} doors for {LOCK_TIME}s.");
    }

    public static void Register()
    {
        // 専用ポスター画像が無ければ通常スプライトで代用
        Texture2D poster = SpriteHelper.LoadTexture("mogigi_poster.png")
                           ?? SpriteHelper.LoadTexture("mogigi.png");

        Mogigi npc = new NPCBuilder<Mogigi>(MyPlugin.Instance.Info)
            .SetName("Mogigi")
            .SetEnum("Mogigi")
            .SetMetaName("Mogigi")
            .SetPoster(
                poster,
                "Mogigi",
                "X - @_SaffroN0\nX - @R1zVn\nX - @catano7ma\nRushes straight at you — locks every door in the school the instant it catches you.")
            .AddTrigger()
            .AddLooker()
            .AddSpawnableRoomCategories(RoomCategory.Hall)
            .Build();

        sprNormal = SpriteHelper.LoadSprite("mogigi.png");
        npc.spriteRenderer[0].sprite = sprNormal;
        // 見た目が小さすぎるため、スプライトを3倍サイズに拡大
        npc.spriteRenderer[0].transform.localScale = npc.spriteRenderer[0].transform.localScale * 3f;
        EditorSprites.Register("Mogigi", sprNormal);

        // --- ChalkFace から audSpawn を流用 ---
        foreach (ChalkFace cf in Resources.FindObjectsOfTypeAll<ChalkFace>())
        {
            SoundObject sound = (SoundObject)typeof(ChalkFace)
                .GetField("audSpawn", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(cf);
            if (sound != null)
            {
                audSpawn = sound;
                break;
            }
        }
        if (audSpawn == null)
            MyPlugin.Log.LogWarning("[Mogigi] ChalkFace audSpawn not found — activation will be silent.");

        MyPlugin.Log.LogInfo("Mogigi registered.");
    }
}

// =====================================================================
// 追跡ステート — スポーンした瞬間からプレイヤーに向かって接近し、
//   触れた瞬間に発動（1回限り）
//   BitenState_Chaseと同じく NavigationState_TargetPlayer でナビメッシュ経由
//   で追わせる（角を曲がれる）。毎フレームUpdatePosition()でプレイヤーの
//   現在位置に目標を更新し続ける。
// =====================================================================
public class MogigiState_Chase : NpcState, IEntityTrigger
{
    Mogigi mogigi;

    public MogigiState_Chase(Mogigi npc) : base(npc) { mogigi = npc; }

    public override void Enter()
    {
        base.Enter();

        mogigi.SetMoveSpeed(Mogigi.START_SPEED);

        PlayerManager pm = (npc.ec.Players != null && npc.ec.Players.Length > 0)
            ? npc.ec.Players[0] : null;
        Vector3 initialTarget = pm != null ? pm.transform.position : npc.transform.position;

        // Bitenと同じナビゲーション方式（優先度63はBitenの値を流用）
        ChangeNavigationState(new NavigationState_TargetPlayer(npc, 63, initialTarget));
    }

    public override void Update()
    {
        base.Update();

        if (mogigi.IsSquished) return;
        if (npc.ec.Players == null || npc.ec.Players.Length == 0) return;

        PlayerManager pm = npc.ec.Players[0];
        if (pm == null) return;
        if (SpriteHelper.HasNametag(pm)) return;

        Vector3 diff = pm.transform.position - npc.transform.position;
        diff.y = 0f;
        float dist = diff.magnitude;

        if (dist <= Mogigi.TOUCH_DISTANCE)
        {
            MyPlugin.Log.LogInfo("[Mogigi] Reached player! Activating (one-shot).");
            mogigi.Activate();
            return;
        }

        // 一度でもプレイヤーを発見したら、以後は視認が外れても常時加速し続ける
        if (!mogigi.hasSpotted && mogigi.CanSee(diff, dist))
            mogigi.hasSpotted = true;
        if (mogigi.hasSpotted)
            mogigi.Accelerate();

        // ナビゲーションの目標をプレイヤーの現在位置に更新し続ける（曲がれる）
        npc.behaviorStateMachine.CurrentNavigationState.UpdatePosition(pm.transform.position);
    }

    public void EntityTriggerEnter(Entity otherEntity, Collider other, bool validCollision)
    {
        if (!validCollision) return;
        PlayerManager pm = otherEntity?.GetComponent<PlayerManager>();
        if (pm == null) pm = other.GetComponentInParent<PlayerManager>();
        if (pm != null) mogigi.Activate();
    }

    public void EntityTriggerStay(Entity otherEntity, Collider other, bool validCollision)
    {
        if (!validCollision) return;
        PlayerManager pm = otherEntity?.GetComponent<PlayerManager>();
        if (pm == null) pm = other.GetComponentInParent<PlayerManager>();
        if (pm != null) mogigi.Activate();
    }

    public void EntityTriggerExit(Entity otherEntity, Collider other, bool validCollision) { }
}
