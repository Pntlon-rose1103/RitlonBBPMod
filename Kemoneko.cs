using MTM101BaldAPI;
using MTM101BaldAPI.ObjectCreation;
using MTM101BaldAPI.Registers;
using System.Collections.Generic;
using UnityEngine;

public class Kemoneko : NPC
{
    const float PUSH_SPEED = 20f;
    const float PUSH_ACCEL = -10f;
    const float PLAYER_TOUCH_MAX = 10f;
    const float PUSH_INTERVAL = 0.1f; // 同じ相手を弾く間隔

    // プレイヤー以外を弾いた時に Kemoneko 自身へ与える反動ノックバック
    const float SELF_RECOIL_SPEED = 3f;
    const float SELF_RECOIL_ACCEL = -10f;

    private float playerTouchTimer = 0f;
    private PlayerManager touchingPlayer = null;
    private PropagatedAudioManager propAudMan;
    private Entity selfEntity;

    private List<Cell> hallTiles = new List<Cell>();
    private Dictionary<Entity, float> lastPushTime = new Dictionary<Entity, float>();

    public override void Initialize()
    {
        base.Initialize();
        propAudMan = GetComponent<PropagatedAudioManager>();
        selfEntity = GetComponent<Entity>();

        // 通常のスポーン位置はゲームが設定するので上書きしない
        // ホールタイルリストは参照用のみ
        for (int x = 0; x < ec.levelSize.x; x++)
        {
            for (int z = 0; z < ec.levelSize.z; z++)
            {
                Cell cell = ec.cells[x, z];
                if (!cell.Null && cell.room != null
                    && cell.room.type == RoomType.Hall)
                {
                    hallTiles.Add(cell);
                }
            }
        }

        // 速度0で静止
        navigator.SetSpeed(0f);
        navigator.maxSpeed = 0f;

        // コライダーを solid にして物理的にブロック（サイズも拡大）
        foreach (Collider col in GetComponentsInChildren<Collider>())
        {
            col.isTrigger = false;
            CapsuleCollider cap = col as CapsuleCollider;
            if (cap != null)
            {
                cap.radius = 1.5f;
                cap.height = 5f;
            }
        }

        // 他NPCのナビゲーションから壁として扱われるよう、コライダーのレイヤーを壁と同じに設定
        // BBPの壁レイヤーを名前で取得（"Wall" → "Obstacle" → "Environment" の順で試す）
        int wallLayer = LayerMask.NameToLayer("Wall");
        if (wallLayer < 0) wallLayer = LayerMask.NameToLayer("Obstacle");
        if (wallLayer < 0) wallLayer = LayerMask.NameToLayer("Environment");
        if (wallLayer >= 0)
        {
            // コライダーを持つオブジェクトのみ壁レイヤーに（スプライトは元のまま）
            foreach (Collider col in GetComponentsInChildren<Collider>())
                col.gameObject.layer = wallLayer;
            MyPlugin.Log.LogInfo("[Kemoneko] Colliders set to wall layer: " + wallLayer);
        }
        else
        {
            MyPlugin.Log.LogWarning("[Kemoneko] Wall layer not found! NPCs may still path through.");
        }

        behaviorStateMachine.ChangeState(new KemonekoState_Block(this));
    }

    public void PushEntity(Entity ent, bool isPlayer = false)
    {
        if (ent == null) return;
        // 同じ相手は PUSH_INTERVAL 秒に1回だけ弾く
        if (lastPushTime.TryGetValue(ent, out float last) && Time.time - last < PUSH_INTERVAL) return;
        lastPushTime[ent] = Time.time;

        Vector3 dir = (ent.transform.position - transform.position).normalized;
        ent.AddForce(new Force(dir, PUSH_SPEED, PUSH_ACCEL));

        // プレイヤー以外（NPC等）を弾いた場合は、その反動で Kemoneko 自身も少しノックバックする
        if (!isPlayer && selfEntity != null)
        {
            Vector3 recoilDir = -dir;
            selfEntity.AddForce(new Force(recoilDir, SELF_RECOIL_SPEED, SELF_RECOIL_ACCEL));
        }

        if (ToyBazookaProjectile.SquishSound != null)
        {
            // オレンジ
            SpriteHelper.SetSubtitle(ToyBazookaProjectile.SquishSound, "Boyooong", new Color(1f, 0.55f, 0.1f));
            if (propAudMan != null)
            {
                SpriteHelper.ApplySubtitleColor(propAudMan, ToyBazookaProjectile.SquishSound);
                propAudMan.PlaySingle(ToyBazookaProjectile.SquishSound);
            }
            else
            {
                SpriteHelper.ApplySubtitleColor(Singleton<CoreGameManager>.Instance.audMan, ToyBazookaProjectile.SquishSound);
                Singleton<CoreGameManager>.Instance.audMan.PlaySingle(ToyBazookaProjectile.SquishSound);
            }
        }
    }

