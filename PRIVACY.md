# プライバシー ポリシー

SimpleCap の開発者は、利用者の個人情報を収集・保存・送信しません。利用状況の記録(テレメトリ)や広告もありません。

## パソコンの中にだけ保存するもの

- キャプチャした画像・ビデオ・GIF・MP4 は、利用者が選んだ保存先フォルダ(既定は `ピクチャ\SimpleCap`)にのみ保存されます。
- 設定(ショートカットキー・ウィンドウ位置・クリック表示など)は、お使いのパソコン内(`%APPDATA%\SimpleCap\`)にのみ保存されます。
- 「AI で名前付け」のシークレットキーは、このパソコンのこのユーザーだけが読める形(Windows の DPAPI)で暗号化して保存します。設定のエクスポートにも含めません。

## インターネットに接続する場合

SimpleCap は、利用者が次の機能を使ったときだけインターネットに接続します。どの場合も、開発者のサーバーには何も送られません。

- **AI で名前付け**:利用者がボタンを押したときだけ、選んだ画像(ビデオ・GIF・MP4 は 1 コマ目、長い辺 1568px までに縮小)を、利用者が設定した AI サービス(OpenAI / Anthropic Claude / OpenRouter)へ利用者自身のシークレットキーで送ります。送った画像は、それぞれのサービスのプライバシー ポリシーに従って扱われます。個人情報や業務上の機密が写った画像には使わないでください。
- **ffmpeg のダウンロード**:MP4 / GIF を初めて作るとき、確認のうえで ffmpeg を公開サイト(gyan.dev / GitHub)からダウンロードします。個人の情報は送りません。
- **スタンプの取り込み(Web から)**:利用者が入力した URL のページや画像をダウンロードします。個人の情報は送りません。

## お問い合わせ

ご不明な点があれば、[GitHub リポジトリ](https://github.com/onboard-dev/SimpleCap) の Issue にてお問い合わせください。

---

# Privacy Policy

The developer of SimpleCap does not collect, store, or transmit your personal information. There is no telemetry and no advertising.

## Stored only on your PC

- Captured images, videos, GIFs and MP4s are saved only to the folder you choose (default: `Pictures\SimpleCap`).
- Settings (shortcut keys, window positions, click effects, etc.) are saved only on your PC (`%APPDATA%\SimpleCap\`).
- The secret key for "AI naming" is encrypted with Windows DPAPI so that only your Windows user account on this PC can read it. It is not included in exported settings.

## When SimpleCap connects to the internet

SimpleCap connects to the internet only when you use the features below. In no case is anything sent to the developer.

- **AI naming**: Only when you press the button, the selected image (the first frame for videos, GIFs and MP4s, resized to at most 1568 px on the long side) is sent to the AI service you configured (OpenAI / Anthropic Claude / OpenRouter) using your own secret key. The image is handled according to that service's privacy policy. Please do not use this feature on images that contain personal or confidential information.
- **ffmpeg download**: The first time you create an MP4 / GIF, SimpleCap asks for confirmation and downloads ffmpeg from public sites (gyan.dev / GitHub). No personal information is sent.
- **Stamp import (from the web)**: The page or images at a URL you enter are downloaded. No personal information is sent.

## Contact

If you have any questions, please open an Issue in the [GitHub repository](https://github.com/onboard-dev/SimpleCap).
