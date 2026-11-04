# PCtoPC

Windows Home / Pro向けのリモートデスクトップ接続・電源管理アプリ（v0.2）。外部IPv4／DDNS名と任意のUDPポートを指定するWoL送信に対応しています。

同じWi-Fi内ではLANのIP／PC名、外部からはTailscaleのIP／MagicDNS名で接続します。画面・音声・マウス・キーボードは、別途インストールする **Sunshine（被接続側）＋Moonlight（操作側）** が処理します。PCtoPCは接続先・低遅延設定・診断・電源操作をまとめるWindowsアプリです。独自の画面転送エンジンや、Windows HomeのRDP制限を解除するものではありません。

## 必要なもの

| 端末 | 導入するもの |
| --- | --- |
| 操作するWindows PC | PCtoPC、[Moonlight PC](https://moonlight-stream.org/) |
| 操作されるWindows PC | [Sunshine](https://github.com/LizardByte/Sunshine/releases/latest)、電源管理にPCtoPC Agent |
| 別ネットワークから接続 | 両方の端末に[Tailscale](https://tailscale.com/download/windows)、同一tailnetの許可 |
| 外部から電源オン | 対象PCのWoL対応、有線LAN推奨。外部IPv4へのポート転送、または同じLANで常時稼働する別のAgent端末 |

Windows x64を主な対象としています。ソースからWindows ARM64版も発行できますが実機未検証です。Linux版AgentはWoL中継に使用できる構成ですが実機未検証です。

自己完結版は.NETの別途導入が不要です。framework-dependent版には **.NET 10 Desktop Runtime** と **ASP.NET Core 10 Runtime** が必要です。Sunshine／Moonlight／Tailscaleは同梱せず、公式配布を利用します。

## 最初の接続

1. ZIPをフォルダーごと展開して `PCtoPC.exe` を開きます。
2. 被接続側でSunshineを導入し、`https://localhost:47990` の初期設定を済ませます。配信アプリ名 `Desktop` を確認します。
3. 操作側でMoonlightを導入します。PCtoPCの「設定とログ」でMoonlight.exeを選択します（標準パスは自動検出）。
4. 「接続する」→「追加」でPC名と被接続側のLAN IP／PC名を入力し保存します。Wi-Fiのゲストネットワーク／AP分離は端末間通信を遮断する場合があります。
5. 「初回ペアリング」を押し、表示されたPINを**被接続側のSunshine**のPIN画面へ入力します。
6. 「接続する」を押します。画面はMoonlightの別ウィンドウで開きます。終了ショートカットは `Ctrl+Alt+Shift+Q` です。

Sunshineの設定・ファイアウォール・画面が正しく構成されている必要があります。標準ポートのSunshineを対象にしています。接続先は手動入力またはTailscale一覧から追加できます。独自のLAN自動検出はありません。

## Tailscale越しの接続

1. 両方のPCでTailscaleへログインし、端末間通信が許可されていることを確認します。
2. 接続先に `100.x.x.x` のTailscale IPまたはMagicDNS名を使います。「Tailscale端末から追加」も利用できます。
3. 同じ接続先アドレスでMoonlightのペアリングを行います。LAN用とTailscale用を別プロファイルとして保存しても構いません。
4. Sunshineの受信ファイアウォールとtailnetポリシーで、必要なTCP/UDP通信を操作側から許可します。[Sunshine公式ポート表](https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2configuration.html)を参照してください。PCtoPCはSunshineのルールやルーター設定を変更しません。
5. 「接続を診断」の `tailscale ping` 結果を確認します。Directが低遅延に適しています。DERP／Peer Relay経由では経路と回線によって遅延が増えます。[Tailscaleの接続方式](https://tailscale.com/docs/reference/connection-types)

外部へのポート転送は不要です。Tailscaleの中継利用をアプリ側から強制的にDirectへ変えることはできません。

## 電源管理を有効にする

被接続側で「このPCで接続を受ける」を開きます。

1. 操作側から到達できる、このPCのLAN IPまたはTailscale IP／MagicDNS名を入力します。
2. 待受IPに、このPC自身に割り当てられたLANまたはTailscaleのIPを入力します。既定の `127.0.0.1` はローカルテスト専用です。LANとTailscaleの両方で待ち受ける場合は `0.0.0.0` と限定したファイアウォールルールを使います。
3. 「電源操作を許可する」を必要に応じて有効にし、「Agentを初期化して接続ファイルを保存」を押します。
4. 「Agentを起動」を押します。コンソールが動作中であることを確認します。停止は `Ctrl+C` です。
5. 操作側のIPだけにAgent TCP `47831` の受信を許可します。配布フォルダーで管理者PowerShellを開く例:

```powershell
# LAN: 操作するPCのIPを指定
.\scripts\Configure-Firewall.ps1 -RemoteAddress 192.168.1.10
# Tailscale: 操作するPCのTailscale IPを指定。Tailscaleアダプターだけに適用
.\scripts\Configure-Firewall.ps1 -RemoteAddress 100.80.1.10 -TailscaleInterface
```

6. 接続ファイル `*.pctopc.json` を操作側へ安全に移し、「接続ファイルを取り込む」で読み込みます。**接続ファイルは電源操作の鍵です。公開・Gitへの登録をせず、取り込み後は転送元・転送先の不要なコピーを削除します。**
7. 「状態を確認」で通信を確認します。スリープ・再起動・シャットダウンは確認画面の後、10秒後に実行します。「予約を取り消す」で実行前に中止できます。

認証用AgentとSunshineのペアリングは別です。保存されたAgentのアドレスは取り込んだ接続ファイルに固定されます。画面転送のアドレスを編集しても電源操作の送信先は変わりません。LAN／Tailscaleの双方で電源操作する場合は、両方から到達できるAgentの名前・IPを設定してください。

Windows上の設定・秘密鍵・保存済み接続情報は現在のユーザーのDPAPIで保護します。Agentは**初期化したユーザーと同じユーザー**で起動します。標準保存先は `%LOCALAPPDATA%\PCtoPC` です。

任意の自動起動: `.\scripts\Register-AgentStartup.ps1`。現在のユーザーの**ログイン後**に起動します。ログイン前からの電源管理やWindowsサービス登録はこの版には含みません。解除は `-Remove` です。再起動後は同じユーザーがログインするまでAgentを利用できません。

## Tailscale越しに電源をオンにする

対象PCが停止すると、そのPCのTailscaleも停止します。Tailscale自体はLANブロードキャストを転送しないため、同じLAN上の別の常時稼働端末からWoLを送ります。[Tailscale公式の説明](https://tailscale.com/blog/wake-on-lan-tailscale-upsnap)

追加の中継端末を置かずにルーターのポート転送を利用する場合は、次の「外部IPv4／DDNSへ直接送る」を利用してください。電源オンの信号は通常のIPv4 UDP通信で送り、起動後のリモート操作にはTailscaleを使えます。

中継端末でもAgentを初期化します（電源許可は不要）。その接続ファイルを操作側に取り込みます。中継端末で次を実行し、Agentを再起動します。

```powershell
.\agent\PCtoPC.Agent.exe add-wake-target --id desktop --mac AA:BB:CC:DD:EE:FF --broadcast 192.168.1.255 --local-address 192.168.1.30
```

`--local-address` は中継端末のLANインターフェースIPです。複数NICがある場合に推奨します。ブロードキャストは実際のサブネットに合わせます。対象PCの接続先設定で中継PCとWakeターゲットID `desktop` を指定し、「中継PCから起動する」を押します。Agentは登録済みIDの宛先にだけ送信します。

同じLAN内からは、対象PCのMACとブロードキャストを入力して「LANで起動する」を使えます。WoLは送信成功だけで起動完了を保証しません。

BIOS/UEFIのWake-on-LAN／PCIe復帰、NICのMagic Packet復帰、電源状態への対応が必要です。Wi-Fi復帰（WoWLAN）・完全シャットダウンからの起動・高速スタートアップの影響は機種依存です。有線LANでの検証を推奨します。これらに対応しないPCをアプリだけで電源オンにはできません。

## 外部IPv4／DDNSへ直接送る（v0.2）

「外部から起動」タブで、起動するPC、対象PCの有線LAN MACアドレス、HGW／ルーターの外部IPv4またはDDNS名、外部UDPポート（初期値3000）を指定します。「保存」または「外部へ起動信号を送る」で、接続先ごとの設定を保存します。MACアドレスはLAN起動と共通です。

送信先にはURLの `https://` や `:3000` を含めず、ホスト名とポートを別々に入力します。DDNSは各送信時にWindowsの名前解決を使ってIPv4を取得します。IPv6送信には対応しません。名前解決が複数のIPv4を返す場合は最初のアドレスに送り、選択したIPとポートをログに表示します。起動信号は約50ms間隔で3回送信します。

ルーターでは、指定した外部UDPポートを対象PCのUDP 9へ転送します。二段ルーターでは、上流ルーターから下流ルーターのWAN側IP、下流ルーターから対象PCへと二段で設定してください。対象PCのIP予約とIP・MACバインディングも必要です。具体例は [docs/wan-wake.md](docs/wan-wake.md) を参照してください。

この機能はルーターの設定を自動変更しません。UDP送信成功は起動完了の確認ではありません。テストは自宅Wi-Fiを離れた回線から実施してください。グローバルIPv4がルーターへ届かない回線では、上流側の設定・契約の確認、またはAgent中継方式が必要です。WoLパケット自体には認証や暗号化がないため、認証付き中継を使いたい場合はTailscale＋Agentを利用します。

v0.1で保存した接続先はそのまま読み込めます。更新はZIPを新しいフォルダーへ展開して `PCtoPC.exe` を起動してください。設定の保存先は同じWindowsユーザーの `%LOCALAPPDATA%\PCtoPC` です。

## 低遅延の調整

- 初期設定は1080p / 60fps / H.264 / 20Mbps。帯域が細い場合は720p / 60fps / 8Mbpsから始めます。
- SunshineでNVENC／QuickSync／AMFなどのハードウェアエンコードを利用し、Moonlightではハードウェアデコードを要求します。非対応GPUでは起動に失敗するため、機器の対応を先に確認してください。
- VSyncとフレームペーシングを無効にし、待ち時間を抑えています。ティアリングが生じることがあります。フルスクリーンは枠なし表示です。
- Wi-Fi 5/6GHz帯・良好な電波・混雑の少ないチャンネルを使い、可能なら被接続側を有線LANにします。GPU／CPU負荷と高すぎるビットレートは遅延やパケット損失の原因になります。
- 120fpsは対応する画面・エンコーダー・デコーダー・回線がある場合に検討します。単に上限へ上げると遅くなる場合があります。
- Moonlightの性能統計でネットワーク・デコード・ドロップを確認します。PCtoPCのTCP接続診断の数値は**操作から表示までの遅延ではありません**。遅延ゼロや一定ms以下を保証しません。

## 制限と検証範囲

v0.2は外部ストリーミングソフトを組み合わせる初期版です。実デスクトップを共有する方式であり、Windows RDPの独立したログインセッション・ファイル転送・クリップボード共有・USB転送は独自実装していません。日本語IME、複数画面、ロック／ログイン画面、UAC、画面を外したPCはSunshine／Moonlightと機器の設定に依存し、実機確認が必要です。

ビルド、ローカルHTTPS認証、証明書ピンニング、電源操作の模擬実行と取り消し、WoLのローカルUDP送受信を自動テストします。**2台のPC間での映像・音声・入力、Tailscale越しの実通信、実機の起動／停止、実測遅延は利用環境での検証が必要です。**

## 開発と発行

Windowsの.NET 10 SDKを使います。外部NuGetライブラリは不要です。

```powershell
dotnet build PCtoPC.slnx -c Release -m:1
dotnet run --project tests/PCtoPC.Tests -c Release
# 自己完結版（MicrosoftのランタイムパックをNuGetから取得）
.\scripts\Publish.ps1
# ランタイム導入済み端末向け
.\scripts\Publish.ps1 -FrameworkDependent
```

発行先は `artifacts/PCtoPC-win-x64`。アプリ本体と `agent` サブフォルダーを一緒に配布します。ビルド時の権限が制限された環境でコンパイラー用パイプに接続できない場合は `-p:UseSharedCompilation=false` を追加します。

Linux中継は `dotnet publish src/PCtoPC.Agent -c Release -r linux-arm64 --self-contained true --source https://api.nuget.org/v3/index.json` などで発行します。Linuxでは専用ユーザーを用い、設定ファイルは0600で保存します。Linux上のDPAPI暗号化はありません。対象インターフェースとUDPブロードキャストが利用可能なホスト上で動かします。

内部仕様・復旧手順は [docs/architecture.md](docs/architecture.md) を参照してください。