    public void UpdatePlayerContact(PlayerManager pm)
    {
        if (pm == null)
        {
            playerTouchTimer = 0f;
            touchingPlayer = null;
            return;
        }

        if (touchingPlayer != pm)
        {
            touchingPlayer = pm;
            playerTouchTimer = 0f;
        }

        playerTouchTimer += Time.deltaTime * TimeScale;
        if (playerTouchTimer >= PLAYER_TOUCH_MAX)
        {
            playerTouchTimer = 0f;
            PushEntity(pm.plm.Entity, isPlayer: true);
        }
    }

    public void ClearPlayerContact()
    {
        playerTouchTimer = 0f;
        touchingPlayer = null;
    }

    public static void Register()
    {
        Kemoneko npc = new NPCBuilder<Kemoneko>(MyPlugin.Instance.Info)
            .SetName("Kemoneko")
            .SetEnum("Kemoneko")
            .SetMetaName("Kemoneko")
            .SetPoster(
                SpriteHelper.LoadTexture("kemoneko_poster.png"),
                "Kemoneko",
                "x.com/kemoneko2603\nBlocks the halls.\nDon't touch for too long!")
            .AddTrigger()
            .AddLooker()
            .AddSpawnableRoomCategories(RoomCategory.Hall)
            .Build();

        Sprite sprite = SpriteHelper.LoadSprite("kemoneko.png");
        npc.spriteRenderer[0].sprite = sprite;
        EditorSprites.Register("Kemoneko", sprite);
        // いじめっ子と同程度のサイズに拡大
        npc.spriteRenderer[0].transform.localScale = new Vector3(3.25f, 3.25f, 3.25f);
        MyPlugin.Log.LogInfo("Kemoneko registered.");
    }
}

public class KemonekoState_Block : NpcState, IEntityTrigger
{
    Kemoneko cat;

    public KemonekoState_Block(Kemoneko npc) : base(npc) { cat = npc; }

    public override void Enter()
    {
        base.Enter();
        ChangeNavigationState(new NavigationState_DoNothing(npc, 0));
    }

    public override void Update()
    {
        base.Update();

        // 近くのNPCを毎フレーム押し返す（通り抜け防止）
        foreach (NPC other in npc.ec.Npcs)
        {
            if (other == npc) continue;
            Entity otherEnt = other.GetComponent<Entity>();
            if (otherEnt == null) continue;
            float dist = Vector3.Distance(npc.transform.position, other.transform.position);
            if (dist < 5f && dist > 0.01f)
                cat.PushEntity(otherEnt);
        }

        // プレイヤーの接触タイマー
        if (npc.looker.PlayerInSight())
        {
            PlayerManager pm = npc.ec.Players[0];
            float dist = Vector3.Distance(
                npc.transform.position, pm.transform.position);

            if (dist < 8f)
                cat.UpdatePlayerContact(pm);
            else
                cat.ClearPlayerContact();
        }
        else
        {
            cat.ClearPlayerContact();
        }
    }

    public void EntityTriggerEnter(
        Entity otherEntity, Collider other, bool validCollision)
    {
        if (!validCollision) return;
        if (other.CompareTag("Player")) return;
        if (otherEntity == null) return;
        cat.PushEntity(otherEntity);
    }

    public void EntityTriggerStay(
        Entity otherEntity, Collider other, bool validCollision)
    {
        if (!validCollision) return;
        if (other.CompareTag("Player")) return;
        if (otherEntity == null) return;
        cat.PushEntity(otherEntity);
    }

    public void EntityTriggerExit(
        Entity otherEntity, Collider other, bool validCollision)
    { }
}
