using MTM101BaldAPI;
using MTM101BaldAPI.ObjectCreation;
using MTM101BaldAPI.Registers;
using System.Linq;
using UnityEngine;

// =====================================================================
// 美天 (Biten)
//   徘徊中→走っているプレイヤーを発見→追跡→捕捉→
//   廊下を40秒間ともに徘徊（加速あり）→解放 (90秒クールタイム)
//
//   Pompと同じ MovementModifier ドラッグ方式を使用:
//     moveMod.movementMultiplier = 0f  → プレイヤーの自主移動を完全封鎖
//     moveMod.movementAddend           → 美天の隣に引き寄せるベクトル
//
//   解放条件: 40秒経過 / 12f以上離れて脱出 / Quarter使用 / 捕まった瞬間にQuarter所持
// =====================================================================

public class Biten : NPC, IItemAcceptor
{
    public bool isDetaining = false;
    private float cooldown = 0f;
    public PlayerManager capturedPlayer = null;

    // Pompと同じ MovementModifier でプレイヤーを引っ張る (Pompと同じくnewしない)
    [SerializeField]
    public MovementModifier moveMod;

    // PropagatedAudioManager で距離減衰あり (Anubisと同じ)
    [SerializeField]
    public PropagatedAudioManager propAudMan;

    [SerializeField]
    public SoundObject audHug;

    [SerializeField]
    public SoundObject audEnd;

    // 口笛と同じ乱数生成器
    private System.Random huggingRng = new System.Random();

    // 2秒ごとに約5%の確率で鳴らす
    private float hugChance = 0.05f;

    public override void Initialize()
    {
        base.Initialize();
        if (propAudMan == null)
            propAudMan = GetComponent<PropagatedAudioManager>();
        behaviorStateMachine.ChangeState(new BitenState_Wander(this));
    }

    // 口笛と同スパンで鳴らすメソッド（WanderステートのUpdateから呼ぶ）
    public void HugSoundChance()
    {
        if (audHug == null || propAudMan == null) return;
        if (huggingRng.NextDouble() < (double)hugChance && !propAudMan.audioSourceManager.isPlaying)
        {
            SpriteHelper.ApplySubtitleColor(propAudMan, audHug);
            propAudMan.PlaySingle(audHug);
            if (propAudMan.audioDevice != null) propAudMan.audioDevice.pitch = 1f;
        }
    }

    // NPC.Update()と被らないようにLateUpdateで処理
    private void LateUpdate()
    {
        if (cooldown > 0f)
            cooldown -= Time.deltaTime;
    }

    public bool IsOnCooldown => cooldown > 0f;

    public void StartCooldown()
    {
        cooldown = 90f;
    }

    // ── IItemAcceptor ────────────────────────────────────────────────────
    // Quarterを右クリックで使うと美天が満足してクールダウンに入る
    // 拘束中・徘徊中どちらでも受け付ける
    public bool ItemFits(Items item)
    {
        return item == Items.Quarter && !IsOnCooldown;
    }

    public void InsertItem(PlayerManager pm, EnvironmentController ec)
    {
        // 拘束中なら連行ステートを経由せずここで解放
        if (isDetaining && behaviorStateMachine.CurrentState is BitenState_Carry carry)
        {
            carry.ForceRelease();
        }
        else
        {
            // 徘徊中でもクールダウンを開始
            isDetaining = false;
            capturedPlayer = null;
            StartCooldown();
            behaviorStateMachine.ChangeState(new BitenState_Wander(this));
        }

        MyPlugin.Log.LogInfo("[Biten] Satisfied by Quarter. 90s cooldown started.");
    }


