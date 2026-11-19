# ThumbFree for Windows

Tap **Ctrl+Win**, speak, tap it again: your words are typed into whatever app you're using. Speech is recognised on
your PC; nothing you say is uploaded, and there's no account.

ThumbFree replaces Voice Dictation. Your Voice Dictation settings, personal dictionary and downloaded models carry
over the first time ThumbFree starts.

## Install

1. From [Releases](https://github.com/shivam098/thumbfree/releases), download the zip for your PC:
   - `ThumbFree-x.y.z-win-arm64.zip`: Snapdragon and other ARM PCs (Copilot+ PCs, Surface Pro X).
   - `ThumbFree-x.y.z-win-x64.zip`: Intel and AMD PCs (most others).
2. Unzip it anywhere (Documents, for example) and run `ThumbFree.exe`. Windows may say "Windows protected your PC"
   because the app isn't code-signed: click **More info**, then **Run anyway**.
3. A short guide walks you through:
   - the microphone;
   - the speech model (it downloads once; 79 MB for live English);
   - a practice email and list;
   - notifications;
   - "What do you do?";
   - whether to keep your dictations to share.

Exit **Voice Dictation** and **Voice Assistant** first: they also use Ctrl+Win, so text would be typed twice.
ThumbFree warns you if either is running.

To build and install from source instead (no administrator rights needed), run `windows\scripts\install.ps1`. It
installs to `%LOCALAPPDATA%\Programs\ThumbFree` and adds a Start menu shortcut; `-StartAtSignIn` starts it when you
sign in.

## Dictate

1. Click where you want to type, for example a chat message box.
2. **Tap Ctrl+Win** (press and let go). You hear a short tone, and the pill at the bottom of the screen shows your voice
   level.
3. Speak in phrases, the way you'd talk to someone. With **Live typing** on (the default), each phrase is typed as
   soon as you've moved on to the next one, and the words not typed yet show in the pill.
4. **Tap Ctrl+Win** again when you're done. The rest is typed.

Why one phrase behind? Text typed into another app can't be taken back safely, so only words that can't change any
more are typed: "Friday at 3 pm… actually 4 pm" or "scratch that" can still change the phrase you're saying. A list or
an email waits until you finish, so it can be laid out.

- **Esc** cancels. Words already typed stay.
- **Ctrl+Win+Space** works the same as Ctrl+Win.
- **Alt+Shift+Z** types your last dictation again.
- **Alt+Shift+H** switches between English and Hindi + English.
- A dictation finishes by itself after 10 minutes, so the microphone is never left on.

## Speaking tips

- Punctuation: "comma", "full stop" or "period", "question mark", "exclamation mark", "colon", "new line",
  "new paragraph".
- Fillers ("um", "uh", "you know", "basically", "thik hai na") and words said twice are left out.
- "Scratch that" drops the phrase before it.
- **Smart formatting** (on by default):
  - Emails get their greeting and sign-off on lines of their own. In a mail app or webmail, a greeting or a sign-off
    alone is enough; elsewhere it needs a greeting and your name at the end.
  - Spoken lists become lists: "Things to do today: order groceries, go on a walk, and call mom".
  - "3 pm… actually 4 pm" keeps only the correction.
  - Lists and emails need a box that takes several lines.

## Speech models

Pick them in **Settings › Speech model**. Each downloads once and is checked against a fixed SHA-256 fingerprint.

| Language | Model | Download | Live typing |
|---|---|---|---|
| English, **Live** (default) | Kroko streaming | 79 MB | Words as you speak (about 1 s behind you) |
| English, Standard | NVIDIA Parakeet TDT 110M | 137 MB | Each phrase, a moment after you pause |
| English, Accurate | NVIDIA Parakeet TDT 0.6B | 661 MB | Each phrase, a moment after you pause (needs 6 GB+ memory) |
| Hindi + English, Standard | Oriserve Whisper Swift | 82 MB | Each phrase, a moment after you pause |
| Hindi + English, Accurate | Oriserve Whisper Apex | 874 MB | No: typed when you finish, a few seconds later (needs 8 GB+ memory). If the Standard model is downloaded, your words show in the pill while you speak |

Hindi + English is typed in Roman letters, with English words left in English: "kal ki meeting cancel ho gayi hai".
Models bigger than 300 MB ask before downloading, and warn you on a metered connection.

On a Snapdragon X Elite, 10 s of speech takes Parakeet 110M about 0.14 s, Parakeet 0.6B about 0.4 s, and Kroko about
0.24 s.

## Never lose a dictation

- Text only ever goes into the box you started in. If you click into another box or switch windows while
  speaking, the rest isn't typed anywhere. A notification, **Your dictation is safe**, keeps the whole dictation;
  click it to copy. It's also in History and in the tray menu (**Copy the text that wasn't typed**).
- If the microphone sends only silence (a call or another app took it), ThumbFree tells you straight away.
- Password fields, and apps running as administrator, are never typed into.

## Settings

