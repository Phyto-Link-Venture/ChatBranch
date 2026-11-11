'use client'

/**
 * The phone's own address book → the contact importer.
 *
 * This is the ONLY place that touches a device address book. It reads; it never
 * writes, never syncs in the background and never uploads anything on its own:
 * the caller (`DeviceContactsPicker`) shows a disclosure screen, the person ticks
 * the contacts they want, and only those rows are handed to
 * `POST /api/contacts/import` — the same endpoint a .csv / .vcf / Google import
 * goes through. Nothing here phones home.
 *
 * Two ways in, feature-detected, in this order:
 *
 *  * **The phone app** (Capacitor) — `@capacitor-community/contacts`, read off
 *    Capacitor's own plugin registry rather than imported. The plugin is a
 *    dependency of the MOBILE workspace, not of the dashboard, so importing it
 *    here (even dynamically) would make the web build depend on a package it
 *    must not need. Same trick as `push.ts`.
 *  * **Chrome on Android** — the Contact Picker API (`navigator.contacts`),
 *    where the *browser* draws the picker and hands back only what the person
 *    selected. It does not exist on iOS Safari, on desktop, or inside the
 *    Android WebView, hence the feature test.
 *
 * Anywhere else `deviceContactsSource()` is `'none'` and the caller hides the
 * feature — a plain browser must not see a button that cannot work.
 *
 * Phone numbers are passed through UNTOUCHED. The server normalises them to
 * E.164 with the organization's own `default_country_code` (`PhoneNormalizer`),
 * which is the one normaliser imports, identity discovery and the unknown-customer
 * parser all share; a second, client-side one would drift from it.
 */

import { inNativeApp } from './push'

/** What the OS says about reading contacts. `limited` is iOS 18's partial access. */
export type DeviceAccess = 'granted' | 'limited' | 'prompt' | 'denied' | 'unsupported'

/** Where contacts can come from on this device. */
export type DeviceSource = 'native' | 'web' | 'none'

/** One device contact, flattened to what ChatBranch actually imports. */
export interface DeviceContact {
  /** Stable per device (the OS id, or an index for the web picker). */
  id: string
  name: string
  firstName: string
  lastName: string
  company: string
  phones: string[]
  emails: string[]
}

/** A contact with neither a phone nor an email cannot be imported — the server rejects the row. */
export function isImportable(c: DeviceContact): boolean {
  return c.phones.length > 0 || c.emails.length > 0
}

// ── the native plugin ───────────────────────────────────────────────────────

/**
 * What `@capacitor-community/contacts` v8 exposes, narrowed to the read side.
 * `createContact` / `deleteContact` / `pickContact` exist on the plugin and are
 * deliberately not declared here: this feature never writes to the address book.
 */
interface NativeContactsPlugin {
  checkPermissions(): Promise<{ contacts: string }>
  requestPermissions(): Promise<{ contacts: string }>
  getContacts(options: { projection: Record<string, boolean> }): Promise<{ contacts: NativePayload[] }>
}

interface NativePayload {
  contactId: string
  name?: { display?: string | null; given?: string | null; family?: string | null } | null
  organization?: { company?: string | null } | null
  phones?: { number?: string | null }[] | null
  emails?: { address?: string | null }[] | null
}

/**
 * Only what is imported is asked for: names, phones, emails, company.
 *
 * Everything else the plugin can return is left out on purpose — `image` is slow
 * and personal, `postalAddresses` is not imported, and `note` would oblige the
 * app to hold Apple's `com.apple.developer.contacts.notes` entitlement. Asking
 * for less is both the PDPA answer (collect only what the purpose needs) and one
 * fewer thing to justify to a reviewer.
 */
const PROJECTION = { name: true, phones: true, emails: true, organization: true } as const

function nativePlugin(): NativeContactsPlugin | null {
  if (!inNativeApp()) return null
  const cap = (window as unknown as { Capacitor?: { Plugins?: Record<string, unknown> } }).Capacitor
  return (cap?.Plugins?.Contacts as NativeContactsPlugin | undefined) ?? null
}

// ── the web Contact Picker API ─────────────────────────────────────────────