    public static void Register()
    {
        Biten npc = new NPCBuilder<Biten>(MyPlugin.Instance.Info)
            .SetName("Biten")
            .SetEnum("Biten")
            .SetMetaName("Biten")
            .SetPoster(
                SpriteHelper.LoadTexture("biten_poster.png"),
                "Son Biten",
                "She loves to hug\nrunning students!\nShe'll take you on\na date in the halls!\n  I Love Quarter!\n   HELP ME")
            .AddTrigger()
            .AddLooker()
            .AddSpawnableRoomCategories(RoomCategory.Hall)
            .Build();

        var bitenSprite = SpriteHelper.LoadSprite("biten.png");
        npc.spriteRenderer[0].sprite = bitenSprite;
        EditorSprites.Register("Biten", bitenSprite);
        npc.spriteRenderer[0].transform.localScale = new Vector3(3.25f, 3.25f, 3.25f);

        // PropagatedAudioManager で距離減衰あり (Anubisと同じ)
        PropagatedAudioManager propAud = npc.gameObject.GetComponent<PropagatedAudioManager>();
        if (propAud == null)
        {
            propAud = npc.gameObject.AddComponent<PropagatedAudioManager>();
            propAud.audioDevice = npc.gameObject.AddComponent<AudioSource>();
        }
        npc.propAudMan = propAud;

        // 固有SE をロード (暗い黄色)
        Color bitenColor = new Color(0.75f, 0.65f, 0.1f);
        SoundObject hugSound = SpriteHelper.LoadSound("Biten_hug.wav", "BitenHug", "Hug", bitenColor);
        if (hugSound != null) npc.audHug = hugSound;

        SoundObject endSound = SpriteHelper.LoadSound("Biten_end.wav", "BitenEnd", "Hug finish", bitenColor);
        if (endSound != null) npc.audEnd = endSound;

        MyPlugin.Log.LogInfo("Biten registered.");
    }
}

// =====================================================================
// 徘徊ステート
//   走っているプレイヤー（walkSpeed超え）を発見→追跡へ
// =====================================================================
public class BitenState_Wander : NpcState, IEntityTrigger
{
    Biten biten;
    // 口笛と同じスパン: Principalは約2秒ごとにチャンス判定
    private float soundTimer = 2f;

    public BitenState_Wander(Biten npc) : base(npc) { biten = npc; }

    public override void Enter()
    {
        base.Enter();
        ChangeNavigationState(new NavigationState_WanderRandom(npc, 0));
    }

    public override void Update()
    {
        base.Update();

        // 口笛と同スパンでサウンド判定
        soundTimer -= Time.deltaTime;
        if (soundTimer <= 0f)
        {
            soundTimer = 2f;
            biten.HugSoundChance();
        }

        if (biten.IsOnCooldown) return;
        // スクワッシュ中は行動しない
        Entity bitenEnt = npc.GetComponent<Entity>();
        if (bitenEnt != null && bitenEnt.Squished) return;
        if (npc.ec.Players == null || npc.ec.Players.Length == 0) return;

        PlayerManager pm = npc.ec.Players[0];
        if (pm == null) return;

        if (npc.looker.PlayerInSight())
        {
            MyPlugin.Log.LogInfo("[Biten] Spotted player! Chasing.");
            // 発見時にhugサウンドを即時再生
            if (biten.propAudMan != null && biten.audHug != null)
            {
                SpriteHelper.ApplySubtitleColor(biten.propAudMan, biten.audHug);
                biten.propAudMan.PlaySingle(biten.audHug);
                if (biten.propAudMan.audioDevice != null) biten.propAudMan.audioDevice.pitch = 1f;
            }
            npc.behaviorStateMachine.ChangeState(new BitenState_Chase(biten, pm));
        }
    }

    public void EntityTriggerEnter(Entity otherEntity, Collider other, bool validCollision)
    {
        if (!validCollision || biten.IsOnCooldown) return;
        PlayerManager pm = otherEntity?.GetComponent<PlayerManager>();
        if (pm == null) pm = other.GetComponentInParent<PlayerManager>();
        if (pm != null)
            npc.behaviorStateMachine.ChangeState(new BitenState_Carry(biten, pm));
    }

    public void EntityTriggerStay(Entity otherEntity, Collider other, bool validCollision)
    {
        EntityTriggerEnter(otherEntity, other, validCollision);
    }

