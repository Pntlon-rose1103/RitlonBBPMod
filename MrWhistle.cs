using MTM101BaldAPI;
using MTM101BaldAPI.ObjectCreation;
using MTM101BaldAPI.Registers;
using System.Collections;
using System.Reflection;
using UnityEngine;

public class MrWhistle : NPC
{
    const float WHISTLE_INTERVAL = 2f;
    const float DISABLED_DURATION = 180f;
    const float MOVE_SPEED = 24f;

    bool isDisabled = false;
    float whistleTimer = 0f;
    private SoundObject whistleSfx;
    private PropagatedAudioManager propAudMan;

    public override void Initialize()
    {
        base.Initialize();
        navigator.SetSpeed(MOVE_SPEED);
        navigator.maxSpeed = MOVE_SPEED;
        propAudMan = GetComponent<PropagatedAudioManager>();

        foreach (var item in Resources.FindObjectsOfTypeAll<ItemObject>())
        {
            if (item.itemType == Items.PrincipalWhistle)
            {
                ITM_PrincipalWhistle w = item.item as ITM_PrincipalWhistle;
                if (w != null)
                    whistleSfx = (SoundObject)typeof(ITM_PrincipalWhistle)
                        .GetField("audWhistle",
                            BindingFlags.NonPublic | BindingFlags.Instance)
                        .GetValue(w);
                break;
            }
        }

        behaviorStateMachine.ChangeState(new MrWhistleState_Wander(this));
    }

    public void OnPlayerSeen(PlayerManager player)
    {
        if (isDisabled) return;
        whistleTimer += Time.deltaTime * TimeScale;
        if (whistleTimer >= WHISTLE_INTERVAL)
        {
            whistleTimer = 0f;
            BlowWhistle(player);
        }
    }

    public void ResetWhistleTimer() { whistleTimer = 0f; }

    void BlowWhistle(PlayerManager player)
    {
        ec.MakeNoise(player.transform.position, 127);
        if (whistleSfx != null && propAudMan != null)
        {
            SpriteHelper.SetSubtitle(whistleSfx, "Whistle");
            SpriteHelper.ApplySubtitleColor(propAudMan, whistleSfx);
            propAudMan.PlaySingle(whistleSfx);
        }
        MyPlugin.Log.LogInfo("Mr. Whistle blew the whistle!");
    }

    public void Repel()
    {
        if (isDisabled) return;
        StartCoroutine(DisableRoutine());
    }

    IEnumerator DisableRoutine()
    {
        isDisabled = true;
        navigator.SetSpeed(0f);
        behaviorStateMachine.ChangeState(new MrWhistleState_Disabled(this));
        yield return new WaitForSeconds(DISABLED_DURATION);
        isDisabled = false;
        navigator.SetSpeed(MOVE_SPEED);
        navigator.maxSpeed = MOVE_SPEED;
        behaviorStateMachine.ChangeState(new MrWhistleState_Wander(this));
    }

    public bool IsDisabled => isDisabled;

    public static void Register()
    {
        MrWhistle npc = new NPCBuilder<MrWhistle>(MyPlugin.Instance.Info)
            .SetName("Mr. Whistle")
            .SetEnum("MrWhistle")
            .SetMetaName("MrWhistle")
            .SetPoster(
                SpriteHelper.LoadTexture("mrwhistle_poster.png"),
                "Mr. Whistle",
                "Baldi's best friend,\nthe gym teacher.\nLoves his whistle.")
            .AddLooker()
            .AddSpawnableRoomCategories(RoomCategory.Office)
            .Build();

        var spr = SpriteHelper.LoadSprite("mrwhistle.png");
        npc.spriteRenderer[0].sprite = spr;
        EditorSprites.Register("MrWhistle", spr);
        MyPlugin.Log.LogInfo($"MrWhistle registered. spr={(spr != null ? spr.name : "NULL")} cached={EditorSprites.Get("MrWhistle") != null}");
    }
}

public class MrWhistleState_Wander : NpcState
{
    MrWhistle whistle;
    public MrWhistleState_Wander(MrWhistle npc) : base(npc) { whistle = npc; }

    public override void Enter()
    {
        base.Enter();
        ChangeNavigationState(new NavigationState_WanderRandom(npc, 0));
    }

    public override void Update()
    {
        base.Update();
        if (whistle.IsDisabled) return;
        if (npc.looker.PlayerInSight())
            whistle.OnPlayerSeen(npc.ec.Players[0]);
        else
            whistle.ResetWhistleTimer();
    }
}

public class MrWhistleState_Disabled : NpcState
{
    public MrWhistleState_Disabled(MrWhistle npc) : base(npc) { }

    public override void Enter()
    {
        base.Enter();
        npc.Navigator.SetSpeed(0f);
    }
}