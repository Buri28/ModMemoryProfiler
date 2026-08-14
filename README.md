# ModMemoryProfiler

**試作段階です**

Beat Saber で「長時間プレイするとカクつく」原因MODを特定するための診断MOD。

**MOD別のアセットメモリ使用量（MB）とフレーム時間（ms/frame）を計測し、CSV に出力する。**

対象: Beat Saber 1.40.8 / BSIPA 4.3+ / BSML 1.12+

[イメージ]

<img width="44%" height="44%" alt="image" src="https://github.com/user-attachments/assets/4ae8809c-f9ec-45af-8b48-a65197715728" />


## これは何をするMODか

Mono の GC ヒープは全MOD共有なので、後からメモリを見ても「どのMODが食っているか」は分からない。
このMODは **アセットが生成された瞬間にスタックトレースを遡って生成元MODを記録し**、
定期的に生存アセットを走査して実バイト数をMOD別に合計する。

さらに、曲終了ごとにスナップショットを取るので
**「1曲プレイするごとに何MB積み上がるMODか」**が差分で分かる。3時間待たなくても傾きが見える。

## これは何をしないか

- FPS / フレームタイムのグラフ表示はしない
- ゲームの挙動には一切干渉しない（計測のみ）

## 制約

- **テクスチャ・RenderTexture・メッシュ・音声は MB 単位でMOD別に出る。**
  最有力の容疑（カメラ系＝RenderTexture、カスタムアセット系＝Texture/Mesh）はこの層に乗る。
- **マネージドヒープ（純粋な C# オブジェクト）のMOD別 MB は原理的に取得できない。**
  インスタンス数の相対比較に留まる。
- 起動時から存在するアセットは生成をフックできないため `(Untracked)` になる。
  絶対値ではなく**スナップショット間の差分**で見ること。

## 出力

`<BeatSaberPath>\UserData\ModMemoryProfiler\session_yyyyMMdd_HHmmss.csv`

縦持ち（1行 = 1スナップショット × 1MOD）:

| 列 | 内容 |
|---|---|
| `timestamp` / `elapsedMin` | 記録時刻 / 起動からの経過分 |
| `phase` | `baseline` / `menu` / `song` / `songStart` / `songEnd` / `manual` |
| `songsPlayed` | それまでにプレイした曲数 |
| `mod` | MOD名。`(TOTAL)` はプロセス全体、`(BaseGame)` はゲーム本体、`(Untracked)` は生成元を記録できなかったもの |
| `textureMB` / `renderTextureMB` / `meshMB` / `audioMB` | 実バイト数（`Profiler.GetRuntimeMemorySizeLong`） |
| `textureCount` / `renderTextureCount` / `spriteCount` / `meshCount` / `audioCount` | 型別のインスタンス数 |
| `materialCount` / `gameObjectCount` / `monoBehaviourCount` | インスタンス数 |
| `unfreedBundles` | 未解放 AssetBundle 数（ロード数 − アンロード数） |
| `msPerFrame` | そのMODの `Update`/`LateUpdate`/`FixedUpdate` の合計時間 |

`(TOTAL)` 行だけは列を流用しているので注意:

| 列 | `(TOTAL)` 行での意味 |
|---|---|
| `textureMB` | マネージドヒープ MB（スナップショット時点の値） |
| `renderTextureMB` | Unity の確保総量 MB（ネイティブ側を含む） |
| `meshMB` | **マネージドヒープの区間内最小値（GC直後の「床」）** |
| `audioMB` | マネージドヒープの区間内最大値 |
| `materialCount` | GC 回数。Unity の GC は世代を持たないため 1 種類のみ |
| 個数の各列 | 全MOD合計のインスタンス数（意味の流用なし） |
| `unfreedBundles` | レート制限で生成元の記録を見送った累計件数 |

マネージドヒープが横ばいなのに Unity の確保総量だけ増える場合、ネイティブ側のリーク。
`gameObjectCount` / `monoBehaviourCount` が曲数に比例して増える場合はマネージド側の解放漏れ。

### マネージドリークの見つけ方

**`textureMB`（その瞬間の値）ではなく `meshMB`（区間内の最小値）を見ること。**

マネージドヒープの瞬間値は「ゴミが溜まっては GC で消える」ため大きく上下し、
1点だけを見ても傾向が読めない。見るべきは GC 直後の値、つまり区間内の最小値。

| `meshMB`（床）の推移 | 意味 |
|---|---|
| ほぼ一定 | 正常。上下しているのは一時的なゴミ |
| **単調増加** | **回収できないオブジェクトが積み上がっている＝マネージドリーク** |

マネージドヒープの MB を MOD 別に分けることは原理的にできない。
リークが見つかったら `Dump` を使い、`MonoBehaviour` の型別インスタンス数から
どのクラスが生き残っているかを特定する（型からアセンブリを引くので帰属は正確）。

