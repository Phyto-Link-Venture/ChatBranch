// Server-safe (no 'use client'): imported by the root layout, which is a
// server component — a constant exported from a client module would arrive
// there as a client reference, not a string.
const STORE_KEY = 'ui-appearance' // keep in step with lib/theme.ts

/** Inline pre-paint script (layout.tsx): re-applies the cached classes,
 *  variables and data attributes (navigation layout, motion, surfaces) before
 *  first paint. Kept tiny and dependency-free. */
export const PREPAINT_SCRIPT = `(function(){try{var d=document.documentElement,c=d.classList,s=localStorage.getItem('${STORE_KEY}');if(s){var o=JSON.parse(s),p=o.prefs||{},dk=o.dark;if(p.mode==='system'&&window.matchMedia){dk=window.matchMedia('(prefers-color-scheme: dark)').matches}c.remove('light','dark');c.add(o.cls||'theme-paper',dk?'dark':'light');var v=o.vars||{};for(var k in v){d.style.setProperty(k,v[k])}var a=o.attrs||{};for(var n in a){d.setAttribute(n,a[n])}d.style.colorScheme=dk?'dark':'light';return}var t=localStorage.getItem('theme');if(t==='dark'){c.remove('light');c.add('dark')}}catch(e){}})();`
