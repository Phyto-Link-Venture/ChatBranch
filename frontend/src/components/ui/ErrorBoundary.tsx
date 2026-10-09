'use client'

import { Component, type ErrorInfo, type ReactNode } from 'react'
import { isStaleBuildError, reloadForNewBuild, reportClientError } from '@/lib/clientErrors'

/**
 * Keeps one widget's crash from taking the whole page down (the assistant
 * dock, the command palette…): the widget renders `fallback` (default:
 * nothing), the error goes to the API log, and a stale-build chunk error
 * reloads the page once.
 */
export class ErrorBoundary extends Component<{ name: string; fallback?: ReactNode; children: ReactNode }, { failed: boolean }> {
  state = { failed: false }

  static getDerivedStateFromError() {
    return { failed: true }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    if (isStaleBuildError(error) && reloadForNewBuild()) return
    reportClientError(error, { where: this.props.name })
    if (process.env.NODE_ENV !== 'production') console.error(`[${this.props.name}]`, error, info.componentStack)
  }

  render() {
    return this.state.failed ? (this.props.fallback ?? null) : this.props.children
  }
}
