'use client'

import { useEffect } from 'react'
import { isStaleBuildError, reloadForNewBuild, reportClientError } from '@/lib/clientErrors'

/**
 * Last resort: the root layout itself (the shell, the providers) threw. No
 * providers or stylesheet can be trusted here, so it is plain English with
 * inline styles. A stale-build chunk error reloads once.
 */
export default function GlobalError({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  useEffect(() => {
    if (isStaleBuildError(error) && reloadForNewBuild()) return
    reportClientError(error, { digest: error.digest, where: 'root' })
  }, [error])

  const btn: React.CSSProperties = {
    padding: '8px 14px', borderRadius: 8, border: '1px solid #c9c2b6', background: '#fff', cursor: 'pointer', fontSize: 13,
  }
  return (
    <html lang="en">
      <body style={{ margin: 0, fontFamily: 'system-ui, sans-serif', background: '#faf7f2', color: '#1c1a17' }}>
        <div role="alert" style={{ maxWidth: 560, margin: '12vh auto', padding: 24 }}>
          <h1 style={{ fontSize: 22, margin: '0 0 8px' }}>ChatBranch hit a problem</h1>
          <p style={{ fontSize: 14, lineHeight: 1.5, margin: '0 0 12px', color: '#5c564d' }}>
            The app failed to start in this tab. Reloading usually fixes it; if it keeps happening, send the details below
            to your administrator.
          </p>
          <pre style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word', background: '#f0ebe3', padding: 12, borderRadius: 8, fontSize: 12 }}>
            {error.name}: {error.message}
            {error.digest ? `\ndigest: ${error.digest}` : ''}
          </pre>
          <div style={{ display: 'flex', gap: 8 }}>
            <button type="button" style={btn} onClick={reset}>Try again</button>
            <button type="button" style={btn} onClick={() => window.location.reload()}>Reload</button>
          </div>
        </div>
      </body>
    </html>
  )
}
