/**
 * The ChatBranch mark: a vertical stem with one branch curving off it to a
 * tangerine dot — a conversation that branches. Drawn on a 26-unit grid.
 *
 * The same geometry is the source of the PWA icons in public/icons/ (rendered
 * to PNG from `brandMarkSvg`), so a change here means re-rendering those.
 * Server-safe (no hooks), so the login page and the shell can both use it.
 */

/** Stroke paths + dots of the mark, in a 26×26 viewBox. */
export const MARK = {
  viewBox: '0 0 26 26',
  // The stem stops at the root ring rather than running through it.
  stem: 'M7 4v15.8',
  branch: 'M7 13c0-4 3-6 7-6h5',
  dot: { cx: 20, cy: 7, r: 2.6 },
  root: { cx: 7, cy: 22, r: 2.2 },
  stroke: 1.8,
} as const

/** The mark in the current ink (`currentColor`) with the theme accent dot. */
export function BranchMark({ size = 26, className = '', title }: { size?: number; className?: string; title?: string }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox={MARK.viewBox}
      fill="none"
      stroke="currentColor"
      strokeWidth={MARK.stroke}
      strokeLinecap="round"
      className={className}
      role={title ? 'img' : undefined}
      aria-label={title}
      aria-hidden={title ? undefined : true}
    >
      <path d={MARK.stem} />
      <path d={MARK.branch} />
      {/* var() only resolves through `style`, never an SVG attribute */}
      <circle cx={MARK.dot.cx} cy={MARK.dot.cy} r={MARK.dot.r} style={{ fill: 'var(--accent)', stroke: 'var(--accent)' }} />
      <circle cx={MARK.root.cx} cy={MARK.root.cy} r={MARK.root.r} />
    </svg>
  )
}

/** Mark + "ChatBranch" wordmark in the theme's display face. */
export function Logo({
  size = 26,
  wordmark = true,
  className = '',
  textClassName = 'text-[21px]',
}: {
  size?: number
  wordmark?: boolean
  className?: string
  textClassName?: string
}) {
  return (
    <span className={`inline-flex items-center gap-2.5 text-text-heading ${className}`}>
      <BranchMark size={size} title={wordmark ? undefined : 'ChatBranch'} />
      {wordmark && <span className={`display leading-none ${textClassName}`}>ChatBranch</span>}
    </span>
  )
}

/**
 * Standalone SVG markup of the mark for icon rendering (static colours — an
 * icon has no theme). `pad` is the fraction of the canvas left around the
 * mark: ~0.16 for a regular icon, ~0.24 to sit inside a maskable safe zone.
 */
export function brandMarkSvg({
  size = 512,
  ink = '#1A1A1A',
  accent = '#FF5A1F',
  background = '#FAF7F2',
  pad = 0.16,
  radius = 0,
  stroke = MARK.stroke,
}: {
  size?: number
  ink?: string
  accent?: string
  background?: string | null
  pad?: number
  radius?: number
  /** Stroke in mark units — heavier for favicon sizes so the stem survives. */
  stroke?: number
} = {}): string {
  const inner = size * (1 - pad * 2)
  const scale = inner / 26
  const offset = size * pad
  // The mark's ink sits right of and below the grid centre (13,13) by about
  // (0.7, 1.1) units — shift it so an icon is optically centred.
  const dx = offset - 0.7 * scale
  const dy = offset - 1.1 * scale
  const bg = background
    ? `<rect width="${size}" height="${size}" rx="${radius}" fill="${background}"/>`
    : ''
  return (
    `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 ${size} ${size}">${bg}` +
    `<g transform="translate(${dx} ${dy}) scale(${scale})" fill="none" stroke="${ink}" stroke-width="${stroke}" stroke-linecap="round">` +
    `<path d="${MARK.stem}"/><path d="${MARK.branch}"/>` +
    `<circle cx="${MARK.dot.cx}" cy="${MARK.dot.cy}" r="${MARK.dot.r}" fill="${accent}" stroke="${accent}"/>` +
    `<circle cx="${MARK.root.cx}" cy="${MARK.root.cy}" r="${MARK.root.r}"/>` +
    `</g></svg>`
  )
}
