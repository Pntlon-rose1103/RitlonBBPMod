using System.Collections.Generic;

// 【経緯】
// 当初は LocalizationManager.LoadLocalizedText() の Postfix で
// localizedText 辞書に Mod アイテムの文言を追記する方式を試した。
// しかし実機ログで確認したところ、プレイセッション中に
// LoadLocalizedText が一度も呼ばれておらず（Prefix/Postfixとも発火なし）、
// パッチの登録自体は成功していても実行タイミングが来ないことが判明した。
//
// 【対策①（失敗）】
// GetLocalizedText() 自体に [HarmonyPatch] 属性ベースで Prefix を仕込む方式に
// 変更したが、実機ログで確認したところ
// 「GetLocalizedText(string) Prefixes=0」「GetLocalizedText(string,bool) Prefixes=0」
// となっており、メソッド自体は解決できている（resolved: True）にもかかわらず
// harmony.PatchAll() によるこのクラスへのパッチ登録そのものが行われていないことが
// 判明した（他のクラス、例えば AddVanillaNPCsPatch 等は PatchAll() で正常に
// 登録・発火しているため、原因はこのクラス固有の属性の組み合わせにあると推測される）。
//
// 【対策②（採用・成功）】
// 属性ベースの自動検出に依存せず、Plugin.cs の Awake() 内で
// harmony.Patch(methodInfo, prefix: new HarmonyMethod(...)) を使って
// このクラスの Prefix メソッドを明示的・手動でパッチする方式に変更した。
// このクラス自体はもう [HarmonyPatch] 属性を持たない、ただのロジック置き場。
// → 実機確認の結果、ショップのポップアップで説明文が表示されることを確認。
//
// 【追加調整①】
// バニラアイテム（例: Techno-Boots）のショップポップアップは
// 「アイテム名（太字1行目）\n説明文（2行目）」という1つの文字列で構成されている。
// Mod アイテムの説明文（*_desc キー）には説明文だけを入れていたため、
// 1行目のアイテム名が表示されていなかった。
// → 各 *_desc の値の先頭に「アイテム名\n」を付加して解決。
//
// 【追加調整②】
// Plus Level Studio のアイテムツール詳細ポップアップは、上記のショップ説明文とは
// 別に "Ed_Tool_item_<alias(小文字)>_Desc" というキーで説明文を取得している
// （タイトル行は別経路で正しく表示されていたため、こちらは素の説明文のみでよい）。
// このキーがModText辞書に無かったため生キー文字列がそのまま表示されていた。
// → Studio用のキー（Ed_Tool_item_*）も辞書に追加して解決。
//
// 【追加③ PartyHat】
// パーティハット追加に伴い、通常プレイ用キーとStudio用キーの両方を追加。
public static class LocalizationManager_GetLocalizedText_Patch
{
    static readonly Dictionary<string, string> ModText = new Dictionary<string, string>
    {
        // ---- 通常プレイ中（ショップ等）のアイテム名・説明文 ----
        // ショップのポップアップは「アイテム名\n説明文」の1本の文字列を
        // desc キーから取得して表示しているため、説明文側に名前+改行を含める
        { "ToyBazooka",            "Toy Bazooka" },
        { "ToyBazooka_desc",       "Toy Bazooka\nKnocks NPCs into walls!" },
        { "ManualTeleporter",      "Manual Teleportation Machine" },
        { "ManualTeleporter_desc", "Manual Teleportation Machine\nTeleports you to the nearest elevator!" },
        { "Present",               "Present" },
        { "Present_desc",          "Present\nSpawns a random item in front of you!" },
        { "PartyHat",               "Party Hat" },
        { "PartyHat_desc",          "Party Hat\nStarts a party!" },
        { "ACola",                  "ACOLA" },
        { "ACola_desc",             "ACOLA\nB Soda's rival — its overflowing fizz blocks your sight too!" },

        // ---- メインメニューの Extreme モードボタン ----
        { "Men_Extreme",            "EX" },
        { "Men_Extreme_Desc",       "Let's play Extreme Baldi!!!!" },
        { "Men_Extreme_Reset",      "Reset Record" },
        { "Men_Extreme_Continue",   "Continue" },

        // ---- Plus Level Studio（エディタ）のアイテムツール用キー ----
        // ItemTool 側は "Ed_Tool_item_<alias>" / "Ed_Tool_item_<alias>_Desc"
        // （alias は小文字: toybazooka / manualteleporter / present / partyhat）という
        // 独自のキーでタイトル・説明文を参照しているため、別途登録しておく。
        // タイトルは他経路で既に正しく表示されていたが、念のため同じ文言で登録。
        { "Ed_Tool_item_toybazooka",            "Toy Bazooka" },
        { "Ed_Tool_item_toybazooka_Desc",       "Knocks NPCs into walls!" },
        { "Ed_Tool_item_manualteleporter",      "Manual Teleportation Machine" },
        { "Ed_Tool_item_manualteleporter_Desc", "Teleports you to the nearest elevator!" },
        { "Ed_Tool_item_present",               "Present" },
        { "Ed_Tool_item_present_Desc",          "Spawns a random item in front of you!" },
        { "Ed_Tool_item_partyhat",              "Party Hat" },
        { "Ed_Tool_item_partyhat_Desc",         "Starts a party!" },
        { "Ed_Tool_item_acola",                 "ACOLA" },
        { "Ed_Tool_item_acola_Desc",            "B Soda's rival — its overflowing fizz blocks your sight too!" },
    };

    // public string GetLocalizedText(string key)
    // Plugin.cs の Awake() から harmony.Patch(...) で手動Prefix登録される
    public static bool Prefix_OneArg(string key, ref string __result)
    {
        if (key != null && ModText.TryGetValue(key, out string value))
        {
            __result = value;
            return false; // 元のメソッドをスキップ
        }
        return true; // Mod アイテムのキーでなければ通常通り処理
    }

    // public string GetLocalizedText(string key, bool encrypted)
    // Plugin.cs の Awake() から harmony.Patch(...) で手動Prefix登録される
    public static bool Prefix_TwoArg(string key, bool encrypted, ref string __result)
    {
        // Mod側テキストは暗号化されていない平文なので、encrypted=true指定の
        // 呼び出しに対しても平文をそのまま返す（復号処理をスキップできるため安全）
        if (key != null && ModText.TryGetValue(key, out string value))
        {
            __result = value;
            return false;
        }
        return true;
    }
}
