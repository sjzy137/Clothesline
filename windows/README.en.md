# Clothesline for Windows — First Candidate

English · [简体中文](README.md)

A native WPF/.NET 10 LTS application for Windows 10 1809+ / Windows 11 x64 (Intel/AMD). The installer includes the runtime and the full MIT license, including the original author notice.

Install Setup.exe or extract the entire portable ZIP into a permanent directory and run Clothesline.exe. Do not copy the EXE alone. Find the app in the taskbar notification area (possibly under hidden icons), then move the pointer to the top of the primary work area. The tray menu can keep the clothesline visible.

Downloads is watched by default using the Windows known-folder location. Add more watched folders, or drag files/folders from Explorer onto the clothesline. Downloads must remain stable across scans for about two seconds; common temporary extensions are filtered. Dragging out provides real file paths. Right-click cards to open, copy, copy image, copy text, edit, save as, show in Explorer, rename, pin, or take down. Use the mouse wheel to reach overflow cards. Removing cards never deletes source files; renaming keeps extensions and rejects collisions or stale metadata.

The tray menu provides recent-file search, launch at login, manual Windows release checks, About/license, and Chinese/English language selection. Clipboard image/text intake is **on by default** and can be turned off at any time; enabled types accept new copies only, deduplicate content, and ignore the app's own copies. Notes can be edited; clipboard items can be saved elsewhere. Owned session copies have current-user-only permissions and are excluded from persistent recent records. Normal exit removes only the owned session directory; crashes may leave temporary copies.

The clothesline slides back down after a **1.8 second** grace period and hides itself while something is playing full screen. **Ctrl+Alt+Up** toggles always-show and auto-hide at any time.

Right-click an image card to **Extract text (OCR)**. The default engine is PP-OCRv6, which ships with the app; the Windows built-in recogniser (`Windows.Media.Ocr`) is also available and needs no download. Nothing leaves the machine: no network, no upload. The text is copied to the clipboard and also shown in a window for checking.

This candidate uses the primary work area only. It does not include macOS Quick Look, system sharing, Trash deletion, or pointer-following across displays. Folders are not recursively monitored or sized. Hidden/system files and links are excluded. Full Windows desktop drag-and-drop, login, and multi-DPI acceptance are still pending.

## Interaction reference

| Action | Result |
|---|---|
| Pointer to the top of the screen | The line slides down; it retracts 1.8 s after the pointer leaves |
| **Ctrl+Alt+Up** | Toggle always-show / auto-hide |
| Single click a card | Copy (images copy as a bitmap); the card flashes "Copied" |
| Double click a card | Open with the default app |
| Drag a card | Send a copy; the original stays |
| **Ctrl + drag** | Moves the original: it is sent to the Recycle Bin once the drop succeeded, and the card leaves the line |
| ● / ○ top-left | Pin / unpin |
| × top-right | Take down: hides the card, never touches the file |
| Right click a card | Open, copy, copy image, copy text, **extract text (OCR)**, edit in Paint, save as, show in Explorer, rename, pin, take down (keep original), **take down (delete original)** |
| Full-screen video or game | The clothesline hides itself; it still slides out while a file is being dragged |

Ctrl+drag writes `CFSTR_PREFERREDDROPEFFECT` to request a move, but **Explorer does not move a CF_HDROP that came from another application**. Clothesline therefore finishes the move itself once the drop succeeded: if the target really moved the file it is already gone and the card is simply dropped, otherwise the original goes to the Recycle Bin first. The bin rather than a hard delete is deliberate — the app cannot see where the target put its copy, so it cannot confirm the copy landed, and the bin keeps that one failure recoverable.

## Extract text (OCR)

Two engines, switchable under Settings → Text recognition. Neither sends the image anywhere.

### PP-OCRv6 (recommended, markedly more accurate)

The Tiny tier ships with the app and is unpacked on first use, so it needs **no network**. Small (30 MB) and Medium (132 MB) are downloaded from ModelScope from the settings window and verified against the SHA256 PaddlePaddle publishes before being used.

| Tier | Size | Labels | Fits |
|---|---|---|---|
| Tiny | 6 MB | 6,904 | Screenshots and printed text, fine day to day |
| Small | 31 MB | 18,708 | Rare characters, small type |
| Medium | 139 MB | 18,708 | Scans and complex layouts |

