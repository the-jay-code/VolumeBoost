# Volume Boost

Laptop speakers too quiet even at 100%? Volume Boost works like VLC's volume boost, but for **everything** on your PC: YouTube, Spotify, games, calls. Drag a slider from 100% up to 400%.

It's a tiny tray app (about 15 KB) for Windows 10/11. It uses [Equalizer APO](https://sourceforge.net/projects/equalizerapo/), a free audio processor, to amplify your speakers.

## Install

1. **Install Equalizer APO.** Download it from [sourceforge.net/projects/equalizerapo](https://sourceforge.net/projects/equalizerapo/). During setup, the Configurator asks which devices to install to: tick your **speakers**. Then restart your PC.
2. **Download `VolumeBoost.exe`** from the [Releases](../../releases) page and run it.
3. The first time, it asks for admin approval once. This lets it change Equalizer APO's settings without admin rights after that.

Windows SmartScreen may warn you because the app isn't signed. Click **More info → Run anyway**, or build it yourself (see below).

## Use

- Drag the slider. The boost applies instantly.
- Closing the window keeps the boost running in the tray (by the clock). Left-click the tray icon to open it again.
- Right-click the tray icon → **Exit** turns the boost off.
- To start it with Windows, press `Win + R`, type `shell:startup`, and put a shortcut to `VolumeBoost.exe` there.

## ⚠️ Be careful

Boosting past 100% can **distort** loud sounds (clipping). Played loud for long periods, it can also **damage small laptop speakers**. If the sound crackles, turn the boost down.

## How it works

Volume Boost writes one line, `Preamp: +X dB`, to its own file `VolumeBoost.txt` in Equalizer APO's config folder. It adds `Include: VolumeBoost.txt` to `config.txt`. Your other Equalizer APO settings are left alone. 200% = +6 dB, 400% = +12 dB.

To uninstall, exit the app and delete the `Include: VolumeBoost.txt` line from `C:\Program Files\EqualizerAPO\config\config.txt`.

## Build it yourself

No Visual Studio needed. Windows already ships the C# compiler. Run:

```
build.bat
```

This creates `VolumeBoost.exe` next to it.

## License

MIT
