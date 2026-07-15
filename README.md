# AutoVJ

Windows 11でLINE入力を監視し、登録音源を検出して対応動画をブラウザ上で同期再生するオートVJプロトタイプです。

現在は最小動作確認版の **M0** です。最終的な企画は `auto_vj_project_spec.md`、現在の実装仕様は `prototype_spec.md`、ユーザーからの次の指示は `prompt.md` を参照してください。

## 現在できること

- `input-audio` 内のMP3/WAVと `main-videos` 内のMP4を再生時間で自動対応付け
- FFmpegで参照音源を11,025Hz・モノラルPCMへ変換
- 独自の周波数ピーク指紋をSQLiteへ保存
- WindowsのWASAPI録音エンドポイントからLINE入力を取得
- 8秒の音声窓を2秒ごとに登録曲へ照合
- 検出曲、信頼度、推定位置をローカルWeb UIへ表示
- 対応動画を推定位置から再生し、ずれが2秒を超えた場合に再同期
- 映像領域のブラウザ全画面表示

## 必要環境

- Windows 11 64bit
- .NET SDK 10
- PATHから実行できる `ffmpeg` と `ffprobe`
- NAudioから参照できるWindows録音デバイス

現在PCに導入されているFFmpegはGPLビルドです。ローカル開発確認には使用できますが、商用配布物へは含めません。配布段階では企画書どおり、GPL/nonfreeを無効にしたLGPLビルドとライセンス文書へ差し替えます。

## 素材の配置

```text
input-audio/
├─ 任意の名前.mp3
└─ 任意の名前.wav

main-videos/
└─ 任意の名前.mp4
```

ファイル名は一致させる必要がありません。M0では再生時間が最も近く、差が `config.yaml` の `pairing_duration_tolerance_seconds` 以下となる未使用動画を自動的に割り当てます。誤対応がないか、起動後の「登録素材」一覧で確認してください。

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

ブラウザで次を開きます。

```text
http://127.0.0.1:5180/
```

初回起動時は、登録DBが空の場合に素材を自動解析します。素材を入れ替えた場合はWeb UIの「素材を再解析」を押してください。

## 自己診断

実音声デバイスを使わず、登録音源の途中8秒を未知入力として照合します。

```powershell
dotnet run -- --self-test
```

## 主な設定値

| 設定 | 初期値 | 説明 |
|---|---:|---|
| `server.port` | `5180` | ローカルWeb UIのポート |
| `media.pairing_duration_tolerance_seconds` | `5` | 自動対応付けを許容する再生時間差 |
| `audio.sample_rate` | `11025` | 指紋処理用サンプルレート |
| `audio.detection_window_seconds` | `8` | 1回の照合に使う音声長 |
| `detection.interval_seconds` | `2` | 照合間隔 |
| `detection.confidence_threshold` | `0.62` | 曲確定に必要な信頼度 |

## 使用ライブラリ

- `Microsoft.Data.Sqlite.Core 10.0.10`
- `SQLitePCLRaw.bundle_e_sqlite3 3.0.3`
- `NAudio 2.2.1`

依存ライブラリのライセンス文書を配布物へまとめる処理は、配布フェーズで実装します。

## 現在の制限

- 音声指紋はM0用の独自方式で、テンポ・ピッチ変更やミックスへの耐性は未調整です。
- WASAPIループバック入力は未実装です。現在はLINEなどの録音エンドポイントを使用します。
- 動画出力はブラウザのHTML Videoです。専用Windows出力やDirect3D合成は未実装です。
- 重ね合わせ、LAN公開、PIN認証、インストーラーは未実装です。
- 自動対応付けは時間差だけを使用するため、同じ長さの素材が多数ある場合には手動確認が必要です。