    public void EntityTriggerExit(Entity otherEntity, Collider other, bool validCollision) { }
}

// =====================================================================
// 追跡ステート
//   距離3f以内 or IEntityTrigger で捕捉→連行へ
// =====================================================================
public class BitenState_Chase : NpcState, IEntityTrigger
{
    Biten biten;
    PlayerManager target;
    float chaseTimer = 0f;

    // 追跡中の加速パラメータ
    const float CHASE_ACCEL_PER_SEC = 3f;  // 毎秒 +3f 加速
    const float CHASE_MAX_SPEED = 80f; // 追跡中の上限速度
    float baseChaseSpeed;

    public BitenState_Chase(Biten npc, PlayerManager player) : base(npc)
    {
        biten = npc;
        target = player;
    }

    public override void Enter()
    {
        base.Enter();
        // 初速: プレイヤーのwalkSpeedと同じ（追いかけ始め）
        baseChaseSpeed = target.plm != null ? target.plm.walkSpeed : 25f;
        npc.Navigator.SetSpeed(baseChaseSpeed);
        npc.Navigator.maxSpeed = baseChaseSpeed;
        chaseTimer = 0f;
        ChangeNavigationState(new NavigationState_TargetPlayer(npc, 63, target.transform.position));
    }

    public override void Update()
    {
        base.Update();

        // スクワッシュ中は追跡停止
        Entity bitenEnt2 = npc.GetComponent<Entity>();
        if (bitenEnt2 != null && bitenEnt2.Squished)
        {
            npc.behaviorStateMachine.ChangeState(new BitenState_Wander(biten));
            return;
        }

        // 追跡中に加速
        chaseTimer += Time.deltaTime * npc.TimeScale;
        float currentSpeed = Mathf.Min(baseChaseSpeed + CHASE_ACCEL_PER_SEC * chaseTimer, CHASE_MAX_SPEED);
        npc.Navigator.SetSpeed(currentSpeed);
        npc.Navigator.maxSpeed = currentSpeed;

        npc.behaviorStateMachine.CurrentNavigationState.UpdatePosition(target.transform.position);

        float dist = Vector3.Distance(npc.transform.position, target.transform.position);
        if (dist < 3f)
        {
            MyPlugin.Log.LogInfo("[Biten] Caught player! dist=" + dist);
            npc.behaviorStateMachine.ChangeState(new BitenState_Carry(biten, target));
            return;
        }

        if (!npc.looker.PlayerInSight())
            npc.behaviorStateMachine.ChangeState(new BitenState_Wander(biten));
    }

    public void EntityTriggerEnter(Entity otherEntity, Collider other, bool validCollision)
    {
        if (!validCollision) return;
        PlayerManager pm = otherEntity?.GetComponent<PlayerManager>();
        if (pm == null) pm = other.GetComponentInParent<PlayerManager>();
        if (pm != null)
            npc.behaviorStateMachine.ChangeState(new BitenState_Carry(biten, pm));
    }

    public void EntityTriggerStay(Entity otherEntity, Collider other, bool validCollision)
    {
        EntityTriggerEnter(otherEntity, other, validCollision);
    }

    public void EntityTriggerExit(Entity otherEntity, Collider other, bool validCollision) { }
}

// =====================================================================
// 連行ステート (Pompの MovementModifier 方式)
//   moveMod.movementMultiplier = 0f  → プレイヤーの自主移動を完全封鎖
//   moveMod.movementAddend           → 美天の隣に引き寄せるベクトル
//   美天は時間とともに加速、40秒経過 or 十分離れる or Quarter使用で解放
//   解放後は90秒クールダウン
// =====================================================================
public class BitenState_Carry : NpcState
{
    Biten biten;
    PlayerManager captive;
    float timer = 40f;  // 固定40秒
    bool released = false;

    // 加速パラメータ
    float baseSpeed;
    const float ACCEL_PER_SEC = 2f;   // 毎秒 +2f ずつ加速
    const float MAX_SPEED = 120f; // 上限

