# RitlonBBPMod

Baldi's Basics Plus（v0.14.x）向けの Mod です。NPC・アイテムの追加と、Plus Level Studio への対応を含みます。

## 必要な環境
- Baldi's Basics Plus v0.14.x（Unity 2020.3.49）
- BepInEx 5.4.21
- MTM101BaldAPI 11.3.0.0
- （任意）Plus Level Studio / Level Loader — 入っていればエディタに Mod のNPC・アイテムが追加されます。なくても動作します

## ビルド
1. Visual Studio でプロジェクトを開く
2. ゲームの `Managed` フォルダ、BepInEx、MTM101BaldAPI などの DLL を参照に追加する（リポジトリには含めていません）
3. ビルドした DLL を `BepInEx/plugins` に置く

## 配布
GameBanana にて公開予定。バージョンごとの変更点は `CHANGELOG.md` を参照してください。
