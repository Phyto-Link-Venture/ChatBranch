/**
 * The app icon and splash screen, from the ONE place the brand mark already
 * lives: the dashboard's PWA icons (`frontend/public/icons/`, rendered from
 * `components/shell/Logo.tsx`). Keeping a second copy of the artwork in this
 * folder is how an app icon silently drifts from the product's, so nothing here
 * is committed — `npm run icons` regenerates all of it.
 *
 * It stages `assets/`, then runs @capacitor/assets for the platforms that are
 * actually added, so a repo with only Android does not grow an unused iOS set
 * (and neither grows a second copy of the PWA icons the dashboard already has).
 */
import { spawnSync } from 'child_process'
import { copyFileSync, existsSync, mkdirSync } from 'fs'
import { dirname, join } from 'path'
import { fileURLToPath } from 'url'

const here = dirname(fileURLToPath(import.meta.url))
const root = join(here, '..')
const icons = join(root, '..', 'frontend', 'public', 'icons')
const assets = join(root, 'assets')

const source = join(icons, 'icon-512.png')
if (!existsSync(source)) {
  console.error(`Cannot find ${source} — run this inside the repository, with the dashboard present.`)
  process.exit(1)
}

mkdirSync(assets, { recursive: true })
// @capacitor/assets' own conventions: icon.png and splash.png, from which it
// renders every size the two stores ask for.
copyFileSync(source, join(assets, 'icon.png'))
copyFileSync(source, join(assets, 'splash.png'))
const maskable = join(icons, 'maskable-512.png')
if (existsSync(maskable)) copyFileSync(maskable, join(assets, 'icon-foreground.png'))
console.log("Staged the icon + splash from the dashboard's own mark.")

const platforms = []
if (existsSync(join(root, 'android'))) platforms.push('--android')
if (existsSync(join(root, 'ios'))) platforms.push('--ios')
if (platforms.length === 0) {
  console.log('No platform added yet — run `npm run add:android` (or `add:ios`), then this again.')
  process.exit(0)
}

// Paper & Ink's paper, light and dark, so the splash matches the app it opens
const run = spawnSync(
  join(root, 'node_modules', '.bin', 'capacitor-assets'),
  [
    'generate',
    ...platforms,
    '--iconBackgroundColor', '#FAF7F2',
    '--iconBackgroundColorDark', '#1A1714',
    '--splashBackgroundColor', '#FAF7F2',
    '--splashBackgroundColorDark', '#1A1714',
  ],
  { cwd: root, stdio: 'inherit' },
)
process.exit(run.status ?? 1)
