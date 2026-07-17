# AutoVJ

Windows 11でLINE入力を監視し、登録動画の音声を検出して対応動画をブラウザ上で同期再生するオートVJプロトタイプです。

現在は画面分離、WASAPIループバック入力、BPM安全補正を追加した **M0.7** です。最終的な企画は `auto_vj_project_spec.md`、現在の実装仕様は `prototype_spec.md`、ユーザーからの次の指示は `prompt.md` を参照してください。

## 現在できること

- 独立した `AutoVJ.Catalog` で `main-videos` の新規・更新MP4だけを差分解析
- 再生アプリを止めずに解析結果を共有SQLite DBへ反映
- 更新時も既存の動画IDを維持し、削除された動画だけをDBから除去
- FFmpegで動画音声を11,025Hz・モノラルPCMへ変換
- 旧4帯域ピーク指紋とM0.5の8帯域局所スペクトルピーク指紋をSQLiteへ保存
- 指紋方式名・バージョンを保存し、未再解析レコードは旧方式で検出を継続
- 登録時に基準BPM・推定信頼度・解析元を保存
- キーロックを維持した `0.90〜1.17` の時間軸倍率候補を照合
- WindowsのWASAPI録音エンドポイントからLINE入力を取得
- Windowsの再生エンドポイントからWASAPIループバック入力を取得
- 4秒の音声窓を1秒ごとに登録曲へ照合
- 62%以上は即時確定し、55%以上62%未満は同じ候補を2回確認して確定
- -48dB未満の入力を無音として照合対象外にする
- 検出曲、信頼度、推定位置、基準BPM、入力推定BPM、テンポ倍率をローカルWeb UIへ表示
- 検出テンポ倍率をHTML Videoの `playbackRate` へ反映
- 再生倍率を設定可能な下限・上限へ制限
- BPMが設定範囲外の場合、倍テン・半テンとして範囲内へ補正
- 同曲再検出時に位置差と再生速度差を補正し、汎用動画では速度を `1.0` へ復帰
- 対応動画を推定位置から再生し、ずれが2秒を超えた場合に再同期
- 未検出中は直前の動画をそのまま進め、新しい位置検出時だけシーク
- 2枚の動画レイヤーで汎用動画・検出動画・別の検出動画をクロスフェード
- 設定したCSSブレンドモード候補からサーバー側で切り替えごとにランダム選択
- 複数の再生タブでブレンドモード、グリッチ素材、汎用動画開始位置を共有
- ブレンド演出を無効化して通常クロスフェードだけにする設定
- 暗転しやすいブレンドモードでは、黒背景を露出させない二段階切り替えを使用
- 切り替え後は新しい動画レイヤーを再生対象として追跡し、旧レイヤーの待機で状態更新が停止しないように制御
- 動画メタデータを10秒以内に読み込めない場合はタイムアウトして状態更新を再開
- 起動直後と曲未検出時に `common_movie.mp4` を強制ループ再生
- 検出を10秒間失った場合に汎用動画へ復帰
- 入力信号レベルをdBメーターで表示
- 監視中の音声入力を止めずに入力デバイスを切り替え
- サーバー起動後にOS既定ブラウザで操作画面を自動表示
- 映像領域のブラウザ全画面表示
- 通常起動直後に既定入力デバイスの監視を自動開始
- 1つのボタンで音声入力を一時停止・再開
- ウェルカムページ、再生専用ページ、設定専用ページを分離
- 設定ページから入力元、再生、BPM範囲、クロスフェード、ブレンド、グリッチしきい値を保存して即時反映
- 登録動画一覧を設定ページ内の専用スクロール領域へ表示
- 信頼度低下から汎用動画復帰まで、設定したグリッチ動画を加算系表示
- 汎用動画へ切り替える際、設定に応じてランダム位置から再生

## 必要環境

- Windows 11 64bit
- .NET SDK 10
- PATHから実行できる `ffmpeg` と `ffprobe`
- NAudioから参照できるWindows録音デバイス

