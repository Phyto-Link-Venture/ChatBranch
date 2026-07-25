'use client'

import { useEffect, useRef, useState } from 'react'
import { ControlledSelect, ControlledToggle, Row } from './MockRows'
import { useSettingsDirty } from './SettingsDirtyContext'

/**
 * The demo (not-yet-wired) rows of the AI Configuration tab. They stage their
 * values locally and commit/revert through the shared Settings dirty-state
 * header (Save changes / Discard) — the reference implementation of that model.
 * There is no backend for these yet, so Save just bakes the staged values in as
 * the new baseline (no network call); it demonstrates the commit-or-discard UX
 * the design calls for without faking persistence.
 */
interface Values {
  maxDuration: string
  autoRetry: boolean
  retryInterval: string
  maxRetries: string
  record: boolean
  transcribe: boolean
}

const DEFAULTS: Values = {
  maxDuration: '5 minutes',
  autoRetry: true,
  retryInterval: '1 hour',
  maxRetries: '3',
  record: true,
  transcribe: true,
}

export function AiConfigDemoRows() {
  const dirty = useSettingsDirty()
  const [values, setValues] = useState<Values>(DEFAULTS)
  const baseline = useRef<Values>(DEFAULTS)
  const valuesRef = useRef<Values>(values)
  valuesRef.current = values

  // Register save/discard with the shared header, and clear dirty state when
  // the tab unmounts (switching tabs abandons unsaved demo edits).
  useEffect(() => {
    dirty.register({
      save: () => {
        baseline.current = valuesRef.current
      },
      discard: () => setValues(baseline.current),
    })
    return () => {
      dirty.register(null)
      dirty.setDirty(false)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const set = <K extends keyof Values>(key: K, v: Values[K]) => {
    setValues((prev) => ({ ...prev, [key]: v }))
    dirty.setDirty(true)
  }

  return (
    <>
      <Row
        label="Max Call Duration"
        sub="Abort call after this duration"
        control={
          <ControlledSelect
            opts={['3 minutes', '5 minutes', '10 minutes', 'Unlimited']}
            value={values.maxDuration}
            onChange={(v) => set('maxDuration', v)}
          />
        }
      />
      <Row
        label="Auto Retry on No Answer"
        sub="Automatically retry failed calls"
        control={<ControlledToggle on={values.autoRetry} onChange={(v) => set('autoRetry', v)} />}
      />
      <Row
        label="Retry Interval"
        sub="Time between retry attempts"
        control={
          <ControlledSelect
            opts={['30 minutes', '1 hour', '2 hours', 'Next day']}
            value={values.retryInterval}
            onChange={(v) => set('retryInterval', v)}
          />
        }
      />
      <Row
        label="Max Retries"
        control={
          <ControlledSelect
            opts={['1', '2', '3', '5']}
            value={values.maxRetries}
            onChange={(v) => set('maxRetries', v)}
          />
        }
      />
      <Row
        label="Record Calls"
        sub="Store audio for compliance and review"
        control={<ControlledToggle on={values.record} onChange={(v) => set('record', v)} />}
      />
      <Row
        label="AI Transcription"
        sub="Auto-transcribe all calls with summaries"
        control={<ControlledToggle on={values.transcribe} onChange={(v) => set('transcribe', v)} />}
      />
    </>
  )
}
