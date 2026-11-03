# SlideDock privacy policy

SlideDock runs on your PC and does not collect, store or send personal data.

- **Settings** (pinned apps, Start apps, theme, position and other preferences) are saved only on your PC, in `%APPDATA%\SlideDock`.
- **Logs** (`startup.log`, `error.log`) stay in the same folder and are never uploaded.
- **Notifications** shown in the calendar are read from Windows on your PC, only after you allow it, and are not sent anywhere.
- **Calendar**: public holidays are downloaded from Google's public holiday calendar for the country set in Windows. If you add your own calendar link, SlideDock downloads that calendar directly from its address; the link is stored encrypted (Windows DPAPI) and the events are cached only on your PC.
- **Update check** (GitHub version only): SlideDock asks `api.github.com` for the latest release of `hubble99/slidedock-windows` and, when you choose to update, downloads it from GitHub. The request contains the app version in its user agent and nothing else. GitHub's own privacy statement applies to that request. The Microsoft Store version doesn't do this; the Store handles its updates.

There are no ads, accounts, analytics or tracking.

Questions: open an issue at https://github.com/hubble99/slidedock-windows/issues
