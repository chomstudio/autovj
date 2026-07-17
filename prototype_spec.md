# AutoVJ プロトタイプ仕様書

更新日: 2026-07-17
現在の段階: M0.8 デバイス別位置補正・LAN公開・PIN認証まで実装完了

## 1. 本書の位置付け

本書は、現在動作するプロトタイプの実装仕様だけを簡潔に記録する。最終目標は `auto_vj_project_spec.md`、次の作業指示は `prompt.md` を正とする。

## 2. 現在の構成

- `AutoVJ.Catalog`: `main-videos` のMP4を差分解析し、SQLiteへ音声指紋とBPM情報を登録する。
- `AutoVJ`: WASAPI音声入力、リアルタイム曲検出、Web API、ローカルWeb UI、動画配信を担当する。
- `data/autovj.db`: 解析アプリと再生アプリが共有するSQLite DB。
- `config.yaml`: 素材、音声入力、検出、再生、演出の設定。

Web UIは既定で `http://127.0.0.1:5180/` に公開し、通常起動では既定ブラウザを自動表示する。`--lan` 起動時は全インターフェースで待ち受ける。

## 3. 素材登録とDB

- 登録対象は `main-videos/*.mp4` とする。
- `AutoVJ.Catalog scan` はファイルサイズと更新日時が変わった動画だけを再解析する。
- 解析失敗時は既存レコードを維持し、ほかの動画の処理を継続する。
- 同一DBを対象とするCatalogの多重起動はロックファイルで拒否する。
- DBはWALモード、5秒のビジー待機、動画パスを一意キーとするUPSERTを使用する。
- DBスキーマは `PRAGMA user_version = 4` とする。
- 動画パス、長さ、ファイル情報、旧指紋、M0.5指紋、指紋方式・バージョン、BPM、BPM信頼度、解析元を保存する。
- 旧DBは既存指紋を維持したまま移行し、新指紋の生成完了まで旧方式で検出できる。

## 4. 音声入力と曲検出

- NAudioの `WasapiCapture` でWindows録音エンドポイントを取得する。
- NAudioの `WasapiLoopbackCapture` でWindows再生エンドポイントの出力音声を取得できる。
- 設定ページでは録音入力とWASAPIループバックを分類表示し、同名デバイスも一意なエンドポイントIDで区別する。
- 選択した入力元の表示名と種類を `config.yaml` へ保存し、次回起動時にも使用する。
- 再生位置補正をWASAPIエンドポイントIDごとにミリ秒単位で保存する。
- 通常起動直後に `audio.preferred_input` の監視を自動開始する。
- 起動時にデバイスを開けない場合もWeb UIは起動し、失敗理由を入力状態へ表示する。
- 16bit、24bit、32bit PCMと一般的な32bit float入力をモノラル化し、11,025Hzへ変換する。
- 入力レベルをdBへ変換し、Web UIの信号インジケーターへ表示する。
- 最新4秒の入力を1秒ごとに登録曲へ照合する。
- `spectral-landmarks` v2指紋で局所スペクトルピークを比較する。
- キーロックを維持した `0.90`、`0.93`、`0.95`、`1.00`、`1.05`、`1.10`、`1.15`、`1.17` の時間軸倍率を探索する。
- BPMは倍率候補の優先度に使い、最終確定は音声指紋で行う。
- 信頼度62%以上は即時確定し、55%以上62%未満は同じ曲を2回連続確認して確定する。
- -48dB未満の入力は無音として扱う。
- 未検出や弱い候補の確認中は直前の曲と動画を維持し、既定10秒後に汎用動画へ戻る。

テンポ変更とEQを含むLINE入力は2026-07-16の実機確認で実用レベルと判断した。自動ベンチマークの再現結果は `benchmark-results/m05-20260716-095829.csv` に保存する。

## 5. 動画出力と演出

- MP4はHTTP Range要求に対応して配信する。
- 2枚のHTML Videoを重ね、動画切り替え時にクロスフェードする。
- 切り替え中は設定されたCSS `mix-blend-mode` を使用し、完了後は `normal` に戻す。
- `multiply`、`overlay`、`soft-light` など暗転しやすいモードは、旧動画を不透明に保った合成演出の後で通常クロスフェードし、黒背景への合成と切り替え終端の瞬間的な復帰を防ぐ。
- 検出曲は推定位置へシークし、推定テンポ倍率を `playbackRate` へ反映する。
- 選択中入力元の再生位置補正を検出位置へ加算する。正の値は動画を先へ進め、負の値は遅らせる。
- 動画再生倍率は設定可能な下限・上限（既定 `0.8`～`1.2`）へ制限する。
- 基準BPMと入力推定BPMが設定範囲外の場合、倍テン・半テンを疑って2倍または半分にし、既定 `100` 以上 `200` 未満へ収める。
- 同じ曲の再検出時は、位置差と再生速度差を補正する。
- 未検出時は `material-videos/common_movie.mp4` をミュート・ループ再生する。
- `playback.randomize_common_start` が有効な場合、汎用動画へ切り替えるたびにランダム位置から再生する。
- 検出中の信頼度が `glitch.confidence_threshold` 未満になると、現在動画を維持したままグリッチ動画を重ねる。
- グリッチ素材は `glitch.files` からランダム選択し、`screen` ブレンドとコントラスト補正で加算系表示する。
- 曲の信頼度回復、汎用動画への復帰、入力停止時はグリッチ表示を終了する。
- ランダムなブレンドモード、グリッチ素材番号、汎用動画の開始位置比率はサーバー共有状態で選ぶ。複数の再生タブは同じ演出指定を使用する。

