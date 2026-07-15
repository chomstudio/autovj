# AutoVJ

Windows 11でLINE入力を監視し、登録動画の音声を検出して対応動画をブラウザ上で同期再生するオートVJプロトタイプです。

現在は動画切り替え演出を追加した **M0.3** です。最終的な企画は `auto_vj_project_spec.md`、現在の実装仕様は `prototype_spec.md`、ユーザーからの次の指示は `prompt.md` を参照してください。

## 現在できること

- `main-videos` 内の各MP4から音声トラックを抽出して動画単位で登録
- FFmpegで動画音声を11,025Hz・モノラルPCMへ変換
- 独自の周波数ピーク指紋をSQLiteへ保存
- WindowsのWASAPI録音エンドポイントからLINE入力を取得
- 4秒の音声窓を1秒ごとに登録曲へ照合
- 62%以上は即時確定し、55%以上62%未満は同じ候補を2回確認して確定
- -48dB未満の入力を無音として照合対象外にする
- 検出曲、信頼度、推定位置をローカルWeb UIへ表示
- 対応動画を推定位置から再生し、ずれが2秒を超えた場合に再同期
- 未検出中は直前の動画をそのまま進め、新しい位置検出時だけシーク
- 2枚の動画レイヤーで汎用動画・検出動画・別の検出動画をクロスフェード
- 設定したCSSブレンドモード候補から切り替えごとにランダム選択
- ブレンド演出を無効化して通常クロスフェードだけにする設定
- 切り替え後は新しい動画レイヤーを再生対象として追跡し、旧レイヤーの待機で状態更新が停止しないように制御
- 動画メタデータを10秒以内に読み込めない場合はタイムアウトして状態更新を再開
- 起動直後と曲未検出時に `common_movie.mp4` を強制ループ再生
- 検出を10秒間失った場合に汎用動画へ復帰
- 入力信号レベルをdBメーターで表示
- 監視中の音声入力を止めずに入力デバイスを切り替え
- サーバー起動後にOS既定ブラウザで操作画面を自動表示
- 映像領域のブラウザ全画面表示

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
```

Windows側の表示名は環境によって大文字・小文字が変わることがあります。アプリは大文字・小文字を区別せずに一致させます。

## 起動方法

```powershell
dotnet restore --source https://api.nuget.org/v3/index.json
dotnet run
```

サーバー起動後、OS既定ブラウザで次のURLを自動的に開きます。自動表示に失敗した場合は手動で開いてください。

```text
http://127.0.0.1:5180/
```

初回起動時は、登録DBが空の場合に素材を自動解析します。素材を入れ替えた場合はWeb UIの「素材を再解析」を押してください。

## 自己診断

実音声デバイスを使わず、`input-audio` の各音源の途中4秒を未知入力として、MP4由来の指紋へ照合します。

```powershell
dotnet run -- --self-test
```

2・3・4・6・8秒の窓を複数位置で比較する場合は、次を実行します。

```powershell
dotnet run -- --benchmark-detection
```

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
| `playback.detection_lost_timeout_seconds` | `10` | 未検出から汎用動画へ戻るまでの秒数 |
| `playback.resync_tolerance_seconds` | `2` | 動画位置を再同期するずれの秒数 |
| `transition.enabled` | `true` | クロスフェード全体の有効化 |
| `transition.duration_ms` | `1200` | 切り替え時間（ミリ秒） |
| `transition.blend_modes_enabled` | `true` | クロスフェード中の重ね合わせ演出 |
| `transition.randomize_blend_mode` | `true` | 候補から毎回ランダム選択 |
| `transition.blend_modes` | 5種類 | 使用するCSSブレンドモード候補 |

## 使用ライブラリ

- `Microsoft.Data.Sqlite.Core 10.0.10`
- `SQLitePCLRaw.bundle_e_sqlite3 3.0.3`
- `NAudio 2.2.1`

依存ライブラリのライセンス文書を配布物へまとめる処理は、配布フェーズで実装します。

## 現在の制限

- 音声指紋はM0.3用の独自方式で、テンポ・ピッチ変更やミックスへの耐性は未調整です。
- WASAPIループバック入力は未実装です。現在はLINEなどの録音エンドポイントを使用します。
- 動画出力はブラウザのHTML Videoです。専用Windows出力やDirect3D合成は未実装です。
- 重ね合わせ、LAN公開、PIN認証、インストーラーは未実装です。
- ブレンド演出はブラウザのCSS `mix-blend-mode` を利用しており、GPU・ブラウザによって見え方や負荷が変わる可能性があります。
- `input-audio` の確認用ファイルと検出動画の正解対応はDBへ保存しません。自己診断ではしきい値以上で動画を検出できたかを確認します。
