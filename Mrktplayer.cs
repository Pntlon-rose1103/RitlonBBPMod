using MTM101BaldAPI;
using MTM101BaldAPI.ObjectCreation;
using MTM101BaldAPI.Registers;
using System.Reflection;
using UnityEngine;

public class MrKtPlayer : NPC
{
    private bool activated = false;
    const float VERTICAL_TOLERANCE = 2.5f; // これ以上高さが違うと発動しない

    public override void Initialize()
    {
        base.Initialize();

        // Kemoneko同様、静止
        navigator.SetSpeed(0f);
        navigator.maxSpeed = 0f;

        // 物理ブロック：コライダーをソリッドにして横幅だけ拡大
        // （縦の高さはバグの原因になるため変更しない＝通常サイズ）
        foreach (Collider col in GetComponentsInChildren<Collider>())
        {
            col.isTrigger = false;
            CapsuleCollider cap = col as CapsuleCollider;
            if (cap != null)
                cap.radius = 1.5f;
        }

        behaviorStateMachine.ChangeState(new MrKtState_Wait(this));
    }

    public void TryActivate(PlayerManager pm)
    {
        if (activated) return;
        // 縦方向の当たり判定が広すぎてベント内でも発動していた対策:
        // 高さがずれている(ベントの中など)場合は発動しない
        if (Mathf.Abs(pm.transform.position.y - transform.position.y) > VERTICAL_TOLERANCE) return;
        try { if (pm.plm != null && pm.plm.Entity != null && pm.plm.Entity.InteractionDisabled) return; } catch { }
        // スクワッシュ中は能力発動しない
        Entity ent = GetComponent<Entity>();
        if (ent != null && ent.Squished) return;
        // ネームタグ中はアイテム置き換えしない
        if (SpriteHelper.HasNametag(pm)) return;
        activated = true;

        // ダイエットBsodaのItemObjectを取得
        ItemObject dietBsoda = null;
        foreach (ItemObject io in Resources.FindObjectsOfTypeAll<ItemObject>())
        {
            if (io.itemType == Items.DietBsoda)
            {
                dietBsoda = io;
                break;
            }
        }

        if (dietBsoda == null)
        {
            MyPlugin.Log.LogWarning("[MrKtPlayer] DietBsoda ItemObject not found!");
        }
        else
        {
            // スロット2〜4（インデックス1〜3）をダイエットBsodaに変換
            for (int i = 1; i <= 3; i++)
            {
                if (i <= pm.itm.maxItem)
                {
                    pm.itm.SetItem(dietBsoda, i);
                    MyPlugin.Log.LogInfo("[MrKtPlayer] Set slot " + (i + 1) + " to DietBsoda.");
                }
            }
        }

        // audTeleport を ITM_Teleporter から取得して再生
        foreach (ItemObject io in Resources.FindObjectsOfTypeAll<ItemObject>())
        {
            if (io.itemType == Items.Teleporter && io.item != null)
            {
                ITM_Teleporter orig = io.item as ITM_Teleporter;
                if (orig != null)
                {
                    SoundObject audTeleport = (SoundObject)typeof(ITM_Teleporter)
                        .GetField("audTeleport", BindingFlags.NonPublic | BindingFlags.Instance)
                        .GetValue(orig);
                    if (audTeleport != null)
                    {
                        SpriteHelper.SetSubtitle(audTeleport, "#########", new Color(1f, 0.6f, 0.75f));
                        SpriteHelper.ApplySubtitleColor(Singleton<CoreGameManager>.Instance.audMan, audTeleport);
                        Singleton<CoreGameManager>.Instance.audMan.PlaySingle(audTeleport);
                    }
                    break;
                }
            }
        }

        // 発動後即デスポーン
        MyPlugin.Log.LogInfo("[MrKtPlayer] Despawning.");
        gameObject.SetActive(false);
        ec.Npcs.Remove(this);
        Destroy(gameObject);
    }

    public static void Register()
    {
        MrKtPlayer npc = new NPCBuilder<MrKtPlayer>(MyPlugin.Instance.Info)
            .SetName("Mr.ktPlayer")
            .SetEnum("MrKtPlayer")
            .SetMetaName("MrKtPlayer")
            .SetPoster(
                SpriteHelper.LoadTexture("mrktplayer_poster.png"),
                "Mr.ktPlayer",
                "The Everyday Monster\nWho Frequents\nHamburger Chains.")
            .AddTrigger()
            .AddLooker()
            .AddSpawnableRoomCategories(RoomCategory.Hall)
            .Build();

        var ktSprite = SpriteHelper.LoadSprite("mrktplayer.png");
        npc.spriteRenderer[0].sprite = ktSprite;
        EditorSprites.Register("MrKtPlayer", ktSprite);
        npc.spriteRenderer[0].transform.localScale = new Vector3(3.25f, 3.25f, 3.25f);
        MyPlugin.Log.LogInfo("MrKtPlayer registered.");
    }
}

public class MrKtState_Wait : NpcState, IEntityTrigger
{
    MrKtPlayer kt;

    public MrKtState_Wait(MrKtPlayer npc) : base(npc) { kt = npc; }

    public override void Enter()
    {
        base.Enter();
        ChangeNavigationState(new NavigationState_DoNothing(npc, 0));
    }

    public override void Update()
    {
        base.Update();

        // Anubis同様の距離判定
        if (npc.ec.Players == null || npc.ec.Players.Length == 0) return;
        PlayerManager pm = npc.ec.Players[0];
        if (pm == null) return;

        Vector3 dv = npc.transform.position - pm.transform.position; dv.y = 0f; // 水平距離のみ
        float dist = dv.magnitude;
        if (dist < 3f)
        {
            MyPlugin.Log.LogInfo("[MrKtPlayer] Player in range! dist=" + dist);
            kt.TryActivate(pm);
        }
    }

    public void EntityTriggerEnter(Entity otherEntity, Collider other, bool validCollision)
    {
        if (!validCollision) return;
        PlayerManager pm = otherEntity?.GetComponent<PlayerManager>();
        if (pm == null) pm = other.GetComponentInParent<PlayerManager>();
        if (pm != null) kt.TryActivate(pm);
    }

    public void EntityTriggerStay(Entity otherEntity, Collider other, bool validCollision)
    {
        if (!validCollision) return;
        PlayerManager pm = otherEntity?.GetComponent<PlayerManager>();
        if (pm == null) pm = other.GetComponentInParent<PlayerManager>();
        if (pm != null) kt.TryActivate(pm);
    }

    public void EntityTriggerExit(Entity otherEntity, Collider other, bool validCollision) { }
}