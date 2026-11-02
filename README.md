# Kronos CLI

An OGFN launcher made by helix.67. Supports Fortnite 1.7.2 up to the latest version.

## How to use

1. Start your backend.
2. Open `Kronos CLI.exe`. The first time, it walks you through your build, backend and login.
3. Press **1** to launch a client.

The **Next step** bar at the top always tells you what's missing. Use ↑↓ and Enter, type a number, or click. Esc goes back, `?` shows help.

## Menu

```
1  Launch Client     play
2  Launch Server     start a gameserver
3  Launch Both       client + gameserver
4  Stop Everything   close everything Kronos started
5  Settings          build, DLLs, login, backend, headless server, proxy
6  Advanced          Live Reversing, caldera token
7  Exit
```

## 30.10 and newer

These builds have EAC, so clients launch through `FortniteClient-Win64-Shipping_EAC_EOS.exe` with Kronos's built-in proxy (no redirect DLL needed). Kronos comes with a caldera token, so it works out of the box.

With the proxy you can't use client/server DLLs and only one game can run at a time, so servers and **Launch Both** always launch without it. With a patched exe, a gameserver and a client work without the proxy.

Older builds always launch without the proxy, using your redirect DLL.

## Live Reversing

Under **Advanced**. It's for backend devs: it restarts the Epic Games Launcher with a capture proxy and saves the real responses Fortnite gets from Epic to `Reversals\`. Images go in `Images\`, lobby videos are put together into normal `.mp4` files with sound in `Videos\` (except DRM-protected ones, which only play inside Fortnite), and repeated lookups like accounts and avatars are combined into one `_lookups.json` per endpoint. Use a throwaway account and don't share the files, they contain your tokens.

## Requirements

- Windows 10 or newer, 64-bit
- A Fortnite build and a running backend
- For proxy launches: a trusted Fiddler root certificate in `CurrentUser\My`

## Tested

I've tested 33.30, 37.51, 41.30, 42.30, and 42.40 and they all work fine, but if you want to run server with the client (with a patched exe and your own or open sourced gameserver) launching it with proxy will not work. Since this launcher has been mostly tested on higher versions we don't know if it works fine on lower versions which I need you guys to do if you can. the only things that need to be tested are just Server, Client, Redirect dll's get injected into the Fortnite process fine. Make an issue if you have any problems on versions below 30.10.

## Credits

- [Reboot Launcher](https://github.com/Auties00/Reboot-Launcher) by Auties00. Kronos borrows a few things from it: when DLLs get injected on older builds, the launch order of the suspended launcher and anti-cheat, the engine build to version list, the universal caldera token, and the headless server option.

## Issues

- Open an issue if you have a problem with the launcher or find a bug.
