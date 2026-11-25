using MTM101BaldAPI;
using MTM101BaldAPI.ObjectCreation;
using MTM101BaldAPI.Registers;
using UnityEngine;

// =====================================================================
// Minto
//   徘徊中→プレイヤーを発見→49秒間、3秒ごとに自分の位置へFireWallを
//   設置しながら徘徊→終了後クールダウン→徘徊に戻る
//
//   FireWall の中身は FireWall.cs を参照。
// =====================================================================
public class Minto : NPC
{
    public const float ACTIVE_DURATION = 49f;   // 発動時間
    public const float PLACE_INTERVAL = 3f;     // FireWall設置間隔
    public const bool PLACE_ON_START = true;    // 発動した瞬間に1枚目を置くか（false=3秒後が1枚目）
    public const float COOLDOWN = 30f;          // 発動終了後、再発動できるまでの時間
    public const float WANDER_SPEED = 18f;      // 通常時の速度
    public const float ACTIVE_SPEED = 22f;      // 発動中の速度

    // Register()で読み込んだスプライト（全インスタンス共通）
    public static Sprite sprNormal;
    public static Sprite sprFire;

    // 炎を生むときのSE（PropagatedAudioManagerで距離減衰あり、Anubisと同じ）
    [SerializeField] public PropagatedAudioManager propAudMan;
    [SerializeField] public SoundObject audFire;

    private float cooldown = 0f;
    public bool IsOnCooldown => cooldown > 0f;

    public override void Initialize()
    {
        base.Initialize();
        if (propAudMan == null) propAudMan = GetComponent<PropagatedAudioManager>();
        behaviorStateMachine.ChangeState(new MintoState_Wander(this));
    }

    private void LateUpdate()
    {
        if (cooldown > 0f)
            cooldown -= Time.deltaTime * TimeScale;
    }

    public void StartCooldown() { cooldown = COOLDOWN; }

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

    public void SetFireSprite(bool fire)
    {
        Sprite s = fire ? sprFire : sprNormal;
        if (s != null && spriteRenderer != null && spriteRenderer.Length > 0)
            spriteRenderer[0].sprite = s;
    }

    // ── FireWall設置 ─────────────────────────────────────────────────
    public void PlaceFireWall()
    {
        // Kagutsuchi に触れた後の消火中は、Minto の炎も出ない
        if (Kagutsuchi.SUPPRESS_MINTO_FIRE && Kagutsuchi.FireSuppressed) return;
        SpriteRenderer template = (spriteRenderer != null && spriteRenderer.Length > 0) ? spriteRenderer[0] : null;
        FireWall.Spawn(ec, transform.position, template);

        if (propAudMan != null && audFire != null)
        {
            propAudMan.PlaySingle(audFire);
            if (propAudMan.audioDevice != null) propAudMan.audioDevice.pitch = 1f;
        }
    }

    public static void Register()
    {
        // ポスター画像が無ければ通常スプライトで代用
        Texture2D poster = SpriteHelper.LoadTexture("minto_poster.png")
                           ?? SpriteHelper.LoadTexture("minto_normal.png");

        Minto npc = new NPCBuilder<Minto>(MyPlugin.Instance.Info)
            .SetName("Minto")
            .SetEnum("Minto")
            .SetMetaName("Minto")
            .SetPoster(
                poster,
                "Minto",
                "x.com/minmintotot\nFireFireFire!!")
            .AddTrigger()
            .AddLooker()
            .AddSpawnableRoomCategories(RoomCategory.Hall)
            .Build();

        sprNormal = SpriteHelper.LoadSprite("minto_normal.png");
        sprFire = SpriteHelper.LoadSprite("minto_fire.png");
        FireWall.LoadAssets();

        npc.spriteRenderer[0].sprite = sprNormal;
        EditorSprites.Register("Minto", sprNormal);
        // 見た目だけ2倍（当たり判定はそのまま）
        npc.spriteRenderer[0].transform.localScale = new Vector3(6.5f, 6.5f, 6.5f);

        // PropagatedAudioManager で距離減衰あり (Anubisと同じ)
        PropagatedAudioManager propAud = npc.gameObject.GetComponent<PropagatedAudioManager>();
        if (propAud == null)
        {
            propAud = npc.gameObject.AddComponent<PropagatedAudioManager>();
            propAud.audioDevice = npc.gameObject.AddComponent<AudioSource>();
        }
        npc.propAudMan = propAud;

        // 炎を生むSE（字幕: オレンジがかった赤）
        SoundObject fireSound = SpriteHelper.LoadSound("Fire_Minto_SE.wav", "MintoFire", "Fire!", new Color(1f, 0.35f, 0.1f));
        if (fireSound != null) npc.audFire = fireSound;

        MyPlugin.Log.LogInfo("Minto registered.");
    }
}

// =====================================================================
// 徘徊ステート — プレイヤーを見つけたら発動
// =====================================================================
public class MintoState_Wander : NpcState
{
    Minto minto;

    public MintoState_Wander(Minto npc) : base(npc) { minto = npc; }

    public override void Enter()
    {
        base.Enter();
        minto.SetFireSprite(false);
        minto.SetMoveSpeed(Minto.WANDER_SPEED);
        ChangeNavigationState(new NavigationState_WanderRandom(npc, 0));
    }

    public override void Update()
    {
        base.Update();

        if (minto.IsOnCooldown) return;
        if (minto.IsSquished) return;
        if (npc.ec.Players == null || npc.ec.Players.Length == 0) return;

        PlayerManager pm = npc.ec.Players[0];
        if (pm == null) return;
        if (SpriteHelper.HasNametag(pm)) return;

        if (npc.looker.PlayerInSight())
        {
            MyPlugin.Log.LogInfo("[Minto] Spotted player! Activating for 49s.");
            npc.behaviorStateMachine.ChangeState(new MintoState_Active(minto));
        }
    }
}

// =====================================================================
// 発動ステート — 49秒間、3秒ごとにFireWallを置きながら徘徊
// =====================================================================
public class MintoState_Active : NpcState
{
    Minto minto;
    float timer;
    float placeTimer;

    public MintoState_Active(Minto npc) : base(npc) { minto = npc; }

    public override void Enter()
    {
        base.Enter();
        timer = Minto.ACTIVE_DURATION;
        placeTimer = Minto.PLACE_ON_START ? 0f : Minto.PLACE_INTERVAL;
        minto.SetFireSprite(true);
        minto.SetMoveSpeed(Minto.ACTIVE_SPEED);
        ChangeNavigationState(new NavigationState_WanderRandom(npc, 0));
    }

    public override void Update()
    {
        base.Update();

        float dt = Time.deltaTime * npc.TimeScale;
        timer -= dt;

        // スクワッシュ中は設置しない（タイマー自体は進む）
        if (!minto.IsSquished)
        {
            placeTimer -= dt;
            if (placeTimer <= 0f)
            {
                placeTimer += Minto.PLACE_INTERVAL;
                minto.PlaceFireWall();
            }
        }

        if (timer <= 0f)
        {
            MyPlugin.Log.LogInfo("[Minto] 49s over. Back to wander.");
            minto.StartCooldown();
            npc.behaviorStateMachine.ChangeState(new MintoState_Wander(minto));
        }
    }
}