interface WebContactsManager {
  select(properties: string[], options?: { multiple?: boolean }): Promise<WebContact[]>
  getProperties?(): Promise<string[]>
}

interface WebContact {
  name?: (string | null)[]
  tel?: (string | null)[]
  email?: (string | null)[]
}

function webPicker(): WebContactsManager | null {
  if (typeof window === 'undefined' || typeof navigator === 'undefined') return null
  // In the app the native plugin wins: the WebView has no picker anyway, and a
  // half-supported one must never shadow the real thing.
  if (inNativeApp()) return null
  const manager = (navigator as unknown as { contacts?: WebContactsManager }).contacts
  // MDN's own test: the manager AND its interface. Chrome on Android only.
  if (!manager || typeof manager.select !== 'function') return null
  if (!('ContactsManager' in window)) return null
  return manager
}

/** Where this device's contacts can come from — `'none'` means hide the feature. */
export function deviceContactsSource(): DeviceSource {
  if (typeof window === 'undefined') return 'none'
  try {
    if (nativePlugin()) return 'native'
    if (webPicker()) return 'web'
  } catch {
    /* a hostile or half-built shell: treat as unsupported */
  }
  return 'none'
}

export function deviceContactsSupported(): boolean {
  return deviceContactsSource() !== 'none'
}

// ── permission ─────────────────────────────────────────────────────────────

function asAccess(state: string | undefined): DeviceAccess {
  switch (state) {
    case 'granted':
      return 'granted'
    case 'limited':
      return 'limited'
    case 'denied':
      return 'denied'
    case 'prompt':
    case 'prompt-with-rationale':
      return 'prompt'
    default:
      return 'prompt'
  }
}

/** What the OS allows right now. Never asks for anything. */
export async function checkDeviceAccess(): Promise<DeviceAccess> {
  const source = deviceContactsSource()
  // The browser picker IS the permission: Chrome asks per `select()` call and
  // there is nothing to check beforehand.
  if (source === 'web') return 'prompt'
  if (source !== 'native') return 'unsupported'
  const plugin = nativePlugin()
  if (!plugin) return 'unsupported'
  try {
    return asAccess((await plugin.checkPermissions()).contacts)
  } catch {
    // An older shell without the plugin compiled in: let the request decide.
    return 'prompt'
  }
}

/**
 * Ask the OS. Must be called from a tap, and only AFTER the disclosure screen —
 * Google Play's Prominent Disclosure rule and Apple's purpose string both expect
 * the person to have been told why before the system prompt appears.
 *
 * A `denied` here is as far as the app can go: neither iOS nor Android will show
 * the prompt a second time, so the caller routes the person to Settings.
 */
export async function requestDeviceAccess(): Promise<DeviceAccess> {
  const plugin = nativePlugin()
  if (!plugin) return deviceContactsSource() === 'web' ? 'prompt' : 'unsupported'
  try {
    return asAccess((await plugin.requestPermissions()).contacts)
  } catch {
    return 'denied'
  }
}

// ── reading ────────────────────────────────────────────────────────────────

function clean(values: (string | null | undefined)[] | null | undefined): string[] {
  const out: string[] = []
  for (const raw of values ?? []) {
    const v = (raw ?? '').replace(/\s+/g, ' ').trim()
    // same value twice (a number saved as both "mobile" and "whatsapp") counts once
    if (v.length > 0 && !out.some((x) => x.toLowerCase() === v.toLowerCase())) out.push(v)
  }
  return out
}

function displayName(c: DeviceContact): string {
  return c.name || [c.firstName, c.lastName].filter(Boolean).join(' ').trim() || c.company
}

/** A–Z by the name the person sees, so the picker list reads like their Contacts app. */
function byName(a: DeviceContact, b: DeviceContact): number {
  return displayName(a).localeCompare(displayName(b), undefined, { sensitivity: 'base' })
}

/**
 * Every contact on the device, for the picker to list. The person has already
 * granted access and nothing is uploaded by reading it — the selection they make
 * afterwards is what travels.
 */
