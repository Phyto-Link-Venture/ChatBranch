'use client'

import { Fragment, type ReactNode } from 'react'
import { useT } from './I18nProvider'
import type { MessageKey } from './types'

/**
 * A translated sentence with React nodes inside it — links, bold words,
 * chips. The message keeps its `{name}` placeholders whole so translators can
 * move them; each one is filled from `values` (string, number or element).
 *
 *   en: "Ask an admin under {link}."  →  <Trans k="shell.guard.askAdmin" values={{ link: <Link …/> }} />
 */
export function Trans({ k, values }: { k: MessageKey; values: Record<string, ReactNode> }) {
  const t = useT()
  // translate with the placeholders left in place, then splice the nodes in
  const text = t(k)
  const parts = text.split(/(\{\w+\})/g)
  return (
    <>
      {parts.map((part, i) => {
        const m = /^\{(\w+)\}$/.exec(part)
        if (m && m[1] in values) return <Fragment key={i}>{values[m[1]]}</Fragment>
        return <Fragment key={i}>{part}</Fragment>
      })}
    </>
  )
}
