/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        brand: {
          50:  '#f0f4ff',
          100: '#e0eaff',
          500: '#4f6df5',
          600: '#3b5bdb',
          700: '#2f4acf',
        },
      },
    },
  },
  plugins: [],
}
