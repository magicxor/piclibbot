# piclibbot
inline image search Telegram bot

[Demo](https://github.com/magicxor/piclibbot/assets/8275793/eefe028d-c7c1-4119-8bed-fda125f0457e)

### Docker

```powershell
docker build . --progress=plain --file=PicLibBot/Dockerfile -t piclibbot:latest
```

Environment variables: `PICLIBBOT_PicLibBot__TelegramBotApiKey`, `PICLIBBOT_PicLibBot__ImagesFetchTimeoutInSeconds`, `PICLIBBOT_PicLibBot__MaxInlineResults`, `PICLIBBOT_PicLibBot__LibreYApiMirrors__0`, `PICLIBBOT_PicLibBot__LibreYApiMirrors__1`, ...

### Configuration
```
    "LibreyApiMirrors": [
      "https://librex.nohost.network",
      "https://librex.uk.to",
      "https://librey.baczek.me",
      "https://librey.franklyflawless.org",
      "https://librey.myroware.net",
      "https://librey.nezumi.party",
      "https://librey.org",
      "https://lx.benike.me",
      "https://ly.owo.si",
      "https://search.funami.tech"
    ]
```

### Back-end

* https://github.com/Ahwxorg/librey
* https://github.com/hnhx/librex