現在PCに導入されているFFmpegはGPLビルドです。ローカル開発確認には使用できますが、商用配布物へは含めません。配布段階では企画書どおり、GPL/nonfreeを無効にしたLGPLビルドとライセンス文書へ差し替えます。

## 素材と確認用音源の配置

```text
input-audio/
├─ 検出確認用の任意の名前.mp3
└─ 検出確認用の任意の名前.wav

main-videos/
└─ 登録・再生対象の任意の名前.mp4

material-videos/
└─ common_movie.mp4
```

指紋DBは `main-videos` のMP4に含まれる音声トラックだけから生成します。`input-audio` は本番の登録処理には使用せず、自己診断やLINE入力へ流す検出確認用です。MP3とMP4のファイル名を一致させる必要はありません。

## 設定

`config.yaml` で素材フォルダ、録音デバイス、検出間隔、信頼度しきい値などを変更できます。

現在の入力デバイス設定は次のとおりです。

```yaml
audio:
  preferred_input: "LINE (Yamaha AG03MK2)"
  preferred_input_type: capture
```

`preferred_input_type` は通常の録音入力なら `capture`、再生出力のループバックなら `loopback` です。設定ページで選択すると両方の値が自動保存されます。Windows側の表示名は環境によって大文字・小文字が変わることがあります。アプリは大文字・小文字を区別せずに一致させます。

## 起動方法

最初に解析アプリを実行し、動画指紋を登録します。引数を省略した場合も `scan` を実行します。

Windowsでは初回の `dotnet restore` 後、`scan-videos.cmd` をダブルクリックしても実行できます。ランチャーは暗黙の再復元を行わず、完了結果を確認できるようウィンドウをキー入力まで閉じません。

```powershell
dotnet restore Catalog\AutoVJ.Catalog.csproj --source https://api.nuget.org/v3/index.json
dotnet run --project Catalog\AutoVJ.Catalog.csproj --no-restore -- scan
```

別の設定ファイルを使う場合は次のように指定できます。

```powershell
dotnet run --project Catalog\AutoVJ.Catalog.csproj --no-restore -- scan --config config.yaml
```

解析はファイルサイズと最終更新日時が変わったMP4だけに行います。指紋生成はDBトランザクション外で行い、完成した動画から短いトランザクションで順次反映します。同じDBを対象とする解析アプリの多重起動は拒否します。

続いて再生アプリを起動します。再生中に解析アプリを再実行しても構いません。起動すると既定入力デバイスの監視を自動的に開始します。

```powershell
dotnet restore --source https://api.nuget.org/v3/index.json
dotnet run --no-restore
```

サーバー起動後、OS既定ブラウザで次のURLを自動的に開きます。自動表示に失敗した場合は手動で開いてください。

```text
http://127.0.0.1:5180/
```

ウェルカムページから、映像だけを表示する `/output` と、操作・設定を行う `/setting` を別タブで開いてください。

再生アプリ自身は動画解析を行いません。DBが空の場合はコンソールに解析アプリの実行案内を表示します。解析アプリがDBを更新すると、Web UIの登録動画一覧は5秒以内に自動更新されます。

## 自己診断

実音声デバイスを使わず、`input-audio` の各音源の途中4秒を未知入力として、MP4由来の指紋へ照合します。

```powershell
dotnet run -- --self-test
```

2・3・4・6・8秒の窓を複数位置で比較する場合は、次を実行します。

```powershell
dotnet run -- --benchmark-detection
```

M0.5のキーロック変換、EQ、フィルター解除、速度＋ピッチ比較を測定する場合は、次を実行します。FFmpegで一時変換したPCMを直接照合し、結果を `benchmark-results/m05-*.csv` へ保存します。

```powershell
dotnet run -- --benchmark-tempo
```

2026-07-16の実測では430件中414件成功、誤検出0件でした。主要範囲 `0.93〜1.15` の無加工キーロック条件は225/225件成功し、平均位置誤差は0.11〜0.14秒でした。追加目標の `0.90` と `1.17` はそれぞれ44/45件成功しました。速度とピッチを同時変更する比較条件は必須対象外のため未検出となります。全8倍率・登録5曲を探索する4秒自己診断の照合処理は開発PCで296〜309ミリ秒でした。

