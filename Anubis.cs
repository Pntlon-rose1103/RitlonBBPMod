using MTM101BaldAPI;
using MTM101BaldAPI.ObjectCreation;
using MTM101BaldAPI.Registers;
using System.Collections;
using System.Reflection;
using UnityEngine;

public class Anubis : NPC
{
    private bool hasKilled = false;

    [SerializeField] public PropagatedAudioManager propAudMan;
    [SerializeField] public SoundObject audLoop;

    public override void Initialize()
    {
        base.Initialize();
        if (propAudMan == null) propAudMan = GetComponent<PropagatedAudioManager>();

        // 常時BGMを開始 (Playtimeと同じパターン、PropagatedAudioManagerで距離減衰)
        if (propAudMan != null && audLoop != null && !propAudMan.audioSourceManager.isPlaying)
        {
            SpriteHelper.ApplySubtitleColor(propAudMan, audLoop);
            propAudMan.PlaySingle(audLoop);
            if (propAudMan.audioDevice != null) propAudMan.audioDevice.pitch = 1f;
        }

        // プレイヤーの徒歩速度×1.05倍
        float speed = 25f * 1.05f; // フォールバック
        if (ec.Players != null && ec.Players.Length > 0)
        {
            PlayerMovement plm = ec.Players[0].GetComponent<PlayerMovement>();
            if (plm != null) speed = plm.walkSpeed * 1.05f;
        }
        navigator.SetSpeed(speed);
        navigator.maxSpeed = speed;

        behaviorStateMachine.ChangeState(new AnubisState_Wander(this));
    }

    protected override void VirtualUpdate()
    {
        base.VirtualUpdate();
        // Playtimeと同じパターン: 再生が止まったら即再生
        if (propAudMan != null && audLoop != null && !propAudMan.audioSourceManager.isPlaying)
        {
            SpriteHelper.ApplySubtitleColor(propAudMan, audLoop);
            propAudMan.PlaySingle(audLoop);
            if (propAudMan.audioDevice != null) propAudMan.audioDevice.pitch = 1f;
        }
    }

    public void ActivateAbility(PlayerManager pm)
    {
        if (hasKilled) return;
        // スクワッシュ中は能力発動しない
        Entity ent = GetComponent<Entity>();
        if (ent != null && ent.Squished) return;
        // ネームタグ中はテレポート不可
        if (SpriteHelper.HasNametag(pm)) return;
        hasKilled = true;
        MyPlugin.Log.LogInfo("[Anubis] ActivateAbility called!");
        StartCoroutine(WarpBaldi(pm));
    }

    private IEnumerator WarpBaldi(PlayerManager pm)
    {
        // ArtsAndCraftersのaudIntroをreflectionで取得して再生
        SoundObject introSound = null;
        ArtsAndCrafters crafters = FindObjectOfType<ArtsAndCrafters>();
        if (crafters != null)
        {
            introSound = (SoundObject)typeof(ArtsAndCrafters)
                .GetField("audIntro",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(crafters);
        }
        if (introSound != null)
        {
            SpriteHelper.SetSubtitle(introSound, "Teleport", new Color(0.35f, 0.35f, 0.35f));
            Singleton<CoreGameManager>.Instance.audMan.PlaySingle(introSound);
        }

        yield return new WaitForSecondsRealtime(0.01f);

        // Baldiをアヌビスのいる位置にワープ
        // ec.GetBaldi() が最も確実（ArtsAndCraftersと同じ方法）
        Baldi baldi = ec.GetBaldi();
        if (baldi == null)
        {
            // フォールバック: Npcsリストから探す
            foreach (NPC n in ec.Npcs)
            {
                baldi = n as Baldi;
                if (baldi != null) break;
            }
        }
        if (baldi == null)
        {
            // 最終フォールバック
            baldi = FindObjectOfType<Baldi>();
        }

        if (baldi != null)
        {
            baldi.transform.position = transform.position;
            MyPlugin.Log.LogInfo("[Anubis] Warped Baldi to: " + transform.position);
        }
        else
        {
            MyPlugin.Log.LogWarning("[Anubis] Baldi not found by any method!");
        }

        // クールダウン後に再び発動できるようにする
        yield return new WaitForSeconds(10f);
        hasKilled = false;
    }

    public static void Register()
    {
        Anubis npc = new NPCBuilder<Anubis>(MyPlugin.Instance.Info)
            .SetName("Anubis")
            .SetEnum("Anubis")
            .SetMetaName("Anubis")
            .SetPoster(
                SpriteHelper.LoadTexture("anubis_poster.png"),
                "Anubis",
                "God of death.\nOne touch means the end.")
            .AddTrigger()
            .AddLooker()
            .AddSpawnableRoomCategories(RoomCategory.Hall)
            .Build();

        var anubisSprite = SpriteHelper.LoadSprite("anubis.png");
        npc.spriteRenderer[0].sprite = anubisSprite;
        EditorSprites.Register("Anubis", anubisSprite);
        npc.spriteRenderer[0].transform.localScale = new Vector3(3.25f, 3.25f, 3.25f);

        // PropagatedAudioManager で距離減衰あり (Playtimeと同じ)
        // NPCBuilder が既に追加している場合はGetComponentで取得
        PropagatedAudioManager propAud = npc.gameObject.GetComponent<PropagatedAudioManager>();
        if (propAud == null)
        {
            propAud = npc.gameObject.AddComponent<PropagatedAudioManager>();
            propAud.audioDevice = npc.gameObject.AddComponent<AudioSource>();
        }
        npc.propAudMan = propAud;

        // 暗い灰色
        Color anubisColor = new Color(0.35f, 0.35f, 0.35f);
        SoundObject loopSound = SpriteHelper.LoadSound("Mus_Anutime.wav", "AnubisLoop", "Cursed Music", anubisColor);
        if (loopSound != null)
            npc.audLoop = loopSound;

        MyPlugin.Log.LogInfo("Anubis registered.");
    }
}

public class AnubisState_Wander : NpcState, IEntityTrigger
{
    Anubis anubis;

