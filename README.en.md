[简体中文](README.md) | [日本語](README.ja.md) | [English](README.en.md)

# SF6 Recorder

Save your `Street Fighter 6` match history from [CAPCOM's Buckler's Boot Camp](https://www.streetfighter.com/6/buckler/profile/) to your PC. The official website only keeps recent matches. Each time you refresh, `SF6 Recorder` saves new match records to a local SQLite database for long-term storage, so they remain available after they disappear from the website.

## Automated builds and packages

### Download automated builds

Download from the [latest release](https://github.com/MooWantFree/SF6-Recorder/releases/latest).

- `SF6-Recorder-win-x64-self-contained.zip`: includes the .NET runtime
- `SF6-Recorder-win-x64-framework-dependent.zip`: does not include the .NET runtime; requires .NET 10 Windows Desktop Runtime (x64) to be installed

### Local builds

To build and package both versions locally:

```powershell
pwsh -File .\build.ps1 -ChecksOnly
pwsh -File .\scripts\Publish-Packages.ps1
```

ZIP files are saved to `artifacts\packages` by default.

## Requirements and startup

- Windows 10 or Windows 11 (64-bit)
- The framework-dependent version requires .NET 10 Windows Desktop Runtime (x64); the self-contained version includes .NET and does not require a separate installation
- Microsoft Edge WebView2 Runtime (usually included with Windows 11)

Extract the ZIP and run `SF6 Recorder.exe`.

![Match history](image/image-20261113233624167.png)

![Score trends](image/image-20261113234059975.png)

## Signing in and managing accounts

Sign in to your CAPCOM ID before downloading match history for the first time. Click "Sign in" in the upper-right corner to open the official website in the built-in browser. If a Cloudflare verification challenge appears, complete it as instructed, then enter your account credentials. Once a valid session is detected, the app saves it and closes the sign-in window automatically.

> Sign-in cookies are encrypted with Windows DPAPI and stored in the local `Data` directory. Only the current Windows user can decrypt them.
>
> If your connection to CAPCOM is slow, try configuring a proxy in Settings.

The account menu in the upper-right corner lets you add multiple CAPCOM accounts, switch between them, or sign out of a specific account.

Signing out removes only the sign-in credentials saved on this PC; downloaded match history and scores are kept.

## Syncing and browsing match history

### Manual refresh

The "Matches" tab at the top of the main window groups matches by date. Its category bar includes "All", "Ranked Match", "Custom Room", "Casual Match", "Battle Hub Match", and "Favorites".

Click the refresh button in the upper-left corner (`Ctrl+R`) to incrementally sync match history for the player you are currently viewing.

Combine the filters to search local records:

- The text search box (`Ctrl+F`) supports opponent names, full or partial UIDs, and replay IDs
- The dropdowns filter by your character, the opponent's character, and result (win, loss, draw, or pending)
- The date range picker supports a single day (double-click the same date in the calendar) or a range spanning multiple days

### Automatic refresh

<img src="image/image-20261114061915238.png" alt="Automatic refresh settings" style="zoom: 33%;" />

When you enable `Refresh after each match`, the app checks whether SF6 is running. While the game is running, it captures the game screen at the configured interval to detect when a match ends, then waits the configured number of seconds before refreshing. Enable `Show latest match after refresh` to scroll to the latest match automatically after a refresh.

> How it works: the app captures the top of the game screen and detects the results screen by looking for the English text "RESULT" or the Chinese text "结果". Only Chinese, Japanese, and English have been tested; other languages have not been tested.
>
> Measured CPU usage:
>
> | Measurement | Result (CPU: i5-12490F) |
> | --- | ---: |
> | Screen capture and recognition thread CPU usage | Approx. 0.15% |
> | Total test process CPU usage | Approx. 0.16% |
> | Average time per capture and recognition | 11.88 ms |

When `Refresh after each match` is enabled and the game is running, a pop-up chart in the match history panel shows score changes since the app was launched.

<img src="image/image-20261114064948390.png" alt="Score changes since app launch" style="zoom:33%;" />

## Match details and head-to-head history

<img src="image/image-20261114063209913.png" alt="Match details" style="zoom:33%;" />

Click any match in the list to view both players' information and the match breakdown in the panel on the right.

Enter a 10-digit UID in the player search box in the upper-right corner (`Ctrl+K`) and press Enter, or click "Matches" beneath either player's portrait in the details panel on the right, to open that player's profile.

Click "Ranks" in the match details to open a separate window. View "Rank / Points" (LP and MR) or highest MR for all of that player's characters, using "All-time best" or a specific season.

<img src="image/image-20261114063630308.png" alt="Rank and points history" style="zoom:33%;" />

<img src="image/image-20261114064052137.png" alt="Highest MR history" style="zoom:33%;" />

## How ranked score changes are calculated

The LP and MR values returned by CAPCOM's match history API are pre-match scores. An individual replay does not contain the post-match score.

> The app follows consecutive ranked matches played with the same character. It uses the next match's pre-match score as this match's post-match score, then subtracts this match's pre-match score to calculate the change. For the latest ranked match, if the refresh successfully retrieves the character's current score from the official website, the app uses that score to estimate the post-match score and change. A tooltip explains that this difference may include matches missing from the archive. If adjacent records are missing or continuity across seasons cannot be confirmed, the app keeps the pre-match score and displays "Settlement unknown".

## Tags and favorites

The rightmost column in the match list contains tags. Click the plus button to add or remove tags for a match:

- Enter a name and press `Enter` to create a tag (1 to 32 characters), or select an existing tag.
- Any match with at least one tag appears in "Favorites". Removing its last tag removes it from Favorites, while keeping the match itself in the database.
- In "Favorites", filter matches by one or more tags and choose between "Any tag" and "All tags" matching.
- The "Player" dropdown in the upper-left corner of Favorites includes both players from every favorited match. Selecting a player makes the list and details use that player's perspective, swapping the displayed characters, scores, results, ranks, and points as needed. The saved raw replay data is unchanged.

## Settings and data backup

Click the gear button in the upper-right corner (`Ctrl+,`), or choose "Settings" from the system tray icon's right-click menu, to open Settings.

Proxies using `http://`, `https://`, and `socks5://` without authentication are supported. Saved settings apply to subsequent sign-ins and syncs.

"CAPCOM interval" sets the minimum time between requests for official website data. The default is 2 seconds. Setting it too low may trigger CAPCOM's HTTP 429 rate limiting.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+R` | Start syncing match history; press again during a download to stop it |
| `Ctrl+F` | Switch to "Matches" and focus the list's search box |
| `Ctrl+K` | Activate the player UID search box in the upper-right corner |
| `Ctrl+,` | Open Settings |
| `Alt+←` / mouse back button | Return to the previous page or player |
| `Alt+→` / mouse forward button | Go to the next page or player |
| `←` / `→` | With the chart selected in "Scores", move to the previous or next match |
| `Esc` | Close the open search box, tag popup, or calendar popup |

## License

[GNU GPL v3.0](LICENSE)
