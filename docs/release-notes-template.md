# 릴리스 노트 틀

`gh release create` 의 `--notes-file` 로 넘길 글. 아래를 복사해 버전과 내용만 갈아 끼운다.

에셋이 세 개나 붙어 사용자가 무엇을 받을지 헷갈린다.
**맨 위에서 런처 하나만 가리키고, 나머지는 접어 둔다.**

`AiQuotaTray-standalone.exe` 의 SHA256 은 반드시 남긴다. 런처와 앱이
그 값으로 내려받은 파일을 검증하기 때문이다. 값이 없거나 틀리면 자동 업데이트가 멈춘다.
해시는 `build-release.ps1` 이 빌드 끝에 출력한다.

---

## 👉 Download **AI-Quota-Tray-Setup.exe**

About 2 MB. That is the only file you need — it installs the app, starts it with
Windows, and keeps it updated from then on. No .NET required.

<details>
<summary>What are the other files?</summary>

The launcher downloads `AiQuotaTray-standalone.exe` for you. Take these only if
you want to skip the launcher and update by hand.

| File | Size | Requires | Updates |
|---|---|---|---|
| `AiQuotaTray-standalone.exe` | ~157 MB | Nothing | Manual |
| `AiQuotaTray.exe` | ~550 KB | .NET 10 Desktop Runtime | Manual |

</details>

## What's new

- (이번 판에서 바뀐 것)

## Updating

Installed through the launcher? Open **Settings**, press the update button, and it
downloads with a progress bar and swaps itself on restart. No reboot needed.

## Checksums

```
AiQuotaTray-standalone.exe  <SHA256>
```