export async function readDeviceContacts(): Promise<DeviceContact[]> {
  const plugin = nativePlugin()
  if (!plugin) throw new Error('This app build cannot read phone contacts.')
  const { contacts } = await plugin.getContacts({ projection: { ...PROJECTION } })
  const out = (contacts ?? []).map((raw, i) => {
    const given = (raw.name?.given ?? '').trim()
    const family = (raw.name?.family ?? '').trim()
    return {
      id: raw.contactId || `c${i}`,
      name: (raw.name?.display ?? '').trim(),
      firstName: given,
      lastName: family,
      company: (raw.organization?.company ?? '').trim(),
      phones: clean((raw.phones ?? []).map((p) => p.number)),
      emails: clean((raw.emails ?? []).map((e) => e.address)),
    }
  })
  // A contact with no name at all but a number still deserves a row: the phone
  // number becomes its label in the picker.
  return out.filter((c) => displayName(c).length > 0 || isImportable(c)).sort(byName)
}

/**
 * The browser's own picker (Chrome on Android). The browser draws the list and
 * returns ONLY what the person selected — the page never sees the address book.
 * Resolves to `[]` when they cancel.
 */
export async function pickWebContacts(): Promise<DeviceContact[]> {
  const manager = webPicker()
  if (!manager) throw new Error('This browser cannot open your contacts.')
  let props = ['name', 'tel', 'email']
  try {
    const supported = await manager.getProperties?.()
    if (supported && supported.length > 0) props = props.filter((p) => supported.includes(p))
  } catch {
    /* older Chrome without getProperties: ask for all three */
  }
  if (props.length === 0) throw new Error('This browser cannot share contact details.')
  const picked = await manager.select(props, { multiple: true })
  return (picked ?? [])
    .map((raw, i) => ({
      id: `web-${i}`,
      name: clean(raw.name)[0] ?? '',
      firstName: '',
      lastName: '',
      company: '',
      phones: clean(raw.tel),
      emails: clean(raw.email),
    }))
    .filter((c) => displayName(c).length > 0 || isImportable(c))
    .sort(byName)
}

// ── handing them to the importer ───────────────────────────────────────────

/**
 * The column names the importer already understands.
 *
 * `ImportReaders` presents a vCard and a Google contact book under exactly these
 * headers, and `ContactImporter.Suggest()` maps them to name / first_name /
 * last_name / phone / other_phones / email / other_emails / company_name without
 * the operator touching the mapping step. Keep them in step with
 * `ImportReaders.Col*` (backend-csharp/src/ChatBranch.Data/Contacts/ContactImport.cs).
 */
const COL = {
  name: 'Name',
  first: 'First name',
  last: 'Last name',
  phone: 'Phone',
  otherPhones: 'Other phones',
  email: 'Email',
  otherEmails: 'Other emails',
  company: 'Company',
} as const

/** `source=device` on the import call — 20 characters at most, server side. */
export const DEVICE_IMPORT_SOURCE = 'device'

/**
 * The most contacts one import may carry, mirroring `ContactImporter.MaxRows`.
 * The server answers "split it into smaller files" past this, which is a poor
 * thing to learn after picking; the picker stops the person before the call.
 */
export const MAX_DEVICE_CONTACTS = 20_000

/** The selected contacts as importer rows (one dictionary per contact). */
export function toImportRows(selected: DeviceContact[]): Record<string, string>[] {
  return selected.map((c) => ({
    [COL.name]: c.name,
    [COL.first]: c.firstName,
    [COL.last]: c.lastName,
    [COL.phone]: c.phones[0] ?? '',
    [COL.otherPhones]: c.phones.slice(1).join(', '),
    [COL.email]: c.emails[0] ?? '',
    [COL.otherEmails]: c.emails.slice(1).join(', '),
    [COL.company]: c.company,
  }))
}

/** The label a picker row shows: their name, or the number if that is all there is. */
export function contactLabel(c: DeviceContact): string {
  return displayName(c) || c.phones[0] || c.emails[0] || ''
}

/** Lower-cased haystack for the picker's search box. */
export function searchText(c: DeviceContact): string {
  return [contactLabel(c), c.company, ...c.phones, ...c.emails].join(' ').toLowerCase()
}
