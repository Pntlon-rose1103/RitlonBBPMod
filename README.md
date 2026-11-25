# RitlonBBPMod

Baldi's Basics Plus（v0.14.x）向けの Mod です。NPC・アイテムの追加と、Plus Level Studio への対応を含みます。

## 必要な環境
- Baldi's Basics Plus v0.14.x（Unity 2020.3.49）
- BepInEx 5.4.21
- MTM101BaldAPI 11.3.0.0
- Newtonsoft.Json.dll — MTM101BaldAPI のダウンロードに自動的に付いてくるため、別途入手する必要はありません
- （任意）Plus Level Studio / Level Loader — 入っていればエディタに Mod のNPC・アイテムが追加されます。なくても動作します

## ビルド
1. Visual Studio でプロジェクトを開く
2. 参照 DLL（Assembly-CSharp、UnityEngine 系、0Harmony、BepInEx、MTM101BaldAPI）の HintPath が、作者の Steam インストール先を指しています。自分の環境のパスに直してください（DLL はリポジトリに含めていません）
3. 署名用の `RitlonBiten.pfx` は非公開です。ファイルがない環境では署名なしでビルドされます
4. ビルドした DLL を `BepInEx/plugins` に置く

## 配布
GameBanana にて公開予定。バージョンごとの変更点は `;CHANGELOG.md` を参照してください。
