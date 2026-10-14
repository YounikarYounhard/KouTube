# KouTube

«YouTube без интернета»: видео заранее скачиваются на компьютере, а смотрятся на телефоне или планшете — с флешки, из памяти телефона или по Wi-Fi с другого телефона. Сделано на случай отключений света и интернета.

- **`android/`** — приложение для Android 7+ (телефон и планшет), выглядит как YouTube. Режимы: «Сервер» (видео здесь и раздача по Wi-Fi), «Клиент» (смотреть с другого телефона), «Просто плеер» (любая папка с видео).
- **`windows/`** — программа для Windows: качает видео через yt-dlp (720p AV1, SponsorBlock, субтитры, комментарии), подбирает новые по каналам и темам, пишет на флешку или на телефон по USB-кабелю, убирает просмотренное.
- **`spec/FORMAT.md`** — общий формат папки `.koutube`, которую понимают обе программы.

## Сборка

- **Android:** `cd android && gradlew assembleDebug` (JDK 17+, Android SDK).
- **Windows:** .NET 10 SDK, `dotnet publish windows/KouTube -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`. `yt-dlp.exe`, `ffmpeg.exe`, `ffprobe.exe` и `deno.exe` программа при первом запуске скачает сама — в папку `tools` рядом с собой. Установщик собирает NSIS: `makensis /DVERSION=… /DEXE=…\KouTube.exe /DOUT=…\KouTube-Setup.exe windows/installer.nsi`.
- **Проверки:** `dotnet run --project windows/KouTube.Tests` и `gradlew testDebugUnitTest`.