    // ドラッグ設定 (Pompの dragSpeed/dragMultiplier に相当)
    const float DRAG_SPEED = 60f;  // 引き寄せ速度
    const float DRAG_MULTIPLIER = 0f;   // 0=WASD完全無効, 0.25=Pompと同じ抵抗あり

    // BaldiとAnubis回避設定 (半径5マス = 50f)
    const float AVOID_RADIUS = 66f;  // この距離以内のBaldiとAnubisを回避
    private float avoidCheckTimer = 0f;
    const float AVOID_CHECK_INTERVAL = 0.3f; // 0.3秒ごとにチェック
    private Vector3 avoidOffset = Vector3.zero;
    private bool isFleeing = false; // 現在Uターン逃走中か

    public BitenState_Carry(Biten npc, PlayerManager player) : base(npc)
    {
        biten = npc;
        captive = player;
    }

    public override void Enter()
    {
        base.Enter();

        // ── Quarterを持っていたら全スロットから没収→即満足解放 ──────────
        if (captive?.itm != null)
        {
            bool hadQuarter = false;
            for (int i = 0; i < captive.itm.items.Count(); i++)
            {
                if (captive.itm.items[i] != null && captive.itm.items[i].itemType == Items.Quarter)
                {
                    captive.itm.RemoveItem(i);
                    i--; // 削除後にインデックスをずらす
                    hadQuarter = true;
                }
            }
            if (hadQuarter)
            {
                MyPlugin.Log.LogInfo("[Biten] Quarter confiscated! Satisfied without detaining.");
                biten.isDetaining = false;
                biten.capturedPlayer = null;
                biten.StartCooldown();
                npc.behaviorStateMachine.ChangeState(new BitenState_Wander(biten));
                if (biten.propAudMan != null && biten.audEnd != null)
                    biten.propAudMan.PlaySingle(biten.audEnd);
                return;
            }
        }

        biten.capturedPlayer = captive;
        biten.isDetaining = true;

        // MovementModifier をプレイヤーに追加 (Pompの Attack() と同じ)
        // moveMod がシリアライズされていない場合はスキップ（nullチェック必須）
        if (captive?.Am != null && biten.moveMod != null)
        {
            biten.moveMod.movementAddend = Vector3.zero;
            biten.moveMod.movementMultiplier = DRAG_MULTIPLIER;
            captive.Am.moveMods.Add(biten.moveMod);
        }

        // 初速: プレイヤーのwalkSpeedの1.5倍
        float playerWalk = captive?.plm != null ? captive.plm.walkSpeed : 25f;
        baseSpeed = playerWalk * 1.5f;
        npc.Navigator.SetSpeed(baseSpeed);
        npc.Navigator.maxSpeed = baseSpeed;
        ChangeNavigationState(new NavigationState_WanderRandom(npc, 0));

        MyPlugin.Log.LogInfo("[Biten] Carrying player (40s). baseSpeed=" + baseSpeed);
    }