Open ThumbFree from the Start menu or the tray icon. The **Settings** page has:
- the language and model;
- Live typing and Smart formatting;
- Words I use often, the personal dictionary and "What do you do?";
- sounds;
- History;
- "Help improve ThumbFree";
- whether to start when you sign in.

Changes apply straight away.

The settings file is `%APPDATA%\ThumbFree\settings.json`. Edits by hand are kept, and so are comments, except
comments inside the personal dictionary (`"replacements"`) when you change the dictionary on the Settings page. If the
file has a mistake, Home and Settings say which line, and ThumbFree doesn't overwrite it. These options are only in the
file, and need a restart:

```json
"snippets": { "my signature": "Thanks,\nShivam" },
"newLineKeys": { "Notepad": "Enter", "obsidian": "Enter" },
"insertMethod": "Paste",
"maxDictationSeconds": 600,
"microphoneDevice": -1,
"pasteLastHotkey": "Alt+Shift+Z",
"dictationLanguageHotkey": "Alt+Shift+H"
```

- **Snippets:** say the trigger ("my signature", or "insert my signature") on its own to type the text.
- **New lines in chat apps:** "new line" presses **Shift+Enter**, which chat apps treat as a line break instead of
  "send". Notepad, Word, VS Code and Visual Studio get **Enter**. Add other apps by their process name (Task Manager ›
  Details, without `.exe`).

## History and your data

- **History** keeps the text of your dictations for 30 days (500 at most) on this PC:
  `%LOCALAPPDATA%\ThumbFree\history.txt`.
  - Click one on the History page, on Home or in the tray menu to copy it.
  - Nothing from password fields or private browser windows is kept.
  - Clear it, or turn it off, in Settings.
- **Help improve ThumbFree** (off unless you turn it on) keeps each dictation's audio and text on this PC, to share
  only if you choose to, as a zip you save yourself. It never keeps anything from:
  - password fields;
  - web browsers, because a browser can't reliably say whether a window is private.

  At most 300 dictations (150 MB) are kept. **Delete all** removes them.
- The internet is used only to download speech models. Logs (`%LOCALAPPDATA%\ThumbFree\logs`) hold timings and error
  names, never your words.
- **Chrome, Edge and apps built on them** (VS Code, Slack, Teams, the new Outlook) only tell other apps about the text
  box you're in once their accessibility support is on. Your first dictation into one of them turns it on, as Narrator
  and Voice Access do. It stays on until that app restarts, which costs the app a little speed on big pages.

## Troubleshooting

| Problem | Try this |
|---|---|
| Nothing happens on Ctrl+Win | Check the tray icon. A grey dot means the keys are paused (tray › Resume dictation keys) or the speech model isn't downloaded (open ThumbFree). |
| "Couldn't open the microphone" | Settings › Privacy & security › Microphone: turn on *Microphone access* and *Let desktop apps access your microphone*. Check the right device is the Windows default, or set `microphoneDevice`. |
| "I didn't catch anything" | Speak a little louder or closer; check the microphone level in Settings › System › Sound. |
| Text is typed twice | Voice Assistant or Voice Dictation is also running. Exit it. |
| Nothing is typed into one app | If the app runs as administrator, Windows blocks typing into it. Run the app normally. |
| "You moved to another window or text box" | The box you started in lost the focus while you spoke. Click the notification to copy your dictation. |
| Some characters go missing | Set `"insertMethod": "Paste"` (the default). |
| Slow on an older PC | Pick English Standard or Live, or turn Live typing off. |

Logs are in `%LOCALAPPDATA%\ThumbFree\logs`. They contain timings and error names, never what you said, so they're
safe to share in an issue.

## Build and test

Everything is in `windows\` (`ThumbFree.slnx`):

| Project | What |
|---|---|
| `src/ThumbFree.Core` | The shared text rules, held to `tests/vectors` like Android's core: formatter, layout, live typing, word list, History format |
| `src/ThumbFree.Speech` | Kroko and Parakeet (sherpa-onnx), Whisper (Whisper.net), Silero, the model store |
| `src/ThumbFree.App` | The tray app: the dictation host, UI Automation, typing, the app window and guide, History, samples |
| `tools/SpeechBench` | Speed and accuracy of each model on the test recordings |

Tests:

```powershell
dotnet test tests\ThumbFree.Core.Tests -c Release
dotnet test tests\ThumbFree.Speech.Tests -c Release   # engine tests run when the models are in %LOCALAPPDATA%\ThumbFree\models
dotnet test tests\ThumbFree.App.Tests -c Release
```

Real-app lab: a Chrome test page, with scripted phrases typed live or at the end, and recordings played as if spoken.
There's no microphone. Run it while you're away from the keyboard; it stops at the first failed check, so nothing is
typed elsewhere:

```powershell
.\tests\DictationLab\run.ps1 -Exe .\src\ThumbFree.App\bin\Release\net10.0-windows\win-arm64\ThumbFree.exe
.\tests\DictationLab\run.ps1 -Exe ... -Page gmail-lab.html -Script gmail-lab.selftest.txt
```