## 主な設定値

| 設定 | 初期値 | 説明 |
|---|---:|---|
| `server.port` | `5180` | ローカルWeb UIのポート |
| `server.auto_open_browser` | `true` | 起動時にOS既定ブラウザを開く |
| `audio.sample_rate` | `11025` | 指紋処理用サンプルレート |
| `audio.detection_window_seconds` | `4` | 1回の照合に使う音声長 |
| `detection.interval_seconds` | `1` | 照合間隔 |
| `detection.confidence_threshold` | `0.62` | 曲確定に必要な信頼度 |
| `detection.tentative_confidence_threshold` | `0.55` | 連続確認へ進める最低信頼度 |
| `detection.tentative_confirmation_count` | `2` | 弱い候補に必要な連続回数 |
| `detection.minimum_input_decibels` | `-48` | 照合を行う最低入力レベル |
| `detection.tempo_ratios` | 8倍率 | 探索するキーロック時のテンポ倍率 |
| `detection.playback_rate_tolerance` | `0.015` | 動画再生速度を補正する倍率差 |
| `playback.detection_lost_timeout_seconds` | `10` | 未検出から汎用動画へ戻るまでの秒数 |
| `playback.resync_tolerance_seconds` | `2` | 動画位置を再同期するずれの秒数 |
| `playback.randomize_common_start` | `true` | 汎用動画をランダム位置から再生 |
| `playback.minimum_rate` | `0.8` | 動画再生倍率の下限 |
| `playback.maximum_rate` | `1.2` | 動画再生倍率の上限 |
| `playback.minimum_bpm` | `100` | 倍テン・半テン補正後BPMの下限（以上） |
| `playback.maximum_bpm` | `200` | 倍テン・半テン補正後BPMの上限（未満） |
| `transition.enabled` | `true` | クロスフェード全体の有効化 |
| `transition.duration_ms` | `1200` | 切り替え時間（ミリ秒） |
| `transition.blend_modes_enabled` | `true` | クロスフェード中の重ね合わせ演出 |
| `transition.randomize_blend_mode` | `true` | 候補から毎回ランダム選択 |
| `transition.blend_modes` | 5種類 | 使用するCSSブレンドモード候補 |
| `glitch.enabled` | `true` | 信頼度低下中のグリッチ表示 |
| `glitch.confidence_threshold` | `0.25` | グリッチ表示を開始する信頼度 |
| `glitch.files` | 4ファイル | ランダム選択するグリッチ素材 |

## 使用ライブラリ

- `Microsoft.Data.Sqlite.Core 10.0.10`
- `SQLitePCLRaw.bundle_e_sqlite3 3.0.3`
- `NAudio 2.2.1`

依存ライブラリのライセンス文書を配布物へまとめる処理は、配布フェーズで実装します。

## 現在の制限

- 解析アプリは現在、実行時に一度走査して終了する `scan` モードのみです。フォルダ常駐監視の `watch` モードは未実装です。
- キーロックを維持したテンポ変更には対応していますが、テンポとピッチを同時変更する速度変更には対応していません。
- `1.15` 倍の強いハイパス条件は10件中6件成功です。フィルター適用中の継続検出より解除後の復帰を優先します。
- BPMは自動推定値です。大きな倍テン・半テン誤認識は設定範囲へ補正しますが、細かな手動修正UIは運用方針により実装しません。
- 動画出力はブラウザのHTML Videoです。専用Windows出力やDirect3D合成は未実装です。
- 動画素材の常時重ね合わせ、LAN公開、PIN認証、インストーラーは未実装です。
- ブレンド演出はブラウザのCSS `mix-blend-mode` を利用しており、GPU・ブラウザによって見え方や負荷が変わる可能性があります。
- `input-audio` の確認用ファイルと検出動画の正解対応はDBへ保存しません。自己診断ではしきい値以上で動画を検出できたかを確認します。