On one measured sample the Windows engine read "Clothesline" as "CIothesIine" while all three PP-OCRv6 tiers were correct. Higher tiers are more accurate and slower; Tiny takes roughly half a second on an ordinary screenshot.

### The Windows built-in recogniser

Uses `Windows.Media.Ocr` and downloads nothing.

- **Whether it works at all depends on the language packs.** Windows only installs recognisers the user opted into. The settings window lists the languages this machine genuinely has; when none is installed it says so explicitly and offers to open the Windows language settings.
- **To add Chinese:** Settings → Time & language → Language & region → add Chinese (Simplified) → enable **Optical character recognition** in its language options. Restart Clothesline afterwards; no configuration is needed.
- A Chinese engine also reads Latin text and digits, while an English engine cannot read Chinese, so "Auto" prefers Chinese when it is installed.
- Images larger than the engine's limit (`MaxImageDimension`, measured at 10000 px) are scaled down proportionally instead of failing.

Whichever engine is used, handwriting, decorative type, low-contrast screenshots and very small text are unreliable — the result window exists so you can verify before pasting.

## Settings

Open it from the tray: Settings. Five sections — General, Features, Watched folders, Clipboard, Text recognition.

**Watched folders** can be added and removed one by one. Removing one only stops watching it; nothing on disk is deleted.

**Features** switches every optional behaviour off independently. A switched off feature claims no shortcut and never touches a file:

| Switch | Effect when off |
|---|---|
| Hang files dropped on the rope | The drop passes through to the window underneath |
| Ctrl+drag moves the file | Ctrl+drag copies and the original stays |
| Single click copies | A click does nothing |
| Pin and take-down buttons on cards | Plainer cards; both remain in the right-click menu |
| Hide while something is full screen | The rope no longer retracts |
| Ctrl+Alt+Up shows or hides the rope | The combination is released to other software |
| Delay before retracting | 0.8 / 1.8 / 3 / 5 s |

## Explorer integration

The installer registers both by default and removes both on uninstall:

- **"Hang on Clothesline"** in the right-click menu — a classic static verb under `HKCU`, needing **no administrator rights and installing no COM extension DLL**. On Windows 11 it sits behind "Show more options"; on Windows 10 it appears directly.
- **Send to → Clothesline** — the Send To menu is a folder of shortcuts and stays visible in the main Windows 11 right-click menu, which makes it the quicker of the two.

Both can also be applied without the installer:

```powershell
# Install (build the Windows app first, or pass -ExePath)
./scripts/windows-context-menu.ps1

# Uninstall
./scripts/windows-context-menu.ps1 -Uninstall
```

- **Windows 11**: the entry sits under "Show more options".
- **Windows 10**: it appears directly in the context menu.

When several files are selected, Explorer launches one process per file. Those processes exit immediately because only one instance may run, leaving their paths in `%LocalAppData%\Clothesline\pending`; the running instance hangs all of them on its next scan.

Preferences are stored in `%LocalAppData%\Clothesline\settings.json`. Only manual update checks connect to this repository's GitHub Releases; files and clipboard content are not uploaded. Windows uses `windows-v` release tags so macOS never offers a Windows installer. Launch at login changes only the current user's Run entry when explicitly selected. Installation needs no administrator rights. Uninstallation removes the installed program and its matching startup entry, preserving preferences and source files.

Build on Windows with .NET 10 SDK and Inno Setup 6 or 7:

```powershell
# Self-contained publish + installer only (fastest route to something usable)
./scripts/build-windows-installer.ps1 -Iscc "C:\Program Files (x86)\Inno Setup 7\ISCC.exe"

# Full pipeline: core tests, smoke tests, publish, portable ZIP, checksums
./scripts/build-windows.ps1
```

The Windows workflow runs core safety/watcher tests, self-contained publishing, a WPF construction/file-drop payload smoke test, installation, installed-app smoke test, and uninstallation with a source-file sentinel. These automated checks do not establish complete interactive UI acceptance. The installer is not Authenticode-signed; Windows may show an unknown publisher.

Completion detection is heuristic: a transfer paused for over two seconds under its final name may appear early. Folder contents are not recursively checked for completion.

Final Windows runner verification: [run 38701416790](https://github.com/zhongguozhang/Clothesline/actions/runs/38701416790), source `8b2e09b`. All core, image-lock, app-construction, installed-app, and install/uninstall checks passed. Interactive desktop acceptance remains pending.