`(TOTAL)` 行の `unfreedBundles` が 0 でない場合、MOD別の数値はその分だけ `(Untracked)` に
逃げている。`MaxOwnershipLookupsPerSecond` を上げて測り直すこと。

## 見方

1. `phase == "songEnd"` の行だけを抽出する
2. MOD別に **`liveAssetCount`（個数）** を曲数に対してプロットする
3. **単調増加しているMODがリーク元**

**MB より先に個数を見ること。** アイコンなどの小さなアセットは、1個あたり数十KBしかないため
MB はほとんど動かないまま個数だけが積み上がる。MB だけ見ていると「頭打ちになった＝正常」と
誤判定する。`spriteCount` が曲数に比例して増えていれば、その典型。

1回だけ増えて止まるのはキャッシュなので正常。曲数に比例して増え続けるものが本物。
`unfreedBundles` が曲数と一緒に増えるMODがあれば、それは決定的な証拠になる。

`msPerFrame` が突出しているMODがあれば、そちらはメモリではなくCPU側の原因。

計測ツール自体がリークしていないかも毎回確認すること。
`(BaseGame)` と `ModMemoryProfiler` 自身の数値が単調増加していないかを見る。

## ゲーム内UI

曲選択画面の **GameplaySetup パネルの `MemProfiler` タブ**に、
起動時からの増分でソートしたMOD別ランキングと主要な設定を表示する。

表示は定期スナップショットの使い回しなので、タブを開くだけでは計測負荷は増えない。
`Refresh` を押したときだけ走査が走る（その行は `phase=manual` として CSV にも残る）。

基準となるスナップショットは `baseline` → `menu` → `songEnd` の順に自動で昇格する。
`vs songEnd` になるまでは、表示される増分にメニューのロード分が含まれるので判定に使えない。

## 設定

<img width="50%" height="50%" alt="image" src="https://github.com/user-attachments/assets/c419e305-1797-45f4-affa-a1dbe361a9a6" />

`UserData/ModMemoryProfiler.json`（ゲーム内UIからも変更可能）

| キー | 既定 | 説明 |
|---|---|---|
| `Enabled` | `true` | false で計測もフックも行わない |
| `ShowInGameTab` | `true` | ゲーム内タブを表示する |
| `SampleIntervalSeconds` | `30` | スナップショット間隔 |
| `SampleDuringSong` | `false` | 曲中も走査する（フレーム落ちの可能性あり） |
| `UnloadUnusedAssetsOnMenu` | `false` | 曲終了後のメニューで `GC.Collect()` → `Resources.UnloadUnusedAssets()` を実行し、解放前後のスナップショットを両方記録する。詳細は下記 |
| `TrackOwnership` | `true` | 生成元MODの記録。切るとMOD別に分解できなくなる |
| `TrackInstantiate` | `true` | `Object.Instantiate` もフックする。最も高頻度な経路なので、重い場合はここを切る |
| `MaxOwnershipLookupsPerSecond` | `2000` | スタックトレース取得の上限。超過分は記録を見送る |
| `OutOfSongLookupMultiplier` | `20` | 曲中でないときに上限を緩める倍率。オブジェクトの大量生成はシーン遷移時に集中するため、そこを間引くと帰属が `(Untracked)` に落ちる |
| `EnableCpuProfiling` | `true` | MOD別フレーム時間の計測 |
| `CpuExcludeMods` | `""` | CPU計測から外すMOD名（`;` 区切り、部分一致） |
| `CountMonoBehaviours` | `true` | MonoBehaviour のインスタンス数を数える |
| `DebugLogging` | `false` | 詳細ログ |

`TrackOwnership` / `TrackInstantiate` / `EnableCpuProfiling` は起動時に Harmony パッチを
一括で当てるため、変更は次回起動から反映される。

## 「本物のリーク」と「解放されていないだけ」の切り分け

Unity は参照が切れたアセットを自動では解放せず、`Resources.UnloadUnusedAssets()` が
呼ばれるかシーンが破棄されるまでメモリに残し続ける。したがって
「オブジェクトが積み上がっている」だけでは、どちらなのか判断できない。

`UnloadUnusedAssetsOnMenu` を有効にすると、曲終了後のメニューで実際に解放を試み、
その前後を `songEnd` 行と `afterUnload` 行として CSV に記録する。

| 差分 | 意味 |
|---|---|
| 大きく減る | 参照は切れていた。定期的な解放で解決できる |
| ほとんど減らない | どこかが参照を掴んでいる。**本物のリーク** |

`afterUnload` 行の `(TOTAL)` の `msPerFrame` 列には、解放処理の所要時間（ミリ秒）が入る。
実用に耐える速さかの判断に使う。数百ms〜秒単位かかる重い処理のため、曲中には実行しない。
