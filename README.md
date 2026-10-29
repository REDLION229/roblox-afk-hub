# Roblox AFK Hub

Run several Roblox accounts at once, and keep them in game while you're away.
One small app for Windows that replaces FPS unlockers, Process Explorer tricks, anti-AFK macros and logging in and out in the browser.

## What it does

- **Multi-instance:** open as many Roblox windows as you like, one per account.
- **Accounts:** log in once per account. No more switching accounts in the browser.
- **Anti-AFK:** each window has its own timer, so no account gets kicked after 20 minutes.
- **Keeps you in game:** if an account disconnects, crashes or its window closes, it's rejoined automatically, and you get an alert.
- **Presets:** save a setup per game (accounts, server, FPS, macros) and launch it with one click. Pin up to 3 to Home.
- **Macros:** record your moves in a window (or import a TinyTask `.rec`) and loop them. Several windows take turns.
- **Phone alerts and live status:** through a Discord webhook. One pinned message keeps itself updated with every account's status, optionally with a picture of the Roblox windows.
- **FPS and graphics per account:** lower FPS for idle accounts means less heat and power.
- **Overnight extras:** keep the PC awake, resume after a Windows restart, frozen-window recovery, a daily schedule.

## Download and run

1. Download `Roblox-AFK-Hub-vX.zip` from [Releases](../../releases) and unzip it.
2. Run `Roblox AFK Hub.exe`. It's one portable file, with nothing to install.
3. Windows may say "Windows protected your PC" because the app isn't signed. Click **More info → Run anyway**.
4. **Only download it from this repository's Releases.** To check your copy is the real one, run `Get-FileHash "Roblox AFK Hub.exe"` in PowerShell and compare it with the fingerprint in the release notes. If it doesn't match, delete it.

**Needs:** Windows 10 (1903 or newer) or Windows 11, 64-bit. The Edge WebView2 Runtime is already on almost every PC. If it's missing, the app tells you where to get it.

## Your data and safety

- **Your logins stay on your PC.** Each account's Roblox login cookie is encrypted with your Windows account (DPAPI). It only works on the PC and Windows user it was added on, and it's only ever sent to `roblox.com` over HTTPS.
- **Nothing is sent anywhere else**, except the Discord messages you set up yourself. Those contain only account names, game names and counts, never logins.
- **Where data is kept:** `%APPDATA%\AfkHub` (settings, encrypted logins, macros, stats, `activity.log`). In portable mode it's an `AfkHub-data` folder next to the exe instead. **Never share that folder or zip it up with the app**, because it holds your logins.
- Removing an account in the app doesn't log it out on Roblox. To fully revoke a login, use **Log out of all other sessions** in Roblox's settings.
- This app automates input and multi-instance for your own accounts. Use it at your own risk and follow Roblox's rules.

## Build from source

```
powershell -ExecutionPolicy Bypass -File build.ps1     # builds bin\Roblox AFK Hub.exe
powershell -ExecutionPolicy Bypass -File release.ps1   # clean release\ folder with just the exe + a zip
```

The build script downloads the C# compiler and WebView2 from NuGet into `.tools\`, so no .NET SDK or Visual Studio is needed. The app targets .NET Framework 4.8, which ships with Windows.

The interface is plain HTML/CSS/JS in `src/ui/`, shown with WebView2. `src/MainForm.cs` sends it the app's state every second and handles the commands it posts back.

Handy flags: `--preset "Name"` (launch a preset on startup, useful for a desktop shortcut), `--page <name>`, `--snapshot <png>`, `--test-login`, `--devtools`.

## Credits

Made by swag. Built with Claude (Anthropic). Inspired by Spencer's AFK macro.

Not affiliated with or endorsed by Roblox Corporation.

## License

[MIT](LICENSE): free to use, change and share, as long as the copyright notice stays in.
