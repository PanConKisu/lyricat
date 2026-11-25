<p align="center">
  <img src="assets/icon.png" width="72" alt="">
</p>

<h1 align="center">Lyricat</h1>

<p align="center">Shows the lyrics of the song you're playing on Spotify as your <b>Discord custom status</b>, line by line, in real time.</p>

- Synced lyrics straight from **Spotify** (the same ones you see in the app).
- Works with whatever you play on your account: PC, phone, smart speaker...
- A small, fast app with a live preview of how your status looks on Discord.
- An update-speed slider that tells you how risky your choice is: **Safe**, **Questionable** or **Dangerous!**
- Your details are stored **encrypted in Windows Credential Manager**, never in plain text files.
- When you stop it, your usual status comes back.
- Ninu, the cat on top of the player, can dance to the beat of whatever you're listening to.
- Tempo, energy, danceability and speechiness of the current song, with a little heart beating at the song's BPM.
- Updates are verified with SHA256 before they're installed, and only when you click Update.

> [!CAUTION]
> **Disclaimer.** Lyricat is an independent, unofficial project. It is **not affiliated with, endorsed by, or connected to Spotify or Discord** in any way. Spotify and Discord are trademarks of their respective owners.
>
> To work, it uses your **Discord user token** and **internal Spotify endpoints**, which goes against their terms of service and could get your accounts limited or banned.
>
> **You use this program entirely at your own risk.** The author takes no responsibility for any consequences that may come from using it.
>
> The app never grabs your credentials by itself: you paste them in, and they're only ever sent to Discord and Spotify. Never share your token with anyone.

## Requirements

- Windows 10 (up to date) or Windows 11, 64-bit
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — if it's missing, Windows offers to install it the first time you open the app
- Microsoft Edge WebView2 Runtime (already included in Windows 11 and up-to-date Windows 10)
- A free Spotify developer app (see below)

## Install

1. Download `Lyricat.exe` from the [latest release](../../releases/latest).
2. Open it. That's it — no installer.

The `.exe` is built by GitHub Actions straight from this code ([see the workflow](.github/workflows/release.yml)), and every release ships its SHA256 checksum so you can verify it.

> **Windows SmartScreen warns you?** The app isn't code-signed (certificates are expensive for a hobby project). Click *More info → Run anyway*, or build it yourself (below).

## Set up your accounts

Open the **Accounts** page and fill in these four things (hover the **?** next to each one to see where to find it):

| Detail | What it's for |
|---|---|
| Discord token | Changing your custom status |
| Spotify `sp_dc` cookie | Reading the lyrics |
| Client ID and Client Secret | Knowing what's playing (official Spotify API) |

**Create the Spotify app** (only once):

1. Go to the [Spotify developer dashboard](https://developer.spotify.com/dashboard) and click *Create app*.
2. Pick any name and description.
3. Set the *Redirect URI* to exactly `http://127.0.0.1:8888/callback`.
4. Tick only **Web API** and save.
5. Copy the *Client ID* and *Client Secret* from its settings.

The first time you turn on **Sharing** (bottom left), your browser opens so you can let your app read what's playing. It only asks for two read-only permissions.

## Settings

| Setting | Default | What it does |
|---|---|---|
| Lyrics update speed | `4 s` | Minimum time between status changes (Safe ≥ 4 s, Questionable 2–4 s, Dangerous! < 2 s) |
| Status format | `{line}` | Variables: `{line}`, `{title}`, `{artist}` |
| Emoji | *(empty)* | Emoji shown before your status |
| When a song has no lyrics | `meow` | Also shown before the singing starts |
| Instrumental parts | `♪` | Shown during the parts of a song without lyrics. Empty = blank status |
| When Spotify is paused | *(empty)* | Empty = your usual status comes back |
| Lyrics offset | `+0.3 s` | Fixes lyrics running early or late |
| Show two lines at once | Off | Sends lines in pairs as one status, joined by a separator (`-` by default) |
| Stop if Discord rate-limits me | On | Stops at the first 429 |
| Check for updates on startup | On | Only checks; nothing installs without your click |
| Who peeks over the card | Ninu Concept | Ninu Concept or Ninu by Dracani |
| Ninu dances to the music | On | Ninu by Dracani moves her ears to the beat, and her head and paws too on high-energy songs |
| Reduce motion | Off | Stops the BPM heart and the sliding bars (Ninu's dance has its own switch) |

Settings save and apply instantly, even while it's running. They live in `%APPDATA%\Kiisum!\Lyricat`.

## How it works

1. The **official Spotify API**, through your own app, says what you're playing and how far in you are.
2. A hidden, private **WebView2** (the Edge engine built into Windows) opens `open.spotify.com` with your `sp_dc` cookie to get the web player's token, which is used to fetch the **synced lyrics**. Nothing from it is saved to disk.
3. Every few seconds, the current line becomes your **Discord custom status**.
4. For the dance, the song's tempo comes from Spotify and the exact beat from the sound your PC is playing (Windows loopback, like a music visualizer). Nothing is recorded or saved.

## Troubleshooting

- **"Invalid Discord token (401)"**: your token expired (for example, you changed your password). Paste a new one in *Accounts*.
- **"Spotify returned an anonymous token"**: your `sp_dc` cookie expired or is wrong. Copy it again.
- **"Spotify login failed"**: double-check the Client ID, Client Secret and Redirect URI of your Spotify app.
- **"Stopped: Discord answered 429"**: you were changing your status too fast. Wait a bit and move the slider towards *Safe*.
- **It always says "meow"**: that song has no synced lyrics on Spotify.

## Build it yourself

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet publish src/Lyricat/Lyricat.csproj -c Release -o publish
```

## Releasing a new version (for maintainers)

```
git tag v3.9.1
git push origin v3.9.1
```

GitHub Actions builds the `.exe`, runs its self-test and creates the release with its checksum. Anyone using the app sees the update button next time they open it.

## Credits

- **Ninu by Dracani** was drawn by [Dracani](https://linktr.ee/Dracani). Thank you so much!

## License

The code is under the [MIT license](LICENSE).

The artwork in `assets/` (Ninu and the star icon) is **not** covered by it: it belongs to its authors and is used with permission. Please don't reuse it outside Lyricat.
