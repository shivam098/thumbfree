# Publishing ThumbFree on Google Play

Everything needed to publish the Android app, in order. Text in boxes is ready to paste into the Play Console.

**What gets uploaded:** the **bubble edition** as an app bundle, `ThumbFree-x.y.z-google-play.aab`, attached to every
[GitHub release](https://github.com/shivam098/thumbfree/releases). Play Protect's sideloading block doesn't apply to
apps installed from Play, so Play users get the bubble.

## 0. Already done in the app (v0.5.0)

- Targets **Android 16 (API 36)**, required for new apps since 31 Aug 2026.
- Builds an **Android App Bundle** (`.aab`), signed with the ThumbFree upload key, in the release workflow.
- 64-bit native code with 16 KB pages; minimum Android 8.0.
- A prominent disclosure before the accessibility settings screen (first-run guide, bubble step).
- [Privacy policy](privacy.md):
  `https://github.com/shivam098/thumbfree/blob/main/docs/privacy.md`

## 1. Create the developer account (you)

1. Go to <https://play.google.com/console/signup>. Choose a **Personal** account and pay the US$25 one-time fee.
2. Complete **identity verification**: legal name, address, government ID and a phone number. It can take a few days.
3. Verify a contact email. It's shown on the store listing; a dedicated address such as `thumbfree.app@…` is better than
   your personal one.

> New personal accounts must run a **closed test with at least 12 testers for 14 continuous days** before they can
> publish to production. Start recruiting testers (friends, colleagues) as soon as the account exists.

## 2. Create the app

Play Console → **Create app**:

| Field | Value |
|---|---|
| App name | `ThumbFree – Offline Dictation` |
| Default language | English (United States), or English (India) |
| App or game | App |
| Free or paid | Free |
| Declarations | Tick Developer Program Policies and US export laws |

Then **Setup → App signing**: accept **Play App Signing** (Google holds the app signing key; our keystore becomes the
upload key). Keep backing up `%USERPROFILE%\.thumbfree\` anyway: it's the only way to sign updates.

## 3. Store listing (Grow → Store presence → Main store listing)

**Short description (80 characters max):**

```
Type with your voice, 100% offline. English & Hinglish. Your voice stays yours.
```

**Full description:**

```
ThumbFree turns your voice into clean, punctuated text in any app, and it works completely offline. Speech is recognised on your phone: your voice is never uploaded, there's no account and no cloud.

TAP, TALK, TAP
Tap the mic and speak. You see your voice level and your words as you talk. Tap again, and the text is typed where your cursor is.

WRITES LIKE YOU MEANT TO
• Removes filler words ("um", "you know", "basically", "thik hai na") and repeated phrases
• Adds punctuation, or say "comma", "full stop", "new line"
• Emails laid out for you: greeting, body and sign-off on their own lines
• Spoken lists become bullet points: "Things to do today: order groceries, go on a walk and call mom"
• Keeps only your correction: "Friday at 3 pm… actually 4 pm" becomes "Friday at 4 pm"

WORKS WITH ANY KEYBOARD
Use the ThumbFree voice keyboard, or keep your favourite keyboard and tap the floating ThumbFree bubble.

ENGLISH AND HINGLISH
Accurate English (NVIDIA Parakeet), and Hindi + English typed in Roman letters.

YOUR WORDS
Add names and jargon so they're spelled your way, and a personal dictionary for words it mishears.

NEVER LOSE A DICTATION
If a call or another app takes the microphone, ThumbFree tells you. If a dictation can't be typed, it's kept in a notification for you to copy.

PRIVATE BY DESIGN
• Works in flight mode
• No account, no ads, no analytics
• The internet is used only to download the speech model once
• Sharing recordings to help improve ThumbFree is optional and off by default, and nothing is ever sent without you choosing to

Open source: github.com/shivam098/thumbfree
```

**Graphics** (in `store/google-play/` in the repo):

| Asset | Requirement | File |
|---|---|---|
| App icon | 512 × 512 PNG | `icon-512.png` |
| Feature graphic | 1024 × 500 PNG/JPEG | `feature-graphic.png` |
| Phone screenshots | 2–8, 9:16 portrait, from the Pixel | Take these on the phone (see below) |

Screenshots to take on the Pixel, in this order:

1. The keyboard listening, with live words in Gmail.
2. "Transcribing…".
3. The formatted email.
4. A bulleted list in Keep or Notes.
5. The bubble over Gboard in WhatsApp.
6. The first guide screen ("No internet needed").

**Category:** Productivity. **Tags:** Productivity, Tools. **Contact email:** as in step 1. **Website:**
`https://github.com/shivam098/thumbfree`.

## 4. App content (Policy → App content)

| Section | Answer |
|---|---|
| Privacy policy | `https://github.com/shivam098/thumbfree/blob/main/docs/privacy.md` |
| Ads | No, the app doesn't contain ads |
| App access | All functionality is available without special access (no login) |
| Content rating | Questionnaire, category **Utility, Productivity, Communication, or Other**. Answer **No** to everything (violence, sexual content, language, controlled substances, gambling, user interaction or sharing between users, location sharing, digital purchases). Expected rating: Everyone / 3+. |
| Target audience | **18 and over** (a keyboard that records audio should not be aimed at children; this also keeps it outside the Families policy) |
| News app | No |
| Government app | No |
| Financial features | None |
| Health | None |

### Data safety

ThumbFree doesn't send any user data off the device itself. The optional "Help improve ThumbFree" sharing goes
through Android's share sheet, to an app and recipient the user picks each time (a user-initiated transfer). History
(the text of recent dictations) is stored only on the device, which Play doesn't count as collection.

| Question | Answer |
|---|---|
| Does your app collect or share any of the required user data types? | **No** |
| (Shown on the listing) | "No data collected", "No data shared" |

> If Google questions this during review, switch to the conservative declaration: **Audio → Voice or sound recordings**
> and **App activity → Other user-generated content**: *collected*, *optional*, *not shared*, purpose **App
> functionality / Analytics**, *users can request deletion*. Do the same if you ever add a server upload.

### Sensitive permissions and APIs

**Accessibility API** (bubble). Play asks for:

- **Is your app an accessibility tool?** No (`isAccessibilityTool="false"`).
- **Description:**
  ```
  ThumbFree's bubble is a floating microphone that lets people dictate into any app while keeping their usual keyboard. The AccessibilityService is used only to (1) detect when an editable text field in another app has input focus and a keyboard is on screen, so the bubble can be shown next to it, and (2) insert the user's dictated text into that same field (ACTION_SET_TEXT or ACTION_PASTE) after the user taps the bubble to finish. It does not read, store or transmit any other screen content. Speech is recognised on the device; nothing is uploaded. The service is optional: the app's main feature, the ThumbFree voice keyboard, works without it.
  ```
- **Prominent disclosure:** the first-run guide's bubble step, shown before the Settings screen.
- **Video (required):** a 30–60 s unlisted YouTube video recorded on the phone, showing:
  1. the disclosure screen, then turning on "ThumbFree bubble" in Accessibility settings;
  2. opening WhatsApp with Gboard;
  3. tapping the bubble and speaking;
  4. tapping again to show "Transcribing…" and the text appearing.

**Foreground service: microphone** (`FOREGROUND_SERVICE_MICROPHONE`):

```
When the user taps the ThumbFree bubble while another app is in front, the app starts a foreground service of type microphone so it can record the user's dictation. It runs only from the user's tap to start until their tap to finish (or cancel), shows an ongoing "ThumbFree is listening" notification, and stops immediately afterwards. Audio is processed on the device and never uploaded.
```

Use the same video for both.

## 5. Testing tracks

1. **Internal testing** (instant, up to 100 testers): Testing → Internal testing → Create release → upload the `.aab`
   → add your own Google account as a tester → open the opt-in link on the Pixel and install from Play. It installs
   with no Play Protect block.
2. **Closed testing** (required before production for new personal accounts): create a closed track and add **12 or
   more** testers by email or Google Group. Keep at least 12 opted in and using the app for **14 days in a row**.
   Recruiting about 15 allows for drop-outs.
3. **Production:** after the 14 days, Play Console offers "Apply for production". Answer the short questionnaire about
   the test, then roll out (start at 20% if you like).

Reviews of the first release and of the accessibility declaration usually take a few days. If the accessibility
declaration is rejected, the fallback is to publish the **standard edition** (no accessibility service) and keep the
bubble for later. It needs a separate `.aab`: run `./gradlew :app:bundleStandardRelease`.

## 6. Each later release

1. Tag `vX.Y.Z` in the repo. The release workflow builds the signed APKs and `ThumbFree-X.Y.Z-google-play.aab`.
2. Play Console → Production (or a testing track) → Create release → upload the `.aab` → release notes → roll out.

The version code comes from the tag (1.2.3 → 10203), so every release must have a higher version than the last.

## Apple App Store (iPhone, later)

Covered in [PLAN.md](PLAN.md) section 4.2:

- Apple Developer Program, US$99/year (covers iPhone and Mac).
- Upload through Xcode or Transporter.
- TestFlight beta (internal testers immediately; external testers after a short beta review).
- App Review, including guideline 4.4.1 for keyboards and the privacy "nutrition label" ("Data not collected").
