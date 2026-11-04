import nextVitals from 'eslint-config-next/core-web-vitals'

// ESLint 9 flat config — Next 16 dropped `next lint` and the .eslintrc format.
// The same rules as before: Next's core-web-vitals set + the two hooks rules.
const config = [
  ...nextVitals,
  {
    rules: {
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'warn',
      // eslint-plugin-react-hooks 7 (with eslint-config-next 16) adds the React
      // Compiler's rules as errors. The dashboard doesn't use the compiler;
      // adopting them is its own change, so they stay off for now.
      'react-hooks/set-state-in-effect': 'off',
      'react-hooks/refs': 'off',
      'react-hooks/static-components': 'off',
      'react-hooks/purity': 'off',
      'react-hooks/immutability': 'off',
    },
  },
  { ignores: ['.next/**', 'node_modules/**', 'next-env.d.ts'] },
]

export default config
