# ThumbFree privacy policy

*Last updated: 19 November 2026 (Windows added)*

ThumbFree is a voice-typing app. **It recognises speech on your device and never sends your voice or your text to us
or anyone else.** There is no account, no advertising and no analytics.

## What ThumbFree uses and why

| Data or permission | Used for | Leaves your device? |
|---|---|---|
| **Microphone** | Hearing you between your tap to start and your tap to finish. | No. Audio is kept in memory, turned into text on the device, then discarded. |
| **Text you dictate** | Typing it into the text box you're using, and **History** (below). | No. |
| **Text before the cursor** (keyboard) | Deciding capital letters and spacing, and whether to lay text out as an email. | No. It isn't stored. |
| **Accessibility service** (bubble edition only, off unless you turn it on) | Finding the text box you're typing in and typing your dictation there. | No. It doesn't read, store or send anything else on your screen. |
| **Notifications** | Telling you if the microphone goes silent while you speak, and keeping a dictation that couldn't be typed so you can copy it. | No. |
| **Internet** | Downloading the speech models once, from the addresses listed in the app's source code. Each file is checked against a fixed SHA-256 fingerprint. | Only standard download requests. No personal data is sent. |
| **Settings, word list, personal dictionary** | Your preferences. | No. They're stored in the app's private storage and excluded from cloud backups. |

## History (on by default, can be turned off)

ThumbFree keeps the **text** of your dictations from the last 30 days (at most 500), with the app each was for and
the time, so you can copy one again. It's stored in the app's private storage on your device, excluded from cloud
backups, and never sent anywhere. No audio is kept.

- Nothing is kept from password fields. With the ThumbFree keyboard, nothing is kept from incognito fields either. The
  bubble can't tell whether another app's text box is incognito.
- **Clear history** deletes it all, holding an entry deletes it, and turning **Keep history** off deletes it and stops
  keeping new dictations. Uninstalling the app also removes it.

## "Help improve ThumbFree" (optional, off by default)

If you turn this on, ThumbFree keeps your keyboard dictations on your device so you can choose to share them. Each
one includes the audio, the recognised text, the typed text, the model name and the app version.

- Nothing is kept from password fields, incognito fields or the bubble.
- Nothing is sent automatically. Only if you tap **Share** does ThumbFree make a file and open Android's share sheet,
  where you choose the app and recipient (for example, an email to the ThumbFree developer).
- ThumbFree keeps at most 300 dictations (150 MB). **Delete all** in Settings removes them; uninstalling the app also
  removes them.
- If you send dictations to the developer, they're used only to measure and improve recognition, aren't shared with
  anyone else, and are deleted on request.

## ThumbFree for Windows

The same rules apply on Windows, with these differences:

- **Microphone:** on only from one tap of Ctrl+Win to the next (10 minutes at most).
- **The text box you dictate into:** when you start, ThumbFree uses Windows UI Automation to find out what kind of
  box it is and to read the text before the cursor. It reads whether the box is a password, search or multi-line box,
  and whether it's in an email app. It uses this for spacing, capital letters and email or list layout, and then types
  into that same box. This information isn't stored, and nothing else on your screen is read.
- **History:** stored in `%LOCALAPPDATA%\ThumbFree\history.txt` on your PC. Nothing is kept from password fields or
  from browser windows that say they're private.
- **"Help improve ThumbFree":** dictations are kept in `%LOCALAPPDATA%\ThumbFree\samples`. Nothing is kept from
  password fields, nor from web browsers, because a browser can't reliably say whether a window is private. To share
  them, you save a zip yourself and send it however you like.
- **Settings:** stored in `%APPDATA%\ThumbFree\settings.json`.
- **Logs:** stored in `%LOCALAPPDATA%\ThumbFree\logs`. They hold timings and error names, never your words.
- **Uninstalling:** delete the app's folder, then `%LOCALAPPDATA%\ThumbFree` and `%APPDATA%\ThumbFree`.

## Children

ThumbFree isn't directed at children under 13.

## Changes and contact

Changes to this policy are published at this address, with the date at the top. For questions or deletion requests,
open an issue at <https://github.com/shivam098/thumbfree/issues>.
