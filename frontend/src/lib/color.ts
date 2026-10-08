/** Translucent version of any CSS colour — hex, rgb() or a var() token.
 *  `alpha` is 0..1 or a two-digit hex alpha ('1F'), the old `color + '1F'`
 *  idiom, which broke once palette colours became theme-aware var()s. */
export function tint(color: string, alpha: number | string): string {
  const a = typeof alpha === 'number' ? alpha : parseInt(alpha, 16) / 255
  return `color-mix(in srgb, ${color} ${Math.round(a * 100)}%, transparent)`
}