既定のグリッチ素材は `material-videos/glitch1.mp4` から `glitch4.mp4` とし、既定しきい値は25%とする。

## 6. Web UI

### 6.1 ウェルカムページ `/`

- 従来の再生＋設定画面は廃止する。
- 再生ページと設定ページをそれぞれ新規タブで開くリンクだけを表示する。

### 6.2 再生ページ `/output`

- 2枚の再生動画とグリッチ動画を画面全体に表示する。
- 設定UIや検出モニターは表示しない。
- 全画面表示ボタンを持つ。
- 状態と公開設定を1秒ごとに取得し、設定ページの変更を即時反映する。

### 6.3 設定ページ `/setting`

- 入力状態、入力デバイス、入力レベル、検出曲、信頼度、推定位置、BPM、テンポ倍率、再生速度、指紋方式、切り替え演出を表示する。
- 「一時停止／再開」ボタン1つで音声入力を切り替える。
- 録音入力とループバック入力の選択を切り替えられる。
- 選択中の入力元に紐づく再生位置補正を `-60000`～`60000` ミリ秒で編集できる。
- 登録動画一覧は専用領域内で縦スクロールする。設定ページ全体のスクロールも許可する。
- 未検出待機時間、再同期許容差、再生倍率上下限、BPM範囲、クロスフェード有効化、切り替え時間、ブレンド有効化、ランダム選択、ブレンドモード、グリッチしきい値を編集できる。
- ブレンドモードはチェックボックスで複数選択する。
- 「設定を保存」は対象値を `config.yaml` へ保存し、実行中設定と再生ページへ即時反映する。

## 7. 設定

主な追加設定は次のとおり。

```yaml
playback:
  detection_lost_timeout_seconds: 10
  resync_tolerance_seconds: 2
  randomize_common_start: true
  minimum_rate: 0.8
  maximum_rate: 1.2
  minimum_bpm: 100
  maximum_bpm: 200

audio_position_offsets:
  "loopback:{WASAPIエンドポイントID}": 500
  "capture:{WASAPIエンドポイントID}": 1000

transition:
  enabled: true
  duration_ms: 1200
  blend_modes_enabled: true
  randomize_blend_mode: true
  blend_modes: ["screen", "multiply", "overlay", "soft-light", "difference"]

glitch:
  enabled: true
  confidence_threshold: 0.25
  files: ["glitch1.mp4", "glitch2.mp4", "glitch3.mp4", "glitch4.mp4"]
```

`server.lan_pin` は空欄または4桁の数字とする。空欄の場合は `--lan` 起動でもPINを要求しない。

## 8. LAN公開とPIN認証

- `--lan` を付けると `0.0.0.0` で待ち受け、LAN内のほかの端末からアクセスできる。
- `--port 5180` または `--port=5180` で今回の待受ポートを上書きできる。
- `--pin 1234` または `--pin=1234` で `server.lan_pin` を今回だけ上書きできる。
- PIN認証は `--lan` 起動時かつ有効PINが空欄でない場合だけ有効になる。通常起動ではPINを要求しない。
- PIN認証が無効な場合はPIN入力欄自体を表示しない。認証済みの場合は入力欄を隠して「認証済です」と表示する。
- 認証前に公開するのはウェルカムページ、認証API、ウェルカムページ用CSS・JavaScriptだけとする。
- `/output` または `/setting` への直接アクセスはウェルカムページへ戻す。その他のAPI・動画要求は `401 Unauthorized` とする。
- 認証成功時はランダムなセッショントークンをHttpOnly・SameSite StrictのセッションCookieへ保存する。PIN自体はCookieへ保存しない。

## 9. API

| メソッド | パス | 内容 |
| --- | --- | --- |
| GET | `/api/auth/status` | PIN認証の要否と現在状態 |
| POST | `/api/auth/login` | 4桁PINの照合とセッション開始 |
| GET | `/api/status` | 最新の入力・検出状態 |
| GET | `/api/client-config` | Web再生と演出の公開設定 |
| GET | `/api/settings` | 詳細設定画面の現在値と選択肢 |
| POST | `/api/settings` | 設定の保存と即時反映 |
| GET | `/api/tracks` | 指紋登録済み動画一覧 |
| GET | `/api/audio/devices` | 有効な録音入力・ループバック入力一覧 |
| POST | `/api/capture/start` | 音声入力の開始・再開 |
| POST | `/api/capture/device` | 入力デバイス変更 |
| POST | `/api/capture/stop` | 音声入力の一時停止 |
| GET | `/api/media/{id}` | Range対応の登録動画配信 |
| GET | `/api/material/common` | Range対応の汎用動画配信 |
| GET | `/api/material/glitch/{index}` | Range対応の設定済みグリッチ素材配信 |

`AUTOVJ_ENABLE_TEST_API=1` の場合だけ、UI回帰試験用APIと操作欄を有効にする。

## 10. 依存関係と現在の制限

- .NET 10 / ASP.NET Core
- NAudio 2.2.1
- Microsoft.Data.Sqlite.Core 10.0.10
- SQLitePCLRaw.bundle_e_sqlite3 3.0.3
- FFmpeg / ffprobe

現在の主な未実装項目は、テンポとピッチを同時変更する入力への対応、専用Windows映像出力、配布用インストーラーである。ミックス時の後入り曲判定とBPM手動修正UIは、現在の運用方針では実装対象外とする。

LAN公開はHTTP通信であり暗号化しない。信頼できるローカルネットワーク内での利用を前提とする。

開発PCのFFmpegはGPL有効ビルドのため商用配布物へ含めない。配布前にLGPLビルド、ライセンス文書、サードパーティ通知へ差し替える。
