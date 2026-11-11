/**
 * The store-facing permission declarations, written into the generated native
 * projects.
 *
 * `android/` and `ios/` are created by `npm run add:*` and git-ignored, so a
 * permission typed into them by hand is lost the moment a platform is re-added —
 * and a missing one is not a compile error, it is a crash on iOS (Apple kills an
 * app that touches Contacts with no purpose string) and a silent denial on
 * Android. This script is therefore the ONE place those declarations live, it is
 * idempotent, and `npm run sync` runs it every time.
 *
 * What it writes, and why each line is there:
 *
 *   * **iOS `NSContactsUsageDescription`** — the sentence Apple shows in the
 *     system prompt. Required by `@capacitor-community/contacts`; without it the
 *     app is terminated on the first contacts call.
 *   * **Android `READ_CONTACTS`** — what the contact picker actually needs.
 *   * **Android `WRITE_CONTACTS`** — needed only because the plugin declares
 *     both permissions under ONE Capacitor permission alias ("contacts"), and
 *     Capacitor grants an alias only when every permission in it is granted
 *     (`Bridge.getPermissionStates`: "multiple permissions with the same alias
 *     must all be true, otherwise all false"). An undeclared permission can
 *     never be granted, so declaring READ alone leaves the alias permanently
 *     denied and the picker dead. ChatBranch never calls `createContact` or
 *     `deleteContact`; both permissions are in Android's single CONTACTS group,
 *     so the person still sees one prompt.
 *
 * The purpose string is deliberately the same promise the in-app disclosure
 * screen makes (`contacts.device.*` in frontend/src/i18n/messages/en/contacts.ts).
 * If one changes, change the other.
 */
import { existsSync, readFileSync, writeFileSync } from 'fs'
import { dirname, join } from 'path'
import { fileURLToPath } from 'url'

const root = join(dirname(fileURLToPath(import.meta.url)), '..')

/** Apple's prompt text. Keep it specific, in the first person, and under ~200 characters. */
const IOS_CONTACTS_PURPOSE =
  'ChatBranch shows the contacts on this phone so you can choose which of them to add to your business contact list. ' +
  'Only the contacts you select are uploaded — your address book is never uploaded whole and never synced.'

const ANDROID_PERMISSIONS = [
  ['android.permission.READ_CONTACTS', 'the in-app contact picker (import the contacts you select)'],
  [
    'android.permission.WRITE_CONTACTS',
    "required by @capacitor-community/contacts' single permission alias; ChatBranch never writes to the address book",
  ],
]

let changed = 0
let missing = 0

// ── iOS ────────────────────────────────────────────────────────────────────
const plistPath = join(root, 'ios', 'App', 'App', 'Info.plist')
if (!existsSync(plistPath)) {
  console.log('ios: not added yet (npm run add:ios) — skipped.')
  missing++
} else {
  let plist = readFileSync(plistPath, 'utf8')
  if (plist.includes('<key>NSContactsUsageDescription</key>')) {
    // Keep the sentence in step with this file, but never duplicate the key.
    const rx = /(<key>NSContactsUsageDescription<\/key>\s*<string>)([\s\S]*?)(<\/string>)/
    const [, , current] = plist.match(rx) ?? []
    if (current === IOS_CONTACTS_PURPOSE) console.log('ios: NSContactsUsageDescription already set.')
    else {
      plist = plist.replace(rx, `$1${escapeXml(IOS_CONTACTS_PURPOSE)}$3`)
      writeFileSync(plistPath, plist)
      console.log('ios: NSContactsUsageDescription updated.')
      changed++
    }
  } else {
    const at = plist.lastIndexOf('</dict>')
    if (at < 0) {
      console.error(`ios: ${plistPath} has no closing </dict> — add the key by hand in Xcode.`)
      process.exitCode = 1
    } else {
      const entry = `\t<key>NSContactsUsageDescription</key>\n\t<string>${escapeXml(IOS_CONTACTS_PURPOSE)}</string>\n`
      writeFileSync(plistPath, plist.slice(0, at) + entry + plist.slice(at))
      console.log('ios: NSContactsUsageDescription added.')
      changed++
    }
  }
}

// ── Android ────────────────────────────────────────────────────────────────
const manifestPath = join(root, 'android', 'app', 'src', 'main', 'AndroidManifest.xml')
if (!existsSync(manifestPath)) {
  console.log('android: not added yet (npm run add:android) — skipped.')
  missing++
} else {
  let manifest = readFileSync(manifestPath, 'utf8')
  const lines = []
  for (const [name, why] of ANDROID_PERMISSIONS) {
    if (manifest.includes(`android:name="${name}"`)) {
      console.log(`android: ${name.split('.').pop()} already declared.`)
      continue
    }
    lines.push(`    <!-- ${why} -->`, `    <uses-permission android:name="${name}" />`)
  }
  if (lines.length > 0) {
    const at = manifest.lastIndexOf('</manifest>')
    if (at < 0) {
      console.error(`android: ${manifestPath} has no closing </manifest> — add the permissions by hand.`)
      process.exitCode = 1
    } else {
      const block = `\n    <!-- Contacts import (mobile/scripts/permissions.mjs — do not hand-edit) -->\n${lines.join('\n')}\n`
      writeFileSync(manifestPath, manifest.slice(0, at) + block + manifest.slice(at))
      console.log(`android: declared ${lines.length / 2} contacts permission(s).`)
      changed++
    }
  }
}

if (missing === 2) {
  console.log('Nothing to patch yet — add a platform first, then run this again (or `npm run sync`).')
} else if (changed === 0) {
  console.log('Permissions already in place.')
}

/** Plist and manifest values are XML text. */
function escapeXml(s) {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
}
