# 構成・認証・復旧

## 通信

```text
操作PC: PCtoPC -> Moonlight == UDP映像/音声・入力 ==> Sunshine: 被接続PC
             -> AgentClient == pinned HTTPS/TCP 47831 ==> PCtoPC Agent
             -> 中継Agent (Tailscale) -> UDP 9 broadcast -> 停止中のPC
             -> 外部IPv4/DDNS:指定UDPポート -> ルーター転送 -> 停止中のPC
```

画面転送経路にPCtoPCのプロキシや追加フレームキューは入りません。接続時はMoonlightの公開CLIをシェル経由でなくArgumentListで起動します。初回PIN認証と画面転送の認証はSunshine／Moonlightが管理します。

Windows UIはWPF、電源／WoL AgentはASP.NET Core Kestrel、共通処理は.NETです。エージェントはログに認証トークンを出しません。ネットワーク・電源・ファイアウォール・自動起動のシステム設定はユーザーが指定した手順だけで変更されます。

## 外部WoL（v0.2）

PcProfileのWanWakeHost／WanWakePortを画面転送のHost、Agentの接続先とは独立して保存します。旧JSONにこれらの項目がない場合は空ホスト／UDP 3000を使います。MACは既存のMacフィールドを共用し、UI上のLAN用／外部用入力を双方向バインディングで同期します。

SendToHostAsyncはホスト名を各操作時にIPv4で名前解決し、最初のIPv4の指定UDPポートへ102バイトのMagic Packetを3回送ります。WindowsのDNSキャッシュを使用し、明示的なキャッシュは追加しません。外部送信ソケットのブロードキャストは有効にせず、IPv6・マルチキャスト・未指定アドレス・全体ブロードキャスト・URL付き入力・範囲外ポートを拒否します。LAN／Agent中継の既存のSendAsyncは引き続きUDP 9を使用します。

UIの送信処理は8秒のタイムアウトを設け、失敗時にLAN送信など別経路へフォールバックしません。UDP送信完了と起動完了は区別します。詳細とルーター設定例は[wan-wake.md](wan-wake.md)を参照してください。

## Agentの認証

- 初期化時にRSA 3072自己署名証明書（有効期間2年）と32バイトのランダムトークンを生成します。
- 接続ファイルにはホスト・ポート・証明書全体のSHA-256・トークンを含みます。送付元を信頼できる経路で受け取り、証明書の指紋を被接続側で照合します。
- クライアントは証明書のSHA-256と有効期間を検証します。名前のCA検証ではなく、明示的に取り込んだ証明書を信頼します。HTTPへのフォールバック、全証明書の無条件承認、リダイレクト追跡、プロキシ経由の認証情報送信はありません。
- APIはTLS 1.2/1.3のみ。BearerトークンのSHA-256を定数時間比較します。URL・ログにはトークンを入れません。Cookie認証とCORSを有効にしません。
- 全エンドポイントは認証必須。毎分120リクエストの上限、本文4KiB上限。固定のグローバル制限のため大量アクセス中は正規利用も制限されます。
- 1台のAgentにつき1個の共有トークンです。この初期版では端末ごとの権限・個別失効・証跡管理はありません。接続ファイルを渡した相手は、そのAgentで許可された全操作を行えます。
- 既定の待受はloopback。LAN／Tailscale受信はIP指定と限定したファイアウォールが必要です。インターネットへ公開しません。

WindowsのDPAPI暗号化は「現在のWindowsユーザー」に結びつきます。別ユーザー・SYSTEMサービスへ設定ファイルをコピーしても復号できません。Agentの秘密鍵は設定ファイルにDPAPIで保護して格納します。Linuxの中継設定は0600のファイルで保護します。

WindowsでTLSを提供する間は、Schannelの要件に従い現在のユーザーの暗号プロバイダーに一時鍵を読み込みます。証明書を破棄すると.NETがこの一時鍵を削除します。プロセスを強制終了した場合などの鍵のクリーンアップはOS／ランタイムの動作に依存します。

## 電源操作

`init --enable-power` を指定した端末だけ許可します。指定しない場合は403です。Windows以外はWoL中継専用です。UIの確認画面後にAPIを送り、Agent内で10秒の取り消し可能な待ち時間を設けます。

Windowsのshutdown.exeは `/s /t 0` または `/r /t 0` で起動し、`/f` は付けません。Windowsの `/t > 0` は強制終了が暗黙に有効になるため、待ち時間をAgent側で管理します。[Microsoft shutdown仕様](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/shutdown)

スリープはSetSuspendStateを使用します。OSの権限・電源構成・未保存アプリによって実行が失敗／阻止される場合があります。APIの202は予約受付を意味し、OSの電源遷移完了の通知ではありません。実行失敗はAgentログで確認します。停止中にAgentへ接続することはできません。

Agentの `serve --dry-run` では電源変更とWoL送信を行わず、認証と操作予約を模擬します。テストは必ずこのモードで起動します。

## 接続情報の失効・証明書更新

Agentを停止し、初期化した同じユーザーで実行します。古い接続ファイルの操作権限と証明書をまとめて失効させます。

```powershell
.\agent\PCtoPC.Agent.exe init --reset --host my-pc.example.ts.net --listen 100.80.1.20 --out new-private.pctopc.json --enable-power
.\agent\PCtoPC.Agent.exe serve
```

`--reset` はWoLターゲットの設定も初期化します。必要なIDを登録し直し、操作側では古い接続先を削除して新しい接続ファイルを取り込みます。有効期間内の証明書を用いるため、2年を待たず更新します。時刻がずれている端末では証明書検証が失敗するため、Windowsの時刻同期も確認してください。

## API

| メソッド | パス | 動作 |
| --- | --- | --- |
| GET | `/v1/status` | 電源許可、模擬モード、予約、登録済みWake ID |
| POST | `/v1/power` | JSON `{ "action": "sleep/shutdown/restart/cancel" }` のいずれか |
| POST | `/v1/wake/{id}` | Agentに事前登録したIDへWoLを送信 |

未認証401、無効化された電源操作403、未登録ID 404、予約競合409、過剰アクセス429です。任意コマンド実行APIや、遠隔から送信先MAC／ブロードキャストを指定するAPIはありません。

## 実機受け入れ確認

1. 同じLANの2台でDesktop配信、音声、マウス、英数／日本語入力、再接続を確認。
2. 解像度と60/120fpsを切り替え、Moonlightの統計でネットワーク・デコード・ドロップを記録。
3. 操作側を別ネットワークへ移し、Tailscale Direct／relayの経路を記録。
4. 未保存の作業を閉じた検証用PCで、予約取り消し、スリープ復帰、再起動、停止を確認。
5. 常時稼働の別端末からWoLを実行。Wi-Fi復帰可否・完全停止からの起動可否を機器ごとに記録。
6. 再起動後にユーザーのログインとAgentの起動が必要なことを確認。

2台の実機、GPU、Wi-FiおよびTailscaleの外部接続環境で未検証のため、この確認を完了するまでは常用環境での完成扱いにしません。