    public override void Update()
    {
        base.Update();

        if (released) return;
        if (captive == null) { Release(); return; }

        // ── 加速処理 ─────────────────────────────────────────────────
        float elapsed = 40f - timer;
        float currentSpeed = Mathf.Min(baseSpeed + ACCEL_PER_SEC * elapsed, MAX_SPEED);
        npc.Navigator.SetSpeed(currentSpeed);
        npc.Navigator.maxSpeed = currentSpeed;

        // ── Pompの Drag() と同じ: 美天→プレイヤー方向に引き寄せ ──────
        if (biten.moveMod != null)
        {
            float dist = Vector3.Distance(npc.transform.position, captive.transform.position);

            // 12f以上離れたら抵抗成功→早期解放（シンクロバグ防止）
            if (dist > 12f)
            {
                MyPlugin.Log.LogInfo("[Biten] Player escaped by resisting! dist=" + dist);
                Release();
                return;
            }

            // 距離が離れるほど引き寄せ力を指数的に強化
            float dragMult = Mathf.Max(1f, dist / 5f);
            biten.moveMod.movementAddend =
                (npc.transform.position - captive.transform.position).normalized
                * (DRAG_SPEED + dist * dragMult)
                * npc.TimeScale;

            // 8f以上離れたら強制的にプレイヤーを美天の隣にテレポート
            if (dist > 8f && captive.transform != null)
            {
                captive.transform.position = npc.transform.position
                    + (captive.transform.position - npc.transform.position).normalized * 2f;
            }
        }

        // ── BaldiとAnubisの回避: 半径66f(約6.6マス)以内に脅威→Uターン逃走 ──
        avoidCheckTimer -= Time.deltaTime;
        if (avoidCheckTimer <= 0f)
        {
            avoidCheckTimer = AVOID_CHECK_INTERVAL;
            avoidOffset = Vector3.zero;
            foreach (NPC other in npc.ec.Npcs)
            {
                if (other == npc) continue;
                bool isThreat = other is Baldi || other.Character.ToString() == "Anubis"
                                || other.GetType().Name == "Anubis";
                if (!isThreat) continue;
                float d = Vector3.Distance(npc.transform.position, other.transform.position);
                if (d < AVOID_RADIUS && d > 0.01f)
                {
                    // 脅威と逆方向に回避ベクトルを加算（距離が近いほど強く）
                    avoidOffset += (npc.transform.position - other.transform.position).normalized
                                   * (AVOID_RADIUS - d);
                }
            }

            bool threatNearby = avoidOffset.magnitude > 0.1f;
            if (threatNearby && !isFleeing)
            {
                // 脅威を検知: 進行方向を180度反転してUターン
                isFleeing = true;
                Vector3 fleeDir = avoidOffset.normalized;
                Vector3 fleeTarget = npc.transform.position + fleeDir * 60f;
                // 新しいWanderRandom目標として上書き（Uターン）
                ChangeNavigationState(new NavigationState_WanderRandom(npc, 0));
                npc.behaviorStateMachine.CurrentNavigationState.UpdatePosition(fleeTarget);
                MyPlugin.Log.LogInfo("[Biten] Threat detected! U-turning to flee.");
            }
            else if (!threatNearby && isFleeing)
            {
                // 脅威圏外に出たら通常Wanderに戻す
                isFleeing = false;
                ChangeNavigationState(new NavigationState_WanderRandom(npc, 0));
                MyPlugin.Log.LogInfo("[Biten] Threat cleared. Resuming wander.");
            }
            else if (threatNearby && isFleeing)
            {
                // 逃走中も目標を更新し続ける
                Vector3 fleeTarget = npc.transform.position + avoidOffset.normalized * 60f;
                npc.behaviorStateMachine.CurrentNavigationState.UpdatePosition(fleeTarget);
            }
        }

        timer -= Time.deltaTime * npc.TimeScale;
        if (timer <= 0f)
        {
            MyPlugin.Log.LogInfo("[Biten] 40s over. Releasing player.");
            Release();
        }
    }

    // Update() 内から呼ぶ通常解放（40秒経過 or 距離脱出）
    private void Release()
    {
        if (released) return;
        released = true;

        // MovementModifier を除去してプレイヤーの移動を完全復元 (Pompの Teach() と同じ)
        if (captive?.Am != null && biten.moveMod != null)
        {
            biten.moveMod.movementAddend = Vector3.zero;
            biten.moveMod.movementMultiplier = 1f;
            captive.Am.moveMods.Remove(biten.moveMod);
        }

        if (biten.propAudMan != null && biten.audEnd != null)
            biten.propAudMan.PlaySingle(biten.audEnd);

        biten.isDetaining = false;
        biten.capturedPlayer = null;
        biten.StartCooldown();

        npc.behaviorStateMachine.ChangeState(new BitenState_Wander(biten));
        MyPlugin.Log.LogInfo("[Biten] Released. 90s cooldown started.");
    }

    // IItemAcceptor.InsertItem() から呼ぶ外部強制解放（Quarter使用時）
    public void ForceRelease()
    {
        Release();
    }
}