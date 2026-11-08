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
npm run sync           # copies the config + plugins into both
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

### The one real risk, said plainly

Apple's [guideline 4.2](https://developer.apple.com/app-store/review/guidelines/#minimum-functionality)
rejects apps that are only a repackaged website. This app is not *only* that —
it has real push notifications, native navigation, a native splash and status
bar — but it is a webview at heart, and reviews of business tools in this shape
do sometimes come back. If it does:

* point the reviewer at the notification flow (that is the native function),
* give them a working test login in App Review notes — a reviewer who cannot
  sign in rejects on sight,
* if pressed, the next lever is adding a native capability a browser has no
  answer for (the camera for sending photos, contact import, biometric unlock).

Google Play is far more relaxed about this shape. If you want Android first and
iOS later, that is the lower-friction order. Android also accepts a
[Trusted Web Activity](https://developer.chrome.com/docs/android/trusted-web-activity/)
straight from the existing PWA if you would rather not ship this shell there.

## What is in here

```
capacitor.config.ts   the app: its id, the URL it opens, plugin settings
www/index.html        the "can't reach ChatBranch" card (Capacitor needs a local root)
scripts/icons.mjs     stages the icon + splash from frontend/public/icons
assets/               generated by `npm run icons` (git-ignored)
android/, ios/        generated by `npm run add:*` (git-ignored)
```
