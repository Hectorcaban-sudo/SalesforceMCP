/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{js,jsx}'],
  theme: {
    extend: {
      fontFamily: {
        sans: ['Geist', 'system-ui', 'sans-serif'],
        mono: ['"JetBrains Mono"', 'ui-monospace', 'monospace'],
        display: ['"Instrument Serif"', 'serif'],
      },
      colors: {
        ink: {
          50: '#f6f7f8',
          100: '#e8eaec',
          200: '#c9ced3',
          300: '#9aa1a9',
          400: '#6d7680',
          500: '#4a525c',
          600: '#353c44',
          700: '#252a30',
          800: '#181c21',
          900: '#0e1116',
          950: '#080a0d',
        },
        accent: {
          DEFAULT: '#22d3a0',
          dim: '#0e8f6f',
          glow: '#5eead4',
        },
      },
    },
  },
  plugins: [],
}
