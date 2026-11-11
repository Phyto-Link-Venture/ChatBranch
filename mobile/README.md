# The ChatBranch phone app

A [Capacitor](https://capacitorjs.com) shell that publishes ChatBranch to the
**App Store** and **Google Play**.

The dashboard is a Next.js *server* application — every page fetches the API from
the browser, and routes like `/contacts/[id]` cannot be statically exported
without knowing every id — so the app **loads your deployment** and adds what a
browser cannot:

| What | Where it is wired |
| --- | --- |
| Notifications when the app is closed (FCM on Android, APNs through Firebase on iOS) | `frontend/src/lib/push.ts` + `backend-csharp/.../Notifications/PushSender.cs` |
| Tapping a notification opens that conversation inside the app | `frontend/src/components/shell/NativeBridge.tsx` |
| The Android back button walks the app's own history, and only exits at the root | same |
| A native splash screen, hidden once the shell has painted (not on a timer) | same + `capacitor.config.ts` |
| A status bar that follows the theme | same |
| Importing the contacts on the phone — the ones the operator ticks, and only those | `frontend/src/lib/deviceContacts.ts` + `frontend/src/components/contacts/DeviceContactsPicker.tsx`, declared by `scripts/permissions.mjs` |

One consequence worth knowing up front: **a dashboard deploy updates the app**.
The UI is served from your deployment, so fixes and new pages reach every phone
without a store release. Only the shell itself (plugins, icons, permissions,
the id) needs resubmitting.

## Before you start

* Node 20+
* **Android:** Android Studio + JDK 17 (`npm run doctor` checks it)
* **iOS:** a Mac with Xcode 15+ and CocoaPods (`sudo gem install cocoapods`)
* Your dashboard on **https** at a stable address — the app refuses to be built
  against a cleartext URL, and both stores ask about that

## First time

```bash
cd mobile
npm install

# the deployment the app opens, and your own app id (permanent per listing)
export CHATBRANCH_URL=https://app.yourdomain.com
export APP_ID=com.yourcompany.chatbranch
export APP_NAME=ChatBranch

npm run icons          # app icon + splash, from the dashboard's own brand mark
npm run add:android    # creates mobile/android (a Gradle project)
npm run add:ios        # creates mobile/ios (an Xcode project) — macOS only
npm run sync           # copies the config + plugins into both, and writes the
                       # contacts permission declarations (scripts/permissions.mjs)
```

`mobile/android/` and `mobile/ios/` are **generated and git-ignored**. They are
yours to edit afterwards (signing, entitlements, store metadata) — if you do,
keep your changes in mind before deleting and re-adding a platform.

## Notifications (the part that needs an account)

Browser notifications need nothing: ChatBranch generates its own VAPID key pair.
The phone app needs Firebase, because Apple and Google only accept pushes from a
registered sender.

1. Create a Firebase project (one covers both platforms) and add an **Android
   app** with your `APP_ID`, then an **iOS app** with the same bundle id.
2. Download `google-services.json` → `mobile/android/app/google-services.json`,
   and `GoogleService-Info.plist` → add it to the Xcode project (drag it into the
   `App` target).
3. iOS only: create an **APNs auth key** (`.p8`) in the Apple Developer portal
   and upload it to Firebase → Project settings → Cloud Messaging. Without it
   iOS pushes are accepted by Firebase and delivered nowhere.
4. In Firebase → Project settings → **Service accounts** → *Generate new private
   key*. Paste that JSON into ChatBranch at **Settings → App & provider keys →
   Firebase service account**. (Or set `FCM_SERVICE_ACCOUNT` in the API's
   environment.)
5. On the phone: **Settings → My notifications → Alert me on this device**, then
   *Send a test alert*.

The service-account JSON is a credential for your whole Firebase project. It
lives on the server, never in this app.

## Phone contacts (the picker)

The app can import the contacts already on the phone. It is a **picker**, not a
sync: the operator is told what will happen, taps the people they want, sees
their names, numbers and emails, and only those rows are uploaded — through
`POST /api/contacts/import`, the same endpoint a .csv or .vcf upload uses, with
the same duplicate handling and the same preview before anything is written.

What it deliberately does **not** do, and must not grow into:

* no silent or background reading of the address book,
* no "sync all my contacts" button,
* no two-way sync, change tracking or conflict resolution,
* no photos, notes or postal addresses — the projection asks for names, numbers,
  emails and company only (asking for notes would also drag in Apple's
  `com.apple.developer.contacts.notes` entitlement).

Both stores reject bulk address-book upload, and the people in that address book
never agreed to anything: under Malaysia's PDPA they are a third party's personal
data the moment they reach the server. A picker is the shape that survives both.

| Piece | Where |
| --- | --- |
| Plugin | `@capacitor-community/contacts` 8.0.0 (Capacitor 8; `@capacitor/core >= 8.0.0`) |
| Reading the address book | `frontend/src/lib/deviceContacts.ts` — read off `window.Capacitor.Plugins`, never imported, so the web build stays free of it |
| Disclosure + picker UI | `frontend/src/components/contacts/DeviceContactsPicker.tsx`, inside the import wizard's first step |
| Copy (every string translated) | `contacts.device.*` in `frontend/src/i18n/messages/{en,ms,zh}/contacts.ts` |
| Permission declarations | `scripts/permissions.mjs`, run by `npm run sync` (or `npm run permissions`) |

`npm run sync` writes the declarations into the generated projects every time,
because `android/` and `ios/` are git-ignored and anything typed into them by
hand disappears when a platform is re-added:

* **iOS** `NSContactsUsageDescription` in `ios/App/App/Info.plist`. Without it
  iOS terminates the app the first time it touches Contacts.
* **Android** `READ_CONTACTS` **and** `WRITE_CONTACTS` in
  `android/app/.../AndroidManifest.xml`. The write permission is not used by
  anything in ChatBranch: the plugin declares both under one Capacitor
  permission alias, and Capacitor treats an alias as granted only when every
  permission in it is granted, so declaring READ alone leaves the picker
  permanently "denied". Both live in Android's single CONTACTS group, so the
  person still sees exactly one prompt.

Where there is no address book the feature hides itself: a desktop browser shows
only the file and Google Contacts sources. Chrome on Android gets the browser's
own Contact Picker API instead of the plugin (feature-detected — iOS Safari has
no such thing).

## Running it

```bash
npm run run:android     # a connected device or emulator
npm run run:ios         # a simulator or a provisioned device
npm run open:android    # Android Studio, for signing and release builds
npm run open:ios        # Xcode, for archiving
```

## Releasing

**Google Play.** In Android Studio: *Build → Generate Signed App Bundle*, with
an upload key you keep (losing it means a new listing). Play requires a current
`targetSdk`; bump it in `android/variables.gradle` when Google's deadline comes
round, then `npm run sync`.

**App Store.** In Xcode: set the team and bundle id, then *Product → Archive →
Distribute*. Push needs the **Push Notifications** capability on the target and
an App Store provisioning profile that includes it.

Both stores will ask for: a privacy policy URL, what data the app collects (it
collects nothing itself — your deployment holds the conversations), the reason
for the notification permission, and screenshots at each required size.

### Contacts: what to tell the reviewers

Contacts is a sensitive permission at both stores, and "we upload the address
book" is a standard rejection. The app does not do that, so say so in their own
words. Paste these as they are.

**App Store Connect → App Review Information → Notes** (add to the existing
notes, and leave the test login in place — a reviewer who cannot sign in rejects
on sight):

> Contacts (NSContactsUsageDescription) is used for one feature only: Contacts →
> Import → "Contacts on this phone". Before the system prompt appears the app
> shows a disclosure screen stating what is read (names, phone numbers, email
> addresses, company names), what is uploaded (only the contacts the user ticks,
> only when they tap Import) and what never happens (the address book is never
> uploaded whole and never synced in the background). The user then selects
> individual contacts in a picker and confirms a preview before anything is
> uploaded. There is no bulk upload, no background access and no contact
> writing. To reproduce: sign in with the test account, open Contacts → Import →
> Contacts on this phone → Choose contacts → tick one → Use 1 contact → Import.

**Google Play Console:**

* **App content → Sensitive app permissions / Permissions declaration** —
  `READ_CONTACTS`. Core functionality: *"Import selected contacts into the
  user's business contact list."* State that access is initiated by the user
  from Contacts → Import, that a prominent disclosure screen precedes the
  runtime prompt, and that only user-selected contacts are uploaded.
* **Prominent disclosure** — it is the first screen of the picker
  (`contacts.device.disclosureTitle` … `neverBody`); a screen recording of that
  screen followed by the runtime prompt is what Google asks for as evidence.
* **Data safety** — Contacts: *collected*, not shared with third parties,
  transferred in encrypted form, used for "App functionality", and **optional**
  (the user chooses the feature and the contacts). Declare the same destination
  as the rest of the app: the operator's own ChatBranch deployment.
* `WRITE_CONTACTS` is declared for the reason in the previous section and is
  never exercised; no declaration form is required for it.

**Privacy-policy paragraph** (publish at the privacy policy URL both stores
require — this is the wording the in-app copy promises):

> **Contacts you choose to import.** If you use "Contacts on this phone", the
> app reads the contacts stored on your device to show you a list to choose
> from. That reading happens on your device. Only the contacts you select are
> sent to your ChatBranch deployment, and only when you confirm the import; we
> upload their names, phone numbers, email addresses and company names, and
> nothing else. Your address book is never uploaded in full, never synchronised
> in the background, and the app never adds to or changes contacts on your
> device. Imported contacts become customer records in your organization's
> ChatBranch account, which is responsible for them as personal data — in
> Malaysia, under the Personal Data Protection Act 2010. You can delete any
> imported contact from Contacts in the dashboard, and you can withdraw the
> app's access to your contacts at any time in your phone's settings.

### The one real risk, said plainly

Apple's [guideline 4.2](https://developer.apple.com/app-store/review/guidelines/#minimum-functionality)
rejects apps that are only a repackaged website. This app is not *only* that —
it has real push notifications, native navigation, a native splash and status
bar — but it is a webview at heart, and reviews of business tools in this shape
do sometimes come back. If it does:

* point the reviewer at the notification flow and the phone-contacts import
  (those are the native functions — a browser can do neither),
* give them a working test login in App Review notes — a reviewer who cannot
  sign in rejects on sight,
* if pressed, the next lever is adding one more native capability a browser has
  no answer for (the camera for sending photos, biometric unlock).

Google Play is far more relaxed about this shape. If you want Android first and
iOS later, that is the lower-friction order. Android also accepts a
[Trusted Web Activity](https://developer.chrome.com/docs/android/trusted-web-activity/)
straight from the existing PWA if you would rather not ship this shell there.

## What is in here

```
capacitor.config.ts   the app: its id, the URL it opens, plugin settings
www/index.html        the "can't reach ChatBranch" card (Capacitor needs a local root)
scripts/icons.mjs     stages the icon + splash from frontend/public/icons
scripts/permissions.mjs  writes the contacts permission declarations into ios/ + android/
assets/               generated by `npm run icons` (git-ignored)
android/, ios/        generated by `npm run add:*` (git-ignored)
```
