import type { Messages } from './messages/en'

/** Another language's copy of a namespace: the same keys, any strings. */
export type Dict<T> = { readonly [K in keyof T]: T[K] extends string ? string : Dict<T[K]> }

/**
 * A language that rewrites only SOME strings (Manglish): any subset of the
 * keys, never a key English lacks. A missing key falls back to English, per
 * key (`translate`). Plural pairs are rewritten together (`x_one` + `x_other`).
 */
export type Overlay<T> = { readonly [K in keyof T]?: T[K] extends string ? string : Overlay<T[K]> }

type Join<K, P> = K extends string ? (P extends string ? `${K}.${P}` : never) : never

/** Every dotted path to a string in T. */
export type Paths<T> = {
  [K in keyof T & string]: T[K] extends string ? K : Join<K, Paths<T[K]>>
}[keyof T & string]

/** Plural pairs (`x_one` / `x_other`) are addressed by their base key `x`. */
type StripPlural<K> = K extends `${infer B}_one` ? B : K extends `${infer B}_other` ? B : K

/** Every key `t()` accepts. A typo is a type error. */
export type MessageKey = StripPlural<Paths<Messages>>

/** Values for `{name}` placeholders. `count` also picks the plural form. */
export type Vars = Record<string, string | number>