    public AnubisState_Wander(Anubis npc) : base(npc) { anubis = npc; }

    public override void Enter()
    {
        base.Enter();
        ChangeNavigationState(new NavigationState_WanderRandom(npc, 0));
    }

    public override void Update()
    {
        base.Update();

        // プレイヤーが視界に入ったら追跡ステートへ移行（ネームタグ中は無視）
        if (npc.looker.PlayerInSight())
        {
            PlayerManager pm = npc.ec.Players[0];
            if (SpriteHelper.HasNametag(pm)) return;
            float dist = Vector3.Distance(npc.transform.position, pm.transform.position);
            if (dist < 3f)
            {
                anubis.ActivateAbility(pm);
                return;
            }
            npc.behaviorStateMachine.ChangeState(new AnubisState_Chase(anubis, pm));
        }
    }

    public void EntityTriggerEnter(Entity otherEntity, Collider other, bool validCollision)
    {
        if (!validCollision) return;
        PlayerManager pm = otherEntity?.GetComponent<PlayerManager>();
        if (pm == null) pm = other.GetComponentInParent<PlayerManager>();
        if (pm != null) anubis.ActivateAbility(pm);
    }

    public void EntityTriggerStay(Entity otherEntity, Collider other, bool validCollision)
    {
        if (!validCollision) return;
        PlayerManager pm = otherEntity?.GetComponent<PlayerManager>();
        if (pm == null) pm = other.GetComponentInParent<PlayerManager>();
        if (pm != null) anubis.ActivateAbility(pm);
    }

    public void EntityTriggerExit(Entity otherEntity, Collider other, bool validCollision) { }
}

public class AnubisState_Chase : NpcState, IEntityTrigger
{
    Anubis anubis;
    PlayerManager target;

    public AnubisState_Chase(Anubis npc, PlayerManager player) : base(npc)
    {
        anubis = npc;
        target = player;
    }

    public override void Enter()
    {
        base.Enter();
        ChangeNavigationState(new NavigationState_TargetPlayer(npc, 63, target.transform.position));
    }

    public override void Update()
    {
        base.Update();

        // ネームタグ中は追跡解除して徘徊に戻る
        if (SpriteHelper.HasNametag(target))
        {
            npc.behaviorStateMachine.ChangeState(new AnubisState_Wander(anubis));
            return;
        }

        // 追跡中は毎フレーム位置を更新（Playtimeの PersuePlayer と同じパターン）
        npc.behaviorStateMachine.CurrentNavigationState.UpdatePosition(target.transform.position);

        // 距離で触れたか判定（トリガーが効かない場合の保険）
        float dist = Vector3.Distance(npc.transform.position, target.transform.position);
        if (dist < 3f)
        {
            MyPlugin.Log.LogInfo("[Anubis] Distance trigger! dist=" + dist);
            anubis.ActivateAbility(target);
        }

        // 視界を失ったらランダム徘徊に戻る
        if (!npc.looker.PlayerInSight())
            npc.behaviorStateMachine.ChangeState(new AnubisState_Wander(anubis));
    }

    public void EntityTriggerEnter(Entity otherEntity, Collider other, bool validCollision)
    {
        if (!validCollision) return;
        PlayerManager pm = otherEntity?.GetComponent<PlayerManager>();
        if (pm == null) pm = other.GetComponentInParent<PlayerManager>();
        if (pm != null) anubis.ActivateAbility(pm);
    }

    public void EntityTriggerStay(Entity otherEntity, Collider other, bool validCollision)
    {
        if (!validCollision) return;
        PlayerManager pm = otherEntity?.GetComponent<PlayerManager>();
        if (pm == null) pm = other.GetComponentInParent<PlayerManager>();
        if (pm != null) anubis.ActivateAbility(pm);
    }

    public void EntityTriggerExit(Entity otherEntity, Collider other, bool validCollision) { }
}